using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;

namespace MidiForwarder.Server.Tests;

public sealed class ServerConfigurationTests
{
    [AvaloniaFact]
    public async Task SettingsCanSaveAndStartThenStopTheRelayFromDiagnostics()
    {
        using var reservation = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        reservation.Start();
        int port = ((System.Net.IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var saved = new ServerSettings
        {
            MidiEndpoint = "Saved endpoint", MidiInput = "From device", MidiOutput = "To device",
            ListenUrl = $"tcp://127.0.0.1:{port}", Token = "old-token",
        };
        ServerSettings? started = null;
        await using var service = new ServerRelayService(settings => { started = settings; return true; });
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.StatusChanged += (_, status) =>
        {
            if (status == "Stopped") { stopped.TrySetResult(); }
        };
        var window = new ServerWindow(service, () => saved, settings => saved = settings, _ => { });
        try
        {
            await window.InitializeAsync();
            window.ShowConfiguration();
            window.Control<TextBox>("Token")!.Text = "new-token";
            window.Control<ToggleSwitch>("AllowTakeover")!.IsChecked = true;
            var button = window.Control<Button>("SettingsConnectionButton")!;
            Assert.Equal("Start server", button.Content);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(service.IsRunning);
            Assert.Equal("new-token", saved.Token);
            Assert.Equal("new-token", started?.Token);
            Assert.True(saved.AllowTakeover);
            Assert.True(started?.AllowTakeover);
            Assert.False(window.Control<ToggleSwitch>("AllowTakeover")!.IsEnabled);
            Assert.Equal("Stop server", button.Content);
            Assert.False(window.Control<Button>("SaveConnectionButton")!.IsEnabled);

            window.ShowConfiguration(2);
            window.ConfigurationWindow.UpdateLayout();
            Assert.True(button.IsEffectivelyVisible);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
            Assert.False(service.IsRunning);
            Assert.Equal("Start server", button.Content);
            Assert.True(window.Control<Button>("SaveConnectionButton")!.IsEnabled);
            Assert.True(window.Control<ToggleSwitch>("AllowTakeover")!.IsEnabled);
            Assert.True(window.ConfigurationWindow.IsVisible);
        }
        finally
        {
            window.AllowClose();
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ConnectionSaveDoesNotStartRelayAndCloseDiscardsUnappliedEdits()
    {
        var saved = new ServerSettings
        {
            MidiEndpoint = "Saved endpoint", MidiInput = "From device", MidiOutput = "To device",
            ListenUrl = "tcp://127.0.0.1:5180", Token = "saved-token",
        };
        int deviceChecks = 0;
        int registrations = 0;
        await using var service = new ServerRelayService(_ => { deviceChecks++; return true; });
        var window = new ServerWindow(service, () => saved, settings => saved = settings, _ => registrations++);
        try
        {
            await window.InitializeAsync();
            window.ShowConfiguration();
            window.Control<TextBox>("ListenUrl")!.Text = "tcp://127.0.0.1:5181";
            window.Control<TextBox>("Token")!.Text = "new-token";
            window.Control<ToggleSwitch>("AllowTakeover")!.IsChecked = true;
            window.Control<Button>("SaveConnectionButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("tcp://127.0.0.1:5181", saved.ListenUrl);
            Assert.Equal("new-token", saved.Token);
            Assert.True(saved.AllowTakeover);
            Assert.False(service.IsRunning);
            Assert.Equal(0, deviceChecks);
            Assert.Equal(0, registrations);
            Assert.False(window.Control<Border>("ConfigurationErrorBanner")!.IsVisible);
            Assert.Equal("Connection settings saved.", window.Control<TextBlock>("SaveStatus")!.Text);

            window.Control<TextBox>("ListenUrl")!.Text = "tcp://127.0.0.1:5182";
            window.Control<ToggleSwitch>("AllowTakeover")!.IsChecked = false;
            window.ConfigurationWindow.Close();
            Assert.False(window.ConfigurationWindow.IsVisible);
            window.ShowConfiguration();
            Assert.Equal("tcp://127.0.0.1:5181", window.Control<TextBox>("ListenUrl")!.Text);
            Assert.True(window.Control<ToggleSwitch>("AllowTakeover")!.IsChecked);
            window.Control<TextBox>("ListenUrl")!.Text = "invalid-address";
            window.Control<Button>("SaveConnectionButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("tcp://127.0.0.1:5181", saved.ListenUrl);
            Assert.True(window.Control<Border>("ConfigurationErrorBanner")!.IsVisible);
            Assert.Equal(0, window.Control<TabControl>("SettingsTabs")!.SelectedIndex);
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
    public async Task WindowsStartupSavesImmediatelyAndRollsBackRegistrationWhenSavingFails(bool failSave)
    {
        var saved = new ServerSettings();
        var registrations = new List<bool>();
        await using var service = new ServerRelayService(_ => true);
        var window = new ServerWindow(service, () => saved, settings =>
        {
            if (failSave) { throw new IOException("Settings are read-only."); }
            saved = settings;
        }, registrations.Add);
        try
        {
            await window.InitializeAsync();
            window.ShowConfiguration(1);
            var startup = window.Control<ToggleSwitch>("StartWithSystem")!;
            startup.IsChecked = true;
            if (failSave)
            {
                Assert.Collection(registrations, enabled => Assert.True(enabled), enabled => Assert.False(enabled));
                Assert.False(startup.IsChecked);
                Assert.False(saved.StartWithWindows);
                Assert.True(window.Control<Border>("ConfigurationErrorBanner")!.IsVisible);
            }
            else
            {
                Assert.True(Assert.Single(registrations));
                Assert.True(saved.StartWithWindows);
                window.Control<ToggleSwitch>("AutoListenOnLaunch")!.IsChecked = true;
                Assert.True(saved.AutoListenOnLaunch);
                Assert.Single(registrations);
                startup.IsChecked = false;
                Assert.Collection(registrations, enabled => Assert.True(enabled), enabled => Assert.False(enabled));
                Assert.False(saved.StartWithWindows);
                Assert.True(saved.AutoListenOnLaunch);
            }
            Assert.False(service.IsRunning);
        }
        finally
        {
            window.AllowClose();
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task RegistrationFailureDoesNotPersistOrDisplayAnEnabledStartupSwitch()
    {
        int saves = 0;
        await using var service = new ServerRelayService(_ => true);
        var window = new ServerWindow(service, () => new ServerSettings(), _ => saves++,
            _ => throw new InvalidOperationException("Startup registration unavailable."));
        try
        {
            await window.InitializeAsync();
            window.ShowConfiguration(1);
            window.Control<ToggleSwitch>("StartWithSystem")!.IsChecked = true;
            Assert.False(window.Control<ToggleSwitch>("StartWithSystem")!.IsChecked);
            Assert.Equal(0, saves);
            Assert.Contains("Startup registration unavailable.", window.Control<TextBlock>("ConfigurationErrorText")!.Text);
            Assert.Equal(1, window.Control<TabControl>("SettingsTabs")!.SelectedIndex);
            Assert.False(service.IsRunning);
        }
        finally
        {
            window.AllowClose();
            window.Close();
        }
    }
}
