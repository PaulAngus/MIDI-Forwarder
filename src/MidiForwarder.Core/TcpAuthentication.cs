using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace MidiForwarder.Core;

public static class TcpAuthentication
{
    private static readonly byte[] Magic = "MIDIFWD3"u8.ToArray();
    private static readonly byte[] Busy = "MIDIBUSY"u8.ToArray();

    public static Task AcceptAsync(Stream stream, CancellationToken token) => stream.WriteAsync(Magic, token).AsTask();
    public static Task RejectBusyAsync(Stream stream, CancellationToken token) => stream.WriteAsync(Busy, token).AsTask();

    public static async Task AuthenticateAsync(Stream stream, string token, CancellationToken cancellationToken)
        => await AuthenticateAsync(stream, token, string.Empty, cancellationToken).ConfigureAwait(false);

    public static async Task AuthenticateAsync(Stream stream, string token, string interfaceName, CancellationToken cancellationToken)
    {
        await SendAsync(stream, token, interfaceName, cancellationToken).ConfigureAwait(false);
        byte[] response = new byte[Magic.Length];
        await stream.ReadExactlyAsync(response, cancellationToken).ConfigureAwait(false);
        if (response.AsSpan().SequenceEqual(Busy)) { throw new GatewayUnavailableException(); }
        if (!response.AsSpan().SequenceEqual(Magic)) { throw new IOException("Server rejected the MIDI Forwarder protocol version."); }
    }
    private const int MaximumTokenBytes = 4096;

    public static async Task SendAsync(Stream stream, string token, CancellationToken cancellationToken)
        => await SendAsync(stream, token, string.Empty, cancellationToken).ConfigureAwait(false);

    public static async Task SendAsync(Stream stream, string token, string interfaceName, CancellationToken cancellationToken)
    {
        byte[] tokenBytes = Encoding.UTF8.GetBytes(token);
        if (tokenBytes.Length > MaximumTokenBytes)
        {
            throw new InvalidOperationException("The shared token is too long.");
        }
        byte[] name = Encoding.UTF8.GetBytes(interfaceName);
        if (name.Length > 128 || interfaceName.Any(char.IsControl)) { throw new InvalidOperationException("Invalid virtual MIDI port name."); }

        byte[] header = new byte[Magic.Length + sizeof(ushort)];
        Magic.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(Magic.Length), checked((ushort)tokenBytes.Length));
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(tokenBytes, cancellationToken).ConfigureAwait(false);
        byte[] nameLength = new byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16BigEndian(nameLength, (ushort)name.Length);
        await stream.WriteAsync(nameLength, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(name, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<bool> ReceiveAndValidateAsync(
        Stream stream,
        string expectedToken,
        CancellationToken cancellationToken)
        => await ReceiveRequestAsync(stream, expectedToken, cancellationToken).ConfigureAwait(false) is not null;

    public static async Task<string?> ReceiveRequestAsync(Stream stream, string expectedToken, CancellationToken cancellationToken)
    {
        byte[] header = new byte[Magic.Length + sizeof(ushort)];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(header.AsSpan(0, Magic.Length), Magic))
        {
            return null;
        }

        int tokenLength = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(Magic.Length));
        if (tokenLength > MaximumTokenBytes)
        {
            return null;
        }

        byte[] supplied = new byte[tokenLength];
        await stream.ReadExactlyAsync(supplied, cancellationToken).ConfigureAwait(false);
        byte[] expected = Encoding.UTF8.GetBytes(expectedToken);
        if (supplied.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(supplied, expected)) { return null; }
        byte[] nameLength = new byte[sizeof(ushort)];
        await stream.ReadExactlyAsync(nameLength, cancellationToken).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadUInt16BigEndian(nameLength);
        if (length > 128) { return null; }
        byte[] name = new byte[length];
        await stream.ReadExactlyAsync(name, cancellationToken).ConfigureAwait(false);
        string interfaceName = Encoding.UTF8.GetString(name);
        return interfaceName.Any(char.IsControl) ? null : interfaceName;
    }
}
