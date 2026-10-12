using MidiForwarder.Core;
using MidiForwarder.Midi.WindowsServices;
using MidiForwarder.Midi.WinMM;
using MidiForwarder.Server;
using System.Net;
using System.Net.Sockets;

namespace MidiForwarder.Checks;

/// <summary>Opt-in hardware diagnostic: only reads the FM9 firmware version.</summary>
internal static class Fm9AccessCheck
{
    public static async Task RunAsync(bool ump, bool withServer)
    {
        await using var server = new ServerRelayService();
        if (withServer)
        {
            int tcpPort;
            using (var listener = new TcpListener(IPAddress.Loopback, 0))
            { listener.Start(); tcpPort = ((IPEndPoint)listener.LocalEndpoint).Port; }
            await server.StartAsync(new ServerSettings
            { MidiEndpoint = "FM9", LocalPortNames = [], ListenUrl = $"tcp://127.0.0.1:{tcpPort}" });
            Console.WriteLine("Test server holds the FM9 endpoint open; no forwarded traffic.");
        }
        if (!ump) { WinMmPortCatalog.Print(Console.Out); }
        await using IMidiDuplexPort port = ump
            ? PhysicalMidiEndpointDuplexPort.Open("FM9")
            : WinMmDuplexPort.Open(
                WinMmPortCatalog.GetInputNames().Single(name => name.Contains("FM9", StringComparison.OrdinalIgnoreCase)),
                WinMmPortCatalog.GetOutputNames().Single(name => name.Contains("FM9", StringComparison.OrdinalIgnoreCase)));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var reader = port.ReadAllAsync(timeout.Token).GetAsyncEnumerator();
        byte[] request = [0xf0, 0, 1, 0x74, 0x12, 0x08, 0x1f, 0xf7];
        using var assembler = new SysEx7Assembler();
        for (int query = 0; query < 5; query++)
        {
            await port.SendAsync(new MidiPacket(MidiPacketFormat.Midi1, request), timeout.Token);
            bool matched = false;
            while (await reader.MoveNextAsync())
            {
                MidiPacket packet = reader.Current;
                byte[]? reply = packet.Format switch
                {
                    MidiPacketFormat.Midi1 => packet.Data.ToArray(),
                    MidiPacketFormat.UniversalMidiPacketBatch => UmpMidi1Codec.DecodeSysEx7Batch(packet),
                    _ => UmpMidi1Codec.Decode(packet.ToUmpWords(), assembler),
                };
                if (reply is null) { continue; }
                Console.WriteLine($"{(ump ? "UMP" : "WinMM")} received {reply.Length} bytes: {Convert.ToHexString(reply.AsSpan(0, Math.Min(reply.Length, 24)))}");
                if (reply.Length >= 10 && reply[0] == 0xf0 && reply[1] == 0 && reply[2] == 1 &&
                    reply[3] == 0x74 && reply[4] == 0x12 && reply[5] == 0x08 && reply[^1] == 0xf7)
                {
                    byte checksum = 0;
                    foreach (byte value in reply.AsSpan(0, reply.Length - 2)) { checksum ^= value; }
                    if (reply[^2] != (checksum & 0x7f)) { throw new IOException("FM9 firmware reply checksum failed."); }
                    Console.WriteLine($"PASS firmware query {query + 1}: {reply[6]}.{reply[7]:00}");
                    matched = true;
                    break;
                }
            }
            if (!matched) { throw new IOException("FM9 input ended without a firmware reply."); }
        }
    }
}
