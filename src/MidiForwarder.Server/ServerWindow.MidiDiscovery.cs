using Avalonia.Controls;
using MidiForwarder.Core;
using System.Diagnostics.CodeAnalysis;
#if MACOS
using MightBeUseful.Midi.CoreMidi;
#else
using MightBeUseful.Midi.Windows;
#endif

namespace MidiForwarder.Server;

public sealed partial class ServerWindow
{
    private readonly MidiDeviceDiscovery _midiDiscovery;
    private readonly Task _initialMidiDiscovery;
    private Task? _midiRefreshTask;
    private MidiDeviceSnapshot _midiDevices = new(false, false, "Discovering MIDI devices…", [], [], []);
    private bool _midiReady;
    private bool _refreshingMidi;
    private bool _closed;
    private string? _midiDiscoveryError;

    internal Task MidiDiscoveryTask => _midiRefreshTask ?? Task.CompletedTask;

    private static MidiDeviceSnapshot DiscoverMidiDevices()
    {
#if MACOS
        return new(false, false, "CoreMIDI", CoreMidiPortCatalog.GetNames(), [], []);
#else
        WindowsMidiCapabilities capabilities = WindowsMidiBackend.RefreshCapabilities();
        return new(capabilities.UseMidiServices, capabilities.CanCreateVirtualPorts, capabilities.Description,
            capabilities.UseMidiServices ? WindowsMidiBackend.GetEndpointNames() : [],
            capabilities.UseMidiServices ? [] : LegacyMidiPortCatalog.GetInputNames(),
            capabilities.UseMidiServices ? [] : LegacyMidiPortCatalog.GetOutputNames());
#endif
    }

    internal Task RefreshMidiDevicesAsync()
    {
        if (_closed || _service.IsRunning) { return Task.CompletedTask; }
        return _midiRefreshTask is { IsCompleted: false } ? _midiRefreshTask
            : _midiRefreshTask = RefreshMidiDevicesCoreAsync();
    }

    [SuppressMessage("Design", "CA1031", Justification = "Driver discovery failures must leave the desktop responsive and allow another explicit refresh.")]
    private async Task RefreshMidiDevicesCoreAsync()
    {
        _refreshingMidi = true;
        RefreshPresentation();
        try
        {
            MidiDeviceSnapshot devices = await _midiDiscovery.RefreshAsync().ConfigureAwait(true);
            if (_closed) { return; }
            _midiDevices = devices;
            _midiDiscoveryError = null;
#if !MACOS
            View<StackPanel>("EndpointFields").IsVisible = devices.UseMidiServices;
            View<StackPanel>("LegacyFields").IsVisible = !devices.UseMidiServices;
            SetItems(_midiInput, devices.Inputs, GetText(_midiInput));
            SetItems(_midiOutput, devices.Outputs, GetText(_midiOutput));
#endif
            SetItems(_midiEndpoint, devices.Endpoints, GetText(_midiEndpoint));
            View<TextBlock>("BackendDescription").Text = devices.Description;
            _midiReady = true;
        }
        catch (Exception error)
        {
            if (_closed) { return; }
            _midiReady = false;
            _midiDiscoveryError = $"MIDI discovery failed: {error.Message}";
            View<TextBlock>("BackendDescription").Text = _midiDiscoveryError;
            AppendLog(_midiDiscoveryError);
        }
        finally
        {
            _refreshingMidi = false;
            if (!_closed) { RefreshPresentation(); }
        }
    }
}
