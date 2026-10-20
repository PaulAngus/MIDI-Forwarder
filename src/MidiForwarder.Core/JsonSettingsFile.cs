using System.Text.Json;

namespace MidiForwarder.Core;

public static class JsonSettingsFile
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static string GetPath(string fileName)
    {
        string? configured = Environment.GetEnvironmentVariable("MIDI_FORWARDER_DATA_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(configured) && !Path.IsPathFullyQualified(configured))
        {
            throw new ArgumentException("MIDI_FORWARDER_DATA_DIRECTORY must be an absolute path.");
        }
        string folder = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MIDI Forwarder")
            : Path.GetFullPath(configured);
        return Path.Combine(folder, fileName);
    }

    public static T? Load<T>(string fileName)
    {
        string path = GetPath(fileName);
        if (!File.Exists(path))
        {
            return default;
        }

        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), SerializerOptions);
    }

    public static void Save<T>(string fileName, T settings)
    {
        string path = GetPath(fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, SerializerOptions));
        File.Move(temporaryPath, path, true);
    }
}

