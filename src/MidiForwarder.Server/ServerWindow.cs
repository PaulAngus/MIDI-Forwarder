using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using MidiForwarder.Core;
#if MACOS
using WindowsStartupRegistration = MidiForwarder.Midi.CoreMidi.MacStartupRegistration;
#else
using WindowsStartupRegistration = MidiForwarder.Midi.WinMM.WindowsStartupRegistration;
#endif
using System.Diagnostics.CodeAnalysis;

namespace MidiForwarder.Server;

public sealed partial class ServerWindow : Window
{
    private const string SettingsFileName = "server.json";
    private const string StartupName = "MIDI Forwarder Server";
    private readonly ServerRelayService _service;
    private readonly Func<ServerSettings?> _loadSettings;
    private readonly Action<ServerSettings> _saveSettings;
    private readonly Action<bool> _setStartupRegistration;
    private readonly ServerSettingsWindow _configuration;
    private readonly MidiForwarder.Ui.MidiCapturePanel _capturePanel;
    private readonly ComboBox _midiEndpoint;
    private readonly ComboBox _midiInput;
    private readonly ComboBox _midiOutput;
    private readonly TextBox _listenUrl;
    private readonly TextBox _token;
    private readonly ToggleSwitch _startWithWindows;
    private readonly ToggleSwitch _autoListenOnLaunch;
    private readonly ToggleSwitch _startMinimizedToTray;
    private readonly TextBlock _status;
    private readonly TextBox _log;
    private readonly Button _toggleButton;
    private readonly DispatcherTimer _activityTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private bool _allowClose;
    private bool _busy;
    private bool _applyingSettings;
    private ServerSettings? _savedSettings;
    private string? _startFailure;

    public ServerWindow(ServerRelayService service)
        : this(service, () => JsonSettingsFile.Load<ServerSettings>(SettingsFileName),
            settings => JsonSettingsFile.Save(SettingsFileName, settings))
    {
    }

