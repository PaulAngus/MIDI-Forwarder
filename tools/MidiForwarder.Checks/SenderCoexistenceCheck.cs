using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using NAudio.Midi;

namespace MidiForwarder.Checks;

internal static class SenderCoexistenceCheck
{
    public static async Task RunAsync(string senderExe)
    {
        // Use the real published Preset Sender host, including its separately loaded SDK.
        // Keep it connected across forwarder startup, traffic, restart and shutdown.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        Console.WriteLine("Starting Preset Sender before Forwarder.");
        using var sender = Start(senderExe);
        try
        {
            await ExpectAsync(sender, "READY", timeout.Token);
            Console.WriteLine("Preset Sender ready; opening two receiving applications.");
            using var first = new SenderReceiver();
            using var second = new SenderReceiver();
            var a = first.Messages;
            var b = second.Messages;
            async Task CheckSenderAsync()
            {
                for (int channel = 0; channel < 16; channel++)
                {
                    int raw = 0xc0 | channel | (42 << 8);
                    await sender.StandardInput.WriteLineAsync(raw.ToString(CultureInfo.InvariantCulture));
                    await sender.StandardInput.FlushAsync(timeout.Token);
                    await ExpectAsync(sender, "OK", timeout.Token);
                    await ExpectMessageAsync(a, raw, timeout.Token);
                    await ExpectMessageAsync(b, raw, timeout.Token);
                }
                Console.WriteLine("Preset Sender: both receivers got all 16 channels.");
            }
            await CheckSenderAsync();
            for (int restart = 0; restart < 2; restart++)
            {
                Console.WriteLine($"Forwarder start/stop cycle {restart + 1} with Preset Sender connected.");
                await VirtualGatewayCheck.RunAsync(CheckSenderAsync);
                await CheckSenderAsync();
            }
        }
        finally { await CloseAsync(sender); }
        // Reverse startup order: publish a sender while all four forwarding routes are live.
        Console.WriteLine("Starting Forwarder before Preset Sender.");
        await VirtualGatewayCheck.RunAsync(async () =>
        {
            using var lateSender = Start(senderExe);
            try
            {
                await ExpectAsync(lateSender, "READY", timeout.Token);
                using var receiver = new SenderReceiver();
                var packets = receiver.Messages;
                await lateSender.StandardInput.WriteLineAsync((0xcf | (127 << 8)).ToString(CultureInfo.InvariantCulture));
                await lateSender.StandardInput.FlushAsync(timeout.Token);
                await ExpectAsync(lateSender, "OK", timeout.Token);
                await ExpectMessageAsync(packets, 0xcf | (127 << 8), timeout.Token);
            }
            finally { await CloseAsync(lateSender); }
        });
        Console.WriteLine("PASS: both startup orders, two receivers, all channels, three forwarder cycles and bidirectional SysEx. No physical MIDI hardware was used.");
    }

    private static Process Start(string exe)
    {
        var start = new ProcessStartInfo(Path.GetFullPath(exe))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true
        };
        start.ArgumentList.Add("--virtual-midi-host");
        return Process.Start(start) ?? throw new IOException("Could not start Preset Sender.");
    }

    private static async Task ExpectAsync(Process process, string expected, CancellationToken token)
    {
        string? line = await process.StandardOutput.ReadLineAsync(token);
        if (line != expected) { throw new IOException($"Preset Sender returned {line ?? "EOF"}; expected {expected}."); }
    }

    private static async Task ExpectMessageAsync(ChannelReader<int> messages, int expected, CancellationToken token)
    {
        int message = await messages.ReadAsync(token);
        if (message != expected) { throw new IOException($"Preset Sender returned {message:X}; expected {expected:X}."); }
    }

    private sealed class SenderReceiver : IDisposable
    {
        private readonly MidiIn _input;
        private readonly Channel<int> _messages = Channel.CreateUnbounded<int>();
        public ChannelReader<int> Messages => _messages.Reader;
        public SenderReceiver()
        {
            int index = Enumerable.Range(0, MidiIn.NumberOfDevices)
                .First(index => MidiIn.DeviceInfo(index).ProductName == "Preset Sender");
            _input = new MidiIn(index);
            _input.MessageReceived += (_, args) => _messages.Writer.TryWrite(args.RawMessage);
            _input.Start();
        }
        public void Dispose() { _input.Stop(); _input.Dispose(); }
    }

    private static async Task CloseAsync(Process process)
    {
        if (process.HasExited) { return; }
        process.StandardInput.Close();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (TimeoutException)
        {
            process.Kill();
            await process.WaitForExitAsync();
            throw new IOException("Preset Sender could not remove its device within three seconds.");
        }
    }
}
