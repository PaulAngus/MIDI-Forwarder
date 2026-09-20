namespace MidiForwarder.Midi.WindowsServices;

public static class UmpMidi1Codec
{
    public static IReadOnlyList<uint[]> Encode(ReadOnlySpan<byte> message, byte group = 0)
    {
        MidiForwarder.Core.MidiFrame.Validate(message);
        if (group > 15)
        {
            throw new ArgumentOutOfRangeException(nameof(group));
        }

        if (message[0] == 0xf0)
        {
            if (message[^1] != 0xf7)
            {
                throw new InvalidDataException("A SysEx message must end with 0xF7.");
            }

            return EncodeSysEx7(message[1..^1], group);
        }

        int expectedLength = GetMessageLength(message[0]);
        if (message.Length != expectedLength)
        {
            throw new InvalidDataException($"MIDI status 0x{message[0]:X2} requires {expectedLength} byte(s).");
        }

        byte messageType = message[0] < 0xf0 ? (byte)0x2 : (byte)0x1;
        uint word = (uint)(messageType << 4 | group) << 24;
        word |= (uint)message[0] << 16;
        if (message.Length > 1)
        {
            word |= (uint)message[1] << 8;
        }

        if (message.Length > 2)
        {
            word |= message[2];
        }

        return [new[] { word }];
    }

    public static byte[]? Decode(ReadOnlySpan<uint> words, SysEx7Assembler sysEx)
    {
        ArgumentNullException.ThrowIfNull(sysEx);
        if (words.IsEmpty)
        {
            throw new InvalidDataException("A UMP packet must contain at least one word.");
        }

        byte messageType = (byte)(words[0] >> 28);
        if (messageType is 0x1 or 0x2)
        {
            byte status = (byte)(words[0] >> 16);
            int length = GetMessageLength(status);
            byte[] message = new byte[length];
            message[0] = status;
            if (length > 1)
            {
                message[1] = (byte)(words[0] >> 8);
            }

            if (length > 2)
            {
                message[2] = (byte)words[0];
            }

            return message;
        }

        if (messageType != 0x3)
        {
            return null;
        }

        if (words.Length < 2)
        {
            throw new InvalidDataException("A SysEx7 UMP packet requires two words.");
        }

        byte statusAndCount = (byte)(words[0] >> 16);
        int packetStatus = statusAndCount >> 4;
        int count = statusAndCount & 0x0f;
        if (packetStatus > 3 || count > 6)
        {
            throw new InvalidDataException("Invalid SysEx7 UMP packet header.");
        }

        Span<byte> payload = stackalloc byte[6]
        {
            (byte)(words[0] >> 8),
            (byte)words[0],
            (byte)(words[1] >> 24),
            (byte)(words[1] >> 16),
            (byte)(words[1] >> 8),
            (byte)words[1],
        };
        return sysEx.Push(packetStatus, payload[..count]);
    }

    private static List<uint[]> EncodeSysEx7(ReadOnlySpan<byte> payload, byte group)
    {
        var packets = new List<uint[]>();
        if (payload.IsEmpty)
        {
            packets.Add(BuildSysExPacket(group, 0, payload));
            return packets;
        }

        for (int offset = 0; offset < payload.Length; offset += 6)
        {
            int count = Math.Min(6, payload.Length - offset);
            bool first = offset == 0;
            bool last = offset + count == payload.Length;
            int status = first && last ? 0 : first ? 1 : last ? 3 : 2;
            packets.Add(BuildSysExPacket(group, status, payload.Slice(offset, count)));
        }

        return packets;
    }

    private static uint[] BuildSysExPacket(byte group, int status, ReadOnlySpan<byte> payload)
    {
        Span<byte> bytes = stackalloc byte[8];
        bytes[0] = (byte)(0x30 | group);
        bytes[1] = (byte)(status << 4 | payload.Length);
        payload.CopyTo(bytes[2..]);
        return
        [
            ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3],
            ((uint)bytes[4] << 24) | ((uint)bytes[5] << 16) | ((uint)bytes[6] << 8) | bytes[7],
        ];
    }

    private static int GetMessageLength(byte status)
    {
        if (status < 0x80)
        {
            throw new InvalidDataException($"Invalid MIDI status byte: 0x{status:X2}.");
        }

        if (status < 0xf0)
        {
            return (status & 0xf0) is 0xc0 or 0xd0 ? 2 : 3;
        }

        return status switch
        {
            0xf1 or 0xf3 => 2,
            0xf2 => 3,
            0xf6 or >= 0xf8 => 1,
            _ => throw new InvalidDataException($"Unsupported standalone MIDI status byte: 0x{status:X2}."),
        };
    }
}

public sealed class SysEx7Assembler : IDisposable
{
    private readonly MemoryStream _buffer = new();

    public void Dispose() => _buffer.Dispose();

    public byte[]? Push(int packetStatus, ReadOnlySpan<byte> payload)
    {
        switch (packetStatus)
        {
            case 0:
                _buffer.SetLength(0);
                return Complete(payload);
            case 1:
                _buffer.SetLength(0);
                _buffer.WriteByte(0xf0);
                _buffer.Write(payload);
                return null;
            case 2 when _buffer.Length > 0:
                _buffer.Write(payload);
                return null;
            case 3 when _buffer.Length > 0:
                _buffer.Write(payload);
                _buffer.WriteByte(0xf7);
                byte[] result = _buffer.ToArray();
                _buffer.SetLength(0);
                return result;
            default:
                _buffer.SetLength(0);
                throw new InvalidDataException("Received an out-of-sequence SysEx7 UMP packet.");
        }
    }

    private byte[] Complete(ReadOnlySpan<byte> payload)
    {
        _buffer.WriteByte(0xf0);
        _buffer.Write(payload);
        _buffer.WriteByte(0xf7);
        byte[] result = _buffer.ToArray();
        _buffer.SetLength(0);
        return result;
    }
}