    internal ServerWindow(ServerRelayService service, Func<ServerSettings?> loadSettings, Action<ServerSettings> saveSettings,
        Action<bool>? setStartupRegistration = null, MidiDeviceDiscovery? midiDiscovery = null)
    {
        _service = service;
        _loadSettings = loadSettings;
        _saveSettings = saveSettings;
        _setStartupRegistration = setStartupRegistration ?? (enabled => WindowsStartupRegistration.SetEnabled(StartupName, enabled));
        _midiDiscovery = midiDiscovery ?? new MidiDeviceDiscovery(DiscoverMidiDevices);
        AvaloniaXamlLoader.Load(this);
        _configuration = new ServerSettingsWindow();
        _capturePanel = new MidiForwarder.Ui.MidiCapturePanel(_service.Capture, "server");
        View<ContentControl>("CaptureHost").Content = _capturePanel;
        _midiEndpoint = View<ComboBox>("MidiEndpoint");
        _midiInput = View<ComboBox>("MidiInput");
        _midiOutput = View<ComboBox>("MidiOutput");
        _listenUrl = View<TextBox>("ListenUrl");
        _token = View<TextBox>("Token");
        _startWithWindows = View<ToggleSwitch>("StartWithSystem");
        _autoListenOnLaunch = View<ToggleSwitch>("AutoListenOnLaunch");
        _startMinimizedToTray = View<ToggleSwitch>("StartMinimizedToTray");
        _status = View<TextBlock>("StatusText");
        _log = View<TextBox>("LogText");
        _toggleButton = View<Button>("ToggleServerButton");
        Title = "MIDI Forwarder Server";
        Icon = new WindowIcon(BrandAssets.Logo);
        View<Image>("EndpointLogo").Source = BrandAssets.Logo;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Closing += OnClosing;
        Closed += OnClosed;
        Opened += (_, _) =>
        {
            var screen = Screens.ScreenFromWindow(this);
            if (screen is not null)
            {
                MaxHeight = Math.Max(MinHeight, Math.Min(900, screen.WorkingArea.Height / RenderScaling - 64));
            }
        };
        _toggleButton.Click += async (_, _) => await ToggleServerAsync().ConfigureAwait(true);
        View<Button>("RefreshEndpointsButton").Click += async (_, _) => await RefreshMidiDevicesAsync().ConfigureAwait(true);
        View<Button>("HideButton").Click += (_, _) => HideToTray();
        View<Button>("SettingsButton").Click += (_, _) => ShowConfiguration(
            _service.Capture.GetSnapshot() is { IsRecording: true } or { LostRecords: > 0 } ? 3 : 0);
        View<Button>("SaveConnectionButton").Click += (_, _) => SaveConnectionSettings();
        View<Button>("SettingsConnectionButton").Click += async (_, _) =>
        {
            if (_service.IsRunning || SaveConnectionSettings())
            {
                await ToggleServerAsync().ConfigureAwait(true);
            }
        };
        _configuration.Closing += OnConfigurationClosing;
        View<Button>("QuitButton").Click += (_, _) => QuitRequested?.Invoke(this, EventArgs.Empty);
        _service.LogMessage += OnLogMessage;
        _service.StatusChanged += OnStatusChanged;
        _activityTimer.Tick += (_, _) => RefreshPresentation();
        PropertyChanged += (_, args) =>
        {
            if (args.Property == IsVisibleProperty)
            {
                if (IsVisible)
                {
                    RefreshPresentation();
                    _activityTimer.Start();
                }
                else
                {
                    _activityTimer.Stop();
                }
            }
        };
        SizeChanged += (_, _) => UpdateResponsiveLayout();
        _midiEndpoint.SelectionChanged += (_, _) => RefreshPresentation();
        _midiInput.SelectionChanged += (_, _) => RefreshPresentation();
        _midiOutput.SelectionChanged += (_, _) => RefreshPresentation();
        _autoListenOnLaunch.IsCheckedChanged += (_, _) => SaveLaunchPreferences();
        _startMinimizedToTray.IsCheckedChanged += (_, _) => SaveLaunchPreferences();
        _startWithWindows.IsCheckedChanged += (_, _) => SaveLaunchPreferences();
        _listenUrl.Text = "tcp://0.0.0.0:5180";
#if MACOS
        View<TextBlock>("StartWithSystemLabel").Text = "Start at login";
        Avalonia.Automation.AutomationProperties.SetName(_startWithWindows, "Start at login");
        View<TextBlock>("BackendDescription").Text = "CoreMIDI";
#else
        View<StackPanel>("EndpointFields").IsVisible = false;
        View<StackPanel>("LegacyFields").IsVisible = false;
        View<TextBlock>("BackendDescription").Text = _midiDevices.Description;
#endif
        RefreshPresentation();
        _initialMidiDiscovery = RefreshMidiDevicesAsync();
    }

    public event EventHandler? QuitRequested;

    internal ServerSettingsWindow ConfigurationWindow => _configuration;

    [SuppressMessage("Design", "CA1031", Justification = "The saved-settings startup boundary must keep the desktop app open and report any configuration or device error in the UI.")]
    public async Task InitializeAsync()
    {
        try
        {
            ServerSettings? settings = _loadSettings();
            if (settings is null)
            {
                ShowConfiguration();
                await _initialMidiDiscovery.ConfigureAwait(true);
                return;
            }

            settings = settings with { ListenUrl = UpgradeLegacyListenAddress(settings.ListenUrl) };
            _savedSettings = settings;
            ApplySettings(settings);
            RefreshPresentation();
            if (!settings.StartMinimizedToTray)
            {
                ShowMainWindow();
            }
            await _initialMidiDiscovery.ConfigureAwait(true);
            if (_closed) { return; }
            if (!_midiReady) { ShowConfiguration(); return; }
            if (settings.AutoListenOnLaunch)
            {
                await StartAsync(settings).ConfigureAwait(true);
            }
        }
        catch (Exception error)
        {
            ReportStartFailure("Could not load or apply saved settings", error);
        }
    }

