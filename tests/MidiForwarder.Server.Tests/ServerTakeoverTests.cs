using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using MidiForwarder.Core;

namespace MidiForwarder.Server.Tests;

public sealed class ServerTakeoverTests
{
    [Fact]
    public async Task TakeoverIsDisabledByDefaultAndBusyClientCannotInterruptMidi()
    {
        Assert.False(new ServerSettings().AllowTakeover);
        await using var setup = await ServerConnection.StartAsync(allowTakeover: false);
        using TcpClient first = await setup.ConnectAsync();
        Assert.Equal(TcpSessionStatus.Connected, await setup.StatusAsync(first));
        using TcpClient second = await setup.ConnectAsync();
        Assert.Equal(TcpSessionStatus.Busy, await setup.StatusAsync(second));
        await setup.RoundTripAsync(first);
        Assert.Equal(1, setup.OpenCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyConfirmedTakeoverReleasesTheOldPortAndTransfersBothDirections(bool confirm)
    {
        await using var setup = await ServerConnection.StartAsync(allowTakeover: true);
        using TcpClient first = await setup.ConnectAsync();
        Assert.Equal(TcpSessionStatus.Connected, await setup.StatusAsync(first));
        TestMidiPort oldPort = setup.CurrentPort!;
        using TcpClient second = await setup.ConnectAsync();
        Assert.Equal(TcpSessionStatus.TakeoverAvailable, await setup.StatusAsync(second));
        await setup.RoundTripAsync(first); // The prompt itself must not interrupt the current session.
        Assert.False(oldPort.IsDisposed);
        await TcpSession.SendDecisionAsync(second.GetStream(), confirm, setup.Timeout.Token);
        if (confirm)
        {
            Assert.Equal(TcpSessionStatus.Connected, await setup.StatusAsync(second));
            await Assert.ThrowsAsync<TcpSessionEndedException>(() => TcpMidiBridge.ReadFrameAsync(first.GetStream(), setup.Timeout.Token));
            Assert.True(oldPort.IsDisposed);
            Assert.Equal(2, setup.OpenCount);
            Assert.True(setup.PreviousPortWasReleased);
            await setup.RoundTripAsync(second);
        }
        else
        {
            Assert.Equal(0, await second.GetStream().ReadAsync(new byte[1], setup.Timeout.Token));
            await setup.RoundTripAsync(first);
            Assert.Equal(1, setup.OpenCount);
            Assert.False(oldPort.IsDisposed);
        }
    }

    [Fact]
    public async Task WrongTokenAndAnAbandonedPromptLeaveTheCurrentClientConnected()
    {
        await using var setup = await ServerConnection.StartAsync(allowTakeover: true);
        using TcpClient first = await setup.ConnectAsync();
        Assert.Equal(TcpSessionStatus.Connected, await setup.StatusAsync(first));
        using TcpClient wrong = await setup.ConnectAsync(token: "wrong-token");
        Assert.Equal(TcpSessionStatus.Unauthorized, await setup.StatusAsync(wrong));
        using (TcpClient abandoned = await setup.ConnectAsync())
        {
            Assert.Equal(TcpSessionStatus.TakeoverAvailable, await setup.StatusAsync(abandoned));
        }
        await setup.RoundTripAsync(first);
        Assert.Null(setup.Service.LastConnectionError);
        Assert.Equal(1, setup.OpenCount);
    }

    [Fact]
    public async Task StaleConfirmationCannotReplaceAClientThatConnectedAfterThePrompt()
    {
        await using var setup = await ServerConnection.StartAsync(allowTakeover: true);
        using TcpClient first = await setup.ConnectAsync();
        Assert.Equal(TcpSessionStatus.Connected, await setup.StatusAsync(first));
        using TcpClient second = await setup.ConnectAsync();
        Assert.Equal(TcpSessionStatus.TakeoverAvailable, await setup.StatusAsync(second));
        using TcpClient third = await setup.ConnectAsync();
        Assert.Equal(TcpSessionStatus.TakeoverAvailable, await setup.StatusAsync(third));
        await TcpSession.SendDecisionAsync(second.GetStream(), true, setup.Timeout.Token);
        Assert.Equal(TcpSessionStatus.Connected, await setup.StatusAsync(second));
        await TcpSession.SendDecisionAsync(third.GetStream(), true, setup.Timeout.Token);
        Assert.Equal(TcpSessionStatus.ConnectionChanged, await setup.StatusAsync(third));
        Assert.Equal(2, setup.OpenCount);
        await setup.RoundTripAsync(second);
    }

    [Fact]
    public async Task ServerStopCancelsPendingConfirmationAndReleasesMidi()
    {
        await using var setup = await ServerConnection.StartAsync(allowTakeover: true);
        using TcpClient first = await setup.ConnectAsync();
        Assert.Equal(TcpSessionStatus.Connected, await setup.StatusAsync(first));
        using TcpClient second = await setup.ConnectAsync();
        Assert.Equal(TcpSessionStatus.TakeoverAvailable, await setup.StatusAsync(second));
        await setup.Service.StopAsync().WaitAsync(setup.Timeout.Token);
        Assert.True(setup.CurrentPort!.IsDisposed);
        Assert.Null(setup.Service.ConnectedClient);
        Assert.Equal(0, await second.GetStream().ReadAsync(new byte[1], setup.Timeout.Token));
    }

    [Fact]
    public async Task LegacyClientCanForwardMidiAndBeReplacedByANewerClient()
    {
        await using var setup = await ServerConnection.StartAsync(allowTakeover: true);
        using TcpClient first = await setup.ConnectAsync(negotiateSession: false);
        while (setup.Service.ConnectedClient is null) { await Task.Delay(1, setup.Timeout.Token); }
        await setup.RoundTripAsync(first);
        using TcpClient second = await setup.ConnectAsync();
        Assert.Equal(TcpSessionStatus.TakeoverAvailable, await setup.StatusAsync(second));
        await TcpSession.SendDecisionAsync(second.GetStream(), true, setup.Timeout.Token);
        Assert.Equal(TcpSessionStatus.Connected, await setup.StatusAsync(second));
        Assert.Null(await TcpMidiBridge.ReadFrameAsync(first.GetStream(), setup.Timeout.Token));
        await setup.RoundTripAsync(second);
    }

    private sealed class ServerConnection : IAsyncDisposable
    {
        private ServerConnection(bool allowTakeover)
        {
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            Port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            Settings = new ServerSettings
            {
                MidiEndpoint = "Test device", MidiInput = "Test input", MidiOutput = "Test output",
                ListenUrl = $"tcp://127.0.0.1:{Port}", Token = "test-token", AllowTakeover = allowTakeover,
            };
            Service = new ServerRelayService(_ => true, _ =>
            {
                PreviousPortWasReleased = CurrentPort is null || CurrentPort.IsDisposed;
                OpenCount++;
                return CurrentPort = new TestMidiPort();
            });
        }

        public CancellationTokenSource Timeout { get; } = new(TimeSpan.FromSeconds(10));
        public ServerSettings Settings { get; }
        public ServerRelayService Service { get; }
        public int Port { get; }
        public int OpenCount { get; private set; }
        public TestMidiPort? CurrentPort { get; private set; }
        public bool PreviousPortWasReleased { get; private set; }

        public static async Task<ServerConnection> StartAsync(bool allowTakeover)
        {
            var setup = new ServerConnection(allowTakeover);
            await setup.Service.StartAsync(setup.Settings);
            return setup;
        }

        public async Task<TcpClient> ConnectAsync(string token = "test-token", bool negotiateSession = true)
        {
            var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(IPAddress.Loopback, Port, Timeout.Token);
            await TcpAuthentication.SendAsync(client.GetStream(), token, negotiateSession, Timeout.Token);
            return client;
        }

        public Task<TcpSessionStatus> StatusAsync(TcpClient client) => TcpSession.ReadStatusAsync(client.GetStream(), Timeout.Token);

        public async Task RoundTripAsync(TcpClient client)
        {
            TestMidiPort midi = CurrentPort!;
            var packet = new MidiPacket(MidiPacketFormat.Midi1, new byte[] { 0xc0, 42 });
            await TcpMidiBridge.WriteFrameAsync(client.GetStream(), packet, Timeout.Token);
            Assert.Equal(packet.Data.ToArray(), (await midi.Output.Reader.ReadAsync(Timeout.Token)).Data.ToArray());
            await midi.Input.Writer.WriteAsync(packet, Timeout.Token);
            Assert.Equal(packet.Data.ToArray(), (await TcpMidiBridge.ReadFrameAsync(client.GetStream(), Timeout.Token))?.Data.ToArray());
        }

        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            Timeout.Dispose();
        }
    }

    private sealed class TestMidiPort : IMidiDuplexPort
    {
        public Channel<MidiPacket> Input { get; } = Channel.CreateUnbounded<MidiPacket>();
        public Channel<MidiPacket> Output { get; } = Channel.CreateUnbounded<MidiPacket>();
        public bool IsDisposed { get; private set; }
        public string InputName => "Test input";
        public string OutputName => "Test output";
        public IAsyncEnumerable<MidiPacket> ReadAllAsync(CancellationToken cancellationToken) => Input.Reader.ReadAllAsync(cancellationToken);
        public ValueTask SendAsync(MidiPacket packet, CancellationToken cancellationToken) => Output.Writer.WriteAsync(packet, cancellationToken);
        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
