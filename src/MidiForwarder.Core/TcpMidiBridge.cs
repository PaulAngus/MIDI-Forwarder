using System.Buffers.Binary;

namespace MidiForwarder.Core;

public static class TcpMidiBridge
{
    public static async Task RunAsync(
        Stream stream,
        IMidiDuplexPort midi,
        Action<string>? log,
        CancellationToken cancellationToken,
        MidiTrafficMonitor? traffic = null,
        MidiCapture? capture = null)
    {
        string? connection = capture?.BeginConnection(midi.InputName, midi.OutputName);
        try
        {
            using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task midiToNetwork = PumpMidiToNetworkAsync(stream, midi, log, traffic, capture, connection, sessionCancellation.Token);
            Task networkToMidi = PumpNetworkToMidiAsync(stream, midi, log, traffic, capture, connection, sessionCancellation.Token);

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
        finally
        {
            if (connection is not null) { capture!.EndConnection(connection); }
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
        if (length == 0)
        {
            byte[] status = new byte[1];
            await stream.ReadExactlyAsync(status, cancellationToken).ConfigureAwait(false);
            if (status[0] == (byte)TcpSessionStatus.Replaced) { throw new TcpSessionEndedException(); }
            throw new InvalidDataException("Unexpected session control during MIDI forwarding.");
        }
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
        MidiTrafficMonitor? traffic,
        MidiCapture? capture,
        string? connection,
        CancellationToken cancellationToken)
    {
        await using var summary = new TrafficSummary("MIDI -> network", log);
        bool trace = Trace.Enabled;
        await foreach (MidiPacket packet in midi.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (connection is not null) { capture!.Record(connection, "midi-to-network", packet); }
            await WriteFrameAsync(stream, packet, cancellationToken).ConfigureAwait(false);
            traffic?.RecordMidiToNetwork(packet.Data.Length);
            summary.Add(packet.Data.Length);
            if (trace)
            {
                Trace.Write("MIDI -> network", packet);
            }
        }
    }

    private static async Task PumpNetworkToMidiAsync(
        Stream stream,
        IMidiDuplexPort midi,
        Action<string>? log,
        MidiTrafficMonitor? traffic,
        MidiCapture? capture,
        string? connection,
        CancellationToken cancellationToken)
    {
        await using var summary = new TrafficSummary("Network -> MIDI", log);
        bool trace = Trace.Enabled;
        while (!cancellationToken.IsCancellationRequested)
        {
            MidiPacket? packet = await ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);
            if (packet is null)
            {
                break;
            }

            if (connection is not null) { capture!.Record(connection, "network-to-midi", packet.Value); }

            if (trace)
            {
                Trace.Write("network received", packet.Value);
            }

            await midi.SendAsync(packet.Value, cancellationToken).ConfigureAwait(false);
            traffic?.RecordNetworkToMidi(packet.Value.Data.Length);
            summary.Add(packet.Value.Data.Length);
            if (trace)
            {
                Trace.Write("sent to MIDI", packet.Value);
            }
        }
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

    // Opt-in diagnostics: create an empty "trace.enable" file in the MIDI Forwarder settings folder, then reconnect.
    private static class Trace
    {
        public static bool Enabled => File.Exists(DiagnosticLog.GetPath("trace.enable"));

        public static void Write(string stage, MidiPacket packet)
        {
            ReadOnlySpan<byte> data = packet.Data.Span;
            int head = Math.Min(data.Length, 8);
            DiagnosticLog.Write(
                "trace.log",
                $"{stage}: {data.Length} byte(s), {packet.Format}, starts {Convert.ToHexString(data[..head])}, ends {Convert.ToHexString(data[^1..])}");
        }
    }

    // Per-message logging made the UI log (and the server's log file) a bottleneck during bulk SysEx transfers.
    private sealed class TrafficSummary : IAsyncDisposable
    {
        private readonly string _direction;
        private readonly Action<string>? _log;
        private readonly Lock _sync = new();
        private readonly PeriodicTimer? _timer;
        private readonly Task _reportTask;
        private long _messages;
        private long _bytes;

        public TrafficSummary(string direction, Action<string>? log)
        {
            _direction = direction;
            _log = log;
            _timer = log is null ? null : new PeriodicTimer(TimeSpan.FromSeconds(1));
            _reportTask = ReportAsync();
        }

        public void Add(int length)
        {
            if (_log is null)
            {
                return;
            }

            lock (_sync)
            {
                _messages++;
                _bytes += length;
            }
        }

        public async ValueTask DisposeAsync()
        {
            _timer?.Dispose();
            await _reportTask.ConfigureAwait(false);
            Flush();
        }

        private async Task ReportAsync()
        {
            if (_timer is null)
            {
                return;
            }

            while (await _timer.WaitForNextTickAsync().ConfigureAwait(false))
            {
                Flush();
            }
        }

        private void Flush()
        {
            long messages;
            long bytes;
            lock (_sync)
            {
                messages = _messages;
                bytes = _bytes;
                _messages = 0;
                _bytes = 0;
            }

            if (messages > 0)
            {
                _log?.Invoke($"{_direction}: {messages} message(s), {bytes} byte(s)");
            }
        }
    }
}
