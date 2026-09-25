using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
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
    private readonly CheckBox _startWithWindows = new() { Content = "Start automatically with Windows" };
    private readonly TextBlock _status = new() { Text = "Stopped" };
    private readonly TextBox _log = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly Button _startButton = new() { Content = "Save and Start" };
    private readonly Button _stopButton = new() { Content = "Stop", IsEnabled = false };
    private bool _allowClose;

    public ServerWindow(ServerRelayService service)
    {
        _service = service;
        Title = "MIDI Forwarder Server";
        Width = 700;
        Height = 610;
        MinWidth = 580;
        MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = BuildContent();
        Closing += OnClosing;
        _startButton.Click += async (_, _) => await SaveAndStartAsync().ConfigureAwait(true);
        _stopButton.Click += async (_, _) => await StopAsync().ConfigureAwait(true);
        _service.LogMessage += (_, message) => AppendLog(message);
        _service.StatusChanged += (_, status) => SetStatus(status);
        RefreshPorts();
        _listenUrl.Text = "tcp://0.0.0.0:5180";
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
        var form = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("170,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto"),
        };
        AddField(form, 0, "Physical MIDI endpoint", _midiEndpoint);
        AddField(form, 1, "TCP listen address", _listenUrl);
        AddField(form, 2, "Shared token", _token);
        Grid.SetRow(_startWithWindows, 3);
        Grid.SetColumn(_startWithWindows, 1);
        form.Children.Add(_startWithWindows);

        var refreshButton = new Button { Content = "Refresh MIDI Endpoints" };
        refreshButton.Click += (_, _) => RefreshPorts();
        var hideButton = new Button { Content = "Hide to Tray" };
        hideButton.Click += (_, _) => Hide();
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { _startButton, _stopButton, refreshButton, hideButton },
        };

        return new Grid
        {
            Margin = new Thickness(20),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto"),
            Children =
            {
                new TextBlock { Text = "MIDI Forwarder Server", FontSize = 24, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                Place(form, 1),
                Place(new Border
                {
                    Padding = new Thickness(12),
                    Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(35, 42, 52)),
                    CornerRadius = new CornerRadius(5),
                    Child = _status,
                }, 2),
                Place(_log, 3),
                Place(buttons, 4),
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
        _startButton.IsEnabled = !_service.IsRunning;
        _stopButton.IsEnabled = _service.IsRunning;
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

    private static void AddField(Grid grid, int row, string label, Control control)
    {
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(text, row);
        Grid.SetRow(control, row);
        Grid.SetColumn(control, 1);
        text.Margin = new Thickness(0, 0, 0, 8);
        control.Margin = new Thickness(0, 0, 0, 8);
        grid.Children.Add(text);
        grid.Children.Add(control);
    }

    private static T Place<T>(T control, int row) where T : Control
    {
        Grid.SetRow(control, row);
        return control;
    }
}
