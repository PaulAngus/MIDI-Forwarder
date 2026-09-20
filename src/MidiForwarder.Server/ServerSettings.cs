namespace MidiForwarder.Server;

public sealed record ServerSettings
{
    public string InputPort { get; init; } = string.Empty;

    public string OutputPort { get; init; } = string.Empty;

    public string ListenUrl { get; init; } = "tcp://0.0.0.0:5180";

    public string Token { get; init; } = string.Empty;

    public bool StartWithWindows { get; init; }

    public bool IsComplete => !string.IsNullOrWhiteSpace(InputPort)
        && !string.IsNullOrWhiteSpace(OutputPort)
        && Uri.TryCreate(ListenUrl, UriKind.Absolute, out Uri? uri)
        && uri.Scheme.Equals("tcp", StringComparison.OrdinalIgnoreCase)
        && uri.Port is > 0 and <= 65535;
}
