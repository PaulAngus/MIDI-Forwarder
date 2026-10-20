using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace MidiForwarder.Core;

public static class TcpAuthentication
{
    private static readonly byte[] Magic = "MIDIFWD1"u8.ToArray();
    private static readonly byte[] SessionMagic = "MIDIFWD2"u8.ToArray();
    private const int MaximumTokenBytes = 4096;

    public static async Task SendAsync(Stream stream, string token, CancellationToken cancellationToken)
        => await SendAsync(stream, token, false, cancellationToken).ConfigureAwait(false);

    public static async Task SendAsync(Stream stream, string token, bool negotiateSession, CancellationToken cancellationToken)
    {
        byte[] tokenBytes = Encoding.UTF8.GetBytes(token);
        if (tokenBytes.Length > MaximumTokenBytes)
        {
            throw new InvalidOperationException("The shared token is too long.");
        }

        byte[] header = new byte[Magic.Length + sizeof(ushort)];
        (negotiateSession ? SessionMagic : Magic).CopyTo(header, 0);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(Magic.Length), checked((ushort)tokenBytes.Length));
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(tokenBytes, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<bool> ReceiveAndValidateAsync(
        Stream stream,
        string expectedToken,
        CancellationToken cancellationToken)
        => (await ReceiveRequestAsync(stream, expectedToken, cancellationToken).ConfigureAwait(false)).Authorized;

    public static async Task<(bool Authorized, bool NegotiateSession)> ReceiveRequestAsync(
        Stream stream, string expectedToken, CancellationToken cancellationToken)
    {
        byte[] header = new byte[Magic.Length + sizeof(ushort)];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        bool negotiateSession = CryptographicOperations.FixedTimeEquals(header.AsSpan(0, Magic.Length), SessionMagic);
        if (!negotiateSession && !CryptographicOperations.FixedTimeEquals(header.AsSpan(0, Magic.Length), Magic))
        {
            return (false, false);
        }

        int tokenLength = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(Magic.Length));
        if (tokenLength > MaximumTokenBytes)
        {
            return (false, negotiateSession);
        }

        byte[] supplied = new byte[tokenLength];
        await stream.ReadExactlyAsync(supplied, cancellationToken).ConfigureAwait(false);
        byte[] expected = Encoding.UTF8.GetBytes(expectedToken);
        return (supplied.Length == expected.Length
            && CryptographicOperations.FixedTimeEquals(supplied, expected), negotiateSession);
    }
}