    public void ShowMainWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    public void ShowConfiguration(int page = 0)
    {
        ShowMainWindow();
        _configuration.RequestedThemeVariant = RequestedThemeVariant;
        View<TabControl>("SettingsTabs").SelectedIndex = page;
        if (!_configuration.IsVisible)
        {
            _configuration.Show(this);
        }
        _configuration.WindowState = WindowState.Normal;
        _configuration.Activate();
    }

    private void HideToTray()
    {
        _configuration.Close();
        Hide();
    }

    private void OnConfigurationClosing(object? sender, WindowClosingEventArgs args)
    {
        if (!_allowClose)
        {
            args.Cancel = true;
            _configuration.Hide();
            ApplySettings(_savedSettings ?? new ServerSettings());
            View<TextBlock>("SaveStatus").Text = null;
            RefreshPresentation();
        }
    }

    public async Task StopAsync()
    {
        await _service.StopAsync().ConfigureAwait(true);
        RefreshPresentation();
    }

    public void AllowClose()
    {
        _closed = true;
        _allowClose = true;
        _configuration.IsEnabled = false;
        _configuration.Close();
    }

    private async Task ToggleServerAsync()
    {
        if (_busy || (!_service.IsRunning && (!_midiReady || _refreshingMidi)))
        {
            return;
        }

        _busy = true;
        RefreshPresentation();
        try
        {
            if (_service.IsRunning)
            {
                await StopAsync().ConfigureAwait(true);
            }
            else
            {
                if (_savedSettings is null)
                {
                    ShowConfiguration();
                }
                else
                {
                    await StartAsync(_savedSettings).ConfigureAwait(true);
                }
            }
        }
        finally
        {
            _busy = false;
            RefreshPresentation();
        }
    }

    [SuppressMessage("Design", "CA1031", Justification = "The settings-save UI boundary must report filesystem and startup-registration errors without terminating the app.")]
    private bool SaveConnectionSettings()
    {
        if (_service.IsRunning || _busy || !_midiReady || _refreshingMidi) { return false; }
        View<TextBlock>("SaveStatus").Text = null;
        try
        {
            ServerSettings settings = ReadSettings();
            _service.ValidateSettings(settings, requireAvailableMidi: false);
            _saveSettings(settings);
            _savedSettings = settings;
            _startFailure = null;
            View<TextBlock>("SaveStatus").Text = "Connection settings saved.";
            RefreshPresentation();
            return true;
        }
        catch (Exception error)
        {
            ReportStartFailure("Could not save or apply settings", error);
            return false;
        }
    }

    private ServerSettings ReadSettings() => new()
    {
        MidiEndpoint = GetText(_midiEndpoint),
#if !MACOS
        MidiInput = GetText(_midiInput),
        MidiOutput = GetText(_midiOutput),
#endif
        ListenUrl = _listenUrl.Text?.Trim() ?? string.Empty,
        Token = _token.Text ?? string.Empty,
        AllowTakeover = View<ToggleSwitch>("AllowTakeover").IsChecked == true,
        StartWithWindows = _startWithWindows.IsChecked == true,
        AutoListenOnLaunch = _autoListenOnLaunch.IsChecked == true,
        StartMinimizedToTray = _startMinimizedToTray.IsChecked == true,
    };

