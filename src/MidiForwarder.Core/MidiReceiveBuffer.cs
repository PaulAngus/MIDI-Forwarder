using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace MidiForwarder.Core;

public interface IMidiSessionPort
{
    void BeginSession();
    void EndSession();
    MidiQueueStatistics ReceiveStatistics { get; }
}

public interface IMidiFlowControlPort
{
    void SetPaused(bool paused);
}

public sealed record MidiQueueStatistics(long OfflinePackets, long ClearedMessages, int DroppedFragments,
    long Overflows, int QueuedBytes, int HighWaterBytes, int PendingSystemExclusiveMessages);

/// <summary>Bounds complete messages and prevents queued/partial messages crossing connection boundaries.</summary>
public sealed class MidiReceiveBuffer
{
    public const int MaximumQueuedBytes = 4 * 1024 * 1024;
    private const int MaximumQueuedMessages = 4096;
    private readonly object _sync = new();
    private readonly UmpSysExAssembler _assembler = new();
    private Channel<QueuedPacket> _channel = CreateChannel();
    private long _generation;
    private bool _active = true;
    private bool _closed;
    private bool _paused;
    private Exception? _failure;
    private int _queuedBytes, _highWaterBytes;
    private long _offline, _cleared, _overflows;

    private readonly record struct QueuedPacket(MidiPacket Packet, long Generation);

    private static Channel<QueuedPacket> CreateChannel() => Channel.CreateBounded<QueuedPacket>(
        new BoundedChannelOptions(MaximumQueuedMessages) { FullMode = BoundedChannelFullMode.Wait });

    public void Publish(MidiPacket packet)
    {
        lock (_sync)
        {
            if (!_active || _closed || _paused) { _offline++; return; }
            try
            {
                MidiPacket? complete = _assembler.Accept(packet);
                if (complete is null) { return; }
                var queued = complete.Value with { EnqueuedTimestamp = Stopwatch.GetTimestamp() };
                if (_queuedBytes + queued.Data.Length > MaximumQueuedBytes || !_channel.Writer.TryWrite(new QueuedPacket(queued, _generation)))
                {
                    _overflows++;
                    throw new IOException("MIDI receive queue overflow; connection must restart.");
                }
                _queuedBytes += complete.Value.Data.Length;
                _highWaterBytes = Math.Max(_highWaterBytes, _queuedBytes);
            }
            catch (Exception error) when (error is IOException or InvalidDataException)
            {
                Fail(error);
            }
        }
    }

    public async IAsyncEnumerable<MidiPacket> ReadAllAsync([EnumeratorCancellation] CancellationToken token)
    {
        Channel<QueuedPacket> channel;
        lock (_sync) { channel = _channel; }
        await foreach (QueuedPacket queued in channel.Reader.ReadAllAsync(token).ConfigureAwait(false))
        {
            lock (_sync)
            {
                if (!ReferenceEquals(channel, _channel) || !_active)
                {
                    if (_failure is not null) { throw new IOException("MIDI receive session failed.", _failure); }
                    yield break;
                }
                // Clearing can race with a channel read that has not acquired this lock yet.
                if (queued.Generation != _generation) { _cleared++; continue; }
                _queuedBytes -= queued.Packet.Data.Length;
                if (_paused) { continue; }
            }
            yield return queued.Packet;
        }
    }

    public void BeginSession()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            Clear();
            _channel.Writer.TryComplete();
            _channel = CreateChannel();
            _failure = null;
            _paused = false;
            _active = true;
        }
    }

    public void EndSession()
    {
        lock (_sync) { _active = false; _assembler.Reset(); Clear(); _channel.Writer.TryComplete(); }
    }

    public void SetPaused(bool paused)
    {
        lock (_sync) { Clear(); _paused = paused; }
    }

    public void Close()
    {
        lock (_sync) { _closed = true; EndSession(); }
    }

    public void Fail(Exception error)
    {
        lock (_sync) { _failure = error; _active = false; _assembler.Reset(); _channel.Writer.TryComplete(error); }
    }

    private void Clear()
    {
        _generation++;
        _assembler.Reset();
        while (_channel.Reader.TryRead(out _)) { _cleared++; }
        _queuedBytes = 0;
    }

    public MidiQueueStatistics Statistics
    {
        get { lock (_sync) { return new(_offline, _cleared, _assembler.DroppedFragments, _overflows, _queuedBytes, _highWaterBytes, _assembler.PendingMessages); } }
    }
}
