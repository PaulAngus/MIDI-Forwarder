using System.Buffers.Binary;

namespace MidiForwarder.Core;

public enum MidiPacketFormat : byte
{
    Midi1 = 1,
    UniversalMidiPacket = 2,
}

public readonly record struct MidiPacket
{
    private const int MaximumUmpWordCount = 4;

    public MidiPacket(MidiPacketFormat format, ReadOnlyMemory<byte> data)
    {
        Format = format;
        Data = data;
        Validate();
    }

    public MidiPacketFormat Format { get; }

    public ReadOnlyMemory<byte> Data { get; }

    public static MidiPacket FromUmpWords(ReadOnlySpan<uint> words)
    {
        if (words.IsEmpty || words.Length > MaximumUmpWordCount)
        {
            throw new InvalidDataException($"A UMP packet must contain between 1 and {MaximumUmpWordCount} words.");
        }

        byte[] data = new byte[words.Length * sizeof(uint)];
        for (int index = 0; index < words.Length; index++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(index * sizeof(uint)), words[index]);
        }

        return new MidiPacket(MidiPacketFormat.UniversalMidiPacket, data);
    }

    public uint[] ToUmpWords()
    {
        Validate();
        if (Format != MidiPacketFormat.UniversalMidiPacket)
        {
            throw new InvalidOperationException("This packet is not a Universal MIDI Packet.");
        }

        var words = new uint[Data.Length / sizeof(uint)];
        for (int index = 0; index < words.Length; index++)
        {
            words[index] = BinaryPrimitives.ReadUInt32BigEndian(Data.Span.Slice(index * sizeof(uint), sizeof(uint)));
        }

        return words;
    }

    public void Validate()
    {
        switch (Format)
        {
            case MidiPacketFormat.Midi1:
                MidiFrame.Validate(Data.Span);
                break;
            case MidiPacketFormat.UniversalMidiPacket when Data.Length is >= sizeof(uint) and <= MaximumUmpWordCount * sizeof(uint) && Data.Length % sizeof(uint) == 0:
                break;
            case MidiPacketFormat.UniversalMidiPacket:
                throw new InvalidDataException("A UMP packet must contain between 1 and 4 complete words.");
            default:
                throw new InvalidDataException($"Unsupported MIDI packet format: {(byte)Format}.");
        }
    }
}