    [SuppressMessage("Design", "CA1031", Justification = "Saving a launch preference must report filesystem errors and restore the checkbox without terminating the app.")]
    private void SaveLaunchPreferences()
    {
        if (_applyingSettings)
        {
            return;
        }

        bool registered = false;
        bool previousStartup = _savedSettings?.StartWithWindows == true;
        try
        {
            // Keep the last saved connection, even if the user is editing the form.
            ServerSettings settings = (_savedSettings ?? ReadSettings()) with
            {
                StartWithWindows = _startWithWindows.IsChecked == true,
                AutoListenOnLaunch = _autoListenOnLaunch.IsChecked == true,
                StartMinimizedToTray = _startMinimizedToTray.IsChecked == true,
            };
            if (settings.StartWithWindows != previousStartup)
            {
                _setStartupRegistration(settings.StartWithWindows);
                registered = true;
            }
            _saveSettings(settings);
            _savedSettings = settings;
            _startFailure = null;
            RefreshPresentation();
        }
        catch (Exception error)
        {
            if (registered)
            {
                try { _setStartupRegistration(previousStartup); }
                catch (Exception rollbackError) { AppendLog($"Could not restore startup registration: {rollbackError.Message}"); }
            }
            SetLaunchPreferences(previousStartup, _savedSettings?.AutoListenOnLaunch == true, _savedSettings?.StartMinimizedToTray == true);
            ReportStartFailure("Could not save launch preferences", error, page: 1);
        }
    }

    private void SetLaunchPreferences(bool startWithWindows, bool autoListen, bool startMinimized)
    {
        _applyingSettings = true;
        try
        {
            _startWithWindows.IsChecked = startWithWindows;
            _autoListenOnLaunch.IsChecked = autoListen;
            _startMinimizedToTray.IsChecked = startMinimized;
        }
        finally
        {
            _applyingSettings = false;
        }
    }

    [SuppressMessage("Design", "CA1031", Justification = "All MIDI, socket, and configuration failures must be displayed rather than escape an async UI callback and terminate the process.")]
    private async Task StartAsync(ServerSettings settings)
    {
        try
        {
            _startFailure = null;
            if (_service.IsRunning)
            {
                await _service.StopAsync().ConfigureAwait(true);
            }

            await _service.StartAsync(settings).ConfigureAwait(true);
        }
        catch (Exception error)
        {
            ReportStartFailure("Start failed", error);
        }

        RefreshPresentation();
    }

    private void ApplySettings(ServerSettings settings)
    {
#if MACOS
        SetItems(_midiEndpoint, _midiEndpoint.Items.OfType<string>().ToArray(), settings.MidiEndpoint);
#else
        SetItems(_midiEndpoint, _midiEndpoint.Items.OfType<string>().ToArray(), settings.MidiEndpoint);
        SetItems(_midiInput, _midiInput.Items.OfType<string>().ToArray(), settings.EffectiveMidiInput);
        SetItems(_midiOutput, _midiOutput.Items.OfType<string>().ToArray(), settings.EffectiveMidiOutput);
#endif
        _listenUrl.Text = UpgradeLegacyListenAddress(settings.ListenUrl);
        _token.Text = settings.Token;
        View<ToggleSwitch>("AllowTakeover").IsChecked = settings.AllowTakeover;
        SetLaunchPreferences(settings.StartWithWindows, settings.AutoListenOnLaunch, settings.StartMinimizedToTray);
    }

    private void OnLogMessage(object? sender, string message) => AppendLog(message);

    private void OnStatusChanged(object? sender, string status) => Dispatcher.UIThread.Post(RefreshPresentation);

    private void AppendLog(string message)
    {
        DiagnosticLog.Write("server.log", message);
        Dispatcher.UIThread.Post(() =>
        {
            string line = $"{DateTime.Now:HH:mm:ss}  {message}";
            _log.Text = string.IsNullOrEmpty(_log.Text) ? line : _log.Text + Environment.NewLine + line;
            if (_log.Text.Length > 40_000)
            {
                _log.Text = _log.Text[^30_000..];
            }
            _log.CaretIndex = _log.Text.Length;
        });
    }

    private void ReportStartFailure(string prefix, Exception error, int page = 0)
    {
        AppendLog($"{prefix}: {error.Message}");
        _startFailure = $"{prefix}: {error.Message}";
        ShowConfiguration(page);
        RefreshPresentation();
    }

