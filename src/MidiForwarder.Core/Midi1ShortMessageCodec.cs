namespace MidiForwarder.Core;

public static class Midi1ShortMessageCodec
{
    public static byte[] Decode(int rawMessage)
    {
        byte status = (byte)(rawMessage & 0xff);
        int length = GetLength(status);
        if (length == 0)
        {
            throw new InvalidDataException($"Status 0x{status:X2} is not a MIDI 1.0 short message.");
        }

        var result = new byte[length];
        for (int index = 0; index < length; index++)
        {
            result[index] = (byte)((rawMessage >> (8 * index)) & 0xff);
        }

        return result;
    }

    public static int Encode(ReadOnlySpan<byte> message)
    {
        MidiFrame.Validate(message);
        int expectedLength = GetLength(message[0]);
        if (expectedLength == 0 || message.Length != expectedLength)
        {
            throw new InvalidDataException("The data is not one complete MIDI 1.0 short message.");
        }

        int raw = 0;
        for (int index = 0; index < message.Length; index++)
        {
            raw |= message[index] << (8 * index);
        }

        return raw;
    }

    public static int GetLength(byte status) => status switch
    {
        >= 0x80 and <= 0xbf => 3,
        >= 0xc0 and <= 0xdf => 2,
        >= 0xe0 and <= 0xef => 3,
        0xf1 => 2,
        0xf2 => 3,
        0xf3 => 2,
        0xf6 or 0xf7 => 1,
        >= 0xf8 => 1,
        _ => 0,
    };
}

