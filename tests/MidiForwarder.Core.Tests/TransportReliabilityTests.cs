using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using MidiForwarder.Core;
using MidiForwarder.Midi.WindowsServices;

namespace MidiForwarder.Core.Tests;

public sealed class TransportReliabilityTests
{
    [Fact]
    public void SysExIsForwardedOnlyWhenCompleteAndRetainsItsGroup()
    {
        var assembler = new UmpSysExAssembler();
        byte[] bytes = [0xf0, 0x7d, 1, 2, 3, 4, 5, 6, 7, 8, 0xf7];
        var fragments = UmpMidi1Codec.Encode(bytes, 4).Select(packet => MidiPacket.FromUmpWords(packet)).ToArray();
        Assert.Null(assembler.Accept(fragments[0]));
        var completed = assembler.Accept(fragments[1])!.Value;
        Assert.Equal(MidiPacketFormat.UniversalMidiPacketBatch, completed.Format);
        Assert.Equal(fragments.SelectMany(fragment => fragment.Data.ToArray()), completed.Data.ToArray());
        Assert.Equal(bytes, UmpMidi1Codec.DecodeSysEx7Batch(completed));
        Assert.Equal(4, completed.Data.Span[0] & 15);
    }

    [Fact]
    public void InterleavedGroupsAndSysEx8StreamsKeepSeparateAssemblies()
    {
        var assembler = new UmpSysExAssembler();
        MidiPacket Fragment(byte group, byte stream, byte status)
        {
            byte[] data = new byte[16]; data[0] = (byte)(0x50 | group); data[1] = (byte)(status << 4 | 2);
            data[2] = stream; data[3] = 0xff;
            return new MidiPacket(MidiPacketFormat.UniversalMidiPacket, data);
        }
        Assert.Null(assembler.Accept(Fragment(2, 1, 1)));
        Assert.Null(assembler.Accept(Fragment(2, 2, 1)));
        Assert.Null(assembler.Accept(Fragment(3, 1, 1)));
        foreach (var key in new[] { (Group: (byte)2, Stream: (byte)2), (Group: (byte)3, Stream: (byte)1), (Group: (byte)2, Stream: (byte)1) })
        {
            var complete = assembler.Accept(Fragment(key.Group, key.Stream, 3))!.Value;
            Assert.Equal(key.Group, complete.Data.Span[0] & 15);
            Assert.Equal(key.Stream, complete.Data.Span[2]);
            Assert.Equal(32, complete.Data.Length);
        }
        Assert.Equal(0, assembler.PendingMessages);
    }

    [Fact]
    public async Task ReconnectDiscardsOfflineTrafficQueuedCommandsAndPartialSystemExclusiveMessages()
    {
        var buffer = new MidiReceiveBuffer();
        var fragments = UmpMidi1Codec.Encode([0xf0, 0x7d, 1, 2, 3, 4, 5, 6, 7, 0xf7]);
        buffer.Publish(MidiPacket.FromUmpWords(fragments[0]));
        buffer.Publish(new MidiPacket(MidiPacketFormat.Midi1, new byte[] { 0xc0, 1 }));
        buffer.EndSession();
        buffer.Publish(new MidiPacket(MidiPacketFormat.Midi1, new byte[] { 0xc0, 2 }));
        buffer.BeginSession();
        buffer.Publish(MidiPacket.FromUmpWords(fragments[1])); // Stale tail must not be forwarded.
        buffer.Publish(new MidiPacket(MidiPacketFormat.Midi1, new byte[] { 0xc0, 3 }));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var reader = buffer.ReadAllAsync(timeout.Token).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(new byte[] { 0xc0, 3 }, reader.Current.Data.ToArray());
        Assert.Equal(0, buffer.Statistics.QueuedBytes);
        Assert.Equal(1, buffer.Statistics.OfflinePackets);
        Assert.Equal(1, buffer.Statistics.ClearedMessages);
        Assert.Equal(2, buffer.Statistics.DroppedFragments);
        buffer.Close();
    }

    [Fact]
    public async Task OverflowFailsSessionInsteadOfDrainingOrSilentlyDroppingCommands()
    {
        var buffer = new MidiReceiveBuffer();
        for (int i = 0; i < 4097; i++) { buffer.Publish(MidiPacket.FromUmpWords([0x20903c40])); }
        await using var reader = buffer.ReadAllAsync(default).GetAsyncEnumerator();
        await Assert.ThrowsAsync<IOException>(async () => await reader.MoveNextAsync());
        Assert.Equal(1, buffer.Statistics.Overflows);
        buffer.BeginSession();
        Assert.Equal(0, buffer.Statistics.QueuedBytes);
        buffer.Close();
    }

