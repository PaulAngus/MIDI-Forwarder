using MidiForwarder.Core;

namespace MidiForwarder.Core.Tests;

public sealed class TcpSessionTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task AuthenticationRecognizesBothProtocolsAndValidatesTheToken(bool negotiate, bool correctToken)
    {
        using var stream = new MemoryStream();
        await TcpAuthentication.SendAsync(stream, correctToken ? "shared-token" : "wrong-token", negotiate, CancellationToken.None);
        stream.Position = 0;
        Assert.Equal((correctToken, negotiate), await TcpAuthentication.ReceiveRequestAsync(stream, "shared-token", CancellationToken.None));
    }

    [Theory]
    [InlineData(TcpSessionStatus.Connected)]
    [InlineData(TcpSessionStatus.TakeoverAvailable)]
    [InlineData(TcpSessionStatus.Busy)]
    [InlineData(TcpSessionStatus.Unauthorized)]
    [InlineData(TcpSessionStatus.Replaced)]
    [InlineData(TcpSessionStatus.ConnectionChanged)]
    [InlineData(TcpSessionStatus.Unavailable)]
    public async Task SessionResponsesRoundTrip(TcpSessionStatus status)
    {
        using var stream = new MemoryStream();
        await TcpSession.WriteStatusAsync(stream, status, CancellationToken.None);
        stream.Position = 0;
        Assert.Equal(status, await TcpSession.ReadStatusAsync(stream, CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmationRoundTrips(bool takeOver)
    {
        using var stream = new MemoryStream();
        await TcpSession.SendDecisionAsync(stream, takeOver, CancellationToken.None);
        stream.Position = 0;
        Assert.Equal(takeOver, await TcpSession.ReadDecisionAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task InvalidConfirmationCannotBeTreatedAsConsent()
    {
        using var stream = new MemoryStream([2]);
        await Assert.ThrowsAsync<InvalidDataException>(() => TcpSession.ReadDecisionAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task SessionFramesCannotBeForwardedAsMidi()
    {
        using var stream = new MemoryStream();
        await TcpSession.WriteStatusAsync(stream, TcpSessionStatus.Replaced, CancellationToken.None);
        stream.Position = 0;
        await Assert.ThrowsAsync<TcpSessionEndedException>(() => TcpMidiBridge.ReadFrameAsync(stream, CancellationToken.None));
        stream.SetLength(0);
        stream.Position = 0;
        await TcpSession.WriteStatusAsync(stream, TcpSessionStatus.TakeoverAvailable, CancellationToken.None);
        stream.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => TcpMidiBridge.ReadFrameAsync(stream, CancellationToken.None));
    }
}
