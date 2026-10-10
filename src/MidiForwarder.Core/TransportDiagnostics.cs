using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;

namespace MidiForwarder.Core;

/// <summary>Bounded asynchronous metadata logging, without tokens or MIDI payloads.</summary>
public sealed class TransportDiagnostics : IAsyncDisposable
{
    private static readonly SemaphoreSlim DiskWriter = new(1, 1);
    private readonly Channel<object> _records = Channel.CreateBounded<object>(
        new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private readonly object _sync = new();
    private readonly Dictionary<string, Direction> _directions = [];
    private readonly Task _writer;
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private readonly long _started = Stopwatch.GetTimestamp();
    private long _lastReport = Stopwatch.GetTimestamp(), _dropped;
    private bool _finished;

    public TransportDiagnostics(string path)
    {
        _writer = Task.Run(() => WriteAsync(path));
        Enqueue(new { timestamp = DateTimeOffset.Now, kind = "start", sessionId = _sessionId, protocol = 3 });
    }

    public void Record(string direction, MidiPacket packet, double milliseconds, MidiQueueStatistics? queue = null)
    {
        lock (_sync)
        {
            if (!_directions.TryGetValue(direction, out var counter)) { counter = new Direction(); _directions.Add(direction, counter); }
            counter.Messages++; counter.Bytes += packet.Data.Length;
            counter.TotalMilliseconds += milliseconds; counter.MaxMilliseconds = Math.Max(counter.MaxMilliseconds, milliseconds);
            if (packet.EnqueuedTimestamp > 0)
            {
                counter.MaxQueueMilliseconds = Math.Max(counter.MaxQueueMilliseconds,
                    Math.Max(0, Stopwatch.GetElapsedTime(packet.EnqueuedTimestamp).TotalMilliseconds - milliseconds));
            }
            string type = packet.Format == MidiPacketFormat.UniversalMidiPacketBatch ? "complete-sysex" :
                packet.Format == MidiPacketFormat.Midi1 ? "midi1" : $"ump-{packet.Data.Span[0] >> 4:X}";
            counter.Types[type] = counter.Types.GetValueOrDefault(type) + 1;
            if (Stopwatch.GetElapsedTime(_lastReport).TotalSeconds >= 2)
            { Report("progress", null, queue); _lastReport = Stopwatch.GetTimestamp(); }
        }
    }

    public void Failure(string direction, MidiPacket packet, double milliseconds, Exception error) => Enqueue(new
    {
        timestamp = DateTimeOffset.Now, kind = "failure", sessionId = _sessionId, direction,
        format = packet.Format.ToString(), bytes = packet.Data.Length, milliseconds, failure = error.GetType().Name,
    });

    public void Finish(Exception? error, MidiQueueStatistics? queue)
    {
        lock (_sync)
        {
            if (_finished) { return; }
            _finished = true;
            Report("summary", error?.GetType().Name, queue);
        }
    }

    private void Report(string kind, string? failure, MidiQueueStatistics? queue) => Enqueue(new
    {
        timestamp = DateTimeOffset.Now, kind, sessionId = _sessionId,
        milliseconds = Stopwatch.GetElapsedTime(_started).TotalMilliseconds, failure, queue,
        directions = _directions.ToDictionary(pair => pair.Key, pair => new
        {
            pair.Value.Messages, pair.Value.Bytes, pair.Value.TotalMilliseconds, pair.Value.MaxMilliseconds, pair.Value.MaxQueueMilliseconds,
            types = new Dictionary<string, long>(pair.Value.Types),
        }),
        droppedLogRecords = Interlocked.Read(ref _dropped),
    });

    private void Enqueue(object record)
    { if (!_records.Writer.TryWrite(record)) { Interlocked.Increment(ref _dropped); } }

    private async Task WriteAsync(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            await foreach (object record in _records.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                await DiskWriter.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (File.Exists(path) && new FileInfo(path).Length >= 2 * 1024 * 1024)
                    {
                        if (File.Exists(path + ".1")) { File.Move(path + ".1", path + ".2", true); }
                        File.Move(path, path + ".1", true);
                    }
                    await File.AppendAllTextAsync(path, JsonSerializer.Serialize(record) + Environment.NewLine).ConfigureAwait(false);
                }
                finally { DiskWriter.Release(); }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { _records.Writer.TryComplete(); }
    }

    public async ValueTask DisposeAsync()
    { Finish(null, null); _records.Writer.TryComplete(); await _writer.ConfigureAwait(false); }

    private sealed class Direction
    {
        public long Messages, Bytes;
        public double TotalMilliseconds, MaxMilliseconds, MaxQueueMilliseconds;
        public Dictionary<string, long> Types { get; } = [];
    }
}