    [Fact]
    public void InvalidBatchIsRejectedBeforeSending()
    {
        var bytes = UmpMidi1Codec.Encode([0xf0, 0x7d, 1, 2, 3, 4, 5, 6, 7, 0xf7])
            .SelectMany(words => MidiPacket.FromUmpWords(words).Data.ToArray()).ToArray();
        bytes[8] ^= 1; // The tail now names another group.
        Assert.Throws<InvalidDataException>(() => new MidiPacket(MidiPacketFormat.UniversalMidiPacketBatch, bytes));
    }

    [Fact]
    public void LargeMessagesUseSupportedBatchSizesWithoutSplittingUmpAndStopOnFailure()
    {
        uint[] words = UmpMidi1Codec.Encode([0xf0, 0x7d, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 0xf7]).SelectMany(packet => packet).ToArray();
        var sent = new List<(uint Offset, uint Count)>();
        UmpBatchSender.Send(words, 5, (offset, count) => { sent.Add((offset, count)); return true; });
        Assert.Equal(new[] { (0u, 4u), (4u, 2u) }, sent);
        sent.Clear();
        Assert.Throws<IOException>(() => UmpBatchSender.Send(words, 2, (offset, count) =>
        { sent.Add((offset, count)); return offset == 0; }));
        Assert.Equal(new[] { (0u, 2u), (2u, 2u) }, sent);
    }

    [Fact]
    public void TruncatedTailIsRejectedBeforeAnyTransmission()
    {
        int sends = 0;
        Assert.Throws<InvalidDataException>(() => UmpBatchSender.Send([0x20903c40, 0x30160102], 4,
            (_, _) => { sends++; return true; }));
        Assert.Equal(0, sends);
    }

