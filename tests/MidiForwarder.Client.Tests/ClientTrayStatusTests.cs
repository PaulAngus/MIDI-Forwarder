using System.Net;
using System.Net.Sockets;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MidiForwarder.Core;

namespace MidiForwarder.Client.Tests;

public sealed class ClientTrayStatusTests
{
    [AvaloniaFact]
    public async Task TrayTracksConnectionLossReconnectAndStopWithoutAnOpenWindow()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        using var listener = new TcpListener(IPAddress.Loopback, port);
        await using var service = new ClientRelayService(_ => new RelayTestPort());
        using var tray = new TrayIcon();
        var stop = new NativeMenuItem("Disconnect");
        using var indicator = new TrayStatusIndicator(tray, stop, "MIDI Forwarder Client", () => App.GetTrayStatus(service));
        service.StatusChanged += indicator.OnStatusChanged;
        try
        {
            SaveIconPreviews();
            Assert.Equal("MIDI Forwarder Client: Disconnected", tray.ToolTipText);
            Assert.False(stop.IsEnabled);
            WindowIcon? inactive = tray.Icon;
            Assert.NotNull(inactive);
            var settings = new ClientSettings
            {
                InterfaceName = "Tray test", MidiInput = "From app", MidiOutput = "To app",
                ServerUri = $"tcp://127.0.0.1:{port}", Token = "test-token",
            };

            await service.StartAsync(settings);
            Assert.Equal(TrayConnectionState.Connecting, App.GetTrayStatus(service).State);
            Assert.True(stop.IsEnabled);
            WindowIcon? connecting = tray.Icon;
            Assert.NotSame(inactive, connecting);

            listener.Start();
            using TcpClient peer = await listener.AcceptTcpClientAsync(timeout.Token);
            Assert.True(await TcpAuthentication.ReceiveAndValidateAsync(peer.GetStream(), settings.Token, timeout.Token));
            await TcpSession.WriteStatusAsync(peer.GetStream(), TcpSessionStatus.Connected, timeout.Token);
            await WaitForTooltipAsync(tray, "MIDI Forwarder Client: Connected", timeout.Token);
            Assert.Equal(TrayConnectionState.Active, App.GetTrayStatus(service).State);
            WindowIcon? connected = tray.Icon;
            Assert.NotSame(connecting, connected);

            peer.Dispose();
            await WaitForTooltipAsync(tray, "MIDI Forwarder Client: Reconnecting in 1s", timeout.Token);
            Assert.Equal(TrayConnectionState.Connecting, App.GetTrayStatus(service).State);
            Assert.Same(connecting, tray.Icon);
            Assert.True(stop.IsEnabled);

            using TcpClient nextPeer = await listener.AcceptTcpClientAsync(timeout.Token);
            Assert.True(await TcpAuthentication.ReceiveAndValidateAsync(nextPeer.GetStream(), settings.Token, timeout.Token));
            await TcpSession.WriteStatusAsync(nextPeer.GetStream(), TcpSessionStatus.Connected, timeout.Token);
            await WaitForTooltipAsync(tray, "MIDI Forwarder Client: Connected", timeout.Token);
            Assert.Same(connected, tray.Icon);

            await Task.Run(service.StopAsync, timeout.Token);
            await WaitForTooltipAsync(tray, "MIDI Forwarder Client: Disconnected", timeout.Token);
            Assert.Same(inactive, tray.Icon);
            Assert.False(stop.IsEnabled);
        }
        finally
        {
            service.StatusChanged -= indicator.OnStatusChanged;
        }
    }

    [AvaloniaFact]
    public async Task QueuedUpdatesUseLatestStateAndAreIgnoredAfterDisposal()
    {
        using var tray = new TrayIcon();
        var stop = new NativeMenuItem("Disconnect");
        var status = new TrayStatus(TrayConnectionState.Inactive, "Disconnected");
        int reads = 0;
        using var indicator = new TrayStatusIndicator(tray, stop, "Client", () => { reads++; return status; });
        WindowIcon? inactive = tray.Icon;

        // Queue a network-thread event, then stop before the UI processes it.
        status = new(TrayConnectionState.Active, "Connected");
        NotifyFromNetworkThread(indicator);
        status = new(TrayConnectionState.Inactive, "Disconnected");
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.Same(inactive, tray.Icon);
        Assert.Equal("Client: Disconnected", tray.ToolTipText);

        NotifyFromNetworkThread(indicator);
        indicator.Dispose();
        int readsBeforeShutdown = reads;
        status = new(TrayConnectionState.Active, "Connected");
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.Equal(readsBeforeShutdown, reads);
        Assert.Same(inactive, tray.Icon);
        Assert.False(stop.IsEnabled);
    }

    private static async Task WaitForTooltipAsync(TrayIcon tray, string expected, CancellationToken cancellationToken)
    {
        while (tray.ToolTipText != expected) { await Task.Delay(10, cancellationToken); }
    }

    private static void NotifyFromNetworkThread(TrayStatusIndicator indicator)
    {
        var worker = new Thread(() => indicator.OnStatusChanged(null, "Connected"));
        worker.Start();
        worker.Join();
    }

    private static void SaveIconPreviews()
    {
        string? output = Environment.GetEnvironmentVariable("MIDI_FORWARDER_UI_SNAPSHOTS");
        if (string.IsNullOrWhiteSpace(output)) { return; }
        Directory.CreateDirectory(output);
        foreach (TrayConnectionState state in Enum.GetValues<TrayConnectionState>())
        {
            using var bitmap = TrayStatusIndicator.RenderIcon(state);
            bitmap.Save(Path.Combine(output, $"tray-{state}.png"));
        }
    }
}
