using Avalonia;
using MidiForwarder.Midi.WinMM;
using MidiForwarder.Midi.WindowsServices;

namespace MidiForwarder.Client;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--list-ports", StringComparer.OrdinalIgnoreCase))
        {
            WinMmPortCatalog.Print(Console.Out);
            return 0;
        }

        if (args.Contains("--check-virtual-midi", StringComparer.OrdinalIgnoreCase))
        {
            return CheckVirtualMidiAsync().GetAwaiter().GetResult();
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .LogToTrace();

    private static async Task<int> CheckVirtualMidiAsync()
    {
        try
        {
            await using var virtualPort = WindowsMidiServicesDuplexPort.Open("MIDI Fwd Check");
            await Task.Delay(500).ConfigureAwait(false);
            bool visible = WinMmPortCatalog.GetInputNames().Contains(virtualPort.ApplicationPortName)
                && WinMmPortCatalog.GetOutputNames().Contains(virtualPort.ApplicationPortName);
            return visible ? 0 : 2;
        }
        catch
        {
            return 1;
        }
    }
}
