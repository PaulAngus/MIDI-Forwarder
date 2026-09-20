namespace MidiForwarder.Client;

public sealed record ClientSettings
{
    public string InterfaceName { get; init; } = "MIDI Forwarder";

    public string ServerUri { get; init; } = "tcp://127.0.0.1:5180";

    public string Token { get; init; } = string.Empty;

    public bool StartWithWindows { get; init; }

    public bool IsComplete => !string.IsNullOrWhiteSpace(InterfaceName)
        && Uri.TryCreate(ServerUri, UriKind.Absolute, out Uri? uri)
        && uri.Scheme.Equals("tcp", StringComparison.OrdinalIgnoreCase)
        && uri.Port is > 0 and <= 65535;
}
