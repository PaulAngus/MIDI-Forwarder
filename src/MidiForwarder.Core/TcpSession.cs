namespace MidiForwarder.Core;

public enum TcpSessionStatus : byte
{
    Connected = 1,
    TakeoverAvailable = 2,
    Busy = 3,
    Unauthorized = 4,
    Replaced = 5,
    ConnectionChanged = 6,
    Unavailable = 7,
}

// A zero frame length is reserved for session control; MIDI frames always contain a format and data.
public static class TcpSession
{
    public static ValueTask WriteStatusAsync(Stream stream, TcpSessionStatus status, CancellationToken cancellationToken)
        => stream.WriteAsync(new byte[] { 0, 0, 0, 0, (byte)status }, cancellationToken);

    public static async Task<TcpSessionStatus> ReadStatusAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] frame = new byte[5];
        await stream.ReadExactlyAsync(frame, cancellationToken).ConfigureAwait(false);
        if (frame[0] != 0 || frame[1] != 0 || frame[2] != 0 || frame[3] != 0
            || !Enum.IsDefined((TcpSessionStatus)frame[4]))
        {
            throw new InvalidDataException("Invalid server session response.");
        }
        return (TcpSessionStatus)frame[4];
    }

    public static ValueTask SendDecisionAsync(Stream stream, bool takeOver, CancellationToken cancellationToken)
        => stream.WriteAsync(new byte[] { takeOver ? (byte)1 : (byte)0 }, cancellationToken);

    public static async Task<bool> ReadDecisionAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] decision = new byte[1];
        await stream.ReadExactlyAsync(decision, cancellationToken).ConfigureAwait(false);
        if (decision[0] > 1) { throw new InvalidDataException("Invalid takeover decision."); }
        return decision[0] == 1;
    }
}

public sealed class TcpSessionEndedException : IOException
{
    public TcpSessionEndedException() : base("Another client took over this server connection. Disconnect and connect again to request access.")
    {
    }
}
