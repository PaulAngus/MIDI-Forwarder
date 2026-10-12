using System.Net;
using System.Net.Sockets;
using MidiForwarder.Client;
using MidiForwarder.Core;
using MidiForwarder.Midi.WindowsServices;
using MidiForwarder.Midi.WinMM;
using MidiForwarder.Server;

namespace MidiForwarder.Checks;

internal static class VirtualGatewayCheck
{
    public static async Task CheckSharedAccessAsync(bool large = false, bool forwarded = true)
    {
        string suffix = Guid.NewGuid().ToString("N")[..6];
        string deviceName = "Shared check device " + suffix;
        string remoteName = "Shared check remote " + suffix;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var hardware = WindowsMidiServicesDuplexPort.Open(deviceName);
        await using var server = new ServerRelayService();
        await using var client = new ClientRelayService();
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        { listener.Start(); port = ((IPEndPoint)listener.LocalEndpoint).Port; }
        string url = $"tcp://127.0.0.1:{port}";
        await server.StartAsync(new ServerSettings { MidiEndpoint = deviceName, LocalPortNames = [], ListenUrl = url });
        await client.StartAsync(new ClientSettings { InterfaceName = remoteName, ServerUri = url });
        await WaitAsync(() => server.CurrentGatewayState.Connections.Count == 1, timeout.Token);
        await using IMidiDuplexPort remote = forwarded
            ? PhysicalMidiEndpointDuplexPort.Open(remoteName)
            : WinMmDuplexPort.Open(deviceName, deviceName);
        await using var direct = WinMmDuplexPort.Open(deviceName, deviceName);
        int exchanges = large ? 100 : 200;
        int requestLength = large ? 131072 : 16384;
        int received = 0;
        var requests = new HashSet<(byte App, int Sequence)>();
        async Task DeviceAsync()
        {
            await foreach (MidiPacket packet in hardware.ReadAllAsync(timeout.Token))
            {
                byte[] request = Decode(packet);
                if (request.Length != requestLength || request[0] != 0xf0 || request[1] != 0x7d || request[^1] != 0xf7 ||
                    request.Skip(5).Take(requestLength - 6).Any(value => value != 0x11))
                { throw new IOException($"Shared device received a damaged or unexpected request: length {request.Length}, app {request.ElementAtOrDefault(2)}, sequence {request.ElementAtOrDefault(3)}."); }
                int sequence = request[3] | request[4] << 7;
                if (!requests.Add((request[2], sequence))) { throw new IOException("Shared device received a duplicate request."); }
                byte[] reply = Enumerable.Repeat((byte)0x55, 1024).ToArray();
                request.AsSpan(0, 5).CopyTo(reply); reply[^1] = 0xf7;
                await hardware.SendAsync(new MidiPacket(MidiPacketFormat.Midi1, reply), timeout.Token);
                if (++received == exchanges * 2) { return; }
            }
            throw new IOException("Shared device stopped before all requests arrived.");
        }
        async Task AppAsync(IMidiDuplexPort app, byte id)
        {
            await using var reader = app.ReadAllAsync(timeout.Token).GetAsyncEnumerator();
            for (int index = 0; index < exchanges; index++)
            {
                byte[] request = Enumerable.Repeat((byte)0x11, requestLength).ToArray();
                request[0] = 0xf0; request[1] = 0x7d; request[2] = id;
                request[3] = (byte)(index & 127); request[4] = (byte)(index >> 7); request[^1] = 0xf7;
                await app.SendAsync(new MidiPacket(MidiPacketFormat.Midi1, request), timeout.Token);
                bool matched = false;
                while (await reader.MoveNextAsync())
                {
                    byte[] reply = Decode(reader.Current);
                    if (reply.Length != 1024 || reply[1] != 0x7d || reply[^1] != 0xf7 || reply.Skip(5).Take(1018).Any(value => value != 0x55))
                    { throw new IOException("Shared app received a damaged reply."); }
                    if (reply[2] != id) { continue; } // Normal MIDI inputs broadcast both apps' replies.
                    if ((reply[3] | reply[4] << 7) != index) { throw new IOException("Shared reply was duplicated or out of order."); }
                    matched = true; break;
                }
                if (!matched) { throw new IOException("Shared input ended without its reply."); }
            }
        }
        Task[] work = [DeviceAsync(), AppAsync(remote, 1), AppAsync(direct, 2)];
        var pending = work.ToList();
        while (pending.Count > 0)
        {
            Task completed = await Task.WhenAny(pending);
            pending.Remove(completed);
            if (completed.IsFaulted || completed.IsCanceled)
            {
                await timeout.CancelAsync();
                try { await Task.WhenAll(work); }
                catch (Exception error) when (error is IOException or OperationCanceledException) { }
            }
            await completed;
        }
        Console.WriteLine($"PASS {(forwarded ? "direct WinMM and forwarded MIDI" : "two direct WinMM clients")}: {received} unique {requestLength / 1024} KiB requests and {exchanges} complete 1 KiB replies per app; no reservation or pause.");
    }

    private static byte[] Decode(MidiPacket packet)
    {
        if (packet.Format == MidiPacketFormat.Midi1) { return packet.Data.ToArray(); }
        if (packet.Format == MidiPacketFormat.UniversalMidiPacketBatch) { return UmpMidi1Codec.DecodeSysEx7Batch(packet); }
        using var assembler = new SysEx7Assembler();
        return UmpMidi1Codec.Decode(packet.ToUmpWords(), assembler) ?? throw new IOException("Incomplete test message.");
    }

