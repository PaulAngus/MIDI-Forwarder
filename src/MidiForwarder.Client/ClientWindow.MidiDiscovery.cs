using Avalonia.Controls;
using MidiForwarder.Core;
using System.Diagnostics.CodeAnalysis;
#if !MACOS
using MightBeUseful.Midi.Windows;
#endif

namespace MidiForwarder.Client;

public sealed partial class ClientWindow
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
        return new(false, false, "CoreMIDI", [], [], []);
#else
        WindowsMidiCapabilities capabilities = WindowsMidiBackend.RefreshCapabilities();
        return new(capabilities.UseMidiServices, capabilities.CanCreateVirtualPorts, capabilities.Description, [],
            capabilities.CanCreateVirtualPorts ? [] : LegacyMidiPortCatalog.GetInputNames(),
            capabilities.CanCreateVirtualPorts ? [] : LegacyMidiPortCatalog.GetOutputNames());
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
#if MACOS
            UpdateMidiBackend();
#else
            bool modern = devices.CanCreateVirtualPorts;
            View<StackPanel>("InterfaceFields").IsVisible = modern;
            View<StackPanel>("LegacyFields").IsVisible = !modern;
            SetPorts(_midiInput, devices.Inputs, _midiInput.SelectedItem as string ?? string.Empty);
            SetPorts(_midiOutput, devices.Outputs, _midiOutput.SelectedItem as string ?? string.Empty);
            _midiHelp.Text = modern
                ? "Select the virtual interface below for input and output in your MIDI app."
                : "Create two virtual cables in loopMIDI (or another virtual MIDI driver). Choose the cable your MIDI app sends to as ‘From MIDI app’, and the cable your MIDI app receives from as ‘To MIDI app’. Keep the virtual cable software running. Use different cables to avoid feedback.";
            View<TextBlock>("BackendDescription").Text = devices.Description;
#endif
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
