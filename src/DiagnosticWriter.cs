using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace CNGoldenLink;

public sealed class DiagnosticWriter
{
    private readonly Channel<DiagnosticEntry> queue = Channel.CreateBounded<DiagnosticEntry>(
        new BoundedChannelOptions(256) { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource cancellation = new();
    private readonly object lifecycle = new();
    private bool finished;
    private long dropped;
    private string status = "starting";
    public long Dropped => Interlocked.Read(ref dropped);
    public string Status => Volatile.Read(ref status);
    public Task Completion { get; }

    public DiagnosticWriter(string directory, long maxBytes = 2 * 1024 * 1024)
    {
        Completion = Task.Run(() => Run(directory, maxBytes));
    }

    // Never wait for storage from a game hook.
    public void Enqueue(DiagnosticEntry entry)
    {
        if (!queue.Writer.TryWrite(entry)) Interlocked.Increment(ref dropped);
    }

    public void Stop()
    {
        queue.Writer.TryComplete();
        // Best-effort drain; unloading never joins a worker on the game thread.
        lock (lifecycle)
        {
            if (!finished) cancellation.CancelAfter(TimeSpan.FromSeconds(2));
        }
    }

    private async Task Run(string directory, long maxBytes)
    {
        try
        {
            Directory.CreateDirectory(directory);
            foreach (var old in new DirectoryInfo(directory).GetFiles("diagnostic-*.jsonl")
                         .OrderByDescending(f => f.LastWriteTimeUtc).Skip(3))
                old.Delete();
            var path = Path.Combine(directory, $"diagnostic-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.jsonl");
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
                FileShare.Read, 4096, FileOptions.Asynchronous);
            Volatile.Write(ref status, "recording");
            long bytes = 0;
            await foreach (var entry in queue.Reader.ReadAllAsync(cancellation.Token))
            {
                var data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry,
                    JsonOptions) + "\n");
                if (bytes + data.Length > maxBytes)
                {
                    Interlocked.Increment(ref dropped);
                    Volatile.Write(ref status, "size_limit_reached");
                    break;
                }
                await stream.WriteAsync(data, cancellation.Token);
                await stream.FlushAsync(cancellation.Token);
                bytes += data.Length;
            }
            if (Status == "recording") Volatile.Write(ref status, "stopped");
        }
        catch (OperationCanceledException) { Volatile.Write(ref status, "stopped_incomplete"); }
        catch (Exception ex)
        {
            // Exception messages may contain local paths; retain only the type.
            Volatile.Write(ref status, "storage_error:" + ex.GetType().Name);
        }
        finally
        {
            queue.Writer.TryComplete();
            while (queue.Reader.TryRead(out _)) Interlocked.Increment(ref dropped);
            lock (lifecycle)
            {
                finished = true;
                cancellation.Dispose();
            }
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
