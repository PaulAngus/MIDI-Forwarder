using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using MidiForwarder.Core;
using MidiForwarder.Midi.WinMM;
using System.Diagnostics.CodeAnalysis;

namespace MidiForwarder.Client;

public sealed class ClientWindow : Window
{
    private const string SettingsFileName = "client.json";
    private const string StartupName = "MIDI Forwarder Client";
    private readonly ClientRelayService _service;
    private readonly TextBox _interfaceName = new();
    private readonly TextBox _serverUri = new();
    private readonly TextBox _token = new();
    private readonly CheckBox _startWithWindows = new() { Content = "Start automatically with Windows" };
    private readonly TextBlock _status = new() { Text = "Stopped" };
    private readonly TextBox _log = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly Button _startButton = new() { Content = "Save and Connect" };
    private readonly Button _stopButton = new() { Content = "Disconnect", IsEnabled = false };
    private bool _allowClose;

    public ClientWindow(ClientRelayService service)
    {
        _service = service;
        Title = "MIDI Forwarder Client";
        Width = 700;
        Height = 620;
        MinWidth = 580;
        MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = BuildContent();
        Closing += OnClosing;
        _startButton.Click += async (_, _) => await SaveAndStartAsync().ConfigureAwait(true);
        _stopButton.Click += async (_, _) => await StopAsync().ConfigureAwait(true);
        _service.LogMessage += (_, message) => AppendLog(message);
        _service.StatusChanged += (_, status) => SetStatus(status);
        _interfaceName.Text = "MIDI Forwarder";
        _serverUri.Text = "tcp://127.0.0.1:5180";
    }

    public static bool HasSavedSettings => File.Exists(JsonSettingsFile.GetPath(SettingsFileName));

    [SuppressMessage("Design", "CA1031", Justification = "The saved-settings startup boundary must keep the desktop app open and report any configuration or device error in the UI.")]
    public async Task AutoStartAsync()
    {
        try
        {
            ClientSettings? settings = JsonSettingsFile.Load<ClientSettings>(SettingsFileName);
            if (settings is null)
            {
                ShowConfiguration();
                return;
            }

            settings = settings with { ServerUri = UpgradeLegacyServerAddress(settings.ServerUri) };
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
        var help = new TextBlock
        {
            Text = "The client creates one MIDI input and one MIDI output using the interface name below (default: “MIDI Forwarder”). Select that same name for MIDI input and output in your MIDI application.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };
        var form = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("190,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto"),
        };
        AddField(form, 0, "Virtual MIDI interface", _interfaceName);
        AddField(form, 1, "Server TCP address", _serverUri);
        AddField(form, 2, "Shared token", _token);
        Grid.SetRow(_startWithWindows, 3);
        Grid.SetColumn(_startWithWindows, 1);
        form.Children.Add(_startWithWindows);

        var hideButton = new Button { Content = "Hide to Tray" };
        hideButton.Click += (_, _) => Hide();
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { _startButton, _stopButton, hideButton },
        };

        return new Grid
        {
            Margin = new Thickness(20),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*,Auto"),
            Children =
            {
                new TextBlock { Text = "MIDI Forwarder Client", FontSize = 24, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                Place(help, 1),
                Place(form, 2),
                Place(new Border
                {
                    Padding = new Thickness(12),
                    Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(35, 42, 52)),
                    CornerRadius = new CornerRadius(5),
                    Child = _status,
                }, 3),
                Place(_log, 4),
                Place(buttons, 5),
            },
        };
    }

    [SuppressMessage("Design", "CA1031", Justification = "The settings-save UI boundary must report filesystem and startup-registration errors without terminating the app.")]
    private async Task SaveAndStartAsync()
    {
        try
        {
            var settings = new ClientSettings
            {
                InterfaceName = _interfaceName.Text?.Trim() ?? string.Empty,
                ServerUri = _serverUri.Text?.Trim() ?? string.Empty,
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
    private async Task StartAsync(ClientSettings settings)
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

    private void ApplySettings(ClientSettings settings)
    {
        _interfaceName.Text = settings.InterfaceName;
        _serverUri.Text = UpgradeLegacyServerAddress(settings.ServerUri);
        _token.Text = settings.Token;
        _startWithWindows.IsChecked = settings.StartWithWindows;
    }

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
