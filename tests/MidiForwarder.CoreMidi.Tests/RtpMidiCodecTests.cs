using System.Buffers.Binary;
using MidiForwarder.Midi.CoreMidi;

namespace MidiForwarder.CoreMidi.Tests;

public sealed class RtpMidiCodecTests
{
    private const uint Sender = 0x12345678;

    [Theory]
    [InlineData(2)]
    [InlineData(1002)]
    [InlineData(3082)]
    [InlineData(70000)]
    public void SegmentedSysExPreservesAllBytes(int length)
    {
        byte[] expected = new byte[length]; expected[0] = 0xf0; expected[^1] = 0xf7;
        for (int index = 1; index < expected.Length - 1; index++) { expected[index] = (byte)(index % 128); }
        using var codec = new RtpMidiCodec();
        var received = new List<byte[]>();
        ushort sequence = 65530;
        foreach (byte[] command in RtpMidiCodec.Encode(expected))
        {
            Assert.InRange(command.Length, 2, 1002);
            received.AddRange(codec.Decode(Packet(command, sequence++), Sender));
        }
        Assert.Equal(expected, Assert.Single(received));
    }

    [Fact]
    public void RealtimeInsideSysExRemainsSeparateAndDoesNotBreakAssembly()
    {
        using var codec = new RtpMidiCodec();
        var received = new List<byte[]>();
        ushort sequence = 0;
        foreach (byte[] command in RtpMidiCodec.Encode([0xf0, 0x7d, 1, 0xf8, 2, 0xf7]))
        {
            received.AddRange(codec.Decode(Packet(command, sequence++), Sender));
        }
        Assert.Equal(2, received.Count);
        Assert.Equal(new byte[] { 0xf8 }, received[0]);
        Assert.Equal(new byte[] { 0xf0, 0x7d, 1, 2, 0xf7 }, received[1]);
    }

    [Fact]
    public void DeltaTimesRunningStatusAndRecoveryJournalAreDecodedWithoutExtraEvents()
    {
        using var codec = new RtpMidiCodec();
        byte[] packet = Packet([0, 0x90, 60, 100, 0x81, 0, 61, 90, 0, 0xf8, 0, 62, 80, 0]);
        packet[12] |= 0x60; // Initial delta and recovery-journal flags.
        packet = [.. packet, 0x80, 0, 0, 0xff]; // Journal bytes are not MIDI commands.
        var messages = codec.Decode(packet, Sender);
        Assert.Equal(4, messages.Count);
        Assert.Equal(new byte[] { 0x90, 60, 100 }, messages[0]);
        Assert.Equal(new byte[] { 0x90, 61, 90 }, messages[1]);
        Assert.Equal(new byte[] { 0xf8 }, messages[2]);
        Assert.Equal(new byte[] { 0x90, 62, 80 }, messages[3]);
    }

    [Fact]
    public void SequenceGapRejectsACompleteMessageRatherThanForwardingCorruptData()
    {
        using var codec = new RtpMidiCodec();
        Assert.Empty(codec.Decode(Packet([0xf0, 1, 0xf0], 3), Sender));
        Assert.Throws<IOException>(() => codec.Decode(Packet([0xf7, 2, 0xf7], 5), Sender));
    }

    [Fact]
    public void DuplicatesAndOldPacketsDoNotReplayCommandsOrAlterTheNextSequence()
    {
        using var codec = new RtpMidiCodec();
        Assert.Single(codec.Decode(Packet([0xc0, 1], ushort.MaxValue), Sender));
        Assert.Empty(codec.Decode(Packet([0xc0, 1], ushort.MaxValue), Sender));
        Assert.Empty(codec.Decode(Packet([0xc0, 1], ushort.MaxValue - 1), Sender));
        Assert.Single(codec.Decode(Packet([0xc0, 2], 0), Sender));
    }

    [Fact]
    public void CancellationDiscardsPartialSysExAndTheNextMessageIsIndependent()
    {
        using var codec = new RtpMidiCodec();
        Assert.Empty(codec.Decode(Packet([0xf0, 1, 0xf0], 0), Sender));
        Assert.Empty(codec.Decode(Packet([0xf7, 0xf4], 1), Sender));
        Assert.Equal(new byte[] { 0xf0, 2, 0xf7 }, Assert.Single(codec.Decode(Packet([0xf0, 2, 0xf7], 2), Sender)));
    }

    [Theory]
    [InlineData(new byte[] { 0x90, 60 })]
    [InlineData(new byte[] { 0x90, 60, 0x80 })]
    [InlineData(new byte[] { 0xf0, 1 })]
    [InlineData(new byte[] { 0xf7, 1, 0xf7 })]
    [InlineData(new byte[] { 60, 100 })]
    public void MalformedCommandsAreRejected(byte[] command)
    {
        using var codec = new RtpMidiCodec();
        Assert.Throws<InvalidDataException>(() => codec.Decode(Packet(command), Sender));
    }

    [Fact]
    public void TruncatedLongLengthAndWrongSenderAreRejected()
    {
        using var codec = new RtpMidiCodec();
        byte[] packet = Packet([0xc0, 1]); packet[12] = 0x8f;
        Assert.Throws<InvalidDataException>(() => codec.Decode(packet, Sender));
        using var second = new RtpMidiCodec();
        Assert.Throws<InvalidDataException>(() => second.Decode(Packet([0xc0, 1]), Sender + 1));
    }

    [Fact]
    public void UnterminatedVariableDeltaDoesNotReadBeyondTheCommandList()
    {
        using var codec = new RtpMidiCodec();
        byte[] packet = Packet([0x81, 0x81, 0x81, 0x81]); packet[12] |= 0x20;
        Assert.Throws<InvalidDataException>(() => codec.Decode(packet, Sender));
    }

    private static byte[] Packet(byte[] command, ushort sequence = 0)
    {
        byte[] packet = RtpMidiCodec.Packet(command, sequence, 123, Sender);
        Assert.Equal(Sender, BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(8)));
        return packet;
    }
}
