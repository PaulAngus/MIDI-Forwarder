using System.Runtime.InteropServices;
using System.Security.Cryptography;
using MightBeUseful.Midi.Windows;

namespace MidiForwarder.Core;

/// <summary>The native API travels inside each EXE; extraction is automatic and requires no installer.</summary>
internal static class BundledWindowsMidiApi
{
    private static readonly Lazy<string> Prepared = new(PrepareCore);

    internal static string Prepare() => Prepared.Value;

    private static string PrepareCore()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26200)) { return string.Empty; }
        byte[] dll = ReadResource("Windows.Devices.Midi2.dll");
        byte[] pri = ReadResource("Windows.Devices.Midi2.pri");
        // Content-addressed folders keep simultaneous app versions from replacing a DLL already in use.
        string hash = Convert.ToHexString(SHA256.HashData(dll));
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MIDI Forwarder", "NativeApi", RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(), hash);
        Directory.CreateDirectory(directory);
        Extract(directory, "Windows.Devices.Midi2.dll", dll);
        Extract(directory, "Windows.Devices.Midi2.pri", pri);
        WindowsMidiBackend.ConfigureAppLocalApiDirectory(directory);
        return directory;
    }

    private static byte[] ReadResource(string name)
    {
        using Stream stream = typeof(BundledWindowsMidiApi).Assembly.GetManifestResourceStream("MidiForwarder.Native." + name)
            ?? throw new InvalidOperationException("The executable is missing its bundled MIDI API: " + name);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static void Extract(string directory, string name, byte[] expected)
    {
        string path = Path.Combine(directory, name);
        if (Matches(path, expected)) { return; }
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, expected);
            try { File.Move(temporary, path, overwrite: true); }
            catch (IOException) when (Matches(path, expected))
            {
                // Another client/server process may have just extracted the identical resource.
            }
            catch (UnauthorizedAccessException) when (Matches(path, expected))
            {
                // Another process has already loaded the identical verified file.
            }
        }
        finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
    }

    private static bool Matches(string path, byte[] expected)
    {
        if (!File.Exists(path)) { return false; }
        using FileStream file = File.OpenRead(path);
        return file.Length == expected.Length && SHA256.HashData(file).AsSpan().SequenceEqual(SHA256.HashData(expected));
    }
}
