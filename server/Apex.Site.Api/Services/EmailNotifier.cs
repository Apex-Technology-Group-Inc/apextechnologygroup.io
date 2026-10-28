using System.Text.Json;

using Amazon;
using Amazon.SimpleEmailV2;
using Amazon.SimpleEmailV2.Model;

namespace Apex.Site.Api.Services;

/// <summary>
/// Tells a human that somebody filled in the contact form.
/// <para>
/// Copied from ToxFinder's EmailNotifier, which reads the same shared SES
/// credentials file, with a Reply-To added so an inquiry can be answered by
/// replying to the notification. The JSONL row remains the record; this only
/// does the shouting. A missing credentials file means silence, not a crash.
/// </para>
/// </summary>
public sealed class EmailNotifier : IDisposable
{
    private readonly AmazonSimpleEmailServiceV2Client? _client;
    private readonly ILogger<EmailNotifier> _log;
    private string _from;
    private readonly string _to;

    public bool Configured => _client is not null;

    public EmailNotifier(IConfiguration config, ILogger<EmailNotifier> log)
    {
        _log = log;
        _from = config["Email:From"] ?? "";
        _to = config["Email:To"] ?? "";

        var path = config["Email:CredentialsPath"];

        if (string.IsNullOrWhiteSpace(_to) || path is null || !File.Exists(path))
        {
            log.LogWarning("Email is not configured; contact submissions will be stored but not sent.");
            return;
        }

        try
        {
            var creds = SesCredentials.Read(path);

            // Prefer the sender in the credentials file. SES rejects mail from
            // an address that has not been verified in the account, and the
            // address shipped alongside the keys is the one that has been.
            if (string.IsNullOrWhiteSpace(_from)) _from = creds.FromEmail ?? "";

            if (string.IsNullOrWhiteSpace(_from))
            {
                log.LogError("No sender address in config or {Path}; email stays off.", path);
                return;
            }

            // The display name is free to change; only the address is verified.
            var fromName = config["Email:FromName"];
            if (!string.IsNullOrWhiteSpace(fromName) && !_from.Contains('<'))
                _from = $"{fromName} <{_from}>";

            _client = new AmazonSimpleEmailServiceV2Client(
                creds.AccessKeyId, creds.SecretAccessKey, RegionEndpoint.GetBySystemName(creds.Region));

            log.LogInformation(
                "Email configured from {Path}: region {Region}, sender {From}, notify {To}.",
                path, creds.Region, _from, _to);
        }
        catch (Exception ex)
        {
            // A malformed credentials file must not stop the API booting. The
            // form keeps working and keeps storing; only the notification stops.
            log.LogError(ex, "Email credentials at {Path} could not be read.", path);
        }
    }

    /// <summary>
    /// Sends, and swallows everything.
    /// <para>
    /// Called after the submission is already on disk, so the durable record
    /// exists whatever happens here. An SES outage must never turn into a
    /// failed form for the person filling it in -- they would simply submit
    /// again, and we would get duplicates of the one thing we did not want to lose.
    /// </para>
    /// </summary>
    public async Task TrySendAsync(string subject, string body, string? replyTo = null, CancellationToken ct = default)
    {
        if (_client is null) return;

        try
        {
            await _client.SendEmailAsync(new SendEmailRequest
            {
                FromEmailAddress = _from,
                Destination = new Destination { ToAddresses = [_to] },
                ReplyToAddresses = string.IsNullOrWhiteSpace(replyTo) ? null : [replyTo],
                Content = new EmailContent
                {
                    Simple = new Message
                    {
                        Subject = new Content { Data = subject, Charset = "UTF-8" },
                        Body = new Body { Text = new Content { Data = body, Charset = "UTF-8" } },
                    },
                },
            }, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Notification email failed: {Subject}", subject);
        }
    }

    public void Dispose() => _client?.Dispose();

    /// <summary>
    /// The credentials file, read tolerantly: it belongs to another project, so
    /// every common key spelling is accepted, at the root or one level down.
    /// </summary>
    private sealed record SesCredentials(
        string AccessKeyId, string SecretAccessKey, string Region, string? FromEmail)
    {
        public static SesCredentials Read(string path)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));

            foreach (var scope in Scopes(doc.RootElement))
            {
                var access = Find(scope, "accessKeyId", "accessKey", "aws_access_key_id", "key");
                var secret = Find(scope, "secretAccessKey", "secretKey", "aws_secret_access_key", "secret");
                if (access is null || secret is null) continue;

                var region = Find(scope, "region", "aws_region") ?? "us-east-1";
                var from = Find(scope, "fromEmail", "from", "sender", "sourceEmail");
                return new SesCredentials(access, secret, region, from);
            }

            // Name every key we looked at. A credentials file cannot be pasted
            // into a bug report, so the error has to carry enough to fix it.
            var found = string.Join(" | ", Scopes(doc.RootElement)
                .Where(s => s.ValueKind == JsonValueKind.Object)
                .Select(s => string.Join(", ", s.EnumerateObject().Select(p => p.Name))));

            throw new InvalidDataException(
                $"No access key and secret in {path}. Keys seen: {found}");
        }

        private static IEnumerable<JsonElement> Scopes(JsonElement root)
        {
            yield return root;

            if (root.ValueKind != JsonValueKind.Object) yield break;

            foreach (var property in root.EnumerateObject())
                if (property.Value.ValueKind == JsonValueKind.Object)
                    yield return property.Value;
        }

        private static string? Find(JsonElement root, params string[] names)
        {
            if (root.ValueKind != JsonValueKind.Object) return null;

            foreach (var property in root.EnumerateObject())
                foreach (var name in names)
                    if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) &&
                        property.Value.ValueKind == JsonValueKind.String)
                        return property.Value.GetString();

            return null;
        }
    }
}
