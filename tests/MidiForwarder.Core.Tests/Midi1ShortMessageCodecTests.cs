using MidiForwarder.Core;

namespace MidiForwarder.Core.Tests;

public sealed class Midi1ShortMessageCodecTests
{
    [Theory]
    [InlineData(0x00643c90, new byte[] { 0x90, 0x3c, 0x64 })]
    [InlineData(0x000005c0, new byte[] { 0xc0, 0x05 })]
    [InlineData(0x000000f8, new byte[] { 0xf8 })]
    [InlineData(0x000201f2, new byte[] { 0xf2, 0x01, 0x02 })]
    public void DecodeAndEncodeRoundTrip(int raw, byte[] expected)
    {
        byte[] decoded = Midi1ShortMessageCodec.Decode(raw);

        Assert.Equal(expected, decoded);
        Assert.Equal(raw, Midi1ShortMessageCodec.Encode(decoded));
    }

    [Fact]
    public void SysExMustUseTheLongMessagePath()
    {
        Assert.Throws<InvalidDataException>(() => Midi1ShortMessageCodec.Encode([0xf0, 0x01, 0xf7]));
    }

    [Fact]
    public void RejectsWrongShortMessageLength()
    {
        Assert.Throws<InvalidDataException>(() => Midi1ShortMessageCodec.Encode([0x90, 0x3c]));
    }
}

