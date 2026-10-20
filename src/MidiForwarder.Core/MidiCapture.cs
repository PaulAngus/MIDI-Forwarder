using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace MidiForwarder.Core;

public sealed record MidiCaptureSnapshot(bool IsRecording, bool IsFinishing, string? Path,
    long Packets, long LostRecords, long FileBytes, string Status);

/// <summary>Opt-in, bounded capture at the relay boundary. Never waits for file I/O on the MIDI pumps.</summary>
public sealed class MidiCapture : IAsyncDisposable
{
    public const long DefaultFileLimit = 100 * 1024 * 1024;
    private readonly Lock _sync = new();
    private readonly Dictionary<string, (string Input, string Output)> _connections = new();
    private Session? _session;

    public MidiCaptureSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return _session?.Snapshot ?? new(false, false, null, 0, 0, 0, "Capture is off");
        }
    }

    public string Start(string folder, string role, long fileLimit = DefaultFileLimit)
    {
        lock (_sync)
        {
            if (_session is { IsFinished: false }) { throw new InvalidOperationException("Stop the current capture first."); }
            ArgumentOutOfRangeException.ThrowIfLessThan(fileLimit, 4096);
            Directory.CreateDirectory(folder);
            string path = System.IO.Path.Combine(folder, $"{role}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.jsonl");
            var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536, FileOptions.Asynchronous);
            _session = new Session(stream, path, role, fileLimit);
            foreach (var (id, ports) in _connections)
            {
                _session.Add("connection-start", id, text: $"Input: {ports.Input}; output: {ports.Output}; already connected when capture started");
            }
            return path;
        }
    }

    internal void Start(Stream stream, string path, string role, long fileLimit = DefaultFileLimit)
    {
        lock (_sync)
        {
            if (_session is { IsFinished: false }) { throw new InvalidOperationException("Stop the current capture first."); }
            _session = new Session(stream, path, role, fileLimit);
            foreach (var (id, ports) in _connections)
            {
                _session.Add("connection-start", id, text: $"Input: {ports.Input}; output: {ports.Output}; already connected when capture started");
            }
        }
    }

    public Task StopAsync()
    {
        lock (_sync) { return _session?.StopAsync() ?? Task.CompletedTask; }
    }

    public bool AddMarker(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        text = text.Trim();
        if (text.Length is 0 or > 500) { return false; }
        lock (_sync) { return _session?.Add("marker", text: text) ?? false; }
    }

    public string BeginConnection(string input, string output)
    {
        string id = Guid.NewGuid().ToString("N");
        lock (_sync)
        {
            _connections.Add(id, (input, output));
            _session?.Add("connection-start", id, text: $"Input: {input}; output: {output}");
        }
        return id;
    }

    public void EndConnection(string id)
    {
        lock (_sync)
        {
            _connections.Remove(id);
            _session?.Add("connection-end", id);
        }
    }

    public void Record(string connection, string direction, MidiPacket packet)
    {
        lock (_sync) { _session?.Add("packet", connection, direction, packet); }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private sealed class Session
    {
        // Both a byte budget and a record count protect against large dumps and bursts of tiny messages.
        private const long QueueByteLimit = 8 * 1024 * 1024;
        private readonly Channel<Record> _queue = Channel.CreateBounded<Record>(new BoundedChannelOptions(4096)
        {
            SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false,
        });
        private readonly Lock _gate = new();
        private readonly Stream _stream;
        private readonly string _path;
        private readonly string _role;
        private readonly long _fileLimit;
        private readonly long _origin = Stopwatch.GetTimestamp();
        private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;
        private readonly Task _writer;
        private long _queuedBytes, _sequence, _packets, _lost, _bytes;
        private bool _accepting = true, _finished;
        private string _status = "Recording";

        public Session(Stream stream, string path, string role, long fileLimit)
        {
            _stream = stream; _path = path; _role = role; _fileLimit = fileLimit;
            _writer = Task.Run(WriteAsync);
        }

        public bool IsFinished { get { lock (_gate) { return _finished; } } }
        public MidiCaptureSnapshot Snapshot
        {
            get { lock (_gate) { return new(_accepting, !_accepting && !_finished, _path, _packets, _lost, _bytes, _status); } }
        }

        public bool Add(string kind, string? connection = null, string? direction = null, MidiPacket? packet = null, string? text = null)
        {
            lock (_gate)
            {
                if (!_accepting) { return false; }
                long sequence = ++_sequence;
                int size = packet?.Data.Length ?? 0;
                // A record that cannot be queued is explicitly counted, never silently dropped.
                if (_queuedBytes + size > QueueByteLimit || _queue.Reader.Count >= 4096)
                {
                    _lost++; return false;
                }
                var record = new Record(kind, sequence, DateTimeOffset.UtcNow,
                    Stopwatch.GetTimestamp() - _origin, connection, direction, packet?.Format.ToString(),
                    packet?.Data.ToArray(), text);
                if (!_queue.Writer.TryWrite(record)) { _lost++; return false; }
                _queuedBytes += size;
                return true;
            }
        }

        public Task StopAsync()
        {
            lock (_gate)
            {
                if (_accepting) { _status = "Finishing capture"; }
                _accepting = false;
                _queue.Writer.TryComplete();
                return _writer;
            }
        }

        private async Task WriteAsync()
        {
            try
            {
                await WriteLineAsync(new { kind = "capture-start", schema = 1, role = _role, utc = _started,
                    stopwatchFrequency = Stopwatch.Frequency, fileLimitBytes = _fileLimit,
                    directions = "midi-to-network: local MIDI input received; network-to-midi: network packet received before local MIDI send",
                    note = "Full relay packets, not delivery acknowledgements. UMP bytes use the forwarder's network byte order. Sequence gaps mean lost capture records." }).ConfigureAwait(false);
                bool limitReached = false;
                await foreach (Record record in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    lock (_gate) { _queuedBytes -= record.Data?.Length ?? 0; }
                    byte[] line = Line(new { kind = record.Kind, sequence = record.Sequence, utc = record.Utc,
                        elapsedTicks = record.ElapsedTicks, connection = record.Connection, direction = record.Direction,
                        format = record.Format, length = record.Data?.Length, hex = record.Data is null ? null : Convert.ToHexString(record.Data), text = record.Text });
                    if (limitReached || Interlocked.Read(ref _bytes) + line.Length > _fileLimit - 1024)
                    {
                        lock (_gate)
                        {
                            _lost++; _accepting = false; _status = "Stopped: file size limit reached";
                            _queue.Writer.TryComplete();
                        }
                        limitReached = true;
                        continue;
                    }
                    await _stream.WriteAsync(line).ConfigureAwait(false);
                    lock (_gate) { _bytes += line.Length; if (record.Kind == "packet") { _packets++; } }
                }
                long lost;
                lock (_gate) { lost = _lost; }
                await WriteLineAsync(new { kind = "capture-end", utc = DateTimeOffset.UtcNow,
                    packets = _packets, lostRecords = lost, complete = lost == 0 && !limitReached,
                    reason = limitReached ? "file-size-limit" : "stopped" }).ConfigureAwait(false);
                await _stream.FlushAsync().ConfigureAwait(false);
                lock (_gate)
                {
                    if (!limitReached) { _status = lost > 0 ? $"Stopped: {lost} capture record(s) lost" : "Saved: no capture records lost"; }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                lock (_gate) { _lost++; _status = $"Capture incomplete: {error.Message}"; }
            }
            finally
            {
                lock (_gate) { _accepting = false; _queue.Writer.TryComplete(); }
                while (_queue.Reader.TryRead(out _)) { lock (_gate) { _lost++; } }
                try { await _stream.DisposeAsync().ConfigureAwait(false); }
                catch (IOException error) { lock (_gate) { _status = $"Capture incomplete: {error.Message}"; } }
                lock (_gate) { _finished = true; }
            }
        }

        private static byte[] Line<T>(T value) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value) + "\n");

        private async Task WriteLineAsync<T>(T value)
        {
            byte[] line = Line(value);
            await _stream.WriteAsync(line).ConfigureAwait(false);
            lock (_gate) { _bytes += line.Length; }
        }

        private sealed record Record(string Kind, long Sequence, DateTimeOffset Utc, long ElapsedTicks,
            string? Connection, string? Direction, string? Format, byte[]? Data, string? Text);
    }
}