    private void RefreshPresentation()
    {
        MidiCaptureSnapshot capture = _service.Capture.GetSnapshot();
        View<Button>("SettingsButton").Content = capture.IsRecording ? "Settings (capture on)"
            : capture.LostRecords > 0 ? "Settings (capture issue)" : "Settings";
        bool running = _service.IsRunning;
        bool connected = running && _service.ConnectedClient is not null;
        string? error = _startFailure ?? _midiDiscoveryError ?? (running ? _service.LastConnectionError : null);
        _status.Text = error is not null ? "Attention" : connected ? "Connected" : running ? "Listening" : "Stopped";
        Border pill = View<Border>("StatusPill");
        pill.Classes.Set("connected", connected && error is null);
        pill.Classes.Set("listening", running && !connected && error is null);
        pill.Classes.Set("error", error is not null);
        View<Border>("ErrorBanner").IsVisible = error is not null;
        View<TextBlock>("ErrorText").Text = error;
        View<Border>("ConfigurationErrorBanner").IsVisible = error is not null;
        View<TextBlock>("ConfigurationErrorText").Text = error;

        View<TextBlock>("ClientSummary").Text = connected
            ? $"Client: {_service.ConnectedClient}"
            : running ? "Waiting for a network client" : "Server stopped";
        View<TextBlock>("ConnectionDetail").Text = connected
            ? "MIDI endpoint open. Forwarding in both directions."
            : running ? $"Listening on {_savedSettings?.ListenUrl ?? _listenUrl.Text}" : "Start the server to accept a MIDI client.";

        ServerSettings displayed = _savedSettings ?? ReadSettings();

#if MACOS
        string endpoint = displayed.MidiEndpoint;
        string subtitle = "Physical MIDI endpoint";
#else
        string endpoint = _midiDevices.UseMidiServices
            ? displayed.MidiEndpoint
            : displayed.EffectiveMidiInput.Equals(displayed.EffectiveMidiOutput, StringComparison.OrdinalIgnoreCase)
                ? displayed.EffectiveMidiInput : $"{displayed.EffectiveMidiInput} / {displayed.EffectiveMidiOutput}".Trim(' ', '/');
        string subtitle = _midiDevices.UseMidiServices ? "Physical MIDI endpoint" : "MIDI input + output";
#endif
        View<TextBlock>("EndpointTitle").Text = string.IsNullOrEmpty(endpoint) ? "Choose an endpoint" : endpoint;
        View<TextBlock>("EndpointSubtitle").Text = subtitle;
        _toggleButton.Content = running ? "Stop server" : "Start server";
        _toggleButton.IsEnabled = !_busy && (running || (_midiReady && !_refreshingMidi));
        View<Button>("SettingsConnectionButton").Content = _toggleButton.Content;
        View<Button>("SettingsConnectionButton").IsEnabled = _toggleButton.IsEnabled;
        bool editable = !running && !_busy && _midiReady && !_refreshingMidi;
        View<StackPanel>("EndpointFields").IsEnabled = editable;
        View<StackPanel>("LegacyFields").IsEnabled = editable;
        View<Button>("RefreshEndpointsButton").IsEnabled = !running && !_busy && !_refreshingMidi;
        _listenUrl.IsEnabled = editable;
        _token.IsEnabled = editable;
        View<ToggleSwitch>("AllowTakeover").IsEnabled = editable;
        View<Button>("SaveConnectionButton").IsEnabled = editable;
        _startWithWindows.IsEnabled = _autoListenOnLaunch.IsEnabled = _startMinimizedToTray.IsEnabled = _midiReady && !_refreshingMidi;
        View<TextBlock>("ConnectionEditHelp").Text = running
            ? "Stop the server to edit connection settings."
            : "Choose Save to store your settings, or Start server to save and start listening.";
        UpdateTraffic(_service.Traffic.GetSnapshot(), connected);
    }

