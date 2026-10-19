using System.Threading.Channels;
using MightBeUseful.Midi;

namespace TestLab.Fm9;

internal sealed class Fm9SharedTransport : IFm9Queries
{
    private readonly IMidiDuplexPort port;
    private readonly Channel<byte[]> replies = Channel.CreateBounded<byte[]>(64);
    private readonly CancellationTokenSource shutdown = new();
    private readonly Task reader;
    private bool poisoned;
    public int Sent { get; private set; }
    internal Fm9SharedTransport(IMidiDuplexPort port)
    {
        this.port = port;
        reader = Read();
    }
    private async Task Read()
    {
        var assemblers = Enumerable.Range(0, 16).Select(_ => new SysEx7Assembler()).ToArray();
        try
        {
            await foreach (var packet in port.ReadAllAsync(shutdown.Token))
            {
                byte[]? bytes;
                if (packet.Format == MidiPacketFormat.Midi1) { bytes = packet.Data.ToArray(); }
                else
                {
                    uint[] words = packet.ToUmpWords();
                    bytes = UmpMidi1Codec.Decode(words, assemblers[(words[0] >> 24) & 15]);
                }
                if (bytes is { Length: > 0 } && bytes[0] == 0xf0 && !replies.Writer.TryWrite(bytes))
                {
                    throw new IOException("FM9 reply queue overflow.");
                }
            }
            replies.Writer.TryComplete();
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        catch (Exception error) { replies.Writer.TryComplete(error); }
        finally { foreach (var assembler in assemblers) { assembler.Dispose(); } }
    }
    private async Task<byte[]> Exchange(int query, byte function, int? length, CancellationToken token)
    {
        byte[] request = Fm9ReadOnly.Query(query);
        Fm9ReadOnly.Authorize(request, token);
        if (poisoned) { throw new IOException("Failed exchange requires disposal and deliberate reopen."); }
        await Task.Delay(100, token);
        while (replies.Reader.TryRead(out _)) { }
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(token);
        bound.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            await port.SendAsync(new MidiPacket(MidiPacketFormat.Midi1, request), bound.Token); Sent++;
            for (int i = 0; i < 256; i++)
            {
                byte[] frame = await replies.Reader.ReadAsync(bound.Token);
                if (frame.Length < 6 || frame[4] != 0x12 || frame[5] != function) { continue; }
                if (!Fm9ReadOnly.Reply(frame, function, length)) { throw new IOException("Malformed FM9 reply."); }
                return frame;
            }
            throw new IOException("Too many unrelated FM9 replies.");
        }
        catch { poisoned = true; throw; }
    }
    public async Task<string> Identify(CancellationToken token)
    {
        var identity = await Exchange(0, 0x64, 10, token);
        if (identity[6] != 0 || identity[7] != 0) { throw new IOException("FM9 identity rejected."); }
        var version = await Exchange(1, 8, null, token);
        if (version.Length < 10) { throw new IOException("Invalid firmware reply length."); }
        return FormattableString.Invariant($"{version[6]}.{version[7]:00}");
    }
    public async Task<string> State(CancellationToken token)
    {
        var preset = await Exchange(2, 0x0d, 42, token);
        var scene = await Exchange(3, 0x0c, 9, token);
        var bracket = await Exchange(2, 0x0d, 42, token);
        if ((preset[6] | preset[7] << 7) >= 512 || scene[6] >= 8 || !preset.SequenceEqual(bracket))
        {
            throw new IOException("Current state invalid or changed during scene read.");
        }
        // Keep personal preset names/numbers out of redistributed evidence.
        return Fm9ReadOnly.Hash(Convert.ToHexString(preset) + Convert.ToHexString(scene));
    }
    public async ValueTask DisposeAsync()
    {
        shutdown.Cancel();
        try { await port.DisposeAsync(); await reader.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { shutdown.Dispose(); }
    }
}
