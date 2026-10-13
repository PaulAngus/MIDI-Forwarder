using Avalonia;
using MidiForwarder.Core;
using MidiForwarder.Midi.CoreMidi;
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

    private static async Task<int> CheckVirtualMidiAsync()
    {
        string name = $"MIDI Fwd Check {Guid.NewGuid():N}"[..22];
        await using var virtualPort = CoreMidiDuplexPort.OpenVirtual(name);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
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

        Console.WriteLine("CoreMIDI virtual ports, Program Change, MIDI clock and SysEx pass in both directions.");
        return 0;
    }

    private static async Task CheckMessageAsync(CoreMidiDuplexPort sender, IAsyncEnumerator<MidiPacket> receiver,
        byte[] message, CancellationToken cancellationToken)
    {
        await sender.SendAsync(new MidiPacket(MidiPacketFormat.Midi1, message), cancellationToken).ConfigureAwait(false);
        if (!await receiver.MoveNextAsync().ConfigureAwait(false) || !receiver.Current.Data.Span.SequenceEqual(message))
        {
            throw new IOException("CoreMIDI did not preserve the test message.");
        }
    }
}
