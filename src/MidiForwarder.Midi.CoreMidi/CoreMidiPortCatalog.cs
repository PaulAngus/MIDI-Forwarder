namespace MidiForwarder.Midi.CoreMidi;

public static class CoreMidiPortCatalog
{
    public static IReadOnlyList<string> GetInputNames() => GetNames(true);

    public static IReadOnlyList<string> GetOutputNames() => GetNames(false);

    public static IReadOnlyList<string> GetNames() => GetInputNames()
        .Intersect(GetOutputNames(), StringComparer.OrdinalIgnoreCase).ToArray();

    public static void Print(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteLine("CoreMIDI inputs:");
        foreach (string name in GetInputNames())
        {
            writer.WriteLine($"  {name}");
        }

        writer.WriteLine("CoreMIDI outputs:");
        foreach (string name in GetOutputNames())
        {
            writer.WriteLine($"  {name}");
        }
    }

    private static IReadOnlyList<string> GetNames(bool input)
    {
        IntPtr handle = RtMidiNative.Create(input);
        try
        {
            return RtMidiNative.GetNames(handle);
        }
        finally
        {
            RtMidiNative.Free(handle, input);
        }
    }
}
