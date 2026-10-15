using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using MidiForwarder.Core;
#if MACOS
using WindowsStartupRegistration = MidiForwarder.Midi.CoreMidi.MacStartupRegistration;
#else
using WinMmPortCatalog = MightBeUseful.Midi.Windows.LegacyMidiPortCatalog;
using WindowsStartupRegistration = MidiForwarder.Midi.WinMM.WindowsStartupRegistration;

using MightBeUseful.Midi.Windows;
#endif
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
#if MACOS
    private readonly CheckBox _startWithWindows = new() { Content = "Start at login" };
    private readonly ComboBox _midiBackend = new()
    {
        ItemsSource = new[] { "Apple network MIDI (recommended)", "Virtual MIDI (compatibility)" },
        SelectedIndex = 0,
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };
    private readonly TextBox _networkMidiEndpoint = new() { Text = "Network RTP Session 1" };
    private readonly TextBox _networkMidiPort = new() { Text = "5004" };
    private TextBlock? _networkPortLabel;
    private readonly TextBlock _midiHelp = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
#else
    private readonly CheckBox _startWithWindows = new() { Content = "Start automatically with Windows" };
    private readonly ComboBox _midiInput = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _midiOutput = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
#endif
    private readonly TextBlock _status = new()
    {
        Text = "Stopped",
        Foreground = Avalonia.Media.Brushes.White,
        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
    };
    private readonly TextBox _log = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly Button _startButton = new() { Content = "Save and Connect" };
    private readonly Button _stopButton = new() { Content = "Disconnect", IsEnabled = false };
    private bool _allowClose;

    public ClientWindow(ClientRelayService service)
    {
        _service = service;
        Title = "MIDI Forwarder Client";
        Icon = new WindowIcon(BrandAssets.Logo);
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
#if !MACOS
        Height = 740;
        RefreshPorts();
        AppendLog(WindowsMidiBackend.Capabilities.Description);
#endif
#if MACOS
        _midiBackend.SelectionChanged += (_, _) => UpdateMidiBackend();
        _networkMidiEndpoint.TextChanged += (_, _) => UpdateMidiBackend();
        UpdateMidiBackend();
#endif
    }

    public static bool HasSavedSettings => File.Exists(JsonSettingsFile.GetPath(SettingsFileName));

    public event EventHandler? QuitRequested;

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
#if MACOS
        TextBlock help = _midiHelp;
#else
        var help = new TextBlock
        {
            Text = WindowsMidiBackend.Capabilities.CanCreateVirtualPorts
                ? "Windows MIDI Services creates a virtual input and output with the interface name below. Select that name for input and output in your MIDI app."
                : "Create two virtual cables in loopMIDI (or another virtual MIDI driver). Choose the cable your MIDI app sends to as ‘From MIDI app’, and the cable your MIDI app receives from as ‘To MIDI app’. Keep the virtual cable software running. Use different cables to avoid feedback.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };
#endif
        var form = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("190,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,Auto"),
        };
#if MACOS
        AddField(form, 0, "MIDI backend", _midiBackend);
        AddField(form, 1, "MIDI interface", _interfaceName);
        Grid.SetRow(_networkMidiEndpoint, 1);
        Grid.SetColumn(_networkMidiEndpoint, 1);
        _networkMidiEndpoint.Margin = new Thickness(0, 0, 0, 8);
        form.Children.Add(_networkMidiEndpoint);
        _networkPortLabel = AddField(form, 2, "Apple session UDP port", _networkMidiPort);
        AddField(form, 3, "Server TCP address", _serverUri);
        AddField(form, 4, "Shared token", _token);
        Grid.SetRow(_startWithWindows, 5);
        var midiSetup = new Button { Content = "Open Audio MIDI Setup", Margin = new Thickness(0, 0, 0, 8) };
        midiSetup.Click += (_, _) => OpenMidiSetup();
        Grid.SetRow(midiSetup, 6);
        Grid.SetColumn(midiSetup, 1);
        form.Children.Add(midiSetup);
