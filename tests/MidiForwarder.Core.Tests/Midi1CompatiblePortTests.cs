using System.Threading.Channels;
using MidiForwarder.Core;
using MightBeUseful.Midi.Windows;

namespace MidiForwarder.Core.Tests;

public sealed class Midi1CompatiblePortTests
{
    [Theory]
    [InlineData(new byte[] { 0x90, 60, 100 })]
    [InlineData(new byte[] { 0xc0, 5 })]
    [InlineData(new byte[] { 0xf2, 1, 2 })]
    [InlineData(new byte[] { 0xf8 })]
    [InlineData(new byte[] { 0xf0, 0x7d, 1, 2, 3, 4, 5, 6, 7, 0xf7 })]
    public async Task SendsModernPeerMessagesToLegacyPortThroughTcpFraming(byte[] expected)
    {
        var hardware = new RecordingPort();
        await using var legacy = new Midi1CompatiblePort(hardware);
        foreach (uint[] words in UmpMidi1Codec.Encode(expected, group: 3))
        {
            using var stream = new MemoryStream();
            await TcpMidiBridge.WriteFrameAsync(stream, MidiPacket.FromUmpWords(words), CancellationToken.None);
            stream.Position = 0;
            MidiPacket? packet = await TcpMidiBridge.ReadFrameAsync(stream, CancellationToken.None);
            await legacy.SendAsync(packet!.Value, CancellationToken.None);
        }
        MidiPacket actual = Assert.Single(hardware.Sent);
        Assert.Equal(MidiPacketFormat.Midi1, actual.Format);
        Assert.Equal(expected, actual.Data.ToArray());
    }

    [Fact]
    public async Task PreservesLegacyPeerMessagesAndDisposesHardware()
    {
        var hardware = new RecordingPort();
        await using (var legacy = new Midi1CompatiblePort(hardware))
        {
            await legacy.SendAsync(new MidiPacket(MidiPacketFormat.Midi1, new byte[] { 0xc0, 42 }), CancellationToken.None);
        }
        Assert.Equal(new byte[] { 0xc0, 42 }, Assert.Single(hardware.Sent).Data.ToArray());
        Assert.True(hardware.Disposed);
    }

    [Fact]
    public async Task ReassemblesInterleavedSysExGroupsWithoutMixingDevices()
    {
        byte[] first = [0xf0, 0x7d, 1, 2, 3, 4, 5, 6, 7, 0xf7];
        byte[] second = [0xf0, 0x7d, 11, 12, 13, 14, 15, 16, 17, 0xf7];
        var hardware = new RecordingPort();
        await using var legacy = new Midi1CompatiblePort(hardware);
        IReadOnlyList<uint[]> a = UmpMidi1Codec.Encode(first, 1);
        IReadOnlyList<uint[]> b = UmpMidi1Codec.Encode(second, 2);
        await legacy.SendAsync(MidiPacket.FromUmpWords(a[0]), CancellationToken.None);
        await legacy.SendAsync(MidiPacket.FromUmpWords(b[0]), CancellationToken.None);
        Assert.Empty(hardware.Sent);
        await legacy.SendAsync(MidiPacket.FromUmpWords(a[1]), CancellationToken.None);
        await legacy.SendAsync(MidiPacket.FromUmpWords(b[1]), CancellationToken.None);
        Assert.Equal(first, hardware.Sent[0].Data.ToArray());
        Assert.Equal(second, hardware.Sent[1].Data.ToArray());
    }

    [Fact]
    public async Task IgnoresUtilityButReportsUnsupportedMidi2()
    {
        var hardware = new RecordingPort();
        await using var legacy = new Midi1CompatiblePort(hardware);
        await legacy.SendAsync(MidiPacket.FromUmpWords([0x00200000]), CancellationToken.None);
        Assert.Empty(hardware.Sent);
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await legacy.SendAsync(MidiPacket.FromUmpWords([0x40903c00, 0x7fff0000]), CancellationToken.None));
    }

    private sealed class RecordingPort : IMidiDuplexPort
    {
        private readonly Channel<MidiPacket> _input = Channel.CreateUnbounded<MidiPacket>();
        public List<MidiPacket> Sent { get; } = [];
        public bool Disposed { get; private set; }
        public string InputName => "Input";
        public string OutputName => "Output";
        public IAsyncEnumerable<MidiPacket> ReadAllAsync(CancellationToken cancellationToken) => _input.Reader.ReadAllAsync(cancellationToken);
        public ValueTask SendAsync(MidiPacket packet, CancellationToken cancellationToken)
        {
            Sent.Add(packet);
            return ValueTask.CompletedTask;
        }
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            _input.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
