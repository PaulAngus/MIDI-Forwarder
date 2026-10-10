using System.Net;
using System.Net.Sockets;
using MidiForwarder.Client;
using MidiForwarder.Core;
using MidiForwarder.Midi.WindowsServices;
using MidiForwarder.Server;

namespace MidiForwarder.Checks;

internal static class VirtualGatewayCheck
{
    public static async Task RunAsync()
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
        string localId = server.CurrentGatewayState.Connections.Single(connection => connection.Name == localNames[0]).Id;
        await server.ReserveAsync(localId);
        await WaitAsync(() => Volatile.Read(ref clientStatus).Contains("Waiting for device access", StringComparison.Ordinal), timeout.Token);
        uint stale = 0x20905051, marker = 0x20904041, fresh = 0x20904445;
        await appA.SendAsync(MidiPacket.FromUmpWords([stale]), timeout.Token);
        await localApp.SendAsync(MidiPacket.FromUmpWords([marker]), timeout.Token);
        await TakeAsync(deviceReader, packet => IsWord(packet, marker));
        await server.ReserveAsync(null);
        await WaitAsync(() => server.CurrentGatewayState.Connections.Count == 4 && Connected(), timeout.Token);
        await appA.SendAsync(MidiPacket.FromUmpWords([fresh]), timeout.Token);
        await TakeAsync(deviceReader, packet =>
        {
            if (IsWord(packet, stale)) { throw new IOException("Paused command replayed after release."); }
            return IsWord(packet, fresh);
        });
        await using var appAReader = appA.ReadAllAsync(timeout.Token).GetAsyncEnumerator();
        await using var appBReader = appB.ReadAllAsync(timeout.Token).GetAsyncEnumerator();
        await hardware.SendAsync(MidiPacket.FromUmpWords([fresh]), timeout.Token);
        await TakeAsync(appAReader, packet => IsWord(packet, fresh));
        await TakeAsync(appBReader, packet => IsWord(packet, fresh));
        Console.WriteLine("Local reservation paused both remote connections; release reconnected them without replaying the offline command. Replies reached both apps in shared mode.");
        string remoteId = server.CurrentGatewayState.Connections.Single(connection => connection.Name.Contains(remoteNames[0], StringComparison.Ordinal)).Id;
        await server.ReserveAsync(remoteId);
        await client.StopAsync();
        await WaitAsync(() => server.CurrentGatewayState.OwnerId is null && server.CurrentGatewayState.Connections.Count == 2, timeout.Token);
        await server.StopAsync();
        Console.WriteLine("Owner disconnect released the reservation. Client/server stopped and released the isolated virtual device and ports.");
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
