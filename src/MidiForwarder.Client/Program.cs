using Avalonia;
using MidiForwarder.Core;
using MightBeUseful.Midi.Windows;
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
            WindowsMidiBackend.Print(Console.Out);
            return 0;
        }

        if (args.Contains("--midi-backend", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine(WindowsMidiBackend.Capabilities.Description);
            return 0;
        }

        if (args.Contains("--check-virtual-midi", StringComparer.OrdinalIgnoreCase))
        {
            // The command-line check has no UI message pump for STA COM callbacks.
            try
            {
                return Task.Run(() => WindowsMidiBackend.Capabilities.CanCreateVirtualPorts
                    ? CheckVirtualMidiAsync() : CheckLegacyMidiAsync(args))
                    .WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
            }
            catch (TimeoutException)
            {
                Console.Error.WriteLine("The virtual MIDI check did not complete within 30 seconds.");
                return 3;
            }
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseWin32()
        .UseSkia()
        .LogToTrace();

    private static async Task<int> CheckVirtualMidiAsync()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26200))
        {
            throw new PlatformNotSupportedException("Windows MIDI Services requires Windows 11 25H2 or later.");
        }
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

    private static async Task<int> CheckLegacyMidiAsync(string[] args)
    {
        try
        {
            string input = GetOption(args, "--midi-input");
            string output = GetOption(args, "--midi-output");
            WindowsMidiBackend.ValidateClientPorts(input, output);
            Console.WriteLine(WindowsMidiBackend.Capabilities.Description);
            await using IMidiDuplexPort relay = WindowsMidiBackend.OpenClient(string.Empty, input, output);
            // The application's ends of the two loopback cables are reversed.
            await using var app = WinMmDuplexPort.Open(output, input);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var relayMessages = relay.ReadAllAsync(timeout.Token).GetAsyncEnumerator();
            await using var appMessages = app.ReadAllAsync(timeout.Token).GetAsyncEnumerator();
            byte[][] messages = [[0xc0, 0x05], [0xf0, 0x7d, 1, 2, 3, 4, 5, 6, 7, 0xf7]];
            foreach (byte[] message in messages)
            {
                await CheckMessageAsync(app, relayMessages, message, timeout.Token).ConfigureAwait(false);
                await CheckMessageAsync(relay, appMessages, message, timeout.Token).ConfigureAwait(false);
            }
            Console.WriteLine("WinMM virtual cables preserve Program Change and SysEx in both directions.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            Console.Error.WriteLine("Usage: --check-virtual-midi --midi-input \"From App\" --midi-output \"To App\". Create both virtual cables first and close other MIDI apps during the check.");
            return 1;
        }
    }

    private static string GetOption(string[] args, string option)
    {
        int index = Array.FindIndex(args, arg => arg.Equals(option, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : string.Empty;
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
