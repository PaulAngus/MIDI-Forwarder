using System.Buffers.Binary;

namespace MidiForwarder.Core;

public static class TcpMidiBridge
{
    public static async Task RunAsync(
        Stream stream,
        IMidiDuplexPort midi,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task midiToNetwork = PumpMidiToNetworkAsync(stream, midi, log, sessionCancellation.Token);
        Task networkToMidi = PumpNetworkToMidiAsync(stream, midi, log, sessionCancellation.Token);

        await Task.WhenAny(midiToNetwork, networkToMidi).ConfigureAwait(false);
        await sessionCancellation.CancelAsync().ConfigureAwait(false);

        try
        {
            await Task.WhenAll(midiToNetwork, networkToMidi).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (sessionCancellation.IsCancellationRequested)
        {
        }
    }

    public static async Task WriteFrameAsync(
        Stream stream,
        MidiPacket packet,
        CancellationToken cancellationToken)
    {
        packet.Validate();
        ReadOnlyMemory<byte> message = packet.Data;
        const int headerLength = sizeof(int) + 1;

        // One write per frame keeps small MIDI messages in a single TCP segment (TCP_NODELAY would otherwise
        // send the header and payload separately, doubling packets on Wi-Fi).
        byte[] frame = new byte[headerLength + message.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, checked(message.Length + 1));
        frame[sizeof(int)] = (byte)packet.Format;
        message.CopyTo(frame.AsMemory(headerLength));
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<MidiPacket?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] header = new byte[sizeof(int)];
        if (!await ReadExactlyOrEndAsync(stream, header, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        int length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length <= 1 || length > MidiFrame.MaximumLength + 1)
        {
            throw new InvalidDataException($"Invalid MIDI frame length: {length}.");
        }

        byte[] frame = new byte[length];
        await stream.ReadExactlyAsync(frame, cancellationToken).ConfigureAwait(false);
        return new MidiPacket((MidiPacketFormat)frame[0], frame.AsMemory(1));
    }

    private static async Task PumpMidiToNetworkAsync(
        Stream stream,
        IMidiDuplexPort midi,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        var summary = new TrafficSummary("MIDI -> network", log);
        await foreach (MidiPacket packet in midi.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await WriteFrameAsync(stream, packet, cancellationToken).ConfigureAwait(false);
            summary.Add(packet.Data.Length);
        }

        summary.Flush();
    }

    private static async Task PumpNetworkToMidiAsync(
        Stream stream,
        IMidiDuplexPort midi,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        var summary = new TrafficSummary("Network -> MIDI", log);
        while (!cancellationToken.IsCancellationRequested)
        {
            MidiPacket? packet = await ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);
            if (packet is null)
            {
                break;
            }

            await midi.SendAsync(packet.Value, cancellationToken).ConfigureAwait(false);
            summary.Add(packet.Value.Data.Length);
        }

        summary.Flush();
    }

    private static async Task<bool> ReadExactlyOrEndAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (offset == 0)
                {
                    return false;
                }

                throw new EndOfStreamException("The TCP connection ended inside a MIDI frame header.");
            }

            offset += read;
        }

        return true;
    }

    // Per-message logging made the UI log (and the server's log file) a bottleneck during bulk SysEx transfers.
    private sealed class TrafficSummary(string direction, Action<string>? log)
    {
        private static readonly long Interval = System.Diagnostics.Stopwatch.Frequency;
        private long _last = System.Diagnostics.Stopwatch.GetTimestamp();
        private int _messages;
        private long _bytes;

        public void Add(int length)
        {
            _messages++;
            _bytes += length;
            if (System.Diagnostics.Stopwatch.GetTimestamp() - _last >= Interval)
            {
                Flush();
            }
        }

        public void Flush()
        {
            if (_messages > 0)
            {
                log?.Invoke($"{direction}: {_messages} message(s), {_bytes} byte(s)");
            }

            _messages = 0;
            _bytes = 0;
            _last = System.Diagnostics.Stopwatch.GetTimestamp();
        }
    }
}
