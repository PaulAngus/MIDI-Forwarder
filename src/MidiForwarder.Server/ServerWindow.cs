using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.Automation;
using MidiForwarder.Core;
using MidiForwarder.Midi.WinMM;
using MidiForwarder.Midi.WindowsServices;
using System.Diagnostics.CodeAnalysis;

namespace MidiForwarder.Server;

public sealed class ServerWindow : Window
{
    private const string SettingsFileName = "server.json";
    private const string StartupName = "MIDI Forwarder Server";
    private readonly ServerRelayService _service;
    private readonly ComboBox _midiEndpoint = new();
    private readonly TextBox _listenUrl = new();
    private readonly TextBox _token = new();
    private readonly TextBox _localPorts = new() { AcceptsReturn = true, Height = 76 };
    private readonly TextBlock _connections = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly TextBlock _directAccess = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly Button _refreshButton = new() { Content = "Refresh endpoints" };
    private readonly Expander _settingsPanel = new() { Header = "Connection settings", IsExpanded = true, HorizontalAlignment = HorizontalAlignment.Stretch };
    private bool _wasRunning;
    private StackPanel? _deviceAccessPanel;
    private readonly bool _preview;
    private bool _previewRunning;
    private readonly CheckBox _startWithWindows = new() { Content = "Start automatically with Windows" };
    private readonly TextBlock _status = new() { Text = "Stopped" };
    private readonly TextBox _log = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly Button _startButton = new() { Content = "Save and Start" };
    private readonly Button _stopButton = new() { Content = "Stop", IsEnabled = false };
    private bool _allowClose;

    public ServerWindow(ServerRelayService service, bool preview = false)
    {
        _service = service;
        _preview = preview;
        Title = "MIDI Forwarder Server";
        Width = 760;
        Height = 900;
        MinWidth = 480;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = BuildContent();
        Closing += OnClosing;
        _startButton.Click += async (_, _) => await SaveAndStartAsync().ConfigureAwait(true);
        _stopButton.Click += async (_, _) => await StopAsync().ConfigureAwait(true);
        _refreshButton.Click += (_, _) => RefreshPorts();
        _service.LogMessage += (_, message) => AppendLog(message);
        _service.StatusChanged += (_, status) => SetStatus(status);
        _service.GatewayChanged += (_, state) => Dispatcher.UIThread.Post(() => ApplyGatewayState(state));
        if (!_preview) { RefreshPorts(); }
        else { SetItems(_midiEndpoint, ["Demo MIDI device"], "Demo MIDI device"); }
        _listenUrl.Text = "tcp://0.0.0.0:5180";
        _localPorts.Text = string.Join(Environment.NewLine, new ServerSettings().LocalPortNames);
    }

    public static bool HasSavedSettings => File.Exists(JsonSettingsFile.GetPath(SettingsFileName));

    [SuppressMessage("Design", "CA1031", Justification = "The saved-settings startup boundary must keep the desktop app open and report any configuration or device error in the UI.")]
    public async Task AutoStartAsync()
    {
        try
        {
            ServerSettings? settings = JsonSettingsFile.Load<ServerSettings>(SettingsFileName);
            if (settings is null)
            {
                ShowConfiguration();
                return;
            }

            settings = settings with { ListenUrl = UpgradeLegacyListenAddress(settings.ListenUrl) };
            ApplySettings(settings);
            await StartAsync(settings).ConfigureAwait(true);
        }
        catch (Exception error)
        {
            ReportStartFailure("Could not load or apply saved settings", error);
        }
    }

    public void ShowConfiguration()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    public async Task StopAsync()
    {
        await _service.StopAsync().ConfigureAwait(true);
        UpdateButtons();
    }

    public void AllowClose() => _allowClose = true;

