using System.Threading.Channels;

namespace Apex.Site.Api.Services;

/// <summary>
/// The single writer for everything the public submits: the file-first record.
/// <para>
/// Two concurrent requests appending to the same file interleave partial
/// lines, so writes funnel through one channel drained by one background loop.
/// The app pool must also be pinned to a single worker process, or there are
/// two of these and the guarantee evaporates. Adapted from ToxFinder's
/// SubmissionWriter.
/// </para>
/// </summary>
public sealed class JsonlWriter : BackgroundService
{
    private readonly Channel<(string Path, string Line)> _channel =
        Channel.CreateBounded<(string, string)>(new BoundedChannelOptions(10_000)
        {
            // Under a flood, drop rather than block the request thread. An
            // analytics event is worth less than a responsive site.
            FullMode = BoundedChannelFullMode.DropWrite,
        });

    private readonly ILogger<JsonlWriter> _log;

    public JsonlWriter(ILogger<JsonlWriter> log) => _log = log;

    /// <summary>Queues an already-serialized line. Never blocks, never throws.</summary>
    public void WriteLine(string path, string line)
        => _channel.Writer.TryWrite((path, line));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<(string Path, string Line)>(256);

        try
        {
            while (await _channel.Reader.WaitToReadAsync(stoppingToken))
            {
                batch.Clear();
                while (batch.Count < 256 && _channel.Reader.TryRead(out var item))
                    batch.Add(item);

                Flush(batch);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down: drain whatever is still queued rather than losing it.
            batch.Clear();
            while (_channel.Reader.TryRead(out var item)) batch.Add(item);
            Flush(batch);
        }
    }

    private void Flush(List<(string Path, string Line)> batch)
    {
        foreach (var group in batch.GroupBy(b => b.Path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(group.Key)!);
                File.AppendAllLines(group.Key, group.Select(g => g.Line));
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed writing {Count} rows to {Path}", group.Count(), group.Key);
            }
        }
    }
}
