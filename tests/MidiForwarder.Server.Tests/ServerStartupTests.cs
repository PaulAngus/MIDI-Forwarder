using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;

namespace MidiForwarder.Server.Tests;

public sealed class ServerStartupTests
{
    [Theory]
    [InlineData("{}", false)]
    [InlineData("{\"StartWithWindows\":true}", false)]
    [InlineData("{\"AutoListenOnLaunch\":false}", false)]
    [InlineData("{\"AutoListenOnLaunch\":true}", true)]
    public void AutomaticListeningRequiresExplicitOptIn(string json, bool expected)
    {
        Assert.False(new ServerSettings().AutoListenOnLaunch);
        Assert.False(new ServerSettings().StartMinimizedToTray);
        Assert.False(JsonSerializer.Deserialize<ServerSettings>(json)!.StartMinimizedToTray);
        Assert.Equal(expected, JsonSerializer.Deserialize<ServerSettings>(json)!.AutoListenOnLaunch);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FirstUseAndLegacySettingsOpenWithoutListening(bool hasSavedSettings)
    {
        int validations = 0;
        int saves = 0;
        await using var service = new ServerRelayService(_ => { validations++; return true; });
        ServerSettings? saved = hasSavedSettings ? JsonSerializer.Deserialize<ServerSettings>(
            """{"MidiEndpoint":"Saved endpoint","ListenUrl":"http://127.0.0.1:5180","Token":"saved-token","StartWithWindows":true}""") : null;
        var window = new ServerWindow(service, () => saved, _ => saves++);
        try
        {
            await window.InitializeAsync();
            Assert.True(window.IsVisible);
            Assert.False(service.IsRunning);
            Assert.Equal(0, validations);
            Assert.Equal(0, saves);
            Assert.False(window.Control<ToggleSwitch>("AutoListenOnLaunch")!.IsChecked);
            Assert.False(window.Control<ToggleSwitch>("StartMinimizedToTray")!.IsChecked);
            Assert.Equal(!hasSavedSettings, window.ConfigurationWindow.IsVisible);
            Assert.Equal("Start server", window.Control<Button>("ToggleServerButton")!.Content);
            if (hasSavedSettings)
            {
                Assert.Equal("Saved endpoint", window.Control<TextBlock>("EndpointTitle")!.Text);
                Assert.Equal("tcp://127.0.0.1:5180", window.Control<TextBox>("ListenUrl")!.Text);
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
    public async Task PreferenceSavesImmediatelyWithoutStartingOrSavingPendingConnectionEdits()
    {
        var original = new ServerSettings
        {
            MidiEndpoint = "Saved endpoint", MidiInput = "Saved input", MidiOutput = "Saved output",
            ListenUrl = "tcp://127.0.0.1:5180", Token = "saved-token", StartWithWindows = true,
        };
        ServerSettings saved = original;
        int saves = 0;
        await using var service = new ServerRelayService(_ => true);
        var window = new ServerWindow(service, () => saved, settings => { saved = settings; saves++; });
        try
        {
            await window.InitializeAsync();
            window.Control<TextBox>("ListenUrl")!.Text = "tcp://127.0.0.1:5181";
            window.Control<TextBox>("Token")!.Text = "unsaved-token";
            var option = window.Control<ToggleSwitch>("AutoListenOnLaunch")!;
            option.IsChecked = true;
            Assert.Equal(original with { AutoListenOnLaunch = true }, saved);
            Assert.False(service.IsRunning);
            Assert.Equal(1, saves);

            option.IsChecked = false;
            Assert.Equal(original, saved);
            Assert.False(service.IsRunning);
            Assert.Equal(2, saves);
            Assert.Equal("tcp://127.0.0.1:5181", window.Control<TextBox>("ListenUrl")!.Text);
            window.Control<ToggleSwitch>("StartMinimizedToTray")!.IsChecked = true;
            Assert.Equal(original with { StartMinimizedToTray = true }, saved);
            option.IsChecked = true;
            Assert.Equal(original with { AutoListenOnLaunch = true, StartMinimizedToTray = true }, saved);
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
    public async Task EnabledPreferenceStartsListeningAndCanBeDisabledForTheNextLaunch(bool startMinimized)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var saved = new ServerSettings
        {
            MidiEndpoint = "Saved endpoint", MidiInput = "Saved input", MidiOutput = "Saved output",
            ListenUrl = $"tcp://127.0.0.1:{port}", Token = "saved-token", AutoListenOnLaunch = true,
            StartMinimizedToTray = startMinimized,
        };
        string json = JsonSerializer.Serialize(saved);
        int saves = 0;
        ServerSettings? startedSettings = null;
        // Exercise a real TCP listener without requiring physical MIDI hardware.
        await using var service = new ServerRelayService(settings => { startedSettings = settings; return true; });
        var window = new ServerWindow(service, () => JsonSerializer.Deserialize<ServerSettings>(json),
            settings => { json = JsonSerializer.Serialize(settings); saves++; });
        int openedWindows = 0;
        window.Opened += (_, _) => openedWindows++;
        try
        {
            await window.InitializeAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(!startMinimized, window.IsVisible);
            Assert.Equal(startMinimized ? 0 : 1, openedWindows);
            Assert.Equal(saved, startedSettings);
            Assert.Equal(0, saves);
            Assert.True(service.IsRunning);
            Assert.Equal("Listening", window.Control<TextBlock>("StatusText")!.Text);
            Assert.Equal("Stop server", window.Control<Button>("ToggleServerButton")!.Content);
            Assert.False(window.Control<TextBox>("ListenUrl")!.IsEnabled);
            Assert.True(window.Control<ToggleSwitch>("StartMinimizedToTray")!.IsEnabled);
            window.ShowConfiguration();
            Assert.True(window.IsVisible);
            Assert.Equal(WindowState.Normal, window.WindowState);
            Assert.True(service.IsRunning);
            using var peer = new TcpClient();
            await peer.ConnectAsync(IPAddress.Loopback, port, timeout.Token);

            var option = window.Control<ToggleSwitch>("AutoListenOnLaunch")!;
            Assert.True(option.IsChecked);
            Assert.True(option.IsEnabled);
            option.IsChecked = false;
            Assert.True(service.IsRunning);
            Assert.Equal(1, saves);
            Assert.Equal(saved with { AutoListenOnLaunch = false }, JsonSerializer.Deserialize<ServerSettings>(json));
            await window.StopAsync();
        }
        finally
        {
            window.AllowClose();
            window.Close();
        }

        int nextLaunchValidations = 0;
        await using var nextService = new ServerRelayService(_ => { nextLaunchValidations++; return true; });
        var nextWindow = new ServerWindow(nextService, () => JsonSerializer.Deserialize<ServerSettings>(json),
            settings => json = JsonSerializer.Serialize(settings));
        try
        {
            await nextWindow.InitializeAsync();
            Assert.Equal(!startMinimized, nextWindow.IsVisible);
            Assert.False(nextService.IsRunning);
            Assert.Equal(0, nextLaunchValidations);
            Assert.False(nextWindow.Control<ToggleSwitch>("AutoListenOnLaunch")!.IsChecked);
            Assert.Equal(startMinimized, nextWindow.Control<ToggleSwitch>("StartMinimizedToTray")!.IsChecked);
            Assert.Equal(saved.ListenUrl, nextWindow.Control<TextBox>("ListenUrl")!.Text);
        }
        finally
        {
            nextWindow.AllowClose();
            nextWindow.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData("AutoListenOnLaunch")]
    [InlineData("StartMinimizedToTray")]
    public async Task FailedPreferenceSaveRestoresCheckboxAndShowsAnError(string optionName)
    {
        await using var service = new ServerRelayService(_ => true);
        var window = new ServerWindow(service, () => new ServerSettings(), _ => throw new IOException("Settings are read-only."));
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

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticStartupFailureKeepsSettingsAndDisplaysTheError(bool midiAvailable)
    {
        using var occupiedPort = new TcpListener(IPAddress.Loopback, 0);
        occupiedPort.Start();
        int port = ((IPEndPoint)occupiedPort.LocalEndpoint).Port;
        var saved = new ServerSettings
        {
            MidiEndpoint = "Saved endpoint", ListenUrl = $"tcp://127.0.0.1:{port}", AutoListenOnLaunch = true,
            StartMinimizedToTray = true,
        };
        int saves = 0;
        await using var service = new ServerRelayService(_ => midiAvailable);
        var window = new ServerWindow(service, () => saved, _ => saves++);
        try
        {
            await window.InitializeAsync();
            Assert.False(service.IsRunning);
            Assert.True(window.IsVisible);
            Assert.True(window.Control<ToggleSwitch>("AutoListenOnLaunch")!.IsChecked);
            Assert.True(window.ConfigurationWindow.IsVisible);
            Assert.True(window.Control<Border>("ErrorBanner")!.IsVisible);
            Assert.StartsWith("Start failed:", window.Control<TextBlock>("ErrorText")!.Text);
            Assert.Equal(saved.ListenUrl, window.Control<TextBox>("ListenUrl")!.Text);
            Assert.Equal(0, saves);
        }
        finally
        {
            window.AllowClose();
            window.Close();
        }
    }
}
