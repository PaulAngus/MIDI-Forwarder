using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

// Diagnostic loopback-only Apple RTP-MIDI responder. This is not a production transport.
sealed class RtpMidiPeer : IAsyncDisposable
{
    readonly UdpClient control = new(new IPEndPoint(IPAddress.Loopback, 55006));
    readonly UdpClient data = new(new IPEndPoint(IPAddress.Loopback, 55007));
    readonly CancellationTokenSource stop = new();
    readonly Channel<byte[]> messages = Channel.CreateUnbounded<byte[]>();
    readonly TaskCompletionSource connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly uint ssrc = (uint)Random.Shared.Next(1, int.MaxValue);
    readonly MemoryStream sysex = new();
    readonly Task controlPump, dataPump;
    IPEndPoint? remoteControl, remoteData;
    ushort sequence;
    ushort? receivedSequence;
    int syncExchanges;
    byte runningStatus;
    public int SentPackets { get; private set; }
    public int ReceivedPackets { get; private set; }
    public int SequenceGaps { get; private set; }
    public long PendingSysExBytes => sysex.Length;
    public ChannelReader<byte[]> Messages => messages.Reader;
    public Task Connected => connected.Task;

    public RtpMidiPeer()
    {
        Console.WriteLine($"Default RTP receive buffer: {data.Client.ReceiveBufferSize} bytes");
        data.Client.ReceiveBufferSize = 262144;
        Console.WriteLine($"RTP test receive buffer: {data.Client.ReceiveBufferSize} bytes");
        controlPump = PumpAsync(control, false);
        dataPump = PumpAsync(data, true);
        Console.WriteLine("RTP diagnostic peer ready on loopback UDP 55006/55007");
    }

    static ulong Clock() => (ulong)(Stopwatch.GetTimestamp() * 10000.0 / Stopwatch.Frequency);

