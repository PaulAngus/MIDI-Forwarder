using Windows.Devices.Midi2;
using Windows.Devices.Midi2.Enumeration;

namespace MidiForwarder.Midi.WindowsServices;

public static class PhysicalMidiEndpointCatalog
{
    public static IReadOnlyList<string> GetNames()
    {
        if (!MidiApi.EnsureServiceAvailable())
        {
            return [];
        }

        return MidiEndpointDeviceInformation
            .FindAll(MidiEndpointDeviceInformationSortOrder.Name, MidiEndpointDeviceInformationFilters.AllStandardEndpoints)
            .Select(endpoint => endpoint.Name)
            .ToList();
    }

    public static void Print(TextWriter writer)
    {
        writer.WriteLine("MIDI endpoints (Windows MIDI Services):");
        foreach (string name in GetNames())
        {
            writer.WriteLine($"  {name}");
        }
    }
}
