using MidiForwarder.Core;
using MightBeUseful.Midi.Windows;

namespace MidiForwarder.Core.Tests;

public sealed class WinMmSysExAssemblerTests
{
    [Fact]
    public void ReassemblesMessagesLargerThanNativeWinMmBuffer()
    {
        byte[] expected = new byte[100_000];
        expected[0] = 0xf0;
        expected[^1] = 0xf7;
        using var assembler = new WinMmSysExAssembler();
        Assert.Empty(assembler.Push(expected.AsSpan(0, 65_536)));
        Assert.Equal(expected, Assert.Single(assembler.Push(expected.AsSpan(65_536))));
    }

    [Fact]
    public void SeparatesMessagesAndPreservesInterleavedClock()
    {
        using var assembler = new WinMmSysExAssembler();
        IReadOnlyList<byte[]> result = assembler.Push([0xf0, 0x7d, 0xf8, 1, 0xf7, 0xf0, 2, 0xf7]);
        Assert.Equal(new byte[] { 0xf8 }, result[0]);
        Assert.Equal(new byte[] { 0xf0, 0x7d, 1, 0xf7 }, result[1]);
        Assert.Equal(new byte[] { 0xf0, 2, 0xf7 }, result[2]);
    }

    [Fact]
    public void RejectsOversizedMessageAndRecoversForNextMessage()
    {
        byte[] input = new byte[MidiFrame.MaximumLength + 1];
        input[0] = 0xf0;
        using var assembler = new WinMmSysExAssembler();
        Assert.Throws<InvalidDataException>(() => assembler.Push(input));
        Assert.Equal(new byte[] { 0xf0, 0x7d, 0xf7 }, Assert.Single(assembler.Push([0xf0, 0x7d, 0xf7])));
    }
}
