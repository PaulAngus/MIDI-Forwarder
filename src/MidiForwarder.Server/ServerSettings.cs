namespace MidiForwarder.Server;

public sealed record ServerSettings
{
    public string MidiEndpoint { get; init; } = string.Empty;

    public string MidiInput { get; init; } = string.Empty;

    public string MidiOutput { get; init; } = string.Empty;

    // Older saved settings selected one duplex endpoint.
    [System.Text.Json.Serialization.JsonIgnore]
    public string EffectiveMidiInput => string.IsNullOrWhiteSpace(MidiInput) ? MidiEndpoint : MidiInput;

    [System.Text.Json.Serialization.JsonIgnore]
    public string EffectiveMidiOutput => string.IsNullOrWhiteSpace(MidiOutput) ? MidiEndpoint : MidiOutput;

    public string ListenUrl { get; init; } = "tcp://0.0.0.0:5180";

    public string Token { get; init; } = string.Empty;

    public bool AllowTakeover { get; init; }

    public bool StartWithWindows { get; init; }

    public bool AutoListenOnLaunch { get; init; }

    public bool StartMinimizedToTray { get; init; }

    public bool IsComplete =>
#if MACOS
        !string.IsNullOrWhiteSpace(MidiEndpoint)
#else
        (MightBeUseful.Midi.Windows.WindowsMidiBackend.Capabilities.UseMidiServices
            ? !string.IsNullOrWhiteSpace(MidiEndpoint)
            : !string.IsNullOrWhiteSpace(EffectiveMidiInput) && !string.IsNullOrWhiteSpace(EffectiveMidiOutput))
#endif
        && Uri.TryCreate(ListenUrl, UriKind.Absolute, out Uri? uri)
        && uri.Scheme.Equals("tcp", StringComparison.OrdinalIgnoreCase)
        && uri.Port is > 0 and <= 65535;
}
