using Avalonia;
using MidiForwarder.Core;
using MightBeUseful.Midi.CoreMidi;
#if CLIENT
using MidiForwarder.Client;
#else
using MidiForwarder.Server;
#endif

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--list-ports", StringComparer.OrdinalIgnoreCase))
            {
                CoreMidiPortCatalog.Print(Console.Out);
                return 0;
            }

            if (args.Contains("--check-virtual-midi", StringComparer.OrdinalIgnoreCase))
            {
                return CheckVirtualMidiAsync().GetAwaiter().GetResult();
            }

            if (args.Contains("--check-network-midi", StringComparer.OrdinalIgnoreCase))
            {
                int nameIndex = Array.IndexOf(args, "--endpoint");
                string name = nameIndex >= 0 && nameIndex + 1 < args.Length ? args[nameIndex + 1] : "Network RTP Session 1";
                return CheckNetworkMidiAsync(name).GetAwaiter().GetResult();
            }

            AppBuilder.Configure<App>().UseAvaloniaNative().UseSkia().LogToTrace()
                .StartWithClassicDesktopLifetime(args);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            DiagnosticLog.WriteException("macos.log", "Application failed", error);
            return 1;
        }
    }

    private static async Task<int> CheckNetworkMidiAsync(string name)
    {
        await using var network = new CoreMidiNetworkPort(name, log: Console.WriteLine);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        Console.WriteLine("Connecting to Apple's local RTP-MIDI session.");
        await using IMidiDuplexPort session = await network.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
        await using var application = CoreMidiDuplexPort.Open(name);
        await using var requests = session.ReadAllAsync(timeout.Token).GetAsyncEnumerator();
        await using var replies = application.ReadAllAsync(timeout.Token).GetAsyncEnumerator();
        byte[][] controls = [[0xc0, 5], [0xf8], [0xb0, 7, 100], [0xf0, 0x7d, 1, 2, 0xf7]];
        foreach (byte[] message in controls)
        {
            await CheckMessageAsync(application, requests, message, timeout.Token).ConfigureAwait(false);
            await CheckMessageAsync(session, replies, message, timeout.Token).ConfigureAwait(false);
        }
        for (int index = 0; index < 100; index++)
        {
            byte[] message = new byte[3082]; message[0] = 0xf0; message[1] = 0x7d;
            message[2] = (byte)index; message[^1] = 0xf7;
            await Task.Delay(50, timeout.Token).ConfigureAwait(false);
            await CheckMessageAsync(session, replies, message, timeout.Token).ConfigureAwait(false);
            await CheckMessageAsync(application, requests, message, timeout.Token).ConfigureAwait(false);
        }
        byte[] large = new byte[70000]; large[0] = 0xf0; large[^1] = 0xf7;
        for (int index = 1; index < large.Length - 1; index++) { large[index] = (byte)(index & 127); }
        await CheckMessageAsync(session, replies, large, timeout.Token).ConfigureAwait(false);
        byte[] reverse = large[..60000]; reverse[^1] = 0xf7;
        await CheckMessageAsync(application, requests, reverse, timeout.Token).ConfigureAwait(false);
        Console.WriteLine("Apple network MIDI: controls, clock, 100 sustained bulk messages both directions and large SysEx passed.");
        return 0;
    }

    private static async Task<int> CheckVirtualMidiAsync()
    {
        string name = $"MIDI Fwd Check {Guid.NewGuid():N}"[..22];
        await using var virtualPort = CoreMidiDuplexPort.OpenVirtual(name);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!CoreMidiPortCatalog.GetNames().Contains(name, StringComparer.Ordinal))
        {
            await Task.Delay(100, timeout.Token).ConfigureAwait(false);
        }

        await using var appPort = CoreMidiDuplexPort.Open(name);
        await using var virtualMessages = virtualPort.ReadAllAsync(timeout.Token).GetAsyncEnumerator();
        await using var appMessages = appPort.ReadAllAsync(timeout.Token).GetAsyncEnumerator();
        byte[][] messages = [[0xc0, 0x05], [0xf8], [0xf0, 0x7d, 1, 2, 3, 4, 5, 6, 7, 0xf7]];
        foreach (byte[] message in messages)
        {
            await CheckMessageAsync(appPort, virtualMessages, message, timeout.Token).ConfigureAwait(false);
            await CheckMessageAsync(virtualPort, appMessages, message, timeout.Token).ConfigureAwait(false);
        }

        // Sustained traffic exposes CoreMIDI loop suppression that a short burst misses.
        for (int index = 0; index < 40; index++)
        {
            byte[] message = new byte[3082];
            message[0] = 0xf0;
            message[1] = 0x7d;
            message[2] = (byte)index;
            message[^1] = 0xf7;
            await Task.Delay(100, timeout.Token).ConfigureAwait(false);
            await CheckMessageAsync(virtualPort, appMessages, message, timeout.Token).ConfigureAwait(false);
            await CheckMessageAsync(appPort, virtualMessages, message, timeout.Token).ConfigureAwait(false);
        }

        Console.WriteLine("CoreMIDI virtual ports, Program Change, MIDI clock and sustained bulk SysEx pass in both directions.");
        return 0;
    }

    private static async Task CheckMessageAsync(IMidiDuplexPort sender, IAsyncEnumerator<MidiPacket> receiver,
        byte[] message, CancellationToken cancellationToken)
    {
        await sender.SendAsync(new MidiPacket(MidiPacketFormat.Midi1, message), cancellationToken).ConfigureAwait(false);
        if (!await receiver.MoveNextAsync().ConfigureAwait(false) || !receiver.Current.Data.Span.SequenceEqual(message))
        {
            throw new IOException("CoreMIDI did not preserve the test message.");
        }
    }
}
