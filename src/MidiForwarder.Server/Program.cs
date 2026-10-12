using Avalonia;
using MidiForwarder.Core;
using MidiForwarder.Midi.WindowsServices;

namespace MidiForwarder.Server;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            DiagnosticLog.Write("server.log", $"Process starting. Version={typeof(Program).Assembly.GetName().Version}; PID={Environment.ProcessId}");
            if (args.Contains("--list-ports", StringComparer.OrdinalIgnoreCase))
            {
                PhysicalMidiEndpointCatalog.Print(Console.Out);
                return 0;
            }

            using var instance = ProcessInstance.TryAcquire(@"Local\MidiForwarder.Server");
            if (instance is null)
            {
                DiagnosticLog.Write("server.log", "Server is already running. Use its notification-area icon to open settings.");
                return 0;
            }
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            DiagnosticLog.Write("server.log", "Process exited normally.");
            return 0;
        }
        catch (Exception error)
        {
            DiagnosticLog.WriteException("server.log", "Fatal exception escaped the server entry point.", error);
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseWin32()
        .UseSkia()
        .LogToTrace();
}
