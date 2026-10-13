using MidiForwarder.Core;
using MidiForwarder.Midi.CoreMidi;
using MidiForwarder.Midi.WindowsServices;

namespace MidiForwarder.CoreMidi.Tests;

public sealed class Midi1PacketDecoderTests
{
    [Theory]
    [InlineData(new byte[] { 0x90, 60, 100 })]
    [InlineData(new byte[] { 0xc0, 5 })]
    [InlineData(new byte[] { 0xf1, 42 })]
    [InlineData(new byte[] { 0xf2, 1, 2 })]
    [InlineData(new byte[] { 0xf8 })]
    [InlineData(new byte[] { 0xfe })]
    [InlineData(new byte[] { 0xf0, 0x7d, 1, 2, 3, 4, 5, 6, 7, 0xf7 })]
    public async Task AcceptsWindowsUmpPacketsThroughExistingTcpProtocol(byte[] expected)
    {
        using var decoder = new Midi1PacketDecoder();
        byte[]? actual = null;
        foreach (uint[] words in UmpMidi1Codec.Encode(expected, group: 3))
        {
            using var stream = new MemoryStream();
            await TcpMidiBridge.WriteFrameAsync(stream, MidiPacket.FromUmpWords(words), CancellationToken.None);
            stream.Position = 0;
            MidiPacket? received = await TcpMidiBridge.ReadFrameAsync(stream, CancellationToken.None);
            actual = decoder.Decode(received!.Value);
        }

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void PreservesRawMidi1Packets()
    {
        byte[] expected = [0xf0, 0, 1, 0x74, 0x10, 0xf7];
        using var decoder = new Midi1PacketDecoder();
        Assert.Equal(expected, decoder.Decode(new MidiPacket(MidiPacketFormat.Midi1, expected)));
    }

    [Fact]
    public void KeepsInterleavedSysExGroupsSeparate()
    {
        byte[] first = [0xf0, 0x7d, 1, 2, 3, 4, 5, 6, 7, 0xf7];
        byte[] second = [0xf0, 0x7d, 11, 12, 13, 14, 15, 16, 17, 0xf7];
        IReadOnlyList<uint[]> firstPackets = UmpMidi1Codec.Encode(first, 1);
        IReadOnlyList<uint[]> secondPackets = UmpMidi1Codec.Encode(second, 2);
        using var decoder = new Midi1PacketDecoder();
        Assert.Null(decoder.Decode(MidiPacket.FromUmpWords(firstPackets[0])));
        Assert.Null(decoder.Decode(MidiPacket.FromUmpWords(secondPackets[0])));
        Assert.Equal(first, decoder.Decode(MidiPacket.FromUmpWords(firstPackets[1])));
        Assert.Equal(second, decoder.Decode(MidiPacket.FromUmpWords(secondPackets[1])));
    }

    [Fact]
    public void IgnoresUmpUtilityPackets()
    {
        using var decoder = new Midi1PacketDecoder();
        Assert.Null(decoder.Decode(MidiPacket.FromUmpWords([0x00200000])));
    }

    [Fact]
    public void ReportsUnsupportedMidi2RatherThanDroppingIt()
    {
        using var decoder = new Midi1PacketDecoder();
        Assert.Throws<IOException>(() => decoder.Decode(MidiPacket.FromUmpWords([0x40903c00, 0x7fff0000])));
    }
}
