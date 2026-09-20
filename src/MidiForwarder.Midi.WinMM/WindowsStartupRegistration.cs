using Microsoft.Win32;

namespace MidiForwarder.Midi.WinMM;

public static class WindowsStartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsEnabled(string applicationName)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
        return key?.GetValue(applicationName) is string;
    }

    public static void SetEnabled(string applicationName, bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true);
        if (!enabled)
        {
            key.DeleteValue(applicationName, false);
            return;
        }

        string executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Unable to determine the executable path.");
        if (Path.GetFileName(executablePath).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Start with Windows is available from a published executable, not dotnet run.");
        }

        key.SetValue(applicationName, $"\"{executablePath}\"");
    }
}

