using System.Diagnostics;

namespace MidiForwarder.Core;

public readonly record struct MidiDirectionTraffic(long Messages, long Bytes, long LastActivityTimestamp)
{
    public bool IsActive(TimeSpan holdTime) => Messages > 0
        && Stopwatch.GetElapsedTime(LastActivityTimestamp) <= holdTime;
}

public readonly record struct MidiTrafficSnapshot(MidiDirectionTraffic MidiToNetwork, MidiDirectionTraffic NetworkToMidi);

/// <summary>Records successful forwarding without sending a UI event for every MIDI packet.</summary>
public sealed class MidiTrafficMonitor
{
    private readonly Lock _sync = new();
    private MidiDirectionTraffic _midiToNetwork;
    private MidiDirectionTraffic _networkToMidi;

    public MidiTrafficSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return new(_midiToNetwork, _networkToMidi);
        }
    }

    public void RecordMidiToNetwork(int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        lock (_sync)
        {
            _midiToNetwork = Add(_midiToNetwork, bytes);
        }
    }

    public void RecordNetworkToMidi(int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        lock (_sync)
        {
            _networkToMidi = Add(_networkToMidi, bytes);
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            _midiToNetwork = default;
            _networkToMidi = default;
        }
    }

    private static MidiDirectionTraffic Add(MidiDirectionTraffic current, int bytes) =>
        new(current.Messages + 1, current.Bytes + bytes, Stopwatch.GetTimestamp());
}
