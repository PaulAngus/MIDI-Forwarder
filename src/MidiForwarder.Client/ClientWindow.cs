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

namespace MidiForwarder.Client;

public sealed partial class ClientWindow : Window
{
    private const string SettingsFileName = "client.json";
    private const string StartupName = "MIDI Forwarder Client";
    private readonly ClientRelayService _service;
    private readonly Func<ClientSettings?> _loadSettings;
    private readonly Action<ClientSettings> _saveSettings;
    private readonly Action<bool> _setStartupRegistration;
    private readonly ClientSettingsWindow _configuration;
    private readonly MidiForwarder.Ui.MidiCapturePanel _capturePanel;
    private readonly TextBox _interfaceName;
    private readonly TextBox _serverUri;
    private readonly TextBox _token;
    private readonly ToggleSwitch _startWithWindows;
    private readonly ToggleSwitch _autoConnectOnLaunch;
    private readonly ToggleSwitch _startMinimizedToTray;
    private readonly ComboBox _midiBackend;
    private readonly TextBox _networkMidiEndpoint;
    private readonly TextBox _networkMidiPort;
    private readonly TextBlock _midiHelp;
    private readonly ComboBox _midiInput;
    private readonly ComboBox _midiOutput;
    private readonly TextBlock _status;
    private readonly TextBox _log;
    private readonly Button _toggleButton;
    private readonly DispatcherTimer _activityTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private bool _allowClose;
    private bool _busy;
    private bool _applyingSettings;
    private ClientSettings? _savedSettings;
    private string? _startFailure;

    public ClientWindow(ClientRelayService service)
        : this(service, () => JsonSettingsFile.Load<ClientSettings>(SettingsFileName),
            settings => JsonSettingsFile.Save(SettingsFileName, settings))
    {
    }

    internal ClientWindow(ClientRelayService service, Func<ClientSettings?> loadSettings, Action<ClientSettings> saveSettings,
        Action<bool>? setStartupRegistration = null, MidiDeviceDiscovery? midiDiscovery = null)
    {
        _service = service;
        _loadSettings = loadSettings;
        _saveSettings = saveSettings;
        _setStartupRegistration = setStartupRegistration ?? (enabled => WindowsStartupRegistration.SetEnabled(StartupName, enabled));
        _midiDiscovery = midiDiscovery ?? new MidiDeviceDiscovery(DiscoverMidiDevices);
        AvaloniaXamlLoader.Load(this);
        _configuration = new ClientSettingsWindow();
        _capturePanel = new MidiForwarder.Ui.MidiCapturePanel(_service.Capture, "client");
        View<ContentControl>("CaptureHost").Content = _capturePanel;
        _interfaceName = View<TextBox>("InterfaceName");
        _serverUri = View<TextBox>("ServerUri");
        _token = View<TextBox>("Token");
        _startWithWindows = View<ToggleSwitch>("StartWithSystem");
        _autoConnectOnLaunch = View<ToggleSwitch>("AutoConnectOnLaunch");
        _startMinimizedToTray = View<ToggleSwitch>("StartMinimizedToTray");
        _midiBackend = View<ComboBox>("MidiBackend");
        _networkMidiEndpoint = View<TextBox>("NetworkMidiEndpoint");
        _networkMidiPort = View<TextBox>("NetworkMidiPort");
        _midiHelp = View<TextBlock>("MidiHelp");
        _midiInput = View<ComboBox>("MidiInput");
        _midiOutput = View<ComboBox>("MidiOutput");
        _status = View<TextBlock>("StatusText");
        _log = View<TextBox>("LogText");
        _toggleButton = View<Button>("ToggleClientButton");
        Title = "MIDI Forwarder Client";
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
        _toggleButton.Click += async (_, _) => await ToggleConnectionAsync().ConfigureAwait(true);
        View<Button>("HideButton").Click += (_, _) => HideToTray();
        View<Button>("SettingsButton").Click += (_, _) => ShowConfiguration(
            _service.Capture.GetSnapshot() is { IsRecording: true } or { LostRecords: > 0 } ? 3 : 0);
        View<Button>("SaveConnectionButton").Click += (_, _) => SaveConnectionSettings();
        View<Button>("SettingsConnectionButton").Click += async (_, _) =>
        {
            if (_service.IsRunning || SaveConnectionSettings())
            {
                await ToggleConnectionAsync().ConfigureAwait(true);
            }
        };
        _configuration.Closing += OnConfigurationClosing;
        View<Button>("QuitButton").Click += (_, _) => QuitRequested?.Invoke(this, EventArgs.Empty);
        _service.LogMessage += OnLogMessage;
        _service.StatusChanged += OnStatusChanged;
        _service.ConfirmTakeoverAsync = ConfirmTakeoverAsync;
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
        _interfaceName.TextChanged += (_, _) => RefreshPresentation();
        _serverUri.TextChanged += (_, _) => RefreshPresentation();
        _midiInput.SelectionChanged += (_, _) => RefreshPresentation();
        _midiOutput.SelectionChanged += (_, _) => RefreshPresentation();
        _autoConnectOnLaunch.IsCheckedChanged += (_, _) => SaveLaunchPreferences();
        _startMinimizedToTray.IsCheckedChanged += (_, _) => SaveLaunchPreferences();
        _startWithWindows.IsCheckedChanged += (_, _) => SaveLaunchPreferences();
#if !MACOS
        View<StackPanel>("InterfaceFields").IsVisible = false;
        View<StackPanel>("LegacyFields").IsVisible = false;
        View<Button>("RefreshPortsButton").Click += async (_, _) => await RefreshMidiDevicesAsync().ConfigureAwait(true);
        View<TextBlock>("BackendDescription").Text = _midiDevices.Description;
#endif
#if MACOS
        View<TextBlock>("StartWithSystemLabel").Text = "Start at login";
        Avalonia.Automation.AutomationProperties.SetName(_startWithWindows, "Start at login");
        View<StackPanel>("MacFields").IsVisible = true;
        View<Button>("RefreshPortsButton").IsVisible = false;
        _midiBackend.ItemsSource = new[] { "Apple network MIDI (recommended)", "Virtual MIDI (compatibility)" };
        _midiBackend.SelectedIndex = 0;
        View<Button>("OpenMidiSetupButton").Click += (_, _) => OpenMidiSetup();
        _midiBackend.SelectionChanged += (_, _) => UpdateMidiBackend();
        _networkMidiEndpoint.TextChanged += (_, _) => UpdateMidiBackend();
        UpdateMidiBackend();
#endif
        RefreshPresentation();
        _initialMidiDiscovery = RefreshMidiDevicesAsync();
    }

