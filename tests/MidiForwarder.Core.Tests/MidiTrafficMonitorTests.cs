using MidiForwarder.Core;

namespace MidiForwarder.Core.Tests;

public sealed class MidiTrafficMonitorTests
{
    [Fact]
    public void CountsConcurrentTrafficWithoutMixingDirections()
    {
        var monitor = new MidiTrafficMonitor();
        Parallel.For(0, 10_000, _ =>
        {
            monitor.RecordMidiToNetwork(3);
            monitor.RecordNetworkToMidi(8);
        });
        MidiTrafficSnapshot actual = monitor.GetSnapshot();
        Assert.Equal(10_000, actual.MidiToNetwork.Messages);
        Assert.Equal(30_000, actual.MidiToNetwork.Bytes);
        Assert.Equal(10_000, actual.NetworkToMidi.Messages);
        Assert.Equal(80_000, actual.NetworkToMidi.Bytes);
        Assert.True(actual.MidiToNetwork.LastActivityTimestamp > 0);
        Assert.True(actual.NetworkToMidi.LastActivityTimestamp > 0);
    }

    [Fact]
    public void ResetClearsCountsAndActivityForBothDirections()
    {
        var monitor = new MidiTrafficMonitor();
        monitor.RecordMidiToNetwork(3);
        monitor.RecordNetworkToMidi(8);
        monitor.Reset();
        Assert.Equal(default, monitor.GetSnapshot());
        Assert.False(monitor.GetSnapshot().MidiToNetwork.IsActive(TimeSpan.FromSeconds(1)));
    }
}
