using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.Themes.Fluent;
using System.Diagnostics.CodeAnalysis;

namespace MidiForwarder.Client;

[SuppressMessage("Design", "CA1001", Justification = "Resources are disposed by the explicit Quit command.")]
public sealed class App : Application
{
    private ClientRelayService? _service;
    private ClientWindow? _window;
    private TrayIcon? _trayIcon;
    private TrayStatusIndicator? _trayStatus;
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private Task? _quitTask;

    public override void Initialize()
    {
        Name = "MIDI Forwarder Client";
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _service = new ClientRelayService();
            _window = new ClientWindow(_service);
            _window.QuitRequested += (_, _) => _ = QuitAsync();
            CreateTrayIcon(_service);
            Dispatcher.UIThread.Post(async () =>
            {
                await _window.InitializeAsync().ConfigureAwait(true);
                // Assign after startup so Avalonia does not show the window before applying the tray preference.
                desktop.MainWindow = _window;
            });
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void CreateTrayIcon(ClientRelayService service)
    {
        var show = new NativeMenuItem("Open MIDI Forwarder");
        show.Click += (_, _) => _window?.ShowMainWindow();
        var open = new NativeMenuItem("Open Settings");
        open.Click += (_, _) => _window?.ShowConfiguration();
        var stop = new NativeMenuItem("Disconnect");
        stop.Click += (_, _) => _ = _window?.StopAsync();
        var quit = new NativeMenuItem("Quit");
        quit.Click += (_, _) => _ = QuitAsync();
        var menu = new NativeMenu();
        menu.Items.Add(show);
        menu.Items.Add(open);
        menu.Items.Add(stop);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(quit);
        _trayIcon = new TrayIcon
        {
            Menu = menu,
        };
        _trayStatus = new TrayStatusIndicator(_trayIcon, stop, "MIDI Forwarder Client", () => GetTrayStatus(service));
        service.StatusChanged += _trayStatus.OnStatusChanged;
        _trayIcon.IsVisible = true;
        _trayIcon.Clicked += (_, _) => _window?.ShowMainWindow();
        TrayIcon.SetIcons(this, new TrayIcons { _trayIcon });
    }

    private Task QuitAsync() => _quitTask ??= QuitCoreAsync();

    internal static TrayStatus GetTrayStatus(ClientRelayService service) => !service.IsRunning
        ? new(TrayConnectionState.Inactive, "Disconnected")
        : service.IsConnected
            ? new(TrayConnectionState.Active, "Connected")
            : service.IsPaused
                ? new(TrayConnectionState.Inactive, "Paused")
            : new(TrayConnectionState.Connecting, service.ConnectionStatus);

    private async Task QuitCoreAsync()
    {
        if (_trayStatus is not null)
        {
            if (_service is not null) { _service.StatusChanged -= _trayStatus.OnStatusChanged; }
            _trayStatus.Dispose();
        }

        if (_window is not null)
        {
            _window.IsEnabled = false;
            _window.AllowClose();
        }

        try
        {
            if (_service is not null)
            {
                await _service.DisposeAsync().ConfigureAwait(true);
            }
        }
        finally
        {
            _trayIcon?.Dispose();
            _desktop?.Shutdown();
        }
    }
}