    internal void UpdateTraffic(MidiTrafficSnapshot traffic, bool connected)
    {
        SetActivity("ToClient", connected && traffic.MidiToNetwork.IsActive(TimeSpan.FromMilliseconds(350)), connected);
        SetActivity("ToDevice", connected && traffic.NetworkToMidi.IsActive(TimeSpan.FromMilliseconds(350)), connected);
        View<TextBlock>("ToClientMessages").Text = FormatMessages(traffic.MidiToNetwork.Messages);
        View<TextBlock>("ToDeviceMessages").Text = FormatMessages(traffic.NetworkToMidi.Messages);
        View<TextBlock>("ToClientBytes").Text = $"{FormatBytes(traffic.MidiToNetwork.Bytes)} forwarded";
        View<TextBlock>("ToDeviceBytes").Text = $"{FormatBytes(traffic.NetworkToMidi.Bytes)} forwarded";
        View<TextBlock>("TrafficSession").Text = connected ? "Current connection"
            : traffic.MidiToNetwork.Messages + traffic.NetworkToMidi.Messages > 0 ? "Last connection" : "No traffic yet";
    }

    private void SetActivity(string prefix, bool active, bool connected)
    {
        View<Border>($"{prefix}Lamp").Classes.Set("active", active);
        TextBlock label = View<TextBlock>($"{prefix}State");
        label.Text = active ? "Active" : connected ? "Idle" : "No activity";
        label.Classes.Set("active", active);
    }

    private void UpdateResponsiveLayout()
    {
        bool narrow = (ClientSize.Width > 0 ? ClientSize.Width : Width) < 440;
        Control clientActivity = View<StackPanel>("ToClientActivity");
        Border deviceActivity = View<Border>("ToDeviceActivity");
        Grid.SetColumnSpan(clientActivity, narrow ? 2 : 1);
        Grid.SetColumn(deviceActivity, narrow ? 0 : 1);
        Grid.SetRow(deviceActivity, narrow ? 1 : 0);
        Grid.SetColumnSpan(deviceActivity, narrow ? 2 : 1);
        deviceActivity.Margin = new Thickness(0, narrow ? 20 : 0, 0, 0);
        deviceActivity.BorderThickness = new Thickness(narrow ? 0 : 1, 0, 0, 0);
        deviceActivity.Padding = new Thickness(narrow ? 0 : 16, 0, 0, 0);
    }

    private static string FormatBytes(long bytes) => bytes >= 1_048_576
        ? $"{bytes / 1_048_576d:N1} MiB" : bytes >= 1_024 ? $"{bytes / 1_024d:N1} KiB" : $"{bytes:N0} B";

    private static string FormatMessages(long messages) => messages == 1 ? "1 message" : $"{messages:N0} messages";

    private T View<T>(string name) where T : Control => this.FindControl<T>(name)
        ?? _configuration.FindControl<T>(name)
        ?? throw new InvalidOperationException($"Missing server control: {name}");

    private void OnClosed(object? sender, EventArgs args)
    {
        _closed = true;
        _allowClose = true;
        _configuration.Close();
        _capturePanel.StopUpdates();
        _activityTimer.Stop();
        _service.LogMessage -= OnLogMessage;
        _service.StatusChanged -= OnStatusChanged;
    }

    private void OnClosing(object? sender, WindowClosingEventArgs args)
    {
        if (!_allowClose)
        {
            args.Cancel = true;
            HideToTray();
        }
    }

    private static string GetText(ComboBox box) => (box.SelectedItem as string ?? string.Empty).Trim();

    private static string UpgradeLegacyListenAddress(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            && (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)))
        {
            return $"tcp://{uri.Host}:{uri.Port}";
        }

        return value;
    }

    private static void SetItems(ComboBox box, IReadOnlyList<string> available, string preferred)
    {
        var items = available.ToList();
        if (!string.IsNullOrWhiteSpace(preferred)
            && !items.Contains(preferred, StringComparer.OrdinalIgnoreCase))
        {
            items.Insert(0, preferred);
        }

        box.ItemsSource = items;
        box.SelectedItem = items.FirstOrDefault(item => item.Equals(preferred, StringComparison.OrdinalIgnoreCase))
            ?? items.FirstOrDefault();
    }

}
