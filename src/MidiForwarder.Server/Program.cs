using Avalonia;
using MidiForwarder.Core;
using MidiForwarder.Midi.WinMM;

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
                WinMmPortCatalog.Print(Console.Out);
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
        .UsePlatformDetect()
        .LogToTrace();
}