    public static Task RunAsync() => RunAsync(null);

    public static async Task RunAsync(Func<Task>? whileRunning)
    {
        string suffix = Guid.NewGuid().ToString("N")[..6];
        string deviceName = "Gw check device " + suffix;
        string[] localNames = ["Gw check local A " + suffix, "Gw check local B " + suffix];
        string[] remoteNames = ["Gw check remote A " + suffix, "Gw check remote B " + suffix];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var hardware = WindowsMidiServicesDuplexPort.Open(deviceName);
        await using var server = new ServerRelayService();
        await using var client = new ClientRelayService();
        string clientStatus = string.Empty;
        client.StatusChanged += (_, status) => Volatile.Write(ref clientStatus, status);
        server.LogMessage += (_, line) => Console.WriteLine(line);
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        { listener.Start(); port = ((IPEndPoint)listener.LocalEndpoint).Port; }
        string url = $"tcp://127.0.0.1:{port}";
        await server.StartAsync(new ServerSettings { MidiEndpoint = deviceName, LocalPortNames = localNames, ListenUrl = url, Token = "gateway-check" });
        await client.StartAsync(new ClientSettings { InterfaceName = remoteNames[0], AdditionalInterfaceNames = [remoteNames[1]], ServerUri = url, Token = "gateway-check" });
        bool Connected() => Volatile.Read(ref clientStatus).Split('\n').All(line => line.TrimEnd().EndsWith(": Connected", StringComparison.Ordinal));
        await WaitAsync(() => server.CurrentGatewayState.Connections.Count == 4 && Connected(), timeout.Token);
        if (whileRunning is not null) { await whileRunning(); }
        await using var appA = PhysicalMidiEndpointDuplexPort.Open(remoteNames[0]);
        await using var appB = PhysicalMidiEndpointDuplexPort.Open(remoteNames[1]);
        await using var localApp = PhysicalMidiEndpointDuplexPort.Open(localNames[0]);
        await using var deviceReader = hardware.ReadAllAsync(timeout.Token).GetAsyncEnumerator();
        var a = UmpMidi1Codec.Encode([0xf0, 0x7d, 1, 1, 1, 1, 1, 1, 1, 0xf7]);
        var b = UmpMidi1Codec.Encode([0xf0, 0x7d, 2, 2, 2, 2, 2, 2, 2, 0xf7]);
        await appA.SendAsync(MidiPacket.FromUmpWords(a[0]), timeout.Token);
        await appB.SendAsync(MidiPacket.FromUmpWords(b[0]), timeout.Token);
        await appA.SendAsync(MidiPacket.FromUmpWords(a[1]), timeout.Token);
        await appB.SendAsync(MidiPacket.FromUmpWords(b[1]), timeout.Token);
        var values = new List<byte>();
        for (int index = 0; index < 2; index++)
        {
            MidiPacket packet = await TakeAsync(deviceReader, packet => packet.Format == MidiPacketFormat.UniversalMidiPacketBatch);
            byte[] data = UmpMidi1Codec.DecodeSysEx7Batch(packet);
            if (data.Skip(2).Take(7).Any(value => value != data[2])) { throw new IOException("SysEx from two applications mixed."); }
            values.Add(data[2]);
        }
        if (!values.Order().SequenceEqual(new byte[] { 1, 2 })) { throw new IOException("Missing virtual SysEx transfer."); }
        Console.WriteLine("Two remote apps: interleaved SysEx7 fragments in group 0 stayed separate through TCP and the single device writer.");
        uint fresh = 0x20904445;
        await appA.SendAsync(MidiPacket.FromUmpWords([fresh]), timeout.Token);
        await TakeAsync(deviceReader, packet => IsWord(packet, fresh));
        await using var appAReader = appA.ReadAllAsync(timeout.Token).GetAsyncEnumerator();
        await using var appBReader = appB.ReadAllAsync(timeout.Token).GetAsyncEnumerator();
        await hardware.SendAsync(MidiPacket.FromUmpWords([fresh]), timeout.Token);
        await TakeAsync(appAReader, packet => IsWord(packet, fresh));
        await TakeAsync(appBReader, packet => IsWord(packet, fresh));
        Console.WriteLine("All four routes stayed connected; device replies reached both remote apps.");
        await client.StopAsync();
        await WaitAsync(() => server.CurrentGatewayState.Connections.Count == 2, timeout.Token);
        await localApp.SendAsync(MidiPacket.FromUmpWords([fresh]), timeout.Token);
        await TakeAsync(deviceReader, packet => IsWord(packet, fresh));
        await server.StopAsync();
        Console.WriteLine("Remote disconnect preserved local forwarding. Client/server released the isolated ports.");
    }
    private static bool IsWord(MidiPacket packet, uint word) => packet.Format == MidiPacketFormat.UniversalMidiPacket &&
        packet.Data.Length == 4 && packet.ToUmpWords()[0] == word;
    private static async Task<MidiPacket> TakeAsync(IAsyncEnumerator<MidiPacket> reader, Func<MidiPacket, bool> accept)
    {
        while (await reader.MoveNextAsync()) { if (accept(reader.Current)) { return reader.Current; } }
        throw new IOException("Virtual MIDI reader ended before the expected message.");
    }
    private static async Task WaitAsync(Func<bool> ready, CancellationToken token)
    { while (!ready()) { await Task.Delay(20, token); } }
}
