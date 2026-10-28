using System.Globalization;
using System.Threading.Channels;

using MongoDB.Bson;
using MongoDB.Driver;

namespace Apex.Site.Api.Services;

/// <summary>
/// Mirrors analytics events and contact submissions into Mongo, after the file has them.
/// <para>
/// The JSONL append is the durability guarantee and stays that way: the
/// endpoint queues the row for the file first and for this second, and nothing
/// here can fail a request. If Mongo is down the site behaves exactly as it
/// would without Mongo, and the rows are still in the files. Every row carries
/// an id minted by the API so a later backfill lands on top of, not beside,
/// what the live write managed. Adapted from ToxFinder's EventStore.
/// </para>
/// </summary>
public sealed class MongoMirror : BackgroundService
{
    public const string Events = "events";
    public const string Contacts = "contacts";

    // Fields stored as BSON dates rather than ISO strings, so range queries work.
    private static readonly string[] DateFields = ["at", "received_at"];

    private readonly Channel<(string Collection, BsonDocument Doc)> _channel =
        Channel.CreateBounded<(string, BsonDocument)>(new BoundedChannelOptions(10_000)
        {
            // Same policy as the file writer: under a flood, drop rather than
            // hold the request thread.
            FullMode = BoundedChannelFullMode.DropWrite,
        });

    private readonly IMongoDatabase? _db;
    private readonly ILogger<MongoMirror> _log;
    private readonly string _target;

    private volatile string? _lastError;
    private DateTime _lastWarned = DateTime.MinValue;

    public MongoMirror(IConfiguration config, ILogger<MongoMirror> log)
    {
        _log = log;

        var connection = config["Mongo:ConnectionString"];
        var database = config["Mongo:Database"] ?? "apextechnologygroup";
        _target = $"db={database}";

        if (string.IsNullOrWhiteSpace(connection))
        {
            log.LogWarning("Mongo is not configured; rows are written to JSONL only.");
            return;
        }

        try
        {
            var settings = MongoClientSettings.FromConnectionString(connection);
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(3);
            _db = new MongoClient(settings).GetDatabase(database);
            log.LogInformation("Mongo configured: {Target}", _target);
        }
        catch (Exception ex)
        {
            // A bad connection string must not stop the API booting.
            log.LogError(ex, "Mongo connection string could not be parsed; rows stay JSONL-only.");
        }
    }

    public bool Configured => _db is not null;

    /// <summary>For /health: not_configured, configured, or unreachable once a flush has failed.</summary>
    public string Status => !Configured ? "not_configured" : _lastError is null ? "configured" : "unreachable";

    /// <summary>Queues the same line that went to the file. Never blocks, never throws.</summary>
    public void Write(string collection, string jsonLine)
    {
        if (_db is null) return;

        try
        {
            var doc = BsonDocument.Parse(jsonLine);
            doc["_id"] = doc["id"];
            foreach (var field in DateFields)
                if (doc.TryGetValue(field, out var value) && value is BsonString s &&
                    DateTimeOffset.TryParse(s.Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                    doc[field] = new BsonDateTime(parsed.UtcDateTime);

            _channel.Writer.TryWrite((collection, doc));
        }
        catch (Exception)
        {
            // The line came from our own serializer, so this is unreachable in
            // practice; if it ever happens the file still has the row.
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_db is null) return;

        await EnsureIndexesAsync(stoppingToken);

        var batch = new List<(string Collection, BsonDocument Doc)>(256);

        try
        {
            while (await _channel.Reader.WaitToReadAsync(stoppingToken))
            {
                batch.Clear();
                while (batch.Count < 256 && _channel.Reader.TryRead(out var item))
                    batch.Add(item);

                await FlushAsync(batch);
            }
        }
        catch (OperationCanceledException)
        {
            batch.Clear();
            while (_channel.Reader.TryRead(out var item)) batch.Add(item);
            await FlushAsync(batch);
        }
    }

    private async Task EnsureIndexesAsync(CancellationToken ct)
    {
        var keys = Builders<BsonDocument>.IndexKeys;
        try
        {
            await _db!.GetCollection<BsonDocument>(Events).Indexes.CreateManyAsync(
            [
                new(keys.Ascending("t").Ascending("received_at")),
                new(keys.Ascending("path").Ascending("received_at")),
                new(keys.Ascending("session").Ascending("received_at")),
                new(keys.Ascending("received_at")),
            ], ct);
            await _db.GetCollection<BsonDocument>(Contacts).Indexes.CreateManyAsync(
            [
                new(keys.Ascending("received_at")),
                new(keys.Ascending("source").Ascending("received_at")),
            ], ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Indexes are a convenience for the reader, not a condition for writing.
            Warn(ex, "Could not create indexes");
        }
    }

    private async Task FlushAsync(List<(string Collection, BsonDocument Doc)> batch)
    {
        foreach (var group in batch.GroupBy(b => b.Collection))
        {
            var writes = group
                .Select(b => (WriteModel<BsonDocument>)new ReplaceOneModel<BsonDocument>(
                    Builders<BsonDocument>.Filter.Eq("_id", b.Doc["_id"]), b.Doc) { IsUpsert = true })
                .ToList();

            try
            {
                await _db!.GetCollection<BsonDocument>(group.Key)
                    .BulkWriteAsync(writes, new BulkWriteOptions { IsOrdered = false });
                if (_lastError is not null)
                {
                    _lastError = null;
                    _log.LogInformation("Mongo writing again: {Target}", _target);
                }
            }
            catch (Exception ex)
            {
                // Dropped here, kept in the file. Warned once a minute rather than
                // once per batch so an outage does not fill the log.
                _lastError = ex.Message;
                Warn(ex, $"Dropped {writes.Count} {group.Key} rows from Mongo (still in JSONL)");
            }
        }
    }

    private void Warn(Exception ex, string what)
    {
        var now = DateTime.UtcNow;
        if (now - _lastWarned < TimeSpan.FromMinutes(1)) return;
        _lastWarned = now;
        _log.LogWarning(ex, "{What}: {Target}", what, _target);
    }
}
