using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using MidiForwarder.Core;
using MidiForwarder.Midi.WindowsServices;

namespace MidiForwarder.Core.Tests;

public sealed class MidiDeviceGatewayTests
{
    private static MidiPacket Note(uint value = 0x20903c40) => MidiPacket.FromUmpWords([value]);
    private static MidiPacket[] Fragments(byte data) => UmpMidi1Codec.Encode(
        [0xf0, 0x7d, data, data, data, data, data, data, data, 0xf7]).Select(words => MidiPacket.FromUmpWords(words)).ToArray();
    private static MidiPacket Complete(byte data)
    {
        var assembler = new UmpSysExAssembler();
        MidiPacket? result = null;
        foreach (MidiPacket packet in Fragments(data)) { result = assembler.Accept(packet); }
        return result!.Value;
    }

    [Fact]
    public async Task SeparateLocalPortsKeepInterleavedSysExInTheSameGroupSeparate()
    {
        var device = new TestPort();
        await using var gateway = new MidiDeviceGateway(device);
        var a = new TestPort(); var b = new TestPort();
        gateway.AddLocalPort("a", "App A", a); gateway.AddLocalPort("b", "App B", b);
        var fragmentsA = Fragments(1); var fragmentsB = Fragments(2);
        a.Input.Publish(fragmentsA[0]); b.Input.Publish(fragmentsB[0]);
        b.Input.Publish(fragmentsB[1]); a.Input.Publish(fragmentsA[1]);
        MidiPacket first = await device.TakeAsync(); MidiPacket second = await device.TakeAsync();
        Assert.Equal(MidiPacketFormat.UniversalMidiPacketBatch, first.Format);
        Assert.Equal(MidiPacketFormat.UniversalMidiPacketBatch, second.Format);
        Assert.Equal(new byte[] { 1, 2 }, new[] { UmpMidi1Codec.DecodeSysEx7Batch(first)[2], UmpMidi1Codec.DecodeSysEx7Batch(second)[2] }.Order().ToArray());
    }

