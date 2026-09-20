using NAudio.Midi;

namespace MidiForwarder.Midi.WinMM;

public static class WinMmPortCatalog
{
    public static IReadOnlyList<string> GetInputNames()
    {
        var result = new string[MidiIn.NumberOfDevices];
        for (int index = 0; index < result.Length; index++)
        {
            result[index] = MidiIn.DeviceInfo(index).ProductName;
        }

        return result;
    }

    public static IReadOnlyList<string> GetOutputNames()
    {
        var result = new string[MidiOut.NumberOfDevices];
        for (int index = 0; index < result.Length; index++)
        {
            result[index] = MidiOut.DeviceInfo(index).ProductName;
        }

        return result;
    }

    public static void Print(TextWriter writer)
    {
        writer.WriteLine("MIDI inputs:");
        foreach ((string name, int index) in GetInputNames().Select((name, index) => (name, index)))
        {
            writer.WriteLine($"  [{index}] {name}");
        }

        writer.WriteLine("MIDI outputs:");
        foreach ((string name, int index) in GetOutputNames().Select((name, index) => (name, index)))
        {
            writer.WriteLine($"  [{index}] {name}");
        }
    }
}

