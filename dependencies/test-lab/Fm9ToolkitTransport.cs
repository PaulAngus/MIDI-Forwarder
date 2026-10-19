using Fractal.Core;
using System.Text.Json;

namespace TestLab.Fm9;

// Exercises the maintained production ReadSession and NativeMidiTransport.
internal sealed class Fm9ToolkitTransport : IFm9Queries
{
    private sealed class Guard(IMidiTransport transport) : IMidiTransport
    {
        internal int Sent { get; private set; }
        public event Action<byte[]>? Received { add => transport.Received += value; remove => transport.Received -= value; }
        public event Action<Exception>? Faulted { add => transport.Faulted += value; remove => transport.Faulted -= value; }
        public async Task SendReadAsync(byte[] request, CancellationToken token)
        {
            Fm9ReadOnly.Authorize(request, token);
            await Task.Delay(100, token);
            await transport.SendReadAsync(request, token); Sent++;
        }
        public ValueTask DisposeAsync() => transport.DisposeAsync();
    }
    private readonly Guard wire;
    private readonly ReadSession session;
    private DeviceContext? device;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);
    public int Sent => wire.Sent;
    internal Fm9ToolkitTransport(Fm9Selection selection)
    {
        // Bind fixed test vectors to the maintained protocol implementation before any open.
        var definition = DeviceDefinition.Create("FM9", null);
        byte[][] generated = [SysexProtocol.IdentityQuery(), SysexProtocol.FirmwareQuery(definition), SysexProtocol.PresetNameQuery(definition), SysexProtocol.CurrentSceneQuery(definition)];
        for (int i = 0; i < generated.Length; i++)
        {
            if (!generated[i].SequenceEqual(Fm9ReadOnly.Query(i))) { throw new InvalidDataException("Read-only query provenance mismatch."); }
        }
        var input = NativeMidiTransport.Ports(true).Where(p => p.Name == selection.LegacyInputName).ToArray();
        var output = NativeMidiTransport.Ports(false).Where(p => p.Name == selection.LegacyOutputName).ToArray();
        if (input.Length != 1 || output.Length != 1) { throw new IOException("Select unique existing FM9 WinMM ports. No fallback."); }
        wire = new Guard(NativeMidiTransport.Open(input[0].Index, output[0].Index));
        session = new ReadSession(wire);
    }
    public async Task<string> Identify(CancellationToken token)
    {
        device = await session.IdentifyAsync("FM9", null, Timeout, token);
        return device.Firmware;
    }
    public async Task<string> State(CancellationToken token)
    {
        var context = device ?? throw new InvalidOperationException("Identify FM9 first.");
        var current = await session.CurrentAsync(context, Timeout, token);
        // ReadSession current is preset+scene; bracket with a second production current read.
        var bracket = await session.CurrentAsync(context, Timeout, token);
        if (current != bracket) { throw new IOException("Current FM9 state changed within snapshot."); }
        return Fm9ReadOnly.Hash(JsonSerializer.Serialize(current));
    }
    public ValueTask DisposeAsync() => session.DisposeAsync();
}