    [Fact]
    public async Task OneWriterSerializesCompleteMessagesFromCompetingNetworkConnections()
    {
        var device = new TestPort { HoldFirstSend = true };
        await using var gateway = new MidiDeviceGateway(device);
        await using var a = gateway.AddNetworkConnection("a", "App A");
        await using var b = gateway.AddNetworkConnection("b", "App B");
        Task first = a.SendAsync(Complete(1), default).AsTask();
        await device.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task second = b.SendAsync(Complete(2), default).AsTask();
        Assert.False(second.IsCompleted);
        device.ReleaseSend.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, device.MaximumConcurrentSends);
        Assert.Equal(1, UmpMidi1Codec.DecodeSysEx7Batch(await device.TakeAsync())[2]);
        Assert.Equal(2, UmpMidi1Codec.DecodeSysEx7Batch(await device.TakeAsync())[2]);
    }

    [Fact]
    public async Task ReservationWaitsForCurrentSendAndClosesOtherNetworkSessions()
    {
        var device = new TestPort { HoldFirstSend = true };
        await using var gateway = new MidiDeviceGateway(device);
        await using var a = gateway.AddNetworkConnection("a", "App A");
        await using var b = gateway.AddNetworkConnection("b", "App B");
        Task first = a.SendAsync(Complete(1), default).AsTask();
        await device.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task reservation = gateway.ReserveAsync("a");
        Task queued = b.SendAsync(Complete(2), default).AsTask();
        Assert.False(reservation.IsCompleted);
        device.ReleaseSend.TrySetResult();
        await first; await reservation;
        await Assert.ThrowsAsync<IOException>(() => queued);
        Assert.Equal("a", gateway.OwnerId);
        Assert.Throws<GatewayUnavailableException>(() => gateway.AddNetworkConnection("c", "App C"));
        await using var reader = b.ReadAllAsync(default).GetAsyncEnumerator();
        Assert.False(await reader.MoveNextAsync());
        await gateway.ReserveAsync(null);
        await using var c = gateway.AddNetworkConnection("c", "App C");
        await c.SendAsync(Complete(3), default);
        Assert.Equal(1, UmpMidi1Codec.DecodeSysEx7Batch(await device.TakeAsync())[2]);
        Assert.Equal(3, UmpMidi1Codec.DecodeSysEx7Batch(await device.TakeAsync())[2]);
        Assert.False(device.Sent.Reader.TryRead(out _));
    }

    [Fact]
    public async Task PausedLocalTrafficAndPartialSysExNeverReplayOnReleaseAndRepliesGoToOwner()
    {
        var device = new TestPort();
        await using var gateway = new MidiDeviceGateway(device);
        var a = new TestPort(); var b = new TestPort();
        gateway.AddLocalPort("a", "App A", a); gateway.AddLocalPort("b", "App B", b);
        var old = Fragments(2); b.Input.Publish(old[0]);
        await gateway.ReserveAsync("a");
        b.Input.Publish(old[1]); b.Input.Publish(Note());
        device.Input.Publish(Note(0x20904041));
        Assert.Equal(0x20904041u, (await a.TakeAsync()).ToUmpWords()[0]);
        Assert.False(b.Sent.Reader.TryRead(out _));
        Assert.Equal(2, b.Input.Statistics.OfflinePackets);
        await gateway.ReserveAsync(null);
        b.Input.Publish(old[1]); // An orphaned tail cannot complete the abandoned message.
        b.Input.Publish(Complete(3));
        Assert.Equal(3, UmpMidi1Codec.DecodeSysEx7Batch(await device.TakeAsync())[2]);
        Assert.False(device.Sent.Reader.TryRead(out _));
        device.Input.Publish(Note(0x20904142));
        Assert.Equal(0x20904142u, (await a.TakeAsync()).ToUmpWords()[0]);
        Assert.Equal(0x20904142u, (await b.TakeAsync()).ToUmpWords()[0]);
    }

    [Fact]
    public async Task OwnerDisconnectReleasesReservationAndLocalPortsResume()
    {
        var device = new TestPort();
        await using var gateway = new MidiDeviceGateway(device);
        var local = new TestPort(); gateway.AddLocalPort("local", "Local app", local);
        var remote = gateway.AddNetworkConnection("remote", "Remote app");
        await gateway.ReserveAsync("remote");
        local.Input.Publish(Note(0x20903031));
        await remote.DisposeAsync();
        Assert.Null(gateway.OwnerId);
        Assert.All(gateway.Connections, connection => Assert.False(connection.IsPaused));
        local.Input.Publish(Note(0x20903233));
        Assert.Equal(0x20903233u, (await device.TakeAsync()).ToUmpWords()[0]);
        Assert.False(device.Sent.Reader.TryRead(out _));
    }

    [Fact]
    public async Task FailedPhysicalSendEndsAllRoutesInsteadOfSendingLaterMessages()
    {
        var device = new TestPort { FailSend = true };
        await using var gateway = new MidiDeviceGateway(device);
        await using var a = gateway.AddNetworkConnection("a", "App A");
        await using var b = gateway.AddNetworkConnection("b", "App B");
        await Assert.ThrowsAsync<IOException>(async () => await a.SendAsync(Complete(1), default));
        await using var reader = b.ReadAllAsync(default).GetAsyncEnumerator();
        Assert.False(await reader.MoveNextAsync());
        Assert.Throws<IOException>(() => gateway.AddNetworkConnection("c", "App C"));
    }

    [Fact]
    public async Task PausingReceiveBufferKeepsReaderAliveAndDiscardsQueuedData()
    {
        var buffer = new MidiReceiveBuffer();
        buffer.Publish(Note(0x20903031)); buffer.SetPaused(true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var reader = buffer.ReadAllAsync(timeout.Token).GetAsyncEnumerator();
        Task<bool> waiting = reader.MoveNextAsync().AsTask();
        Assert.False(waiting.IsCompleted);
        buffer.Publish(Note(0x20903233));
        buffer.SetPaused(false); buffer.Publish(Note(0x20903435));
        Assert.True(await waiting);
        Assert.Equal(0x20903435u, reader.Current.ToUmpWords()[0]);
        buffer.Close();
    }

    [Fact]
    public async Task BusyHandshakeReportsTemporaryReservationRatherThanVersionMismatch()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var client = new TcpClient();
        Task connect = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using TcpClient server = await listener.AcceptTcpClientAsync(); await connect;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        Task auth = TcpAuthentication.AuthenticateAsync(client.GetStream(), "token", timeout.Token);
        Assert.True(await TcpAuthentication.ReceiveAndValidateAsync(server.GetStream(), "token", timeout.Token));
        await TcpAuthentication.RejectBusyAsync(server.GetStream(), timeout.Token);
        await Assert.ThrowsAsync<GatewayUnavailableException>(() => auth);
    }

    private sealed class TestPort : IMidiDuplexPort, IMidiSessionPort, IMidiFlowControlPort
    {
        public MidiReceiveBuffer Input { get; } = new();
        public Channel<MidiPacket> Sent { get; } = Channel.CreateUnbounded<MidiPacket>();
        public TaskCompletionSource SendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSend { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HoldFirstSend { get; init; }
        public bool FailSend { get; init; }
        private int _sends, _concurrent;
        public int MaximumConcurrentSends { get; private set; }
        public string InputName => "Test port";
        public string OutputName => InputName;
        public MidiQueueStatistics ReceiveStatistics => Input.Statistics;
        public IAsyncEnumerable<MidiPacket> ReadAllAsync(CancellationToken token) => Input.ReadAllAsync(token);
        public void BeginSession() => Input.BeginSession();
        public void EndSession() => Input.EndSession();
        public void SetPaused(bool paused) => Input.SetPaused(paused);
        public async ValueTask SendAsync(MidiPacket packet, CancellationToken token)
        {
            int active = Interlocked.Increment(ref _concurrent);
            MaximumConcurrentSends = Math.Max(MaximumConcurrentSends, active);
            try
            {
                if (Interlocked.Increment(ref _sends) == 1)
                {
                    SendStarted.TrySetResult();
                    if (HoldFirstSend) { await ReleaseSend.Task.WaitAsync(token); }
                }
                if (FailSend) { throw new IOException("Simulated physical send failure."); }
                await Sent.Writer.WriteAsync(packet, token);
            }
            finally { Interlocked.Decrement(ref _concurrent); }
        }
        public async Task<MidiPacket> TakeAsync() => await Sent.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        public ValueTask DisposeAsync() { Input.Close(); Sent.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
}
