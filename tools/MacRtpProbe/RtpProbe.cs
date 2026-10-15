using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;
using MidiForwarder.Core;
using MidiForwarder.Midi.CoreMidi;

static class RtpProbe
{
    public static async Task CheckInstalledAsync(string endpointName, int start, int count)
    {
        if (start < 0 || count < 1 || start + count > 512) { throw new ArgumentOutOfRangeException(nameof(start), "Select slots within the FM9's 0–511 range."); }
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        using var input = InputDevice.GetByName(endpointName);
        using var output = OutputDevice.GetByName(endpointName);
        using var encoder = new MidiEventToBytesConverter { BytesFormat = BytesFormat.Device, UseRunningStatus = false };
        using var decoder = new BytesToMidiEventConverter { BytesFormat = BytesFormat.Device };
        var incoming = Channel.CreateUnbounded<byte[]>();
        input.WaitForCompleteSysExEvent = true;
        input.EventReceived += (_, e) => incoming.Writer.TryWrite(encoder.Convert(e.Event));
        input.ErrorOccurred += (_, e) => incoming.Writer.TryComplete(e.Exception);
        input.StartEventsListening();
        await using var native = CoreMidiDuplexPort.Open(endpointName);
        await using var nativeReceived = native.ReadAllAsync(deadline.Token).GetAsyncEnumerator();
        var total = Stopwatch.StartNew();
        for (int slot = start; slot < start + count; slot++)
        {
            byte[] request = [0xf0, 0, 1, 0x74, 0x12, 3, (byte)(slot >> 7), (byte)(slot & 127), 0, 0, 0xf7];
            request[^2] = (byte)(request.AsSpan(0, request.Length - 2).ToArray().Aggregate(0, (v, b) => v ^ b) & 127);
            var watch = Stopwatch.StartNew();
            output.SendEvent(decoder.Convert(request));
            using var replyDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            replyDeadline.CancelAfter(15000);
            for (int index = 0; index < 10; index++)
            {
                byte[] message = await incoming.Reader.ReadAsync(replyDeadline.Token);
                if (!await nativeReceived.MoveNextAsync().AsTask().WaitAsync(replyDeadline.Token)) throw new IOException("Native MIDI receiver ended");
                byte[] independent = nativeReceived.Current.Data.ToArray();
                if (!message.AsSpan().SequenceEqual(independent)) throw new IOException("Independent MIDI receivers differ");
                int length = index == 0 ? 13 : index == 9 ? 11 : 3082;
                byte opcode = index == 0 ? (byte)0x77 : index == 9 ? (byte)0x79 : (byte)0x78;
                if (message.Length != length || message[5] != opcode || message[0] != 0xf0 || message[^1] != 0xf7)
                    throw new IOException($"Malformed preset reply for slot {slot}, message {index}");
                int checksum = message.AsSpan(0, message.Length - 2).ToArray().Aggregate(0, (v, b) => v ^ b) & 127;
                if (message[^2] != checksum) throw new IOException($"Preset reply checksum failed for slot {slot}");
            }
            Console.WriteLine($"Installed client slot {slot}: 10/10 matching messages with valid checksums, {watch.ElapsedMilliseconds}ms");
        }
        Console.WriteLine($"PASS: installed client, {count} FM9 dumps / {count * 10} messages, {total.ElapsedMilliseconds}ms total");
    }

