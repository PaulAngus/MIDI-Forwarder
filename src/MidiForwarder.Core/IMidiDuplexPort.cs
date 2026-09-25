namespace MidiForwarder.Core;

/// <summary>
/// A pair of MIDI ports viewed as one duplex endpoint. Messages read from the
/// input are forwarded to the network; messages received from the network are
/// written to the output.
/// </summary>
public interface IMidiDuplexPort : IAsyncDisposable
{
    string InputName { get; }

    string OutputName { get; }

    IAsyncEnumerable<MidiPacket> ReadAllAsync(CancellationToken cancellationToken);

    ValueTask SendAsync(MidiPacket packet, CancellationToken cancellationToken);
}