    public event EventHandler? QuitRequested;

    internal ClientSettingsWindow ConfigurationWindow => _configuration;

    [SuppressMessage("Design", "CA1031", Justification = "The saved-settings startup boundary must keep the desktop app open and report any configuration or device error in the UI.")]
    public async Task InitializeAsync()
    {
        try
        {
            ClientSettings? settings = _loadSettings();
            if (settings is null)
            {
                ShowConfiguration();
                await _initialMidiDiscovery.ConfigureAwait(true);
                return;
            }

            settings = settings with { ServerUri = UpgradeLegacyServerAddress(settings.ServerUri) };
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
            if (settings.AutoConnectOnLaunch)
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
            ApplySettings(_savedSettings ?? new ClientSettings());
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

    [SuppressMessage("Design", "CA1031", Justification = "Connection buttons must report device cleanup failures instead of terminating the desktop app.")]
    private async Task ToggleConnectionAsync()
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
        catch (Exception error)
        {
            ReportStartFailure("Could not change the connection", error);
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
            ClientSettings settings = ReadSettings();
            if (!settings.IsComplete)
            {
                throw new InvalidOperationException("Complete the MIDI settings and enter a valid tcp:// server address.");
            }
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

    private ClientSettings ReadSettings() => new()
    {
        InterfaceName = _interfaceName.Text?.Trim() ?? string.Empty,
#if !MACOS
        MidiInput = _midiInput.SelectedItem as string ?? string.Empty,
        MidiOutput = _midiOutput.SelectedItem as string ?? string.Empty,
#endif
#if MACOS
        UseAppleNetworkMidi = _midiBackend.SelectedIndex == 0,
        NetworkMidiEndpoint = _networkMidiEndpoint.Text?.Trim() ?? string.Empty,
        NetworkMidiSessionPort = int.TryParse(_networkMidiPort.Text, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out int port) ? port : 0,
#endif
        ServerUri = _serverUri.Text?.Trim() ?? string.Empty,
        Token = _token.Text ?? string.Empty,
        StartWithWindows = _startWithWindows.IsChecked == true,
        AutoConnectOnLaunch = _autoConnectOnLaunch.IsChecked == true,
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
            ClientSettings settings = (_savedSettings ?? ReadSettings()) with
            {
                StartWithWindows = _startWithWindows.IsChecked == true,
                AutoConnectOnLaunch = _autoConnectOnLaunch.IsChecked == true,
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
            SetLaunchPreferences(previousStartup, _savedSettings?.AutoConnectOnLaunch == true, _savedSettings?.StartMinimizedToTray == true);
            ReportStartFailure("Could not save launch preferences", error, page: 1);
        }
    }

    private void SetLaunchPreferences(bool startWithWindows, bool autoConnect, bool startMinimized)
    {
        _applyingSettings = true;
        try
        {
            _startWithWindows.IsChecked = startWithWindows;
            _autoConnectOnLaunch.IsChecked = autoConnect;
            _startMinimizedToTray.IsChecked = startMinimized;
        }
        finally
        {
            _applyingSettings = false;
        }
    }

    [SuppressMessage("Design", "CA1031", Justification = "All MIDI, socket, and configuration failures must be displayed rather than escape an async UI callback and terminate the process.")]
    private async Task StartAsync(ClientSettings settings)
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

    private void ApplySettings(ClientSettings settings)
    {
        _interfaceName.Text = settings.InterfaceName;
        _serverUri.Text = UpgradeLegacyServerAddress(settings.ServerUri);
        _token.Text = settings.Token;
        SetLaunchPreferences(settings.StartWithWindows, settings.AutoConnectOnLaunch, settings.StartMinimizedToTray);
#if !MACOS
        SetPorts(_midiInput, _midiInput.Items.OfType<string>().ToArray(), settings.MidiInput);
        SetPorts(_midiOutput, _midiOutput.Items.OfType<string>().ToArray(), settings.MidiOutput);
#endif
#if MACOS
        _midiBackend.SelectedIndex = settings.UseAppleNetworkMidi ? 0 : 1;
        _networkMidiEndpoint.Text = settings.NetworkMidiEndpoint;
        _networkMidiPort.Text = settings.NetworkMidiSessionPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        UpdateMidiBackend();
#endif
    }

#if !MACOS
    private static void SetPorts(ComboBox box, IReadOnlyList<string> ports, string selected)
    {
        // Never silently substitute a different cable for a saved, disconnected port.
        List<string> items = ports.ToList();
        if (!string.IsNullOrWhiteSpace(selected) && !items.Contains(selected, StringComparer.OrdinalIgnoreCase))
        {
            items.Insert(0, selected);
        }
        box.ItemsSource = items;
        box.SelectedItem = items.FirstOrDefault(item => item.Equals(selected, StringComparison.OrdinalIgnoreCase));
    }
#endif

#if MACOS
    private void UpdateMidiBackend()
    {
        bool native = _midiBackend.SelectedIndex == 0;
        View<StackPanel>("NetworkEndpointField").IsVisible = native;
        View<StackPanel>("NetworkPortField").IsVisible = native;
        View<StackPanel>("InterfaceFields").IsVisible = !native;
        _midiHelp.Text = native
            ? $"Select “{_networkMidiEndpoint.Text}” for input and output in your MIDI app. One-time setup in Audio MIDI Setup: enable an RTP session, add 127.0.0.1:55006 to its directory, and set Connection policy to Directory entries. The client connects automatically."
            : "The compatibility backend creates a virtual input and output with the name below. Bulk transfers are paced. Select that name for input and output in your MIDI app.";
        View<TextBlock>("BackendDescription").Text = native ? "Apple network MIDI" : "CoreMIDI compatibility virtual interface";
        RefreshPresentation();
    }

    private void OpenMidiSetup()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/usr/bin/open")
            {
                ArgumentList = { "-a", "Audio MIDI Setup" },
                UseShellExecute = false,
            })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            AppendLog($"Could not open Audio MIDI Setup: {error.Message}");
        }
    }
#endif

