using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using MidiForwarder.Core;

namespace MidiForwarder.Client.Tests;

public sealed class ClientRelayServiceTests
{
    [Fact]
    public async Task TracksBothDirectionsRetainsDisconnectedTotalsAndResetsOnReconnect()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var midi = new RelayTestPort();
        await using var service = new ClientRelayService(_ => midi);
        var states = Channel.CreateUnbounded<ConnectionObservation>();
        var logs = new ConcurrentQueue<string>();
        service.StatusChanged += (_, status) => states.Writer.TryWrite(new(status, service.IsConnected, service.Traffic.GetSnapshot()));
        service.LogMessage += (_, message) => logs.Enqueue(message);
        var settings = new ClientSettings
        {
            InterfaceName = "Test MIDI interface", MidiInput = "From app", MidiOutput = "To app",
            ServerUri = $"tcp://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}", Token = "test-token",
        };

        await service.StartAsync(settings);
        using TcpClient peer = await listener.AcceptTcpClientAsync(timeout.Token);
        Assert.True(await TcpAuthentication.ReceiveAndValidateAsync(peer.GetStream(), settings.Token, timeout.Token));
        ConnectionObservation connected = await WaitForStateAsync(states.Reader, "Connected", timeout.Token);
        Assert.True(connected.Connected);

        var toServer = new MidiPacket(MidiPacketFormat.Midi1, new byte[] { 0xc0, 5 });
        var toApp = new MidiPacket(MidiPacketFormat.Midi1, new byte[] { 0x90, 60, 100 });
        await midi.Input.Writer.WriteAsync(toServer, timeout.Token);
        await TcpMidiBridge.WriteFrameAsync(peer.GetStream(), toApp, timeout.Token);
        MidiPacket? received = await TcpMidiBridge.ReadFrameAsync(peer.GetStream(), timeout.Token);
        Assert.Equal(toServer.Data.ToArray(), received?.Data.ToArray());
        Assert.Equal(toApp.Data.ToArray(), (await midi.Output.Reader.ReadAsync(timeout.Token)).Data.ToArray());
        while (service.Traffic.GetSnapshot().MidiToNetwork.Messages != 1 || service.Traffic.GetSnapshot().NetworkToMidi.Messages != 1)
        {
            await Task.Delay(1, timeout.Token);
        }

        peer.Dispose();
        ConnectionObservation retrying = await WaitForStateAsync(states.Reader, "Reconnecting in 1s", timeout.Token);
        Assert.False(retrying.Connected);
        Assert.Equal(1, retrying.Traffic.MidiToNetwork.Messages);
        Assert.Equal(2, retrying.Traffic.MidiToNetwork.Bytes);
        Assert.Equal(1, retrying.Traffic.NetworkToMidi.Messages);
        Assert.Equal(3, retrying.Traffic.NetworkToMidi.Bytes);
        Assert.Contains(logs, message => message.StartsWith("MIDI -> network:", StringComparison.Ordinal));
        Assert.Contains(logs, message => message.StartsWith("Network -> MIDI:", StringComparison.Ordinal));

        using TcpClient nextPeer = await listener.AcceptTcpClientAsync(timeout.Token);
        Assert.True(await TcpAuthentication.ReceiveAndValidateAsync(nextPeer.GetStream(), settings.Token, timeout.Token));
        ConnectionObservation reconnected = await WaitForStateAsync(states.Reader, "Connected", timeout.Token);
        Assert.True(reconnected.Connected);
        Assert.Equal(default, reconnected.Traffic);
        Assert.Null(service.LastConnectionError);
        await service.StopAsync();
        Assert.False(service.IsRunning);
        Assert.False(service.IsConnected);
        Assert.Equal("Stopped", service.ConnectionStatus);
        Assert.True(midi.IsDisposed);
    }

    private static async Task<ConnectionObservation> WaitForStateAsync(
        ChannelReader<ConnectionObservation> states, string expected, CancellationToken cancellationToken)
    {
        while (true)
        {
            ConnectionObservation observation = await states.ReadAsync(cancellationToken);
            if (observation.Status == expected) { return observation; }
        }
    }

    private sealed record ConnectionObservation(string Status, bool Connected, MidiTrafficSnapshot Traffic);
}
