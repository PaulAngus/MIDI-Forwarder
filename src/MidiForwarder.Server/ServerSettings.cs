namespace MidiForwarder.Server;

public sealed record ServerSettings
{
    public string MidiEndpoint { get; init; } = string.Empty;

    public string ListenUrl { get; init; } = "tcp://0.0.0.0:5180";

    public string Token { get; init; } = string.Empty;

    public bool StartWithWindows { get; init; }

    public string[] LocalPortNames { get; init; } = ["MIDI Gateway 1", "MIDI Gateway 2"];

    public bool IsComplete => !string.IsNullOrWhiteSpace(MidiEndpoint)
        && Uri.TryCreate(ListenUrl, UriKind.Absolute, out Uri? uri)
        && uri.Scheme.Equals("tcp", StringComparison.OrdinalIgnoreCase)
        && uri.Port is > 0 and <= 65535;
}
