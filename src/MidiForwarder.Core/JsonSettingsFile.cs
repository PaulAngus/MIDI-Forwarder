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
        string folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MIDI Forwarder");
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

