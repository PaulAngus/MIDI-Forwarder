namespace MidiForwarder.Core;

public sealed record MidiDeviceSnapshot(bool UseMidiServices, bool CanCreateVirtualPorts,
    string Description, IReadOnlyList<string> Endpoints, IReadOnlyList<string> Inputs, IReadOnlyList<string> Outputs);

/// <summary>Shares one background discovery operation between startup and refresh callers.</summary>
public sealed class MidiDeviceDiscovery(Func<MidiDeviceSnapshot> discover)
{
    private readonly object _gate = new();
    private Task<MidiDeviceSnapshot>? _pending;

    public Task<MidiDeviceSnapshot> RefreshAsync()
    {
        lock (_gate)
        {
            if (_pending is null || _pending.IsCompleted) { _pending = Task.Run(discover); }
            return _pending;
        }
    }
}
