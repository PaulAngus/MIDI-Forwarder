using MightBeUseful.Midi.Windows;

namespace MidiForwarder.Core.Tests;

public sealed class WindowsUmpReceiveQueueTests
{
    [Theory]
    [InlineData(70_000)]
    [InlineData(MidiFrame.MaximumLength)]
    public async Task NativeSysExBurstFitsBeforeTheManagedConsumerIsScheduled(int length)
    {
        byte[] expected = new byte[length];
        expected[0] = 0xf0;
        expected[^1] = 0xf7;
        var queue = WindowsUmpReceiveQueue.Create();
        foreach (var words in UmpMidi1Codec.Encode(expected))
        {
            Assert.True(queue.Writer.TryWrite(MidiPacket.FromUmpWords(words)));
        }
        queue.Writer.TryComplete();
        using var decoder = new Midi1PacketDecoder();
        byte[]? actual = null;
        await foreach (var packet in queue.Reader.ReadAllAsync())
        {
            actual = decoder.Decode(packet) ?? actual;
        }
        Assert.Equal(expected, actual);
    }
}
