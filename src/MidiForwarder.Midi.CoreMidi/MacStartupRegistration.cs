using System.Xml.Linq;

namespace MidiForwarder.Midi.CoreMidi;

public static class MacStartupRegistration
{
    public static void SetEnabled(string applicationName, bool enabled)
    {
        string label = applicationName.EndsWith("Client", StringComparison.Ordinal)
            ? "org.midiforwarder.client" : "org.midiforwarder.server";
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents");
        string path = Path.Combine(folder, label + ".plist");
        if (!enabled)
        {
            File.Delete(path);
            return;
        }

        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The application executable could not be located.");
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Start at login is available from a published application.");
        }

        var document = new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XDocumentType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
            new XElement("plist", new XAttribute("version", "1.0"),
                new XElement("dict",
                    new XElement("key", "Label"), new XElement("string", label),
                    new XElement("key", "ProgramArguments"), new XElement("array", new XElement("string", executable)),
                    new XElement("key", "RunAtLoad"), new XElement("true"),
                    new XElement("key", "LimitLoadToSessionType"), new XElement("string", "Aqua"))));
        Directory.CreateDirectory(folder);
        document.Save(path);
    }
}