#else
        if (WindowsMidiBackend.Capabilities.CanCreateVirtualPorts)
        {
            AddField(form, 0, "Virtual MIDI interface", _interfaceName);
        }
        else
        {
            AddField(form, 0, "From MIDI app (input)", _midiInput);
            AddField(form, 1, "To MIDI app (output)", _midiOutput);
        }
        AddField(form, 2, "Server TCP address", _serverUri);
        AddField(form, 3, "Shared token", _token);
        Grid.SetRow(_startWithWindows, 4);
#endif
        Grid.SetColumn(_startWithWindows, 1);
        form.Children.Add(_startWithWindows);

        var hideButton = new Button { Content = OperatingSystem.IsMacOS() ? "Hide" : "Hide to Tray" };
        hideButton.Click += (_, _) => Hide();
        var quitButton = new Button { Content = "Quit" };
        quitButton.Click += (_, _) => QuitRequested?.Invoke(this, EventArgs.Empty);
        var buttons = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { _startButton, _stopButton, hideButton, quitButton },
        };
#if !MACOS
        if (!WindowsMidiBackend.Capabilities.CanCreateVirtualPorts)
        {
            var refresh = new Button { Content = "Refresh MIDI Ports" };
            refresh.Click += (_, _) => RefreshPorts();
            buttons.Children.Insert(2, refresh);
        }
#endif
        foreach (Control button in buttons.Children)
        {
            button.Margin = new Thickness(0, 0, 8, 8);
        }

        return new Grid
        {
            Margin = new Thickness(20),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*,Auto"),
            Children =
            {
                BrandAssets.CreateHeader("MIDI Forwarder Client"),
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
            };
#if !MACOS
            if (!WindowsMidiBackend.Capabilities.CanCreateVirtualPorts)
            {
                WindowsMidiBackend.ValidateClientPorts(settings.MidiInput, settings.MidiOutput);
            }
#endif
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
#if !MACOS
        SetPorts(_midiInput, WinMmPortCatalog.GetInputNames(), settings.MidiInput);
        SetPorts(_midiOutput, WinMmPortCatalog.GetOutputNames(), settings.MidiOutput);
#endif
#if MACOS
        _midiBackend.SelectedIndex = settings.UseAppleNetworkMidi ? 0 : 1;
        _networkMidiEndpoint.Text = settings.NetworkMidiEndpoint;
        _networkMidiPort.Text = settings.NetworkMidiSessionPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        UpdateMidiBackend();
#endif
    }

#if !MACOS
    private void RefreshPorts()
    {
        SetPorts(_midiInput, WinMmPortCatalog.GetInputNames(), _midiInput.SelectedItem as string ?? string.Empty);
        SetPorts(_midiOutput, WinMmPortCatalog.GetOutputNames(), _midiOutput.SelectedItem as string ?? string.Empty);
    }

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
        _networkMidiEndpoint.IsVisible = native;
        _interfaceName.IsVisible = !native;
        _networkMidiPort.IsVisible = native;
        if (_networkPortLabel is not null) { _networkPortLabel.IsVisible = native; }
        _midiHelp.Text = native
            ? $"Select “{_networkMidiEndpoint.Text}” for input and output in your MIDI app. One-time setup in Audio MIDI Setup: enable an RTP session, add 127.0.0.1:55006 to its directory, and set Connection policy to Directory entries. The client connects automatically."
            : "The compatibility backend creates a virtual input and output with the name below. Bulk transfers are paced. Select that name for input and output in your MIDI app.";
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

    private static TextBlock AddField(Grid grid, int row, string label, Control control)
    {
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(text, row);
        Grid.SetRow(control, row);
        Grid.SetColumn(control, 1);
        text.Margin = new Thickness(0, 0, 0, 8);
        control.Margin = new Thickness(0, 0, 0, 8);
        grid.Children.Add(text);
        grid.Children.Add(control);
        return text;
    }

    private static T Place<T>(T control, int row) where T : Control
    {
        Grid.SetRow(control, row);
        return control;
    }
}