    private void AppendLog(string message) => Dispatcher.UIThread.Post(() =>
    {
        string line = $"{DateTime.Now:HH:mm:ss}  {message}";
        _log.Text = string.IsNullOrEmpty(_log.Text) ? line : _log.Text + Environment.NewLine + line;
        if (_log.Text.Length > 40_000)
        {
            _log.Text = _log.Text[^30_000..];
        }
        _log.CaretIndex = _log.Text.Length;
    });

    private void OnLogMessage(object? sender, string message) => AppendLog(message);

    private void OnStatusChanged(object? sender, string status) => Dispatcher.UIThread.Post(RefreshPresentation);

    private async Task<bool> ConfirmTakeoverAsync(CancellationToken cancellationToken)
    {
        return await Dispatcher.UIThread.InvokeAsync(() => ShowTakeoverDialogAsync(cancellationToken));
    }

    internal async Task<bool> ShowTakeoverDialogAsync(CancellationToken cancellationToken)
    {
        if (_closed || cancellationToken.IsCancellationRequested) { return false; }
        ShowMainWindow();
        var dialog = new Window
        {
            Name = "TakeoverDialog",
            Title = "Take over server connection?",
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            RequestedThemeVariant = RequestedThemeVariant,
            FontSize = 14,
            Icon = Icon,
        };
        dialog.Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude((Uri?)null)
        {
            Source = new Uri("avares://MidiForwarder.Client/Assets/FluentUtilityStyles.axaml"),
        });
        dialog.Bind(BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("WindowBrush"));
        dialog.Bind(ForegroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("TextBrush"));
        var cancel = new Button { Name = "CancelTakeoverButton", Content = "Cancel", IsCancel = true };
        var takeOver = new Button { Name = "ConfirmTakeoverButton", Content = "Take over" };
        takeOver.Classes.Add("accent");
        cancel.Click += (_, _) => dialog.Close(false);
        takeOver.Click += (_, _) => dialog.Close(true);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(24), Spacing = 20,
            Children =
            {
                new TextBlock { Text = "Take over server connection?", FontSize = 20, FontWeight = Avalonia.Media.FontWeight.Medium, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new TextBlock
                {
                    Text = $"The server at {_savedSettings?.ServerUri ?? _serverUri.Text} already has a connected client. Taking over will disconnect that client and connect this one.",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, takeOver } },
            },
        };
        Task<bool> result = dialog.ShowDialog<bool>(this);
        cancel.Focus();
        using var registration = cancellationToken.Register(() => Dispatcher.UIThread.Post(() => dialog.Close(false)));
        return await result.ConfigureAwait(true);
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
        bool connected = running && _service.IsConnected;
        string? error = _startFailure ?? _midiDiscoveryError ?? (running ? _service.LastConnectionError : null);
        _status.Text = _startFailure is not null || _midiDiscoveryError is not null ? "Attention" : connected ? "Connected" : _service.IsPaused ? "Paused" : !running ? "Disconnected"
            : _service.ConnectionStatus.StartsWith("Reconnecting", StringComparison.Ordinal) ? "Reconnecting"
            : _service.ConnectionStatus == "Waiting for Apple MIDI" ? "Waiting for MIDI" : "Connecting";
        Border pill = View<Border>("StatusPill");
        pill.Classes.Set("connected", connected && error is null);
        pill.Classes.Set("listening", running && !connected && error is null);
        pill.Classes.Set("error", error is not null);
        View<Border>("ErrorBanner").IsVisible = error is not null;
        View<TextBlock>("ErrorText").Text = error;
        View<Border>("ConfigurationErrorBanner").IsVisible = error is not null;
        View<TextBlock>("ConfigurationErrorText").Text = error;
        ClientSettings displayed = _savedSettings ?? ReadSettings();
        View<TextBlock>("ServerSummary").Text = string.IsNullOrWhiteSpace(displayed.ServerUri)
            ? "Choose a server address" : $"Server: {displayed.ServerUri.Trim()}";
        View<TextBlock>("ConnectionDetail").Text = connected
            ? "MIDI interface open. Forwarding in both directions."
            : running ? _service.ConnectionStatus : "Connect to start forwarding MIDI.";