    async Task PumpAsync(UdpClient socket, bool midiPort)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                UdpReceiveResult received = await socket.ReceiveAsync(stop.Token);
                byte[] packet = received.Buffer;
                if (!IPAddress.IsLoopback(received.RemoteEndPoint.Address)) continue;
                if (packet.Length >= 4 && packet[0] == 255 && packet[1] == 255)
                {
                    string command = Encoding.ASCII.GetString(packet, 2, 2);
                    if (command == "IN" && packet.Length >= 16)
                    {
                        if (midiPort) remoteData = received.RemoteEndPoint;
                        else remoteControl = received.RemoteEndPoint;
                        byte[] reply = new byte[16 + 14];
                        packet.AsSpan(0, 12).CopyTo(reply);
                        reply[2] = (byte)'O'; reply[3] = (byte)'K';
                        BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(12), ssrc);
                        Encoding.UTF8.GetBytes("Loopback Test\0").CopyTo(reply, 16);
                        await socket.SendAsync(reply, received.RemoteEndPoint, stop.Token);
                        Console.WriteLine($"Accepted Apple invitation on {(midiPort ? "data" : "control")} port from {received.RemoteEndPoint}");
                    }
                    else if (command == "CK" && packet.Length >= 36)
                    {
                        Console.WriteLine($"Clock count={packet[8]}, t1={BinaryPrimitives.ReadUInt64BigEndian(packet.AsSpan(12))}, t2={BinaryPrimitives.ReadUInt64BigEndian(packet.AsSpan(20))}, t3={BinaryPrimitives.ReadUInt64BigEndian(packet.AsSpan(28))}, ours={Clock()}");
                        if (packet[8] == 0)
                        {
                            byte[] reply = packet.ToArray();
                            BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(4), ssrc);
                            reply[8] = 1;
                            BinaryPrimitives.WriteUInt64BigEndian(reply.AsSpan(20), Clock());
                            await socket.SendAsync(reply, received.RemoteEndPoint, stop.Token);
                        }
                        else if (packet[8] == 2)
                        {
                            // This driver sends six initial sync exchanges at 1.5s intervals.
                            // Begin the throughput test after clock calibration, not just invitation acceptance.
                            if (++syncExchanges >= 6) connected.TrySetResult();
                        }
                    }
                    else if (command == "BY")
                    {
                        Console.WriteLine("Apple MIDI session disconnected");
                    }
                    continue;
                }
                if (ReceivedPackets < 2) Console.WriteLine($"Received datagram on {(midiPort ? "data" : "control")} from {received.RemoteEndPoint}, head {Convert.ToHexString(packet.AsSpan(0, Math.Min(packet.Length, 32)))}");
                if (!midiPort || remoteData is null || !received.RemoteEndPoint.Equals(remoteData)) continue;
                ParseMidi(packet);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error)
        {
            Console.WriteLine("RTP receive failure: " + error);
            connected.TrySetException(error);
            messages.Writer.TryComplete(error);
        }
    }

    void ParseMidi(byte[] packet)
    {
        if (packet.Length < 13 || packet[0] != 0x80 || (packet[1] & 0x7f) != 0x61)
            throw new InvalidDataException("Unexpected RTP header");
        ushort gotSequence = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2));
        if (receivedSequence is ushort previous && gotSequence != unchecked((ushort)(previous + 1)))
        {
            SequenceGaps++;
            throw new InvalidDataException($"RTP sequence gap: {previous} -> {gotSequence}");
        }
        receivedSequence = gotSequence;
        ReceivedPackets++;
        int position = 12;
        byte flags = packet[position++];
        int length = flags & 15;
        if ((flags & 0x80) != 0) length = (length << 8) | packet[position++];
        int end = position + length;
        if (end > packet.Length) throw new InvalidDataException("Truncated MIDI list");
        bool delta = (flags & 0x20) != 0;
        while (position < end)
        {
            if (delta) { while (position < end && (packet[position++] & 0x80) != 0) { } }
            delta = true;
            if (position >= end) break;
            byte status = packet[position] >= 128 ? packet[position++] : runningStatus;
            if (status == 0xf0 || status == 0xf7)
            {
                if (status == 0xf0) { sysex.SetLength(0); sysex.WriteByte(0xf0); }
                else if (sysex.Length == 0) throw new InvalidDataException("Orphan SysEx continuation");
                while (position < end && packet[position] < 128) sysex.WriteByte(packet[position++]);
                if (position >= end) throw new InvalidDataException("Missing SysEx segment terminator");
                byte tail = packet[position++];
                if (tail == 0xf7 || tail == 0xf5)
                {
                    sysex.WriteByte(0xf7); messages.Writer.TryWrite(sysex.ToArray()); sysex.SetLength(0);
                }
                else if (tail == 0xf4) sysex.SetLength(0);
                else if (tail != 0xf0) throw new InvalidDataException("Unsupported SysEx segment tail");
                runningStatus = 0;
            }
            else
            {
                int count = status switch
                {
                    >= 0x80 and < 0xc0 => 2, >= 0xc0 and < 0xe0 => 1, >= 0xe0 and < 0xf0 => 2,
                    0xf1 or 0xf3 => 1, 0xf2 => 2, >= 0xf6 => 0,
                    _ => throw new InvalidDataException("Unsupported MIDI command")
                };
                if (position + count > end) throw new InvalidDataException("Truncated MIDI command");
                byte[] message = new byte[count + 1]; message[0] = status;
                packet.AsSpan(position, count).CopyTo(message.AsSpan(1)); position += count;
                messages.Writer.TryWrite(message);
                if (status < 0xf0) runningStatus = status;
                else if (status < 0xf8) runningStatus = 0;
            }
        }
        // ACK the received sequence so Apple's guard journals remain small.
        if (remoteControl is not null)
        {
            byte[] feedback = new byte[12]; feedback[0] = feedback[1] = 255;
            feedback[2] = (byte)'R'; feedback[3] = (byte)'S';
            BinaryPrimitives.WriteUInt32BigEndian(feedback.AsSpan(4), ssrc);
            BinaryPrimitives.WriteUInt16BigEndian(feedback.AsSpan(8), gotSequence);
            control.Send(feedback, remoteControl);
        }
    }

    public async Task SendMidiAsync(byte[] message, CancellationToken cancellationToken)
    {
        if (remoteData is null) throw new InvalidOperationException("RTP session is not connected");
        const int fragmentDataBytes = 1000;
        if (message.Length <= fragmentDataBytes + 2)
        {
            await SendCommandAsync(message, cancellationToken);
            return;
        }
        if (message[0] != 0xf0 || message[^1] != 0xf7) throw new InvalidDataException("Large message is not SysEx");
        for (int offset = 1; offset < message.Length - 1;)
        {
            int length = Math.Min(fragmentDataBytes, message.Length - 1 - offset);
            byte[] fragment = new byte[length + 2];
            fragment[0] = offset == 1 ? (byte)0xf0 : (byte)0xf7;
            message.AsSpan(offset, length).CopyTo(fragment.AsSpan(1));
            offset += length;
            fragment[^1] = offset == message.Length - 1 ? (byte)0xf7 : (byte)0xf0;
            await SendCommandAsync(fragment, cancellationToken);
        }
    }

    async Task SendCommandAsync(byte[] command, CancellationToken cancellationToken)
    {
        int headerBytes = command.Length > 15 ? 2 : 1;
        byte[] packet = new byte[12 + headerBytes + command.Length];
        // Match the header emitted by this installed driver (M=0, PT=0x61).
        packet[0] = 0x80; packet[1] = 0x61;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), sequence++);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), (uint)Clock());
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), ssrc);
        if (headerBytes == 1) packet[12] = (byte)command.Length;
        else { packet[12] = (byte)(0x80 | (command.Length >> 8)); packet[13] = (byte)command.Length; }
        command.CopyTo(packet, 12 + headerBytes);
        await data.SendAsync(packet, remoteData!, cancellationToken);
        SentPackets++;
    }

    public async ValueTask DisposeAsync()
    {
        if (remoteControl is not null)
        {
            byte[] bye = new byte[16]; bye[0] = bye[1] = 255; bye[2] = (byte)'B'; bye[3] = (byte)'Y';
            BinaryPrimitives.WriteUInt32BigEndian(bye.AsSpan(4), 2);
            BinaryPrimitives.WriteUInt32BigEndian(bye.AsSpan(12), ssrc);
            await control.SendAsync(bye, remoteControl);
        }
        await stop.CancelAsync();
        await Task.WhenAll(controlPump, dataPump);
        messages.Writer.TryComplete();
        sysex.Dispose(); control.Dispose(); data.Dispose(); stop.Dispose();
    }
}
