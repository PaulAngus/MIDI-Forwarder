using MidiForwarder.Core;

namespace MidiForwarder.Core.Tests;

public sealed class MidiFrameTests
{
    [Fact]
    public void AcceptsShortMessageAndSystemExclusiveMessage()
    {
        MidiFrame.Validate([0xf8]);
        MidiFrame.Validate([0xf0, 0x00, 0x01, 0x74, 0xf7]);
    }

    [Fact]
    public void RejectsEmptyFrame()
    {
        Assert.Throws<InvalidDataException>(() => MidiFrame.Validate([]));
    }

    [Fact]
    public void RejectsOversizedFrame()
    {
        Assert.Throws<InvalidDataException>(() => MidiFrame.Validate(new byte[MidiFrame.MaximumLength + 1]));
    }
}
