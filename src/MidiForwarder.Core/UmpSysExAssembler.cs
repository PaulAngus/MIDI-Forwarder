using System.Diagnostics;

namespace MidiForwarder.Core;

/// <summary>Collects one complete SysEx7/8 message per group/stream, without changing its UMPs.</summary>
public sealed class UmpSysExAssembler
{
    private readonly Dictionary<(int Type, int Group, int Stream), Pending> _pending = [];
    private int _bufferedBytes;
    public int DroppedFragments { get; private set; }
    public int PendingMessages => _pending.Count;
    private static readonly TimeSpan MaximumAssemblyAge = TimeSpan.FromSeconds(10);

    public MidiPacket? Accept(MidiPacket packet)
    {
        Expire();
        if (packet.Format != MidiPacketFormat.UniversalMidiPacket || !TryGetSysEx(packet.Data.Span, out var key, out int status))
        {
            return packet;
        }

        if (status is 0 or 1)
        {
            Remove(key, dropped: true);
            if (status == 0) { return packet; }
            _pending.Add(key, new Pending(Stopwatch.GetTimestamp()));
        }
        else if (!_pending.ContainsKey(key))
        {
            DroppedFragments++;
            return null; // A continuation from a previous connection must never reach the device.
        }

        var pending = _pending[key];
        if (pending.Bytes.Count + packet.Data.Length > MidiFrame.MaximumLength ||
            _bufferedBytes + packet.Data.Length > MidiReceiveBuffer.MaximumQueuedBytes)
        {
            throw new InvalidDataException("SysEx assembly exceeded its memory limit.");
        }
        pending.Bytes.AddRange(packet.Data.ToArray());
        pending.LastActivity = Stopwatch.GetTimestamp();
        _bufferedBytes += packet.Data.Length;
        if (status != 3) { return null; }
        byte[] complete = pending.Bytes.ToArray();
        Remove(key, dropped: false);
        return new MidiPacket(MidiPacketFormat.UniversalMidiPacketBatch, complete);
    }

    public void Reset()
    {
        DroppedFragments += _pending.Count;
        _pending.Clear();
        _bufferedBytes = 0;
    }

    private void Expire()
    {
        foreach (var entry in _pending.ToArray())
        {
            if (Stopwatch.GetElapsedTime(entry.Value.LastActivity) > MaximumAssemblyAge) { Remove(entry.Key, dropped: true); }
        }
    }

    private void Remove((int Type, int Group, int Stream) key, bool dropped)
    {
        if (_pending.Remove(key, out var pending))
        {
            _bufferedBytes -= pending.Bytes.Count;
            if (dropped) { DroppedFragments++; }
        }
    }

    public static int WordCount(byte firstByte) => (firstByte >> 4) switch
    {
        0 or 1 or 2 or 6 or 7 => 1,
        3 or 4 or 8 or 9 or 10 => 2,
        11 or 12 => 3,
        _ => 4,
    };

    private static bool TryGetSysEx(ReadOnlySpan<byte> data, out (int Type, int Group, int Stream) key, out int status)
    {
        key = default; status = 0;
        int type = data[0] >> 4;
        if (type is not (3 or 5) || (type == 5 && data[1] >> 4 > 3)) { return false; }
        if (data.Length != (type == 3 ? 8 : 16)) { throw new InvalidDataException("Invalid SysEx UMP size."); }
        status = data[1] >> 4;
        int count = data[1] & 15;
        if (status > 3 || count > (type == 3 ? 6 : 14) || (type == 5 && count == 0))
        { throw new InvalidDataException("Invalid SysEx UMP header."); }
        if (type == 3)
        {
            foreach (byte value in data.Slice(2, count))
            { if (value > 127) { throw new InvalidDataException("Non-MIDI data in SysEx7."); } }
        }
        key = (type, data[0] & 15, type == 5 ? data[2] : 0);
        return true;
    }

    public static void ValidateBatch(ReadOnlySpan<byte> data)
    {
        MidiFrame.Validate(data);
        (int Type, int Group, int Stream)? expected = null;
        int offset = 0;
        int lastStatus = -1;
        while (offset < data.Length)
        {
            int size = WordCount(data[offset]) * 4;
            if (offset + size > data.Length || !TryGetSysEx(data.Slice(offset, size), out var key, out int status))
            { throw new InvalidDataException("A batch must contain complete SysEx UMPs."); }
            if (expected is null)
            {
                if (status != 1) { throw new InvalidDataException("A SysEx batch must begin with a start packet."); }
                expected = key;
            }
            else if (key != expected.Value || status != (offset + size == data.Length ? 3 : 2))
            { throw new InvalidDataException("A SysEx batch has inconsistent group, stream or packet order."); }
            offset += size;
            lastStatus = status;
        }
        if (lastStatus != 3)
        { throw new InvalidDataException("A SysEx batch is incomplete."); }
    }

    private sealed class Pending(long started)
    {
        public long LastActivity { get; set; } = started;
        public List<byte> Bytes { get; } = [];
    }
}
