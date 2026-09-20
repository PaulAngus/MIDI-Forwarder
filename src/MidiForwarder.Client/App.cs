using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.Themes.Fluent;
using System.Diagnostics.CodeAnalysis;

namespace MidiForwarder.Client;

[SuppressMessage("Design", "CA1001", Justification = "Resources are disposed by the explicit Exit command.")]
public sealed class App : Application
{
    private ClientRelayService? _service;
    private ClientWindow? _window;
    private TrayIcon? _trayIcon;
    private IClassicDesktopStyleApplicationLifetime? _desktop;

    public override void Initialize()
    {
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
            desktop.MainWindow = _window;
            CreateTrayIcon();
            Dispatcher.UIThread.Post(async () =>
            {
                if (ClientWindow.HasSavedSettings)
                {
                    await _window.AutoStartAsync().ConfigureAwait(true);
                }
                else
                {
                    _window.ShowConfiguration();
                }
            });
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void CreateTrayIcon()
    {
        using Stream iconStream = AssetLoader.Open(new Uri("avares://MidiForwarder.Client/Assets/midi-forwarder.ico"));
        var icon = new WindowIcon(iconStream);
        var open = new NativeMenuItem("Open Settings");
        open.Click += (_, _) => _window?.ShowConfiguration();
        var stop = new NativeMenuItem("Disconnect");
        stop.Click += (_, _) => _ = _window?.StopAsync();
        var exit = new NativeMenuItem("Exit");
        exit.Click += (_, _) => _ = ExitAsync();
        var menu = new NativeMenu();
        menu.Items.Add(open);
        menu.Items.Add(stop);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exit);
        _trayIcon = new TrayIcon
        {
            Icon = icon,
            ToolTipText = "MIDI Forwarder Client",
            Menu = menu,
            IsVisible = true,
        };
        _trayIcon.Clicked += (_, _) => _window?.ShowConfiguration();
        TrayIcon.SetIcons(this, new TrayIcons { _trayIcon });
    }

    private async Task ExitAsync()
    {
        if (_window is not null)
        {
            _window.AllowClose();
        }

        if (_service is not null)
        {
            await _service.DisposeAsync().ConfigureAwait(true);
        }

        _trayIcon?.Dispose();
        _desktop?.Shutdown();
    }
}
