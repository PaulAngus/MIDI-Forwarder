using MidiForwarder.Core;

namespace MidiForwarder.Core.Tests;

public sealed class TcpMidiBridgeTests
{
    [Fact]
    public async Task RoundTripsLengthPrefixedFrame()
    {
        var expected = new MidiPacket(MidiPacketFormat.Midi1, new byte[] { 0xf0, 0x00, 0x01, 0x74, 0x10, 0xf7 });
        using var stream = new MemoryStream();

        await TcpMidiBridge.WriteFrameAsync(stream, expected, CancellationToken.None);
        stream.Position = 0;
        MidiPacket? actual = await TcpMidiBridge.ReadFrameAsync(stream, CancellationToken.None);

        Assert.Equal(expected.Format, actual?.Format);
        Assert.Equal(expected.Data.ToArray(), actual?.Data.ToArray());
    }

    [Fact]
    public async Task ReturnsNullAtCleanEndOfStream()
    {
        using var stream = new MemoryStream();
        MidiPacket? actual = await TcpMidiBridge.ReadFrameAsync(stream, CancellationToken.None);
        Assert.Null(actual);
    }

    [Fact]
    public async Task RoundTripsMidi2UmpFrameWithoutChangingGroupOrChannel()
    {
        var expected = MidiPacket.FromUmpWords([0x43913c00, 0x7fffffff]);
        using var stream = new MemoryStream();

        await TcpMidiBridge.WriteFrameAsync(stream, expected, CancellationToken.None);
        stream.Position = 0;
        MidiPacket? actual = await TcpMidiBridge.ReadFrameAsync(stream, CancellationToken.None);

        Assert.Equal(expected.Format, actual?.Format);
        Assert.Equal(expected.Data.ToArray(), actual?.Data.ToArray());
    }
}
