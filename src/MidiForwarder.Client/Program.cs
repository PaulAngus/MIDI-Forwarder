using Avalonia;
using MidiForwarder.Core;
using MidiForwarder.Midi.WinMM;
using MidiForwarder.Midi.WindowsServices;
using Windows.Devices.Enumeration;
using Windows.Devices.Midi;

namespace MidiForwarder.Client;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--list-ports", StringComparer.OrdinalIgnoreCase))
        {
            WinMmPortCatalog.Print(Console.Out);
            return 0;
        }

        if (args.Contains("--check-virtual-midi", StringComparer.OrdinalIgnoreCase))
        {
            // The command-line check has no UI message pump for STA COM callbacks.
            try
            {
                return Task.Run(CheckVirtualMidiAsync).WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
            }
            catch (TimeoutException)
            {
                Console.Error.WriteLine("Windows MIDI Services did not complete the virtual MIDI check within 30 seconds.");
                return 3;
            }
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .LogToTrace();

    private static async Task<int> CheckVirtualMidiAsync()
    {
        try
        {
            Console.WriteLine("Creating virtual MIDI device...");
            string name = $"MIDI Fwd Check {Guid.NewGuid():N}"[..22];
            await using var virtualPort = WindowsMidiServicesDuplexPort.Open(name);
            Console.WriteLine($"Created {virtualPort.ApplicationPortName}");
            bool visible = false;
            for (int attempt = 0; attempt < 20; attempt++)
            {
                await Task.Delay(250).ConfigureAwait(false);
                DeviceInformationCollection inputs = await DeviceInformation.FindAllAsync(MidiInPort.GetDeviceSelector());
                DeviceInformationCollection outputs = await DeviceInformation.FindAllAsync(MidiOutPort.GetDeviceSelector());
                visible = HasOnlyPublicPort(WinMmPortCatalog.GetInputNames(), name)
                    && HasOnlyPublicPort(WinMmPortCatalog.GetOutputNames(), name)
                    && HasOnlyPublicPort(inputs.Select(port => port.Name), name)
                    && HasOnlyPublicPort(outputs.Select(port => port.Name), name);
                if (visible)
                {
                    break;
                }
            }

            if (!visible)
            {
                Console.Error.WriteLine("Expected exactly one input and output, without App or Relay suffixes, in WinMM and WinRT MIDI 1.0.");
                return 2;
            }

            Console.WriteLine("WinMM and WinRT MIDI 1.0 each expose only the public input and output.");
            await using var appPort = WinMmDuplexPort.Open(name, name);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var relayMessages = virtualPort.ReadAllAsync(timeout.Token).GetAsyncEnumerator();
            await using var appMessages = appPort.ReadAllAsync(timeout.Token).GetAsyncEnumerator();
            byte[][] messages = [[0xc0, 0x05], [0xf0, 0x7d, 1, 2, 3, 4, 5, 6, 7, 0xf7]];
            foreach (byte[] message in messages)
            {
                await CheckMessageAsync(appPort, relayMessages, message, timeout.Token).ConfigureAwait(false);
                await CheckMessageAsync(virtualPort, appMessages, message, timeout.Token).ConfigureAwait(false);
            }

            Console.WriteLine("Program Change and multipart SysEx pass in both directions.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static bool HasOnlyPublicPort(IEnumerable<string> names, string expectedName) =>
        names.Where(name => name.StartsWith(expectedName, StringComparison.Ordinal)).SequenceEqual([expectedName]);

    private static async Task CheckMessageAsync(
        IMidiDuplexPort sender,
        IAsyncEnumerator<MidiPacket> receiver,
        byte[] message,
        CancellationToken cancellationToken)
    {
        await sender.SendAsync(new MidiPacket(MidiPacketFormat.Midi1, message), cancellationToken).ConfigureAwait(false);
        using var sysEx = new SysEx7Assembler();
        while (await receiver.MoveNextAsync().ConfigureAwait(false))
        {
            MidiPacket received = receiver.Current;
            byte[]? actual = received.Format switch
            {
                MidiPacketFormat.Midi1 => received.Data.ToArray(),
                MidiPacketFormat.UniversalMidiPacket => UmpMidi1Codec.Decode(received.ToUmpWords(), sysEx),
                _ => throw new IOException("The virtual MIDI interface returned an unsupported packet format."),
            };
            if (actual is not null)
            {
                if (!actual.AsSpan().SequenceEqual(message))
                {
                    throw new IOException("The virtual MIDI interface did not preserve the test message.");
                }

                return;
            }
        }

        throw new IOException("The virtual MIDI interface ended before returning the test message.");
    }
}
