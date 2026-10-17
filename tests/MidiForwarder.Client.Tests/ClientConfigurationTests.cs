using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;

namespace MidiForwarder.Client.Tests;

public sealed class ClientConfigurationTests
{
    [AvaloniaFact]
    public async Task SettingsCanSaveAndStartThenStopTheRelayFromDiagnostics()
    {
        using var reservation = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        reservation.Start();
        int port = ((System.Net.IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var saved = new ClientSettings
        {
            InterfaceName = "Saved interface", MidiInput = "From app", MidiOutput = "To app",
            ServerUri = $"tcp://127.0.0.1:{port}", Token = "old-token",
        };
        ClientSettings? started = null;
        await using var service = new ClientRelayService(settings => { started = settings; return new RelayTestPort(); });
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.StatusChanged += (_, status) =>
        {
            if (status == "Stopped") { stopped.TrySetResult(); }
        };
        var window = new ClientWindow(service, () => saved, settings => saved = settings, _ => { });
        try
        {
            await window.InitializeAsync();
            window.ShowConfiguration();
            window.Control<TextBox>("Token")!.Text = "new-token";
            var button = window.Control<Button>("SettingsConnectionButton")!;
            Assert.Equal("Connect", button.Content);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(service.IsRunning);
            Assert.Equal("new-token", saved.Token);
            Assert.Equal("new-token", started?.Token);
            Assert.Equal("Disconnect", button.Content);
            Assert.False(window.Control<Button>("SaveConnectionButton")!.IsEnabled);

            window.ShowConfiguration(2);
            window.ConfigurationWindow.UpdateLayout();
            Assert.True(button.IsEffectivelyVisible);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
            Assert.False(service.IsRunning);
            Assert.Equal("Connect", button.Content);
            Assert.True(window.Control<Button>("SaveConnectionButton")!.IsEnabled);
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
        var saved = new ClientSettings
        {
            InterfaceName = "Saved interface", MidiInput = "From app", MidiOutput = "To app",
            ServerUri = "tcp://127.0.0.1:5180", Token = "saved-token",
        };
        int deviceChecks = 0;
        int registrations = 0;
        await using var service = new ClientRelayService(_ => { deviceChecks++; return new RelayTestPort(); });
        var window = new ClientWindow(service, () => saved, settings => saved = settings, _ => registrations++);
        try
        {
            await window.InitializeAsync();
            window.ShowConfiguration();
            window.Control<TextBox>("ServerUri")!.Text = "tcp://127.0.0.1:5181";
            window.Control<TextBox>("Token")!.Text = "new-token";
            window.Control<Button>("SaveConnectionButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("tcp://127.0.0.1:5181", saved.ServerUri);
            Assert.Equal("new-token", saved.Token);
            Assert.False(service.IsRunning);
            Assert.Equal(0, deviceChecks);
            Assert.Equal(0, registrations);
            Assert.False(window.Control<Border>("ConfigurationErrorBanner")!.IsVisible);
            Assert.Equal("Connection settings saved.", window.Control<TextBlock>("SaveStatus")!.Text);

            window.Control<TextBox>("ServerUri")!.Text = "tcp://127.0.0.1:5182";
            window.ConfigurationWindow.Close();
            Assert.False(window.ConfigurationWindow.IsVisible);
            window.ShowConfiguration();
            Assert.Equal("tcp://127.0.0.1:5181", window.Control<TextBox>("ServerUri")!.Text);
            window.Control<TextBox>("ServerUri")!.Text = "invalid-address";
            window.Control<Button>("SaveConnectionButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("tcp://127.0.0.1:5181", saved.ServerUri);
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
        var saved = new ClientSettings();
        var registrations = new List<bool>();
        await using var service = new ClientRelayService(_ => new RelayTestPort());
        var window = new ClientWindow(service, () => saved, settings =>
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
                window.Control<ToggleSwitch>("AutoConnectOnLaunch")!.IsChecked = true;
                Assert.True(saved.AutoConnectOnLaunch);
                Assert.Single(registrations);
                startup.IsChecked = false;
                Assert.Collection(registrations, enabled => Assert.True(enabled), enabled => Assert.False(enabled));
                Assert.False(saved.StartWithWindows);
                Assert.True(saved.AutoConnectOnLaunch);
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
        await using var service = new ClientRelayService(_ => new RelayTestPort());
        var window = new ClientWindow(service, () => new ClientSettings(), _ => saves++,
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
