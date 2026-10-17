using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MidiForwarder.Core;

namespace MidiForwarder.Client.Tests;

public sealed class ClientMidiDiscoveryTests
{
    private static MidiDeviceSnapshot Legacy => new(false, false, "Legacy test backend", [],
        ["Available input"], ["Available output"]);

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SlowDiscoveryLeavesTheWindowResponsiveAndClosingIgnoresItsResult(bool closeWhilePending)
    {
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool workerThread = false;
        var discovery = new MidiDeviceDiscovery(() =>
        {
            workerThread = Thread.CurrentThread.IsThreadPoolThread;
            started.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) { throw new TimeoutException("Test did not release discovery."); }
            return Legacy;
        });
        int opened = 0;
        await using var service = new ClientRelayService(_ => { opened++; return new RelayTestPort(); });
        var saved = new ClientSettings { InterfaceName = "Saved interface", MidiInput = "Saved input", MidiOutput = "Saved output" };
        saved = saved with { AutoConnectOnLaunch = closeWhilePending };
        var window = new ClientWindow(service, () => saved, _ => { }, midiDiscovery: discovery);
        Task initializing = window.InitializeAsync();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(workerThread);
            Assert.False(initializing.IsCompleted);
            Assert.True(window.IsVisible);
            Assert.False(window.Control<Button>("ToggleClientButton")!.IsEnabled);
            bool dispatched = false;
            await Dispatcher.UIThread.InvokeAsync(() => dispatched = true);
            Assert.True(dispatched);
            Assert.Equal(0, opened);
            if (closeWhilePending) { window.AllowClose(); window.Close(); }

            release.Set();
            await initializing.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(0, opened);
            Assert.False(service.IsRunning);
            if (closeWhilePending)
            {
                Assert.False(window.IsVisible);
                Assert.False(window.Control<Button>("ToggleClientButton")!.IsEnabled);
            }
            else
            {
                Assert.True(window.Control<Button>("ToggleClientButton")!.IsEnabled);
                Assert.Equal("Saved input", window.Control<ComboBox>("MidiInput")!.SelectedItem);
                Assert.Equal("Saved output", window.Control<ComboBox>("MidiOutput")!.SelectedItem);
            }
        }
        finally
        {
            release.Set();
            window.AllowClose();
            window.Close();
            await initializing.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [AvaloniaFact]
    public async Task RefreshRecoversModernCapabilitiesAndKeepsTheSavedSelection()
    {
        int attempts = 0;
        var discovery = new MidiDeviceDiscovery(() => ++attempts == 1 ? Legacy
            : new(true, true, "Recovered modern backend", ["Another endpoint"], [], []));
        int opened = 0;
        await using var service = new ClientRelayService(_ => { opened++; return new RelayTestPort(); });
        var saved = new ClientSettings { InterfaceName = "Saved interface", MidiInput = "Saved input", MidiOutput = "Saved output" };
        var window = new ClientWindow(service, () => saved, _ => { }, midiDiscovery: discovery);
        try
        {
            await window.InitializeAsync();
            Assert.True(window.Control<StackPanel>("LegacyFields")!.IsVisible);

            window.Control<Button>("RefreshPortsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await window.MidiDiscoveryTask;

            Assert.Equal(2, attempts);
            Assert.True(window.Control<StackPanel>("InterfaceFields")!.IsVisible);
            Assert.False(window.Control<StackPanel>("LegacyFields")!.IsVisible);
            Assert.Equal("Recovered modern backend", window.Control<TextBlock>("BackendDescription")!.Text);
            Assert.Equal("Saved input", window.Control<ComboBox>("MidiInput")!.SelectedItem);
            Assert.Equal("Saved output", window.Control<ComboBox>("MidiOutput")!.SelectedItem);
            Assert.Equal("Saved interface", window.Control<TextBox>("InterfaceName")!.Text);
            Assert.True(window.Control<Button>("RefreshPortsButton")!.IsVisible);
            Assert.Equal(0, opened);
        }
        finally { window.AllowClose(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task FailedDiscoveryCanBeRetriedWithoutRestartingTheWindow()
    {
        int attempts = 0;
        var discovery = new MidiDeviceDiscovery(() =>
            ++attempts == 1 ? throw new IOException("Driver is starting.") : Legacy);
        int opened = 0;
        await using var service = new ClientRelayService(_ => { opened++; return new RelayTestPort(); });
        var window = new ClientWindow(service, () => new ClientSettings { InterfaceName = "Saved interface", MidiInput = "Saved input", MidiOutput = "Saved output" }, _ => { }, midiDiscovery: discovery);
        try
        {
            await window.InitializeAsync();
            Assert.Contains("Driver is starting.", window.Control<TextBlock>("ErrorText")!.Text);
            Assert.False(window.Control<Button>("ToggleClientButton")!.IsEnabled);
            Assert.True(window.Control<Button>("RefreshPortsButton")!.IsEnabled);

            await window.RefreshMidiDevicesAsync();

            Assert.Equal(2, attempts);
            Assert.False(window.Control<Border>("ErrorBanner")!.IsVisible);
            Assert.True(window.Control<Button>("ToggleClientButton")!.IsEnabled);
            Assert.Equal("Legacy test backend", window.Control<TextBlock>("BackendDescription")!.Text);
            Assert.Equal(0, opened);
        }
        finally { window.AllowClose(); window.Close(); }
    }
}
