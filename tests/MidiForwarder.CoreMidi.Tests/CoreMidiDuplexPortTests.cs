using MidiForwarder.Core;
using MightBeUseful.Midi.CoreMidi;

namespace MidiForwarder.CoreMidi.Tests;

public sealed class CoreMidiDuplexPortTests
{
    [MacFact]
    public async Task SustainedBulkSysExReachesTheApplicationWithoutCoreMidiLoopSuppression()
    {
        string name = $"MIDI Bulk {Guid.NewGuid():N}"[..22];
        await using var source = CoreMidiDuplexPort.OpenVirtual(name);
        await using var application = CoreMidiDuplexPort.Open(name);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var received = application.ReadAllAsync(deadline.Token).GetAsyncEnumerator();

        // The unpaced implementation loses packets once sustained UMP traffic crosses
        // CoreMIDI's loop detector budget. A fast burst alone does not expose the bug.
        for (int index = 0; index < 40; index++)
        {
            byte[] bytes = new byte[3082];
            bytes[0] = 0xf0;
            bytes[1] = 0x7d;
            bytes[2] = (byte)index;
            bytes[^1] = 0xf7;
            await Task.Delay(100, deadline.Token);
            await source.SendAsync(new MidiPacket(MidiPacketFormat.Midi1, bytes), deadline.Token);
            Assert.True(await received.MoveNextAsync());
            Assert.Equal(bytes, received.Current.Data.ToArray());
        }
    }

    [MacFact]
    public async Task LargeSysExIsPacedAcrossNativePacketsAndReassembledUnchanged()
    {
        string name = $"MIDI Long {Guid.NewGuid():N}"[..22];
        await using var source = CoreMidiDuplexPort.OpenVirtual(name);
        await using var application = CoreMidiDuplexPort.Open(name);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var received = application.ReadAllAsync(deadline.Token).GetAsyncEnumerator();
        byte[] bytes = new byte[70_000];
        for (int index = 1; index < bytes.Length - 1; index++) { bytes[index] = (byte)(index % 128); }
        bytes[0] = 0xf0;
        bytes[^1] = 0xf7;
        await source.SendAsync(new MidiPacket(MidiPacketFormat.Midi1, bytes), deadline.Token);
        Assert.True(await received.MoveNextAsync());
        Assert.Equal(bytes, received.Current.Data.ToArray());
    }

    [MacFact]
    public async Task CancellationEndsAPartialSysExAndLeavesThePortUsable()
    {
        string name = $"MIDI Cancel {Guid.NewGuid():N}"[..22];
        await using var source = CoreMidiDuplexPort.OpenVirtual(name);
        await using var application = CoreMidiDuplexPort.Open(name);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var received = application.ReadAllAsync(deadline.Token).GetAsyncEnumerator();
        byte[] bytes = new byte[32_000];
        bytes[0] = 0xf0;
        bytes[^1] = 0xf7;
        using var cancel = new CancellationTokenSource();
        ValueTask pending = source.SendAsync(new MidiPacket(MidiPacketFormat.Midi1, bytes), cancel.Token);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        Assert.True(await received.MoveNextAsync());
        Assert.Equal(4097, received.Current.Data.Length);
        Assert.Equal(0xf7, received.Current.Data.Span[^1]);
        byte[] next = [0xf0, 0x7d, 42, 0xf7];
        await source.SendAsync(new MidiPacket(MidiPacketFormat.Midi1, next), deadline.Token);
        Assert.True(await received.MoveNextAsync());
        Assert.Equal(next, received.Current.Data.ToArray());
    }
}

internal sealed class MacFactAttribute : FactAttribute
{
    public MacFactAttribute()
    {
        if (!OperatingSystem.IsMacOS()) { Skip = "Requires native CoreMIDI virtual ports."; }
    }
}
