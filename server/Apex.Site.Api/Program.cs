using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;

using Microsoft.Extensions.FileProviders;

using Apex.Site.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// 64 KB, matching web.config's requestLimits. Every endpoint takes a small
// JSON body; IIS enforces this on the server, Kestrel on the dev box.
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 65_536);
builder.Services.Configure<IISServerOptions>(o => o.MaxRequestBodySize = 65_536);

builder.Services.AddSingleton<JsonlWriter>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<JsonlWriter>());
builder.Services.AddSingleton<MongoMirror>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MongoMirror>());
builder.Services.AddSingleton<EmailNotifier>();

// Both endpoints are open to the internet, so the limits are the only thing
// between a script and the inbox. Partitioned by a salted hash of the caller
// rather than the address itself.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy("events", http => RateLimitPartition.GetTokenBucketLimiter(
        CallerKey.For(http),
        _ => new TokenBucketRateLimiterOptions
        {
            // A page load bursts a few events; a firehose does not stop.
            TokenLimit = 120,
            TokensPerPeriod = 60,
            ReplenishmentPeriod = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        }));

    options.AddPolicy("contact", http => RateLimitPartition.GetFixedWindowLimiter(
        CallerKey.For(http),
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromHours(1),
            QueueLimit = 0,
        }));
});

var app = builder.Build();

// On the dev box, serve the site itself so the forms and beacons are
// same-origin exactly as they are under IIS: http://localhost:5080/ is the
// site and /api/* is this app. UsePathBase must precede UseRouting.
if (app.Environment.IsDevelopment())
{
    app.UsePathBase("/api");
    var site = new PhysicalFileProvider(Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "..", "..")));
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = site });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = site });
}

app.UseRouting();
app.UseRateLimiter();

var varDir = app.Configuration["Site:VarDir"] ?? Path.Combine(AppContext.BaseDirectory, "var");
var allowedOrigins = app.Configuration.GetSection("Site:AllowedOrigins").Get<string[]>()
    ?? ["https://apextechnologygroup.io"];

// Incoming bodies: snake_case, and an unknown field is a 400 rather than
// silently ignored, so a client and server that disagree fail loudly.
var requestJson = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
};
var rowJson = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

// NOTE: routes are mapped WITHOUT the /api prefix. /api is a nested IIS
// application, so AspNetCoreModule sets PathBase to /api automatically.
// Mapping "/api/events" here would answer at /api/api/events in production.
app.MapGet("/health", (MongoMirror mongo, EmailNotifier email) => Results.Ok(new
{
    status = "ok",
    mongo = mongo.Status,
    email = email.Configured ? "configured" : "not_configured",
}));

// -------------------------------------------------------------------- events

app.MapPost("/events", async (HttpContext http, JsonlWriter files, MongoMirror mongo) =>
{
    if (!OriginAllowed(http, allowedOrigins)) return Results.StatusCode(StatusCodes.Status403Forbidden);

    var batch = await ReadJson<EventBatch>(http, requestJson);
    if (batch?.Events is not { Count: > 0 }) return Results.NoContent();

    var receivedAt = DateTimeOffset.UtcNow;
    var caller = CallerKey.For(http);
    var ua = Truncate(http.Request.Headers.UserAgent.ToString(), 256);
    var session = Truncate(batch.Session, 64);
    var bot = IsBot(ua);

    foreach (var e in batch.Events.Take(25))
    {
        if (e.T is null || !EventTypes.Contains(e.T)) continue;

        // One line, two destinations, file first. The id is minted here so the
        // file row and the Mongo document are the same event under the same key.
        var line = JsonSerializer.Serialize(new
        {
            id = Guid.NewGuid().ToString("n"),
            t = e.T,
            path = Truncate(e.Path, 300),
            at = e.At,
            ctx = CleanContext(e.Ctx),
            session,
            caller,
            ua,
            bot,
            received_at = receivedAt,
        }, rowJson);

        files.WriteLine(DailyPath(varDir, "events"), line);
        mongo.Write(MongoMirror.Events, line);
    }

    // Always 204, always empty: nothing to scrape.
    return Results.NoContent();
}).RequireRateLimiting("events");

// ------------------------------------------------------------------- contact

app.MapPost("/contact", async (HttpContext http, JsonlWriter files, MongoMirror mongo, EmailNotifier email) =>
{
    if (!OriginAllowed(http, allowedOrigins)) return Results.StatusCode(StatusCodes.Status403Forbidden);

    var form = await ReadJson<ContactRequest>(http, requestJson);
    if (form is null) return Results.BadRequest(new { error = "Unreadable request." });

    // Bots fill the hidden field and submit the instant the page loads. They
    // get the same 202 a person does, so there is nothing to tune against.
    if (!string.IsNullOrEmpty(form.Hp) || form.ElapsedMs is null or < 3000) return Results.Accepted();

    var error = Validate(form);
    if (error is not null) return Results.BadRequest(new { error });

    var id = Guid.NewGuid().ToString("n");
    var line = JsonSerializer.Serialize(new
    {
        id,
        source = form.Source,
        name = form.Name!.Trim(),
        email = form.Email!.Trim(),
        phone = Blank(form.Phone),
        organization = Blank(form.Organization),
        role = Blank(form.Role),
        parish = Blank(form.Parish),
        timeframe = Blank(form.Timeframe),
        interests = form.Interests,
        message = Blank(form.Message),
        caller = CallerKey.For(http),
        ua = Truncate(http.Request.Headers.UserAgent.ToString(), 256),
        received_at = DateTimeOffset.UtcNow,
    }, rowJson);

    files.WriteLine(Path.Combine(varDir, "contacts", "contacts.jsonl"), line);
    mongo.Write(MongoMirror.Contacts, line);

    var (subject, body) = Notification(form, id);
    await email.TrySendAsync(subject, body, replyTo: form.Email!.Trim());

    return Results.Accepted();
}).RequireRateLimiting("contact");

