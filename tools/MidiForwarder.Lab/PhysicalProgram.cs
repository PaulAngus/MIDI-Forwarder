using ApplicationTestLab;
if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This profile requires Windows.");
LabStage[] stages = [
    new("offline-functional-core", "Functional", "bin/tests/MidiForwarder.LabTests.exe", ["MidiForwarder.Core.Tests", "{result}"], ReadTestResult: true),
    new("offline-functional-codecs", "Functional", "bin/tests/MidiForwarder.LabTests.exe", ["MidiForwarder.CoreMidi.Tests", "{result}"], ReadTestResult: true),
    new("native-fm9-read-only", "Native", "bin/hardware/MidiForwarder.Fm9Checks.exe", ["--fm9", "fm9-read-only.json", "{result}"], ReadTestResult: true),
    new("desktop", "Desktop", UnavailableReason: "Interactive desktop automation is excluded by the offline profile; any UI checks are headless.", IncludeInAll: false)
];
return await PortableLab.RunAsync(args, new LabApplication("midi-forwarder", "MIDI Forwarder", stages, ["All combines the audited offline Functional checks with a separate read-only FM9 Native stage.", "Only four exact requests: identity, firmware, current preset and current scene. No navigation, PC/CC/notes, writes, dumps, virtual ports or network MIDI sessions.", "Native covers the production transport boundary; it does not qualify Sender preset sending, Forwarder network routing, interactive desktop behavior or all device operations.", "Current preset/scene fingerprints must agree before and after reopening. This cannot rule out brief external changes between observations."],
    new Dictionary<string, string?> {
        ["PRESET_SENDER_DATA_DIRECTORY"] = "{output}/profile",
        ["PRESET_SENDER_SCREENSHOT_DIR"] = "{output}/screenshots/main",
        ["PRESET_SENDER_CONFIG_SCREENSHOT_DIR"] = "{output}/screenshots/configuration",
        ["PRESET_SENDER_NATIVE_MIDI_TEST"] = null, ["PRESET_SENDER_VIRTUAL_DEVICE_NAME"] = null,
        ["PRESET_MAESTRO_HARDWARE"] = null, ["PRESET_MAESTRO_COMPATIBILITY"] = null,
        ["PRESET_MAESTRO_ALLOW_NAVIGATION"] = null, ["PRESET_MAESTRO_NAVIGATION"] = null,
        ["TMPDIR"] = "{output}/profile", ["TMP"] = "{output}/profile", ["TEMP"] = "{output}/profile"
    }));
