using Avalonia;
using MidiForwarder.Core;
using MightBeUseful.Midi.Windows;

namespace MidiForwarder.Server;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            BundledWindowsMidiApi.Prepare();
            if (args.Contains("--dependency-report", StringComparer.Ordinal))
            {
                return WindowsMidiDependencyReport.Write(Console.Out);
            }
            if (args.Contains("--midi-api-directory", StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine(WindowsMidiBackend.ApiDirectory);
                return 0;
            }
            DiagnosticLog.Write("server.log", $"Process starting. Version={typeof(Program).Assembly.GetName().Version}; PID={Environment.ProcessId}");
            if (args.Contains("--midi-backend", StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine(WindowsMidiBackend.Capabilities.Description);
                return 0;
            }
            if (args.Contains("--list-ports", StringComparer.OrdinalIgnoreCase))
            {
                WindowsMidiBackend.Print(Console.Out);
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