    private Grid BuildContent()
    {
        var form = new StackPanel { Spacing = 12 };
        AddField(form, "Physical MIDI endpoint", _midiEndpoint);
        AddField(form, "Local MIDI ports — one name per line", _localPorts);
        form.Children.Add(new TextBlock
        {
            Text = "Use a different virtual port for each local app's MIDI input and output. Up to eight ports; leave empty for network connections only.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });
        AddField(form, "TCP listen address", _listenUrl);
        AddField(form, "Shared token", _token);
        form.Children.Add(_startWithWindows);
        _settingsPanel.Content = form;

        var sharing = new StackPanel { Spacing = 10, Children =
        {
            new TextBlock { Text = "Device access", FontSize = 18, FontWeight = Avalonia.Media.FontWeight.SemiBold },
            new TextBlock
            {
                Text = "Always shared. All connected apps can send MIDI and receive device messages. The forwarder does not reserve the device or pause other apps.",
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            },
            new ScrollViewer { MaxHeight = 110, Content = _connections },
            _directAccess,
            new TextBlock
            {
                Text = "Complete messages are sent in order. Coordinate bulk transfers in the sending apps when the device requires it.",
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            },
        } };
        _deviceAccessPanel = sharing;
        AutomationProperties.SetName(_log, "Server activity log");
        AutomationProperties.SetName(_status, "Server status");
        _status.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        _log.Height = 140;
        var hideButton = new Button { Content = "Hide to Tray" };
        hideButton.Click += (_, _) => Hide();
        var buttons = new WrapPanel { Children = { _startButton, _stopButton, _refreshButton } };
        foreach (Control button in buttons.Children) { button.Margin = new Thickness(0, 0, 8, 6); }
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 16, 0, 0) };
        footer.Children.Add(buttons); Grid.SetColumn(hideButton, 1); footer.Children.Add(hideButton);
        hideButton.VerticalAlignment = VerticalAlignment.Top;

        return new Grid
        {
            Margin = new Thickness(20),
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            Children =
            {
                new TextBlock { Text = "MIDI Forwarder Server", FontSize = 24, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                Place(new ScrollViewer
                {
                    Margin = new Thickness(0, 16, 0, 0),
                    Content = new StackPanel { Spacing = 20, Children = { _settingsPanel, _status, sharing, _log } },
                }, 1),
                Place(footer, 2),
            },
        };
    }

    [SuppressMessage("Design", "CA1031", Justification = "The settings-save UI boundary must report filesystem and startup-registration errors without terminating the app.")]
    private async Task SaveAndStartAsync()
    {
        try
        {
            var settings = new ServerSettings
            {
                MidiEndpoint = GetText(_midiEndpoint),
                ListenUrl = _listenUrl.Text?.Trim() ?? string.Empty,
                Token = _token.Text ?? string.Empty,
                StartWithWindows = _startWithWindows.IsChecked == true,
                LocalPortNames = (_localPorts.Text ?? string.Empty).Split('\n').Select(name => name.Trim())
                    .Where(name => name.Length > 0).ToArray(),
            };
            JsonSettingsFile.Save(SettingsFileName, settings);
            WindowsStartupRegistration.SetEnabled(StartupName, settings.StartWithWindows);
            await StartAsync(settings).ConfigureAwait(true);
        }
        catch (Exception error)
        {
            ReportStartFailure("Could not save or apply settings", error);
        }
    }

    [SuppressMessage("Design", "CA1031", Justification = "All MIDI, socket, and configuration failures must be displayed rather than escape an async UI callback and terminate the process.")]
    private async Task StartAsync(ServerSettings settings)
    {
        try
        {
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

        UpdateButtons();
    }

    private void ApplySettings(ServerSettings settings)
    {
        SetItems(_midiEndpoint, PhysicalMidiEndpointCatalog.GetNames(), settings.MidiEndpoint);
        _listenUrl.Text = UpgradeLegacyListenAddress(settings.ListenUrl);
        _token.Text = settings.Token;
        _startWithWindows.IsChecked = settings.StartWithWindows;
        _localPorts.Text = string.Join(Environment.NewLine, settings.LocalPortNames ?? []);
    }

    private void RefreshPorts()
    {
        string endpoint = GetText(_midiEndpoint);
        SetItems(_midiEndpoint, PhysicalMidiEndpointCatalog.GetNames(), endpoint);
    }

    private void AppendLog(string message) => Dispatcher.UIThread.Post(() =>
    {
        DiagnosticLog.Write("server.log", message);
        string line = $"{DateTime.Now:HH:mm:ss}  {message}";
        _log.Text = string.IsNullOrEmpty(_log.Text) ? line : _log.Text + Environment.NewLine + line;
        if (_log.Text.Length > 40_000)
        {
            _log.Text = _log.Text[^30_000..];
        }
        _log.CaretIndex = _log.Text.Length;
    });

    private void SetStatus(string status) => Dispatcher.UIThread.Post(() => _status.Text = status);

    private void ReportStartFailure(string prefix, Exception error)
    {
        AppendLog($"{prefix}: {error.Message}");
        SetStatus("Start failed");
        ShowConfiguration();
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool running = _service.IsRunning || (_preview && _previewRunning);
        if (_deviceAccessPanel is not null) { _deviceAccessPanel.IsVisible = running; }
        if (_wasRunning != running) { _settingsPanel.IsExpanded = !running; _wasRunning = running; }
        _startButton.IsEnabled = !running;
        _stopButton.IsEnabled = running;
        _refreshButton.IsEnabled = !running;
        _midiEndpoint.IsEnabled = !running; _localPorts.IsEnabled = !running;
        _listenUrl.IsEnabled = !running; _token.IsEnabled = !running; _startWithWindows.IsEnabled = !running;
    }

    public void ApplyGatewayState(GatewayState state)
    {
        _connections.Text = string.Join(Environment.NewLine, state.Connections.Select(connection =>
            $"{connection.Name}: Available"));
        _directAccess.Text = state.DirectAccessStatus;
        UpdateButtons();
    }

    public void SetPreviewState(GatewayState state, bool running = true)
    {
        if (!_preview) { throw new InvalidOperationException("This window is not a preview."); }
        _previewRunning = running;
        _token.Text = "Preview shared token";
        _status.Text = running ? "Listening on tcp://0.0.0.0:5180" : "Stopped";
        _log.Text = "Local gateway ports created.\nRemote connection 1 connected.\nMIDI access remains shared.";
        ApplyGatewayState(state);
    }

    private void OnClosing(object? sender, WindowClosingEventArgs args)
    {
        if (!_allowClose)
        {
            args.Cancel = true;
            Hide();
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

    private static void AddField(StackPanel panel, string label, Control control)
    {
        AutomationProperties.SetName(control, label);
        control.HorizontalAlignment = HorizontalAlignment.Stretch;
        panel.Children.Add(new StackPanel { Spacing = 4, Children = { new TextBlock { Text = label }, control } });
    }

    private static T Place<T>(T control, int row) where T : Control
    {
        Grid.SetRow(control, row);
        return control;
    }
}
