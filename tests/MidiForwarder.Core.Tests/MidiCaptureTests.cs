using System.Text;
using System.Text.Json;
using MidiForwarder.Core;

namespace MidiForwarder.Core.Tests;

public sealed class MidiCaptureTests
{
    [Fact]
    public async Task KeepsFullBytesMarkersAndExistingConnectionWithoutChangingSourceMemory()
    {
        await using var capture = new MidiCapture();
        Assert.False(capture.GetSnapshot().IsRecording);
        string connection = capture.BeginConnection("FM9 input", "FM9 output");
        var stream = new MemoryStream();
        capture.Start(stream, "test.jsonl", "server");
        byte[] data = Enumerable.Range(0, 70000).Select(i => (byte)(i % 127)).ToArray();
        data[0] = 0xf0; data[^1] = 0xf7;
        byte[] expected = data.ToArray();
        capture.Record(connection, "midi-to-network", new MidiPacket(MidiPacketFormat.Midi1, data));
        Array.Fill<byte>(data, 0);
        var ump = MidiPacket.FromUmpWords([0x43913c00, 0x7fffffff]);
        capture.Record(connection, "network-to-midi", ump);
        Assert.True(capture.AddMarker("Renamed scene → before Save"));
        capture.EndConnection(connection);
        await capture.StopAsync();
        JsonElement[] rows = Rows(stream);
        JsonElement[] packets = rows.Where(r => r.GetProperty("kind").GetString() == "packet").ToArray();
        Assert.Equal(2, packets.Length);
        Assert.Equal(Convert.ToHexString(expected), packets[0].GetProperty("hex").GetString());
        Assert.Equal(70000, packets[0].GetProperty("length").GetInt32());
        Assert.Equal(Convert.ToHexString(ump.Data.Span), packets[1].GetProperty("hex").GetString());
        Assert.Equal("UniversalMidiPacket", packets[1].GetProperty("format").GetString());
        Assert.Contains(rows, r => r.GetProperty("kind").GetString() == "connection-start");
        Assert.Contains(rows, r => r.GetProperty("kind").GetString() == "marker" && r.GetProperty("text").GetString() == "Renamed scene → before Save");
        Assert.True(rows[^1].GetProperty("complete").GetBoolean());
        Assert.True(packets[0].GetProperty("utc").GetDateTimeOffset() <= packets[1].GetProperty("utc").GetDateTimeOffset());
        Assert.True(packets[0].GetProperty("elapsedTicks").GetInt64() <= packets[1].GetProperty("elapsedTicks").GetInt64());
        Assert.Equal(0, capture.GetSnapshot().LostRecords);
    }

    [Fact]
    public async Task SlowWriterDoesNotBlockProducersAndReportsQueueLoss()
    {
        await using var capture = new MidiCapture();
        var stream = new SlowStream();
        capture.Start(stream, "test.jsonl", "server");
        var packet = new MidiPacket(MidiPacketFormat.Midi1, new byte[] { 0xc0, 5 });
        // The writer is held before it can consume any records.
        await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (int i = 0; i < 4200; i++) { capture.Record("connection", "midi-to-network", packet); }
        Assert.True(capture.GetSnapshot().LostRecords > 0);
        stream.Release.TrySetResult();
        await capture.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        JsonElement[] rows = Rows(stream);
        Assert.False(rows[^1].GetProperty("complete").GetBoolean());
        Assert.Equal(capture.GetSnapshot().LostRecords, rows[^1].GetProperty("lostRecords").GetInt64());
        Assert.Contains("lost", capture.GetSnapshot().Status);
    }

    [Fact]
    public async Task FileLimitStopsOnlyCaptureAndLeavesValidFooter()
    {
        await using var capture = new MidiCapture();
        var stream = new MemoryStream();
        capture.Start(stream, "test.jsonl", "client", fileLimit: 4096);
        for (int i = 0; i < 100; i++) { capture.AddMarker(new string('x', 200)); }
        await capture.StopAsync();
        Assert.True(stream.ToArray().Length <= 4096);
        JsonElement[] rows = Rows(stream);
        Assert.Equal("file-size-limit", rows[^1].GetProperty("reason").GetString());
        Assert.False(rows[^1].GetProperty("complete").GetBoolean());
        Assert.False(capture.GetSnapshot().IsRecording);
        Assert.Contains("limit", capture.GetSnapshot().Status);
    }

    [Fact]
    public async Task DiskErrorIsVisibleAndDoesNotEscapeIntoForwarding()
    {
        await using var capture = new MidiCapture();
        capture.Start(new FailingStream(), "test.jsonl", "server");
        capture.Record("c", "network-to-midi", new MidiPacket(MidiPacketFormat.Midi1, new byte[] { 0xc0, 5 }));
        await capture.StopAsync();
        Assert.Contains("Capture incomplete", capture.GetSnapshot().Status);
        Assert.Contains("disk failure", capture.GetSnapshot().Status);
        Assert.False(capture.GetSnapshot().IsRecording);
    }

    [Fact]
    public async Task StopDrainsAndNewCaptureStartsWithFreshCounters()
    {
        await using var capture = new MidiCapture();
        var first = new MemoryStream();
        capture.Start(first, "first.jsonl", "server");
        Assert.True(capture.AddMarker("one"));
        await capture.StopAsync();
        Assert.False(capture.AddMarker("after stop"));
        var second = new MemoryStream();
        capture.Start(second, "second.jsonl", "server");
        await capture.StopAsync();
        Assert.DoesNotContain(Rows(second), r => r.GetProperty("kind").GetString() == "marker");
        Assert.Equal("second.jsonl", capture.GetSnapshot().Path);
    }

    private static JsonElement[] Rows(MemoryStream stream) => Encoding.UTF8.GetString(stream.ToArray())
        .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }).ToArray();

    private sealed class SlowStream : MemoryStream
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            await base.WriteAsync(buffer, cancellationToken);
        }
    }

    private sealed class FailingStream : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("Simulated disk failure"));
    }
}
