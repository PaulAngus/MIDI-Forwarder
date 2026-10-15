namespace MidiForwarder.Client;

public sealed record ClientSettings
{
    public string InterfaceName { get; init; } = "MIDI Forwarder";

    public string MidiInput { get; init; } = string.Empty;

    public string MidiOutput { get; init; } = string.Empty;

#if MACOS
    public bool UseAppleNetworkMidi { get; init; } = true;
#else
    public bool UseAppleNetworkMidi { get; init; }
#endif

    public string NetworkMidiEndpoint { get; init; } = "Network RTP Session 1";

    public int NetworkMidiSessionPort { get; init; } = 5004;

    public string ServerUri { get; init; } = "tcp://127.0.0.1:5180";

    public string Token { get; init; } = string.Empty;

    public bool StartWithWindows { get; init; }

    public bool IsComplete =>
#if MACOS
        (UseAppleNetworkMidi ? !string.IsNullOrWhiteSpace(NetworkMidiEndpoint) : !string.IsNullOrWhiteSpace(InterfaceName))
#else
        (MightBeUseful.Midi.Windows.WindowsMidiBackend.Capabilities.CanCreateVirtualPorts
            ? !string.IsNullOrWhiteSpace(InterfaceName)
            : !string.IsNullOrWhiteSpace(MidiInput) && !string.IsNullOrWhiteSpace(MidiOutput)
                && !MidiInput.Equals(MidiOutput, StringComparison.OrdinalIgnoreCase))
#endif
        && Uri.TryCreate(ServerUri, UriKind.Absolute, out Uri? uri)
        && uri.Scheme.Equals("tcp", StringComparison.OrdinalIgnoreCase)
        && uri.Port is > 0 and <= 65535;
}
