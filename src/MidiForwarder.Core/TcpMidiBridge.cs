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
        ReadOnlyMemory<byte> message,
        CancellationToken cancellationToken)
    {
        MidiFrame.Validate(message.Span);
        byte[] header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(header, message.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(message, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] header = new byte[sizeof(int)];
        if (!await ReadExactlyOrEndAsync(stream, header, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        int length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length <= 0 || length > MidiFrame.MaximumLength)
        {
            throw new InvalidDataException($"Invalid MIDI frame length: {length}.");
        }

        byte[] message = new byte[length];
        await stream.ReadExactlyAsync(message, cancellationToken).ConfigureAwait(false);
        return message;
    }

    private static async Task PumpMidiToNetworkAsync(
        Stream stream,
        IMidiDuplexPort midi,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        await foreach (ReadOnlyMemory<byte> message in midi.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await WriteFrameAsync(stream, message, cancellationToken).ConfigureAwait(false);
            log?.Invoke($"MIDI -> network: {message.Length} byte(s)");
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
            byte[]? message = await ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);
            if (message is null)
            {
                return;
            }

            await midi.SendAsync(message, cancellationToken).ConfigureAwait(false);
            log?.Invoke($"Network -> MIDI: {message.Length} byte(s)");
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