app.Run();

// ------------------------------------------------------------------- helpers

/// <summary>
/// Blocks other websites from posting through a visitor's browser. A missing
/// Origin is allowed: scripts can forge it anyway, and the rate limits are
/// what stop them.
/// </summary>
static bool OriginAllowed(HttpContext http, string[] allowed)
{
    var origin = http.Request.Headers.Origin.ToString();
    return string.IsNullOrEmpty(origin) || allowed.Contains(origin, StringComparer.OrdinalIgnoreCase);
}

static async Task<T?> ReadJson<T>(HttpContext http, JsonSerializerOptions options)
{
    try
    {
        return await JsonSerializer.DeserializeAsync<T>(http.Request.Body, options, http.RequestAborted);
    }
    catch
    {
        // Malformed bodies are noise from the open internet, not incidents.
        return default;
    }
}

static string DailyPath(string varDir, string kind)
    => Path.Combine(varDir, kind, $"{kind}-{DateTime.UtcNow:yyyy-MM-dd}.jsonl");

static string? Truncate(string? value, int max)
    => value is null ? null : value.Length <= max ? value : value[..max];

static string? Blank(string? value)
    => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

/// <summary>Same tokens as flightdose.com's deploy\Get-CampaignVisits.ps1. An empty agent is a script.</summary>
static bool IsBot(string? ua)
    => string.IsNullOrWhiteSpace(ua) || Regex.IsMatch(ua,
        "bot|crawl|spider|slurp|preview|facebookexternalhit|whatsapp|curl|wget|python|go-http|java/|okhttp|headless",
        RegexOptions.IgnoreCase);

static Dictionary<string, string>? CleanContext(Dictionary<string, string>? ctx)
    => ctx is null ? null : ctx
        .Where(p => p.Key.Length is > 0 and <= 40 && p.Value is not null)
        .Take(10)
        .ToDictionary(p => p.Key, p => Truncate(p.Value, 200)!);

static string? Validate(ContactRequest f)
{
    if (f.Source is null || !Sources.ContainsKey(f.Source)) return "Unknown form.";
    if (string.IsNullOrWhiteSpace(f.Name) || f.Name.Length > 120) return "Please enter your name.";
    if (string.IsNullOrWhiteSpace(f.Email) || f.Email.Length > 254 ||
        !Regex.IsMatch(f.Email.Trim(), @"\A[^@\s]+@[^@\s]+\.[^@\s]+\z")) return "Please enter a valid email address.";
    if (f.Phone?.Length > 40 || f.Organization?.Length > 160 || f.Role?.Length > 120 ||
        f.Parish?.Length > 60 || f.Timeframe?.Length > 60) return "One of the fields is too long.";
    if (f.Interests is { } i && (i.Count > 15 || i.Any(x => x is null || x.Length > 100))) return "Too many interests selected.";
    if (f.Message?.Length > 5000) return "Please keep the message under 5,000 characters.";
    return null;
}

/// <summary>Plain text, laid out like the mailto the forms used to compose, so it reads the same in the inbox.</summary>
static (string Subject, string Body) Notification(ContactRequest f, string id)
{
    static string OneLine(string s) => Regex.Replace(s.Trim(), @"[\r\n]+", " ");

    var name = OneLine(f.Name!);
    var org = string.IsNullOrWhiteSpace(f.Organization) ? null : OneLine(f.Organization);
    var subject = $"apextechnologygroup.io: {Sources[f.Source!]} inquiry from {name}" + (org is null ? "" : $" ({org})");

    var body = new StringBuilder()
        .AppendLine($"New inquiry from the {Sources[f.Source!]} form.")
        .AppendLine()
        .AppendLine($"Name: {name}")
        .AppendLine($"Email: {f.Email!.Trim()}")
        .AppendLine($"Phone: {Blank(f.Phone) ?? "-"}")
        .AppendLine($"Organization: {org ?? "-"}");
    if (Blank(f.Role) is { } role) body.AppendLine($"Role: {role}");
    if (Blank(f.Parish) is { } parish) body.AppendLine($"Parish: {parish}");
    if (Blank(f.Timeframe) is { } timeframe) body.AppendLine($"Timeframe: {timeframe}");
    body.AppendLine($"Interested in: {(f.Interests is { Count: > 0 } i ? string.Join(", ", i) : "-")}")
        .AppendLine()
        .AppendLine("Message:")
        .AppendLine(Blank(f.Message) ?? "-")
        .AppendLine()
        .AppendLine("Reply to this email to answer them directly.")
        .AppendLine($"Submission {id}");

    return (subject, body.ToString());
}

internal sealed record EventBatch(string? Session, List<TrackedEvent>? Events);

internal sealed record TrackedEvent(string? T, string? Path, DateTimeOffset? At, Dictionary<string, string>? Ctx);

internal sealed record ContactRequest(
    string? Source,
    string? Name,
    string? Email,
    string? Phone,
    string? Organization,
    string? Role,
    string? Parish,
    string? Timeframe,
    List<string>? Interests,
    string? Message,
    string? Hp,
    int? ElapsedMs);

internal partial class Program
{
    private static readonly HashSet<string> EventTypes =
        ["page_view", "cta_click", "outbound_click", "form_start", "form_submit"];

    /// <summary>Which form sent it, and how it is named in the notification.</summary>
    private static readonly Dictionary<string, string> Sources = new()
    {
        ["home"] = "Homepage",
        ["new-orleans"] = "New Orleans business services",
        ["nuclear"] = "Nuclear training services",
    };
}
