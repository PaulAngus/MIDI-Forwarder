using ApplicationTestLab;

string suffix = OperatingSystem.IsWindows() ? ".exe" : "";
var stages = new List<LabStage>();
if (OperatingSystem.IsWindows()) stages.Add(new("functional-midiforwarder-core-tests", "Functional", "bin/tests/MidiForwarder.LabTests" + suffix, ["MidiForwarder.Core.Tests", "{result}"]));
if (OperatingSystem.IsWindows()) stages.Add(new("functional-midiforwarder-client-tests", "Functional", "bin/tests/MidiForwarder.LabTests" + suffix, ["MidiForwarder.Client.Tests", "{result}"]));
if (OperatingSystem.IsWindows()) stages.Add(new("functional-midiforwarder-server-tests", "Functional", "bin/tests/MidiForwarder.LabTests" + suffix, ["MidiForwarder.Server.Tests", "{result}"]));
stages.Add(new("functional-midiforwarder-coremidi-tests", "Functional", "bin/tests/MidiForwarder.LabTests" + suffix, ["MidiForwarder.CoreMidi.Tests", "{result}"]));
if (OperatingSystem.IsMacOS()) { stages.Add(new("native-coremidi", "Native", "bin/tests/MidiForwarder.LabTests" + suffix, ["MidiForwarder.CoreMidi.Tests", "{result}", "Native"])); }
if (OperatingSystem.IsMacOS()) { stages.Add(new("native-client-server-workflows", "Native", "bin/tests/MidiForwarder.LabTests" + suffix, ["MidiForwarder.MacWorkflow.Tests", "{result}", "Native"])); }
stages.Add(new("native-midi", "Native", UnavailableReason: "Packaged Client/Server routing between separate hosts and real-device preset changes still require coordinated workflow evidence. Same-host development-VM native relay tests do not qualify those workflows."));
stages.Add(new("desktop", "Desktop", UnavailableReason: "Desktop automation is not implemented; headless UI tests are included in Functional.", IncludeInAll: true));
try
{
    return await PortableLab.RunAsync(args, new LabApplication("midi-forwarder", "MIDI Forwarder", stages.ToArray(), ["Functional uses the existing offline .NET tests. Physical hardware is not exercised.", "Two-machine client/server routing and native MIDI loopback need a dedicated coordinated adapter; offline transport tests do not qualify them.", "macOS Functional covers the existing CoreMidi tests; Windows additionally covers Core, Client and Server. Two-machine routing is not tested."], new Dictionary<string, string?> { ["PRESET_MAESTRO_HARDWARE"] = null, ["PRESET_MAESTRO_COMPATIBILITY"] = null, ["PRESET_MAESTRO_ALLOW_NAVIGATION"] = null, ["PRESET_MAESTRO_SCREENSHOT_DIR"] = "{output}/screenshots", ["MIDI_FORWARDER_UI_SNAPSHOTS"] = "{output}/screenshots" }));
}
catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
