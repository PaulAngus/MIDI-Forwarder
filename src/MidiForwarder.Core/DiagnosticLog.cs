using System.Diagnostics.CodeAnalysis;

namespace MidiForwarder.Core;

public static class DiagnosticLog
{
    private static readonly Lock Sync = new();

    public static string GetPath(string fileName) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MIDI Forwarder",
        fileName);

    [SuppressMessage("Design", "CA1031", Justification = "Diagnostic logging must never terminate the application it is intended to diagnose.")]
    public static void Write(string fileName, string message)
    {
        try
        {
            string path = GetPath(fileName);
            lock (Sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, $"{DateTimeOffset.Now:O}  {message}{Environment.NewLine}");
            }
        }
        catch
        {
        }
    }

    public static void WriteException(string fileName, string context, Exception error) =>
        Write(fileName, $"{context}{Environment.NewLine}{error}");
}
