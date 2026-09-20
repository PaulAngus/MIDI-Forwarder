using MidiForwarder.Midi.WindowsServices;

namespace MidiForwarder.Core.Tests;

public sealed class UmpMidi1CodecTests
{
    [Theory]
    [InlineData(0x90, 60, 127)]
    [InlineData(0x80, 60, 0)]
    public void RoundTripsThreeByteChannelMessages(byte status, byte data1, byte data2)
    {
        byte[] expected = [status, data1, data2];
        IReadOnlyList<uint[]> packets = UmpMidi1Codec.Encode(expected);
        using var assembler = new SysEx7Assembler();

        byte[]? actual = UmpMidi1Codec.Decode(packets[0], assembler);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void RoundTripsProgramChange()
    {
        byte[] expected = [0xc2, 42];
        IReadOnlyList<uint[]> packets = UmpMidi1Codec.Encode(expected);
        using var assembler = new SysEx7Assembler();

        byte[]? actual = UmpMidi1Codec.Decode(packets[0], assembler);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void RoundTripsMultiPacketSystemExclusiveMessage()
    {
        byte[] expected = [0xf0, 0x00, 0x01, 0x74, 0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0xf7];
        IReadOnlyList<uint[]> packets = UmpMidi1Codec.Encode(expected);
        using var assembler = new SysEx7Assembler();
        byte[]? actual = null;

        foreach (uint[] packet in packets)
        {
            actual = UmpMidi1Codec.Decode(packet, assembler) ?? actual;
        }

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void RoundTripsRealtimeMessage()
    {
        byte[] expected = [0xf8];
        IReadOnlyList<uint[]> packets = UmpMidi1Codec.Encode(expected);
        using var assembler = new SysEx7Assembler();

        byte[]? actual = UmpMidi1Codec.Decode(packets[0], assembler);

        Assert.Equal(expected, actual);
    }
}
