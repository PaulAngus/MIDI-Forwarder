using MidiForwarder.Core;

namespace MidiForwarder.Core.Tests;

public sealed class TcpMidiBridgeTests
{
    [Fact]
    public async Task RoundTripsLengthPrefixedFrame()
    {
        byte[] expected = [0xf0, 0x00, 0x01, 0x74, 0x10, 0xf7];
        using var stream = new MemoryStream();

        await TcpMidiBridge.WriteFrameAsync(stream, expected, CancellationToken.None);
        stream.Position = 0;
        byte[]? actual = await TcpMidiBridge.ReadFrameAsync(stream, CancellationToken.None);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task ReturnsNullAtCleanEndOfStream()
    {
        using var stream = new MemoryStream();
        byte[]? actual = await TcpMidiBridge.ReadFrameAsync(stream, CancellationToken.None);
        Assert.Null(actual);
    }
}
