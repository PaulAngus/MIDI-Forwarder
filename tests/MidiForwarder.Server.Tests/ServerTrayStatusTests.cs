using System.Net;
using System.Net.Sockets;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using MidiForwarder.Core;

namespace MidiForwarder.Server.Tests;

public sealed class ServerTrayStatusTests
{
    [AvaloniaFact]
    public async Task TrayShowsListeningWithoutAClientAndResetsOnStop()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        await using var service = new ServerRelayService(_ => true);
        using var tray = new TrayIcon();
        var stop = new NativeMenuItem("Stop Relay");
        using var indicator = new TrayStatusIndicator(tray, stop, "MIDI Forwarder Server", () => App.GetTrayStatus(service));
        service.StatusChanged += indicator.OnStatusChanged;
        try
        {
            Assert.Equal("MIDI Forwarder Server: Stopped", tray.ToolTipText);
            Assert.False(stop.IsEnabled);
            WindowIcon? inactive = tray.Icon;
            Assert.NotNull(inactive);
            var settings = new ServerSettings
            {
                MidiEndpoint = "Test endpoint", MidiInput = "From device", MidiOutput = "To device",
                ListenUrl = $"tcp://127.0.0.1:{port}", Token = "test-token",
            };

            // A bind failure must not display a listening badge.
            await Assert.ThrowsAsync<SocketException>(() => service.StartAsync(settings));
            Assert.Same(inactive, tray.Icon);
            Assert.False(stop.IsEnabled);
            reservation.Stop();
            await service.StartAsync(settings);
            Assert.Equal("MIDI Forwarder Server: Listening", tray.ToolTipText);
            Assert.Equal(TrayConnectionState.Active, App.GetTrayStatus(service).State);
            Assert.Null(service.ConnectedClient);
            Assert.True(stop.IsEnabled);
            WindowIcon? listening = tray.Icon;
            Assert.NotSame(inactive, listening);

            // Rejecting a client does not stop the listener or turn off its green badge.
            using var peer = new TcpClient();
            await peer.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            await TcpAuthentication.SendAsync(peer.GetStream(), "wrong-token", timeout.Token);
            byte[] buffer = new byte[1];
            Assert.Equal(0, await peer.GetStream().ReadAsync(buffer, timeout.Token));
            Assert.NotNull(service.LastConnectionError);
            Assert.True(service.IsRunning);
            Assert.Equal("MIDI Forwarder Server: Listening", tray.ToolTipText);
            Assert.Same(listening, tray.Icon);

            await Task.Run(service.StopAsync, timeout.Token);
            while (tray.ToolTipText != "MIDI Forwarder Server: Stopped") { await Task.Delay(10, timeout.Token); }
            Assert.Same(inactive, tray.Icon);
            Assert.False(stop.IsEnabled);
        }
        finally
        {
            service.StatusChanged -= indicator.OnStatusChanged;
        }
    }
}