    public static async Task RunAsync(bool live, bool largeOnly, string endpointName, string? fixturePath)
    {
        await using var peer = new RtpMidiPeer();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        Console.WriteLine("Connect Apple session to directory entry 127.0.0.1:55006, then test begins.");
        await peer.Connected.WaitAsync(deadline.Token);
        await Task.Delay(200, deadline.Token);
        string name = endpointName;
        Console.WriteLine("Available inputs: " + string.Join(",", InputDevice.GetAll().Select(p => p.Name)));
        using var input = InputDevice.GetByName(name);
        using var output = OutputDevice.GetByName(name);
        using var encoder = new MidiEventToBytesConverter { BytesFormat = BytesFormat.Device, UseRunningStatus = false };
        using var decoder = new BytesToMidiEventConverter { BytesFormat = BytesFormat.Device };
        var incoming = Channel.CreateUnbounded<byte[]>();
        input.WaitForCompleteSysExEvent = true;
        input.EventReceived += (_, e) => incoming.Writer.TryWrite(encoder.Convert(e.Event));
        input.ErrorOccurred += (_, e) => incoming.Writer.TryComplete(e.Exception);
        input.StartEventsListening();
        Console.WriteLine("RtMidi endpoints: " + string.Join(",", CoreMidiPortCatalog.GetNames()));
        string nativeName = CoreMidiPortCatalog.GetNames().Single(n => n.Equals(endpointName, StringComparison.Ordinal));
        await using var native = CoreMidiDuplexPort.Open(nativeName);
        var nativeMessages = Channel.CreateUnbounded<byte[]>();
        Task nativePump = Task.Run(async () =>
        {
            try { await foreach (var packet in native.ReadAllAsync(deadline.Token)) nativeMessages.Writer.TryWrite(packet.Data.ToArray()); }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        });
        byte[][] fixture = !live && !largeOnly
            ? File.ReadAllLines(fixturePath ?? throw new ArgumentException("Replay requires --fixture <hex-file>"))
                .Where(l => !string.IsNullOrWhiteSpace(l)).Select(Convert.FromHexString).ToArray()
            : [];
        try
        {
            Console.WriteLine("Checking peer -> Apple source path");
            byte[] sourceProbe = [0xf0, 0, 1, 0x74, 0x12, 0x77, 0, 0, 0, 0, 0, 0, 0xf7];
            await peer.SendMidiAsync(sourceProbe, deadline.Token);
            await ExpectAsync(incoming.Reader, sourceProbe, deadline.Token);
            await ExpectAsync(nativeMessages.Reader, sourceProbe, deadline.Token);
            Console.WriteLine("Checking Apple destination -> peer path with RtMidi");
            byte[] handshakeProbe = [0xb0, 7, 100];
            await native.SendAsync(new MidiPacket(MidiPacketFormat.Midi1, handshakeProbe), deadline.Token);
            await ExpectAsync(peer.Messages, handshakeProbe, deadline.Token);
            if (!live)
            {
                var watch = Stopwatch.StartNew();
                for (int i = 0; i < (largeOnly ? 0 : 30); i++)
                {
                    byte[] request = [0xf0, 0, 1, 0x74, 0x12, 3, (byte)i, 0, 0xf7];
                    output.SendEvent(decoder.Convert(request));
                    await ExpectAsync(peer.Messages, request, deadline.Token);
                    foreach (byte[] message in fixture)
                    {
                        await Task.Delay(80, deadline.Token);
                        await peer.SendMidiAsync(message, deadline.Token);
                        await ExpectAsync(incoming.Reader, message, deadline.Token);
                        await ExpectAsync(nativeMessages.Reader, message, deadline.Token);
                    }
                    Console.WriteLine($"RTP replay dump {i + 1}/30: 10/10 exact messages on both receivers, elapsed {watch.ElapsedMilliseconds}ms");
                }
                byte[] large = new byte[70000]; large[0] = 0xf0; large[^1] = 0xf7;
                for (int i = 1; i < large.Length - 1; i++) large[i] = (byte)(i & 127);
                await peer.SendMidiAsync(large, deadline.Token);
                await ExpectAsync(incoming.Reader, large, deadline.Token);
                await ExpectAsync(nativeMessages.Reader, large, deadline.Token);
                // DryWetMIDI 8.0.3's native output uses one legacy MIDIPacket
                // (16-bit length); keep its reverse test below that limit.
                byte[] outgoingLarge = large.AsSpan(0, 60000).ToArray(); outgoingLarge[^1] = 0xf7;
                output.SendEvent(decoder.Convert(outgoingLarge));
                var largeWatch = Stopwatch.StartNew();
                try { await ExpectAsync(peer.Messages, outgoingLarge, deadline.Token, 3000); }
                catch { Console.WriteLine($"Pending RTP SysEx bytes={peer.PendingSysExBytes}, packets={peer.ReceivedPackets}, gaps={peer.SequenceGaps}"); throw; }
                Console.WriteLine($"DryWetMIDI 60000-byte output completed in {largeWatch.ElapsedMilliseconds}ms");
                byte[][] controls = [[0x90, 60, 100], [0x80, 60, 0], [0xb0, 7, 100], [0xc0, 2], [0xe0, 0, 64]];
                foreach (byte[] message in controls)
                {
                    await peer.SendMidiAsync(message, deadline.Token);
                    await ExpectAsync(incoming.Reader, message, deadline.Token);
                    await ExpectAsync(nativeMessages.Reader, message, deadline.Token);
                    output.SendEvent(decoder.Convert(message));
                    await ExpectAsync(peer.Messages, message, deadline.Token);
                }
                Console.WriteLine($"PASS: {(largeOnly ? "large messages" : "30 replay dumps")}, 70000-byte SysEx input / 60000-byte output, channel messages. RTP sent={peer.SentPackets}, received={peer.ReceivedPackets}, gaps={peer.SequenceGaps}");
            }
            else
            {
                using var settings = JsonDocument.Parse(File.ReadAllText(DiagnosticLog.GetPath("client.json")));
                var address = new Uri(settings.RootElement.GetProperty("ServerUri").GetString()!);
                using var tcp = new TcpClient { NoDelay = true };
                await tcp.ConnectAsync(address.Host, address.Port, deadline.Token);
                await TcpAuthentication.SendAsync(tcp.GetStream(), settings.RootElement.GetProperty("Token").GetString()!, deadline.Token);
                using var midiDecoder = new Midi1PacketDecoder();
                var overall = Stopwatch.StartNew();
                for (int slot = 200; slot < 230; slot++)
                {
                    byte[] request = [0xf0, 0, 1, 0x74, 0x12, 3, (byte)(slot >> 7), (byte)(slot & 127), 0, 0, 0xf7];
                    request[^2] = (byte)(request.AsSpan(0, request.Length - 2).ToArray().Aggregate(0, (v,b) => v ^ b) & 127);
                    var watch = Stopwatch.StartNew();
                    output.SendEvent(decoder.Convert(request));
                    await ExpectAsync(peer.Messages, request, deadline.Token);
                    await TcpMidiBridge.WriteFrameAsync(tcp.GetStream(), new MidiPacket(MidiPacketFormat.Midi1, request), deadline.Token);
                    int count = 0;
                    while (true)
                    {
                        using var receiveDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                        receiveDeadline.CancelAfter(15000);
                        var packet = await TcpMidiBridge.ReadFrameAsync(tcp.GetStream(), receiveDeadline.Token) ?? throw new IOException("Server disconnected");
                        byte[]? message = midiDecoder.Decode(packet);
                        if (message is null) continue;
                        await peer.SendMidiAsync(message, deadline.Token);
                        await ExpectAsync(incoming.Reader, message, deadline.Token);
                        await ExpectAsync(nativeMessages.Reader, message, deadline.Token);
                        byte opcode = message[5];
                        if (opcode != (count == 0 ? 0x77 : count == 9 ? 0x79 : 0x78)) throw new IOException("Unexpected preset reply sequence");
                        count++;
                        if (opcode == 0x79) break;
                    }
                    if (count != 10) throw new IOException("Missing preset reply");
                    Console.WriteLine($"RTP live slot {slot}: 10/10 exact messages on both receivers, {watch.ElapsedMilliseconds}ms");
                }
                Console.WriteLine($"PASS: 30 live FM9 dumps, {overall.ElapsedMilliseconds}ms total. RTP sent={peer.SentPackets}, received={peer.ReceivedPackets}, gaps={peer.SequenceGaps}");
            }
        }
        finally { await deadline.CancelAsync(); await nativePump; }
    }

    static async Task ExpectAsync(ChannelReader<byte[]> reader, byte[] expected, CancellationToken cancellationToken, int deadlineMs = 3000)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(deadlineMs);
        byte[] actual;
        try { actual = await reader.ReadAsync(deadline.Token); }
        catch (OperationCanceledException) { throw new IOException($"MIDI delivery timeout for {expected.Length}-byte message, opcode {(expected.Length > 5 ? expected[5] : 0):X2}"); }
        if (!actual.AsSpan().SequenceEqual(expected)) throw new IOException($"MIDI mismatch: expected {expected.Length} bytes, got {actual.Length}");
    }
}