    [Fact]
    public async Task ProtocolHandshakeRequiresServerAcknowledgementAndRejectsPreviousVersion()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var client = new TcpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var accepted = listener.AcceptTcpClientAsync(timeout.Token).AsTask();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, timeout.Token);
        using var server = await accepted;
        var authenticating = TcpAuthentication.AuthenticateAsync(client.GetStream(), "test", timeout.Token);
        Assert.True(await TcpAuthentication.ReceiveAndValidateAsync(server.GetStream(), "test", timeout.Token));
        Assert.False(authenticating.IsCompleted);
        await TcpAuthentication.AcceptAsync(server.GetStream(), timeout.Token);
        await authenticating;
        using var old = new MemoryStream("MIDIFWD1\0\0"u8.ToArray());
        Assert.False(await TcpAuthentication.ReceiveAndValidateAsync(old, "", default));
        using var previous = new MemoryStream("MIDIFWD2\0\0"u8.ToArray());
        Assert.False(await TcpAuthentication.ReceiveAndValidateAsync(previous, "", default));
    }

    [Fact]
    public async Task ProtocolCarriesAppPortNameAndRejectsInvalidNamesBeforeWriting()
    {
        using var stream = new MemoryStream();
        await TcpAuthentication.SendAsync(stream, "token", "Editor · MIDI", default);
        stream.Position = 0;
        Assert.Equal("Editor · MIDI", await TcpAuthentication.ReceiveRequestAsync(stream, "token", default));
        using var invalid = new MemoryStream();
        await Assert.ThrowsAsync<InvalidOperationException>(() => TcpAuthentication.SendAsync(invalid, "token", "Editor\nOther app", default));
        Assert.Equal(0, invalid.Length);
        await Assert.ThrowsAsync<InvalidOperationException>(() => TcpAuthentication.SendAsync(invalid, "token", new string('a', 129), default));
        Assert.Equal(0, invalid.Length);
    }

    [Fact]
    public async Task ConcurrentDiagnosticSessionsPreserveEverySessionInOneFile()
    {
        string directory = Path.Combine(Path.GetTempPath(), "MidiForwarder-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(directory, "transport.jsonl");
            var sessions = Enumerable.Range(0, 8).Select(_ => new TransportDiagnostics(path)).ToArray();
            foreach (var session in sessions)
            { session.Record("network-to-midi", MidiPacket.FromUmpWords([0x20903c40]), 0); }
            await Task.WhenAll(sessions.Select(session => session.DisposeAsync().AsTask()));
            var records = File.ReadLines(path).Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
            var groups = records.GroupBy(record => record.GetProperty("sessionId").GetString()).ToArray();
            Assert.Equal(8, groups.Length);
            string[] expectedKinds = ["start", "summary"];
            foreach (var group in groups)
            {
                Assert.Equal(expectedKinds, group.Select(record => record.GetProperty("kind").GetString()));
                Assert.Equal(1, group.Last().GetProperty("directions").GetProperty("network-to-midi").GetProperty("Messages").GetInt32());
            }
        }
        finally { if (Directory.Exists(directory)) { Directory.Delete(directory, true); } }
    }

    [Fact]
    public async Task CompleteBatchSurvivesTcpFramingAndTruncatedFrameIsRejected()
    {
        var assembler = new UmpSysExAssembler();
        MidiPacket? complete = null;
        foreach (uint[] words in UmpMidi1Codec.Encode([0xf0, 0x7d, 1, 2, 3, 4, 5, 6, 7, 0xf7], 6))
        { complete = assembler.Accept(MidiPacket.FromUmpWords(words)) ?? complete; }
        using var stream = new MemoryStream();
        await TcpMidiBridge.WriteFrameAsync(stream, complete!.Value, default);
        stream.Position = 0;
        var received = await TcpMidiBridge.ReadFrameAsync(stream, default);
        Assert.Equal(complete.Value.Data.ToArray(), received!.Value.Data.ToArray());
        byte[] truncated = stream.ToArray()[..^1];
        using var bad = new MemoryStream(truncated);
        await Assert.ThrowsAsync<EndOfStreamException>(() => TcpMidiBridge.ReadFrameAsync(bad, default));
    }

    [Fact]
    public async Task DiagnosticsContainBothDirectionsAndNoPayloadAndLoggingFailureIsHarmless()
    {
        string directory = Path.Combine(Path.GetTempPath(), "MidiForwarder-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(directory, "transport.jsonl");
            await using (var diagnostics = new TransportDiagnostics(path))
            {
                var packet = new MidiPacket(MidiPacketFormat.Midi1, "Private payload"u8.ToArray());
                diagnostics.Record("midi-to-network", packet, 1);
                diagnostics.Record("network-to-midi", packet, 2);
                diagnostics.Finish(new IOException("Private error"), null);
            }
            string text = File.ReadAllText(path);
            Assert.DoesNotContain("Private", text);
            var summary = JsonDocument.Parse(File.ReadLines(path).Last()).RootElement;
            Assert.Equal("IOException", summary.GetProperty("failure").GetString());
            Assert.Equal(1, summary.GetProperty("directions").GetProperty("network-to-midi").GetProperty("Messages").GetInt32());
            string blocked = Path.Combine(directory, "blocked"); File.WriteAllText(blocked, "file");
            await using var failed = new TransportDiagnostics(Path.Combine(blocked, "transport.jsonl"));
            failed.Record("network-to-midi", new MidiPacket(MidiPacketFormat.Midi1, new byte[] { 0xf8 }), 0);
        }
        finally { if (Directory.Exists(directory)) { Directory.Delete(directory, true); } }
    }

    [Fact]
    public async Task LiveTcpBridgeForwardsCompleteMessagesBothWaysAndResetsOnCancellation()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var client = new TcpClient { NoDelay = true };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var accepting = listener.AcceptTcpClientAsync(deadline.Token).AsTask();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, deadline.Token);
        using var server = await accepting;
        await using var a = new TestPort(); await using var b = new TestPort();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var pumps = new[] { TcpMidiBridge.RunAsync(client.GetStream(), a, null, stop.Token),
            TcpMidiBridge.RunAsync(server.GetStream(), b, null, stop.Token) };
        await Task.WhenAll(a.Started.Task, b.Started.Task).WaitAsync(deadline.Token);
        byte[] sysex = [0xf0, 0x7d, 1, 2, 3, 4, 5, 6, 7, 0xf7];
        foreach (var words in UmpMidi1Codec.Encode(sysex, 5)) { a.Buffer.Publish(MidiPacket.FromUmpWords(words)); }
        b.Buffer.Publish(MidiPacket.FromUmpWords([0x21903c40]));
        Assert.Equal(sysex, UmpMidi1Codec.DecodeSysEx7Batch(await b.Received.Task.WaitAsync(deadline.Token)));
        Assert.Equal(new uint[] { 0x21903c40 }, (await a.Received.Task.WaitAsync(deadline.Token)).ToUmpWords());
        a.Buffer.Publish(MidiPacket.FromUmpWords(UmpMidi1Codec.Encode(sysex)[0]));
        stop.Cancel();
        await Task.WhenAll(pumps).WaitAsync(deadline.Token);
        Assert.Equal(0, a.Buffer.Statistics.PendingSystemExclusiveMessages);
        Assert.Equal(1, a.Buffer.Statistics.DroppedFragments);
        a.Buffer.Publish(MidiPacket.FromUmpWords([0x20903c40]));
        Assert.Equal(1, a.Buffer.Statistics.OfflinePackets);
    }

    private sealed class TestPort : IMidiDuplexPort, IMidiSessionPort
    {
        public MidiReceiveBuffer Buffer { get; } = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<MidiPacket> Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string InputName => "test";
        public string OutputName => "test";
        public void BeginSession() { Buffer.BeginSession(); Started.TrySetResult(); }
        public void EndSession() => Buffer.EndSession();
        public MidiQueueStatistics ReceiveStatistics => Buffer.Statistics;
        public IAsyncEnumerable<MidiPacket> ReadAllAsync(CancellationToken token) => Buffer.ReadAllAsync(token);
        public ValueTask SendAsync(MidiPacket packet, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Received.TrySetResult(packet); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() { Buffer.Close(); return ValueTask.CompletedTask; }
    }
}
