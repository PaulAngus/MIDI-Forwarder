using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.Themes.Fluent;
using System.Diagnostics.CodeAnalysis;
using MidiForwarder.Core;

namespace MidiForwarder.Server;

[SuppressMessage("Design", "CA1001", Justification = "Resources are disposed by the explicit Quit command.")]
public sealed class App : Application
{
    private ServerRelayService? _service;
    private ServerWindow? _window;
    private TrayIcon? _trayIcon;
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private Task? _quitTask;

    public override void Initialize()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception error)
            {
                DiagnosticLog.WriteException("server.log", $"Unhandled AppDomain exception. Terminating={args.IsTerminating}", error);
            }
            else
            {
                DiagnosticLog.Write("server.log", $"Unhandled non-Exception AppDomain failure. Terminating={args.IsTerminating}");
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            DiagnosticLog.WriteException("server.log", "Unobserved task exception.", args.Exception);
            args.SetObserved();
        };
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        DiagnosticLog.Write("server.log", "Avalonia framework initialization completed.");
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _service = new ServerRelayService();
            _window = new ServerWindow(_service);
            _window.QuitRequested += (_, _) => _ = QuitAsync();
            desktop.MainWindow = _window;
            CreateTrayIcon();
            Dispatcher.UIThread.Post(async () =>
            {
                if (ServerWindow.HasSavedSettings)
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
        using Stream iconStream = AssetLoader.Open(new Uri("avares://MidiForwarder.Server/Assets/midi-forwarder.ico"));
        var icon = new WindowIcon(iconStream);
        var open = new NativeMenuItem("Open Settings");
        open.Click += (_, _) => _window?.ShowConfiguration();
        var stop = new NativeMenuItem("Stop Relay");
        stop.Click += (_, _) => _ = _window?.StopAsync();
        var quit = new NativeMenuItem("Quit");
        quit.Click += (_, _) => _ = QuitAsync();
        var menu = new NativeMenu();
        menu.Items.Add(open);
        menu.Items.Add(stop);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(quit);
        _trayIcon = new TrayIcon
        {
            Icon = icon,
            ToolTipText = "MIDI Forwarder Server",
            Menu = menu,
            IsVisible = true,
        };
        _trayIcon.Clicked += (_, _) => _window?.ShowConfiguration();
        TrayIcon.SetIcons(this, new TrayIcons { _trayIcon });
    }

    private Task QuitAsync() => _quitTask ??= QuitCoreAsync();

    private async Task QuitCoreAsync()
    {
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
