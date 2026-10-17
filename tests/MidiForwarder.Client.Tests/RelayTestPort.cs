using System.Threading.Channels;

namespace MidiForwarder.Client.Tests;

internal sealed class RelayTestPort : IMidiDuplexPort
{
    public Channel<MidiPacket> Input { get; } = Channel.CreateUnbounded<MidiPacket>();
    public Channel<MidiPacket> Output { get; } = Channel.CreateUnbounded<MidiPacket>();
    public bool IsDisposed { get; private set; }
    public string InputName => "Test MIDI app input";
    public string OutputName => "Test MIDI app output";

    public IAsyncEnumerable<MidiPacket> ReadAllAsync(CancellationToken cancellationToken) =>
        Input.Reader.ReadAllAsync(cancellationToken);

    public ValueTask SendAsync(MidiPacket packet, CancellationToken cancellationToken) =>
        Output.Writer.WriteAsync(packet, cancellationToken);

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        Input.Writer.TryComplete();
        Output.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