#if MACOS
        bool native = displayed.UseAppleNetworkMidi;
        string endpoint = native ? displayed.NetworkMidiEndpoint : displayed.InterfaceName;
        string subtitle = native ? "Apple network MIDI" : "Virtual MIDI interface";
#else
        bool modern = _midiDevices.CanCreateVirtualPorts;
        string endpoint = modern ? displayed.InterfaceName
            : $"{displayed.MidiInput} / {displayed.MidiOutput}".Trim(' ', '/');
        string subtitle = modern ? "Virtual MIDI interface" : "MIDI app input + output cables";
#endif
        View<TextBlock>("EndpointTitle").Text = string.IsNullOrEmpty(endpoint) ? "Choose a MIDI interface" : endpoint;
        View<TextBlock>("EndpointSubtitle").Text = subtitle;
        _toggleButton.Content = running ? "Disconnect" : "Connect";
        _toggleButton.IsEnabled = !_busy && (running || (_midiReady && !_refreshingMidi));
        View<Button>("SettingsConnectionButton").Content = _toggleButton.Content;
        View<Button>("SettingsConnectionButton").IsEnabled = _toggleButton.IsEnabled;
        bool editable = !running && !_busy && _midiReady && !_refreshingMidi;
        foreach (Control field in new Control[] { _interfaceName, _serverUri, _token, _midiInput, _midiOutput,
                     _midiBackend, _networkMidiEndpoint, _networkMidiPort, View<Button>("RefreshPortsButton") })
        {
            field.IsEnabled = editable;
        }
        View<Button>("SaveConnectionButton").IsEnabled = editable;
        View<Button>("RefreshPortsButton").IsEnabled = !running && !_busy && !_refreshingMidi;
        _startWithWindows.IsEnabled = _autoConnectOnLaunch.IsEnabled = _startMinimizedToTray.IsEnabled = _midiReady && !_refreshingMidi;
        View<TextBlock>("ConnectionEditHelp").Text = running
            ? "Disconnect to edit connection settings."
            : "Choose Save to store your settings, or Connect to save and connect.";
        UpdateTraffic(_service.Traffic.GetSnapshot(), connected);
    }

    internal void UpdateTraffic(MidiTrafficSnapshot traffic, bool connected)
    {
        SetActivity("ToServer", connected && traffic.MidiToNetwork.IsActive(TimeSpan.FromMilliseconds(350)), connected);
        SetActivity("ToApp", connected && traffic.NetworkToMidi.IsActive(TimeSpan.FromMilliseconds(350)), connected);
        View<TextBlock>("ToServerMessages").Text = FormatMessages(traffic.MidiToNetwork.Messages);
        View<TextBlock>("ToAppMessages").Text = FormatMessages(traffic.NetworkToMidi.Messages);
        View<TextBlock>("ToServerBytes").Text = $"{FormatBytes(traffic.MidiToNetwork.Bytes)} forwarded";
        View<TextBlock>("ToAppBytes").Text = $"{FormatBytes(traffic.NetworkToMidi.Bytes)} forwarded";
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
        Control serverActivity = View<StackPanel>("ToServerActivity");
        Border appActivity = View<Border>("ToAppActivity");
        Grid.SetColumnSpan(serverActivity, narrow ? 2 : 1);
        Grid.SetColumn(appActivity, narrow ? 0 : 1);
        Grid.SetRow(appActivity, narrow ? 1 : 0);
        Grid.SetColumnSpan(appActivity, narrow ? 2 : 1);
        appActivity.Margin = new Thickness(0, narrow ? 20 : 0, 0, 0);
        appActivity.BorderThickness = new Thickness(narrow ? 0 : 1, 0, 0, 0);
        appActivity.Padding = new Thickness(narrow ? 0 : 16, 0, 0, 0);
    }

    private static string FormatBytes(long bytes) => bytes >= 1_048_576
        ? $"{bytes / 1_048_576d:N1} MiB" : bytes >= 1_024 ? $"{bytes / 1_024d:N1} KiB" : $"{bytes:N0} B";

    private static string FormatMessages(long messages) => messages == 1 ? "1 message" : $"{messages:N0} messages";

    private T View<T>(string name) where T : Control => this.FindControl<T>(name)
        ?? _configuration.FindControl<T>(name)
        ?? throw new InvalidOperationException($"Missing client control: {name}");

    private void OnClosed(object? sender, EventArgs args)
    {
        _closed = true;
        _allowClose = true;
        _configuration.Close();
        _capturePanel.StopUpdates();
        _activityTimer.Stop();
        _service.LogMessage -= OnLogMessage;
        _service.StatusChanged -= OnStatusChanged;
        _service.ConfirmTakeoverAsync = null;
    }

    private void OnClosing(object? sender, WindowClosingEventArgs args)
    {
        if (!_allowClose)
        {
            args.Cancel = true;
            HideToTray();
        }
    }

    private static string UpgradeLegacyServerAddress(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            && (uri.Scheme.Equals("ws", StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase)))
        {
            return $"tcp://{uri.Host}:{uri.Port}";
        }

        return value;
    }

}
