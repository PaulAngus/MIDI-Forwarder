using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using MidiForwarder.Core;

namespace MidiForwarder.Core.Tests;

public sealed class TcpMidiBridgeTests
{
    [Fact]
    public async Task RoundTripsLengthPrefixedFrame()
    {
        var expected = new MidiPacket(MidiPacketFormat.Midi1, new byte[] { 0xf0, 0x00, 0x01, 0x74, 0x10, 0xf7 });
        using var stream = new MemoryStream();

        await TcpMidiBridge.WriteFrameAsync(stream, expected, CancellationToken.None);
        stream.Position = 0;
        MidiPacket? actual = await TcpMidiBridge.ReadFrameAsync(stream, CancellationToken.None);

        Assert.Equal(expected.Format, actual?.Format);
        Assert.Equal(expected.Data.ToArray(), actual?.Data.ToArray());
    }

    [Fact]
    public async Task ReturnsNullAtCleanEndOfStream()
    {
        using var stream = new MemoryStream();
        MidiPacket? actual = await TcpMidiBridge.ReadFrameAsync(stream, CancellationToken.None);
        Assert.Null(actual);
    }

    [Fact]
    public async Task RoundTripsMidi2UmpFrameWithoutChangingGroupOrChannel()
    {
        var expected = MidiPacket.FromUmpWords([0x43913c00, 0x7fffffff]);
        using var stream = new MemoryStream();

        await TcpMidiBridge.WriteFrameAsync(stream, expected, CancellationToken.None);
        stream.Position = 0;
        MidiPacket? actual = await TcpMidiBridge.ReadFrameAsync(stream, CancellationToken.None);

        Assert.Equal(expected.Format, actual?.Format);
        Assert.Equal(expected.Data.ToArray(), actual?.Data.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReportsSingleMessagesInBothDirectionsWhileIdleOrWhenCancelled(bool cancelBeforeReport)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var peer = new TcpClient();
        Task connect = peer.ConnectAsync((IPEndPoint)listener.LocalEndpoint, timeout.Token).AsTask();
        using TcpClient relay = await listener.AcceptTcpClientAsync(timeout.Token);
        await connect;
        await using var midi = new TestMidiPort();
        var logs = Channel.CreateUnbounded<string>();
        var traffic = new MidiTrafficMonitor();
        Task bridge = TcpMidiBridge.RunAsync(relay.GetStream(), midi, message => logs.Writer.TryWrite(message), sessionCancellation.Token, traffic);

        try
        {
            var fromMidi = new MidiPacket(MidiPacketFormat.Midi1, new byte[] { 0xc0, 5 });
            var fromNetwork = new MidiPacket(MidiPacketFormat.Midi1, new byte[] { 0x90, 60, 100 });
            await midi.Input.Writer.WriteAsync(fromMidi, timeout.Token);
            await TcpMidiBridge.WriteFrameAsync(peer.GetStream(), fromNetwork, timeout.Token);

            MidiPacket? receivedByPeer = await TcpMidiBridge.ReadFrameAsync(peer.GetStream(), timeout.Token);
            MidiPacket receivedByMidi = await midi.Output.Reader.ReadAsync(timeout.Token);
            Assert.Equal(fromMidi.Data.ToArray(), receivedByPeer?.Data.ToArray());
            Assert.Equal(fromNetwork.Data.ToArray(), receivedByMidi.Data.ToArray());

            if (cancelBeforeReport)
            {
                await sessionCancellation.CancelAsync();
                await bridge.WaitAsync(timeout.Token);
            }

            string first = await logs.Reader.ReadAsync(timeout.Token);
            string second = await logs.Reader.ReadAsync(timeout.Token);
            Assert.Contains("MIDI -> network: 1 message(s), 2 byte(s)", new[] { first, second });
            Assert.Contains("Network -> MIDI: 1 message(s), 3 byte(s)", new[] { first, second });
            Assert.Equal(1, traffic.GetSnapshot().MidiToNetwork.Messages);
            Assert.Equal(2, traffic.GetSnapshot().MidiToNetwork.Bytes);
            Assert.Equal(1, traffic.GetSnapshot().NetworkToMidi.Messages);
            Assert.Equal(3, traffic.GetSnapshot().NetworkToMidi.Bytes);
            if (!cancelBeforeReport)
            {
                Assert.False(bridge.IsCompleted);
            }
        }
        finally
        {
            await sessionCancellation.CancelAsync();
            await bridge.WaitAsync(timeout.Token);
        }

        Assert.False(logs.Reader.TryRead(out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreservesMixedTrafficWhenCaptureStartsAndStopsDuringConnection(bool enableCapture)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var peer = new TcpClient();
        Task connect = peer.ConnectAsync((IPEndPoint)listener.LocalEndpoint, timeout.Token).AsTask();
        using TcpClient relay = await listener.AcceptTcpClientAsync(timeout.Token);
        await connect;
        await using var midi = new TestMidiPort();
        await using var capture = new MidiCapture();
        Task bridge = TcpMidiBridge.RunAsync(relay.GetStream(), midi, null, sessionCancellation.Token, capture: capture);
        var captureStream = new MemoryStream();
        try
        {
            if (enableCapture) { capture.Start(captureStream, "test.jsonl", "server"); }
            byte[] bulk = Enumerable.Repeat((byte)0x55, 70000).ToArray();
            bulk[0] = 0xf0; bulk[^1] = 0xf7;
            MidiPacket[] messages = [new(MidiPacketFormat.Midi1, new byte[] { 0xc0, 5 }),
                new(MidiPacketFormat.Midi1, bulk), MidiPacket.FromUmpWords([0x43913c00, 0x7fffffff])];
            for (int repeat = 0; repeat < 10; repeat++)
            {
                foreach (MidiPacket message in messages)
                {
                    await midi.Input.Writer.WriteAsync(message, timeout.Token);
                    await TcpMidiBridge.WriteFrameAsync(peer.GetStream(), message, timeout.Token);
                    MidiPacket receivedByPeer = (await TcpMidiBridge.ReadFrameAsync(peer.GetStream(), timeout.Token))!.Value;
                    MidiPacket receivedByMidi = await midi.Output.Reader.ReadAsync(timeout.Token);
                    Assert.Equal(message.Format, receivedByPeer.Format);
                    Assert.Equal(message.Format, receivedByMidi.Format);
                    Assert.Equal(message.Data.ToArray(), receivedByPeer.Data.ToArray());
                    Assert.Equal(message.Data.ToArray(), receivedByMidi.Data.ToArray());
                }
            }
            await capture.StopAsync();
            Assert.Equal(enableCapture ? 60 : 0, capture.GetSnapshot().Packets);
            Assert.Equal(0, capture.GetSnapshot().LostRecords);
            // Stopping capture leaves the same bridge running and forwarding.
            await midi.Input.Writer.WriteAsync(messages[0], timeout.Token);
            Assert.NotNull(await TcpMidiBridge.ReadFrameAsync(peer.GetStream(), timeout.Token));
            Assert.False(bridge.IsCompleted);
        }
        finally
        {
            await sessionCancellation.CancelAsync();
            await bridge.WaitAsync(timeout.Token);
        }
    }

    private sealed class TestMidiPort : IMidiDuplexPort
    {
        public Channel<MidiPacket> Input { get; } = Channel.CreateUnbounded<MidiPacket>();
        public Channel<MidiPacket> Output { get; } = Channel.CreateUnbounded<MidiPacket>();
        public string InputName => "Test input";
        public string OutputName => "Test output";

        public IAsyncEnumerable<MidiPacket> ReadAllAsync(CancellationToken cancellationToken) =>
            Input.Reader.ReadAllAsync(cancellationToken);

        public ValueTask SendAsync(MidiPacket packet, CancellationToken cancellationToken) =>
            Output.Writer.WriteAsync(packet, cancellationToken);

        public ValueTask DisposeAsync()
        {
            Input.Writer.TryComplete();
            Output.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
