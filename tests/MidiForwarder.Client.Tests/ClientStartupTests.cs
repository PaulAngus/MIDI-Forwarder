using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;

namespace MidiForwarder.Client.Tests;

public sealed class ClientStartupTests
{
    [Theory]
    [InlineData("{}", false)]
    [InlineData("{\"StartWithWindows\":true}", false)]
    [InlineData("{\"AutoConnectOnLaunch\":false}", false)]
    [InlineData("{\"AutoConnectOnLaunch\":true}", true)]
    public void AutomaticConnectionRequiresExplicitOptIn(string json, bool expected)
    {
        Assert.False(new ClientSettings().AutoConnectOnLaunch);
        Assert.False(new ClientSettings().StartMinimizedToTray);
        Assert.False(JsonSerializer.Deserialize<ClientSettings>(json)!.StartMinimizedToTray);
        Assert.Equal(expected, JsonSerializer.Deserialize<ClientSettings>(json)!.AutoConnectOnLaunch);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FirstUseAndLegacySettingsOpenWithoutConnecting(bool hasSavedSettings)
    {
        int openedPorts = 0;
        int saves = 0;
        await using var service = new ClientRelayService(_ => { openedPorts++; return new RelayTestPort(); });
        // Configurations from earlier versions have no automatic-connection preference.
        ClientSettings? saved = hasSavedSettings ? JsonSerializer.Deserialize<ClientSettings>(
            """{"InterfaceName":"Saved interface","ServerUri":"ws://192.0.2.10:5180","Token":"saved-token","StartWithWindows":true}""") : null;
        var window = new ClientWindow(service, () => saved, _ => saves++);
        try
        {
            await window.InitializeAsync();
            Assert.True(window.IsVisible);
            Assert.False(service.IsRunning);
            Assert.Equal(0, openedPorts);
            Assert.Equal(0, saves);
            Assert.False(window.Control<ToggleSwitch>("AutoConnectOnLaunch")!.IsChecked);
            Assert.False(window.Control<ToggleSwitch>("StartMinimizedToTray")!.IsChecked);
            Assert.Equal(!hasSavedSettings, window.ConfigurationWindow.IsVisible);
            Assert.Equal("Connect", window.Control<Button>("ToggleClientButton")!.Content);
            if (hasSavedSettings)
            {
                Assert.Equal("Saved interface", window.Control<TextBox>("InterfaceName")!.Text);
                Assert.Equal("tcp://192.0.2.10:5180", window.Control<TextBox>("ServerUri")!.Text);
                Assert.Equal("saved-token", window.Control<TextBox>("Token")!.Text);
                Assert.True(window.Control<ToggleSwitch>("StartWithSystem")!.IsChecked);
            }
        }
        finally
        {
            window.AllowClose();
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task PreferenceSavesImmediatelyWithoutConnectingOrSavingPendingConnectionEdits()
    {
        var original = new ClientSettings
        {
            InterfaceName = "Saved interface", MidiInput = "Saved input", MidiOutput = "Saved output",
            ServerUri = "tcp://192.0.2.10:5180", Token = "saved-token", StartWithWindows = true,
        };
        ClientSettings saved = original;
        int saves = 0;
        await using var service = new ClientRelayService(_ => new RelayTestPort());
        var window = new ClientWindow(service, () => saved, settings => { saved = settings; saves++; });
        try
        {
            await window.InitializeAsync();
            window.Control<TextBox>("ServerUri")!.Text = "tcp://192.0.2.20:5180";
            window.Control<TextBox>("Token")!.Text = "unsaved-token";
            var option = window.Control<ToggleSwitch>("AutoConnectOnLaunch")!;
            option.IsChecked = true;
            Assert.Equal(original with { AutoConnectOnLaunch = true }, saved);
            Assert.False(service.IsRunning);
            Assert.Equal(1, saves);

            option.IsChecked = false;
            Assert.Equal(original, saved);
            Assert.False(service.IsRunning);
            Assert.Equal(2, saves);
            Assert.Equal("tcp://192.0.2.20:5180", window.Control<TextBox>("ServerUri")!.Text);
            window.Control<ToggleSwitch>("StartMinimizedToTray")!.IsChecked = true;
            Assert.Equal(original with { StartMinimizedToTray = true }, saved);
            option.IsChecked = true;
            Assert.Equal(original with { AutoConnectOnLaunch = true, StartMinimizedToTray = true }, saved);
            Assert.Equal(4, saves);
            Assert.True(window.IsVisible);
            Assert.False(service.IsRunning);
        }
        finally
        {
            window.AllowClose();
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnabledPreferenceStartsRetryingAndCanBeDisabledForTheNextLaunch(bool startMinimized)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var saved = new ClientSettings
        {
            InterfaceName = "Saved interface", MidiInput = "Saved input", MidiOutput = "Saved output",
            ServerUri = $"tcp://127.0.0.1:{port}", Token = "saved-token", AutoConnectOnLaunch = true,
            StartMinimizedToTray = startMinimized,
        };
        // Round-trip the store so this exercises the persisted preference across launches.
        string json = JsonSerializer.Serialize(saved);
        ClientSettings? openedSettings = null;
        await using var service = new ClientRelayService(settings => { openedSettings = settings; return new RelayTestPort(); });
        var retrying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.StatusChanged += (_, status) =>
        {
            if (status.StartsWith("Reconnecting", StringComparison.Ordinal)) { retrying.TrySetResult(); }
        };
        var window = new ClientWindow(service, () => JsonSerializer.Deserialize<ClientSettings>(json),
            settings => json = JsonSerializer.Serialize(settings));
        int openedWindows = 0;
        window.Opened += (_, _) => openedWindows++;
        try
        {
            await window.InitializeAsync();
            await retrying.Task.WaitAsync(timeout.Token);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(!startMinimized, window.IsVisible);
            Assert.Equal(startMinimized ? 0 : 1, openedWindows);
            Assert.Equal(saved, openedSettings);
            Assert.True(service.IsRunning);
            Assert.False(service.IsConnected);
            Assert.Equal("Reconnecting", window.Control<TextBlock>("StatusText")!.Text);
            Assert.True(window.Control<ToggleSwitch>("StartMinimizedToTray")!.IsEnabled);
            window.ShowConfiguration();
            Assert.True(window.IsVisible);
            Assert.Equal(WindowState.Normal, window.WindowState);
            Assert.True(service.IsRunning);
            var option = window.Control<ToggleSwitch>("AutoConnectOnLaunch")!;
            Assert.True(option.IsChecked);
            Assert.True(option.IsEnabled);
            option.IsChecked = false;
            Assert.True(service.IsRunning);
            Assert.False(JsonSerializer.Deserialize<ClientSettings>(json)!.AutoConnectOnLaunch);
            await window.StopAsync();
        }
        finally
        {
            window.AllowClose();
            window.Close();
        }

        int nextLaunchPorts = 0;
        await using var nextService = new ClientRelayService(_ => { nextLaunchPorts++; return new RelayTestPort(); });
        var nextWindow = new ClientWindow(nextService, () => JsonSerializer.Deserialize<ClientSettings>(json),
            settings => json = JsonSerializer.Serialize(settings));
        try
        {
            await nextWindow.InitializeAsync();
            Assert.Equal(!startMinimized, nextWindow.IsVisible);
            Assert.False(nextService.IsRunning);
            Assert.Equal(0, nextLaunchPorts);
            Assert.False(nextWindow.Control<ToggleSwitch>("AutoConnectOnLaunch")!.IsChecked);
            Assert.Equal(startMinimized, nextWindow.Control<ToggleSwitch>("StartMinimizedToTray")!.IsChecked);
            Assert.Equal(saved.ServerUri, nextWindow.Control<TextBox>("ServerUri")!.Text);
        }
        finally
        {
            nextWindow.AllowClose();
            nextWindow.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData("AutoConnectOnLaunch")]
    [InlineData("StartMinimizedToTray")]
    public async Task FailedPreferenceSaveRestoresCheckboxAndShowsAnError(string optionName)
    {
        await using var service = new ClientRelayService(_ => new RelayTestPort());
        var window = new ClientWindow(service, () => new ClientSettings(), _ => throw new IOException("Settings are read-only."));
        try
        {
            await window.InitializeAsync();
            var option = window.Control<ToggleSwitch>(optionName)!;
            option.IsChecked = true;
            Assert.False(option.IsChecked);
            Assert.False(service.IsRunning);
            Assert.True(window.Control<Border>("ErrorBanner")!.IsVisible);
            Assert.Contains("Settings are read-only.", window.Control<TextBlock>("ErrorText")!.Text);
        }
        finally
        {
            window.AllowClose();
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task StartupDeviceFailureOpensWindowEvenWhenStartMinimizedIsEnabled()
    {
        var saved = new ClientSettings
        {
            MidiInput = "From app", MidiOutput = "To app", AutoConnectOnLaunch = true, StartMinimizedToTray = true,
        };
        int saves = 0;
        await using var service = new ClientRelayService(_ => throw new InvalidOperationException("MIDI unavailable."));
        var window = new ClientWindow(service, () => saved, _ => saves++);
        try
        {
            await window.InitializeAsync();
            Assert.True(window.IsVisible);
            Assert.False(service.IsRunning);
            Assert.True(window.ConfigurationWindow.IsVisible);
            Assert.True(window.Control<ToggleSwitch>("StartMinimizedToTray")!.IsChecked);
            Assert.Contains("MIDI unavailable.", window.Control<TextBlock>("ErrorText")!.Text);
            Assert.Equal(0, saves);
        }
        finally
        {
            window.AllowClose();
            window.Close();
        }
    }
}
