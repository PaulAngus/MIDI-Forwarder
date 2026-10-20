using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using MidiForwarder.Core;

namespace MidiForwarder.Client.Tests;

public sealed class ClientTakeoverTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BusyServerRequiresExplicitConfirmationBeforeConnecting(bool confirm)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var midi = new RelayTestPort();
        await using var service = new ClientRelayService(_ => midi);
        var statuses = Channel.CreateUnbounded<string>();
        service.StatusChanged += (_, status) => statuses.Writer.TryWrite(status);
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int prompts = 0;
        service.ConfirmTakeoverAsync = _ => { prompts++; return decision.Task; };
        await service.StartAsync(Settings(listener));
        using TcpClient peer = await listener.AcceptTcpClientAsync(timeout.Token);
        Assert.Equal((true, true), await TcpAuthentication.ReceiveRequestAsync(peer.GetStream(), "test-token", timeout.Token));
        await TcpSession.WriteStatusAsync(peer.GetStream(), TcpSessionStatus.TakeoverAvailable, timeout.Token);
        await WaitForStatusAsync(statuses.Reader, "Awaiting takeover confirmation", timeout.Token);
        Assert.False(service.IsConnected);
        // Queue MIDI while the prompt is open: it must wait for a successful admission response.
        var packet = new MidiPacket(MidiPacketFormat.Midi1, new byte[] { 0xc0, 7 });
        await midi.Input.Writer.WriteAsync(packet, timeout.Token);
        Assert.False(peer.GetStream().DataAvailable);
        decision.SetResult(confirm);
        Assert.Equal(confirm, await TcpSession.ReadDecisionAsync(peer.GetStream(), timeout.Token));
        if (confirm)
        {
            Assert.False(service.IsConnected);
            await TcpSession.WriteStatusAsync(peer.GetStream(), TcpSessionStatus.Connected, timeout.Token);
            await WaitForStatusAsync(statuses.Reader, "Connected", timeout.Token);
            Assert.Equal(packet.Data.ToArray(), (await TcpMidiBridge.ReadFrameAsync(peer.GetStream(), timeout.Token))?.Data.ToArray());
        }
        else
        {
            await WaitForStatusAsync(statuses.Reader, "Paused", timeout.Token);
            Assert.True(service.IsPaused);
            Assert.False(service.IsConnected);
            Assert.Contains("cancelled", service.LastConnectionError);
        }
        Assert.Equal(1, prompts);
    }

    [Fact]
    public async Task ReplacedClientPausesAndDoesNotAskToReclaimTheConnection()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var service = new ClientRelayService(_ => new RelayTestPort());
        var statuses = Channel.CreateUnbounded<string>();
        service.StatusChanged += (_, status) => statuses.Writer.TryWrite(status);
        int prompts = 0;
        service.ConfirmTakeoverAsync = _ => { prompts++; return Task.FromResult(true); };
        await service.StartAsync(Settings(listener));
        using TcpClient peer = await listener.AcceptTcpClientAsync(timeout.Token);
        Assert.True(await TcpAuthentication.ReceiveAndValidateAsync(peer.GetStream(), "test-token", timeout.Token));
        await TcpSession.WriteStatusAsync(peer.GetStream(), TcpSessionStatus.Connected, timeout.Token);
        await WaitForStatusAsync(statuses.Reader, "Connected", timeout.Token);
        await TcpSession.WriteStatusAsync(peer.GetStream(), TcpSessionStatus.Replaced, timeout.Token);
        await WaitForStatusAsync(statuses.Reader, "Paused", timeout.Token);
        Assert.True(service.IsPaused);
        Assert.False(service.IsConnected);
        Assert.Contains("Another client took over", service.LastConnectionError);
        Assert.Equal(0, prompts);
        await service.StopAsync();
        Assert.False(service.IsPaused);
    }

    [Fact]
    public async Task LostConnectionCannotPromptTheOldClientToReclaimTheServer()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var service = new ClientRelayService(_ => new RelayTestPort());
        var statuses = Channel.CreateUnbounded<string>();
        service.StatusChanged += (_, status) => statuses.Writer.TryWrite(status);
        int prompts = 0;
        service.ConfirmTakeoverAsync = _ => { prompts++; return Task.FromResult(true); };
        await service.StartAsync(Settings(listener));
        using (TcpClient first = await listener.AcceptTcpClientAsync(timeout.Token))
        {
            Assert.True(await TcpAuthentication.ReceiveAndValidateAsync(first.GetStream(), "test-token", timeout.Token));
            await TcpSession.WriteStatusAsync(first.GetStream(), TcpSessionStatus.Connected, timeout.Token);
            await WaitForStatusAsync(statuses.Reader, "Connected", timeout.Token);
        }
        // Covers a lost takeover notice as well as a dropped connection now owned by another client.
        using TcpClient retry = await listener.AcceptTcpClientAsync(timeout.Token);
        Assert.True(await TcpAuthentication.ReceiveAndValidateAsync(retry.GetStream(), "test-token", timeout.Token));
        await TcpSession.WriteStatusAsync(retry.GetStream(), TcpSessionStatus.TakeoverAvailable, timeout.Token);
        Assert.False(await TcpSession.ReadDecisionAsync(retry.GetStream(), timeout.Token));
        await WaitForStatusAsync(statuses.Reader, "Paused", timeout.Token);
        Assert.True(service.IsPaused);
        Assert.Equal(0, prompts);
    }

    [Theory]
    [InlineData(TcpSessionStatus.Busy)]
    [InlineData(TcpSessionStatus.ConnectionChanged)]
    public async Task RejectedAdmissionPausesWithoutPromptingOrReportingConnected(TcpSessionStatus response)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var service = new ClientRelayService(_ => new RelayTestPort());
        var statuses = Channel.CreateUnbounded<string>();
        service.StatusChanged += (_, status) => statuses.Writer.TryWrite(status);
        int prompts = 0;
        service.ConfirmTakeoverAsync = _ => { prompts++; return Task.FromResult(true); };
        await service.StartAsync(Settings(listener));
        using TcpClient peer = await listener.AcceptTcpClientAsync(timeout.Token);
        Assert.True(await TcpAuthentication.ReceiveAndValidateAsync(peer.GetStream(), "test-token", timeout.Token));
        await TcpSession.WriteStatusAsync(peer.GetStream(), response, timeout.Token);
        await WaitForStatusAsync(statuses.Reader, "Paused", timeout.Token);
        Assert.True(service.IsPaused);
        Assert.False(service.IsConnected);
        Assert.NotNull(service.LastConnectionError);
        Assert.Equal(0, prompts);
    }

    [Fact]
    public async Task DisconnectCancelsAnOutstandingTakeoverPrompt()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var midi = new RelayTestPort();
        await using var service = new ClientRelayService(_ => midi);
        var prompted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken promptToken = default;
        service.ConfirmTakeoverAsync = token =>
        {
            promptToken = token;
            prompted.TrySetResult();
            return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        };
        await service.StartAsync(Settings(listener));
        using TcpClient peer = await listener.AcceptTcpClientAsync(timeout.Token);
        Assert.True(await TcpAuthentication.ReceiveAndValidateAsync(peer.GetStream(), "test-token", timeout.Token));
        await TcpSession.WriteStatusAsync(peer.GetStream(), TcpSessionStatus.TakeoverAvailable, timeout.Token);
        await prompted.Task.WaitAsync(timeout.Token);
        await service.StopAsync().WaitAsync(timeout.Token);
        Assert.True(promptToken.IsCancellationRequested);
        Assert.True(midi.IsDisposed);
        Assert.False(service.IsRunning);
        Assert.False(service.IsConnected);
    }

    [Fact]
    public async Task OlderServerFallsBackToOriginalAuthenticationAndMidiFraming()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var midi = new RelayTestPort();
        await using var service = new ClientRelayService(_ => midi);
        var statuses = Channel.CreateUnbounded<string>();
        service.StatusChanged += (_, status) => statuses.Writer.TryWrite(status);
        await service.StartAsync(Settings(listener));
        using (TcpClient probe = await listener.AcceptTcpClientAsync(timeout.Token))
        {
            Assert.Equal((true, true), await TcpAuthentication.ReceiveRequestAsync(probe.GetStream(), "test-token", timeout.Token));
        }
        using TcpClient legacy = await listener.AcceptTcpClientAsync(timeout.Token);
        Assert.Equal((true, false), await TcpAuthentication.ReceiveRequestAsync(legacy.GetStream(), "test-token", timeout.Token));
        await WaitForStatusAsync(statuses.Reader, "Connected", timeout.Token);
        var packet = new MidiPacket(MidiPacketFormat.Midi1, new byte[] { 0xc0, 9 });
        await midi.Input.Writer.WriteAsync(packet, timeout.Token);
        Assert.Equal(packet.Data.ToArray(), (await TcpMidiBridge.ReadFrameAsync(legacy.GetStream(), timeout.Token))?.Data.ToArray());
    }

    private static ClientSettings Settings(TcpListener listener) => new()
    {
        InterfaceName = "Test MIDI interface", MidiInput = "From app", MidiOutput = "To app",
        ServerUri = $"tcp://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}", Token = "test-token",
    };

    private static async Task WaitForStatusAsync(ChannelReader<string> statuses, string expected, CancellationToken cancellationToken)
    {
        while (await statuses.ReadAsync(cancellationToken) != expected) { }
    }
}
