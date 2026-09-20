namespace MidiForwarder.Core;

public static class MidiFrame
{
    // Large enough for typical bulk SysEx while bounding memory use per network frame.
    public const int MaximumLength = 1024 * 1024;

    public static void Validate(ReadOnlySpan<byte> message)
    {
        if (message.IsEmpty)
        {
            throw new InvalidDataException("A MIDI frame cannot be empty.");
        }

        if (message.Length > MaximumLength)
        {
            throw new InvalidDataException($"MIDI frame exceeds the {MaximumLength}-byte limit.");
        }
    }
}
