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
        byte[] header = new byte[sizeof(int) + 1];
        BinaryPrimitives.WriteInt32BigEndian(header, checked(message.Length + 1));
        header[sizeof(int)] = (byte)packet.Format;
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(message, cancellationToken).ConfigureAwait(false);
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
        await foreach (MidiPacket packet in midi.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await WriteFrameAsync(stream, packet, cancellationToken).ConfigureAwait(false);
            log?.Invoke($"MIDI -> network: {packet.Data.Length} byte(s), {packet.Format}");
        }
    }

    private static async Task PumpNetworkToMidiAsync(
        Stream stream,
        IMidiDuplexPort midi,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            MidiPacket? packet = await ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);
            if (packet is null)
            {
                return;
            }

            await midi.SendAsync(packet.Value, cancellationToken).ConfigureAwait(false);
            log?.Invoke($"Network -> MIDI: {packet.Value.Data.Length} byte(s), {packet.Value.Format}");
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
}
