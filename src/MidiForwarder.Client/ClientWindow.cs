using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.Automation;
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
    private readonly TextBox _additionalInterfaces = new() { AcceptsReturn = true, Height = 76 };
    private readonly TextBox _serverUri = new();
    private readonly TextBox _token = new();
    private readonly CheckBox _startWithWindows = new() { Content = "Start automatically with Windows" };
    private readonly TextBlock _status = new() { Text = "Stopped" };
    private readonly TextBox _log = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly Button _startButton = new() { Content = "Save and Connect" };
    private readonly Button _stopButton = new() { Content = "Disconnect", IsEnabled = false };
    private readonly Expander _settingsPanel = new() { Header = "MIDI ports and connection", IsExpanded = true, HorizontalAlignment = HorizontalAlignment.Stretch };
    private bool _allowClose;
    private readonly bool _preview;
    private bool _previewRunning;

    public ClientWindow(ClientRelayService service, bool preview = false)
    {
        _service = service;
        _preview = preview;
        Title = "MIDI Forwarder Client";
        Width = 700;
        Height = 760;
        MinWidth = 480;
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
            Text = "Use a different virtual MIDI port for each app. Select the same port for that app's MIDI input and output. Each port has its own connection to the server; up to eight ports in total.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };
        var form = new StackPanel { Spacing = 12 };
        AddField(form, "Virtual MIDI port", _interfaceName);
        AddField(form, "Additional MIDI ports — one name per line", _additionalInterfaces);
        AddField(form, "Server TCP address", _serverUri);
        AddField(form, "Shared token", _token);
        form.Children.Add(_startWithWindows);
        _settingsPanel.Content = form;

        var hideButton = new Button { Content = "Hide to Tray" };
        hideButton.Click += (_, _) => Hide();
        var buttons = new WrapPanel { Children = { _startButton, _stopButton } };
        foreach (Control button in buttons.Children) { button.Margin = new Thickness(0, 0, 8, 6); }
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 16, 0, 0) };
        footer.Children.Add(buttons); Grid.SetColumn(hideButton, 1); footer.Children.Add(hideButton);
        hideButton.VerticalAlignment = VerticalAlignment.Top;
        _status.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        _log.Height = 180;
        AutomationProperties.SetName(_status, "Connection status");
        AutomationProperties.SetName(_log, "Client activity log");

        return new Grid
        {
            Margin = new Thickness(20),
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            Children =
            {
                new TextBlock { Text = "MIDI Forwarder Client", FontSize = 24, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                Place(new ScrollViewer
                {
                    Margin = new Thickness(0, 16, 0, 0),
                    Content = new StackPanel { Spacing = 20, Children =
                    {
                        help, _settingsPanel, _status,
                        new TextBlock { Text = "If the server reserves another connection, this port waits for access and discards offline traffic. Release the reservation on the server to resume.", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                        _log,
                    } },
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
            var settings = new ClientSettings
            {
                InterfaceName = _interfaceName.Text?.Trim() ?? string.Empty,
                ServerUri = _serverUri.Text?.Trim() ?? string.Empty,
                Token = _token.Text ?? string.Empty,
                StartWithWindows = _startWithWindows.IsChecked == true,
                AdditionalInterfaceNames = (_additionalInterfaces.Text ?? string.Empty).Split('\n').Select(name => name.Trim())
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
        _additionalInterfaces.Text = string.Join(Environment.NewLine, settings.AdditionalInterfaceNames ?? []);
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
        bool running = _service.IsRunning || (_preview && _previewRunning);
        _settingsPanel.IsExpanded = !running;
        _startButton.IsEnabled = !running;
        _stopButton.IsEnabled = running;
        _interfaceName.IsEnabled = !running; _additionalInterfaces.IsEnabled = !running;
        _serverUri.IsEnabled = !running; _token.IsEnabled = !running; _startWithWindows.IsEnabled = !running;
    }

    public void SetPreviewState(string status, bool running = true)
    {
        if (!_preview) { throw new InvalidOperationException("This window is not a preview."); }
        _previewRunning = running;
        _additionalInterfaces.Text = "MIDI Editor";
        _token.Text = "Preview shared token";
        _status.Text = status;
        _log.Text = "MIDI Forwarder: connected to server.\nMIDI Editor: device reserved for another connection. Waiting for access; offline MIDI is discarded.";
        UpdateButtons();
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
