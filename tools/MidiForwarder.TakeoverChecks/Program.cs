using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using ApplicationTestLab;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MidiForwarder.Client;
using MidiForwarder.Server;
#if MACOS
using MightBeUseful.Midi.CoreMidi;
#endif

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 3 || args[2] != "--development-check")
            throw new ArgumentException("Provide client/server published payload paths and --development-check. Mac execution is development-VM only.");
        string[] payloads = args[..2].Select(Path.GetFullPath).ToArray();
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string? file = payloads.Select(p => Path.Combine(p, name.Name + ".dll")).FirstOrDefault(File.Exists);
            return file is null ? null : AssemblyLoadContext.Default.LoadFromAssemblyPath(file);
        };
        return Run(payloads);
    }

    private static int Run(string[] payloads)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        AppBuilder.Configure<CheckApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
        Task worker = Task.Run(async () =>
        {
            using var lease = await HostResourceLease.AcquireAsync(lifetime.Token);
            var session = new Session();
            Emit(new { ready = true, machine = Environment.MachineName, os = RuntimeInformation.OSDescription,
                scope = "Exact distributed assemblies; real cross-host TCP; headless production confirmation dialog; injected server MIDI; Windows client MIDI injected, Mac client uses temporary compatibility CoreMIDI ports on development VM.",
                assemblies = new[] { typeof(ClientRelayService).Assembly, typeof(ServerRelayService).Assembly, typeof(MidiForwarder.Core.TcpMidiBridge).Assembly }
                    .Select(a => new { name = a.GetName().Name, path = a.Location, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(a.Location))) }).ToArray(),
                payloads, portsBefore = session.PortsBefore });
            try
            {
                while (await Console.In.ReadLineAsync(lifetime.Token) is { } line)
                {
                    try
                    {
                        using var request = JsonDocument.Parse(line);
                        JsonElement command = request.RootElement;
                        string op = command.GetProperty("op").GetString()!;
                        object? result = await session.ExecuteAsync(op, command, lifetime.Token);
                        Emit(new { ok = true, op, result });
                        if (op == "quit") { break; }
                    }
                    catch (Exception error) { Emit(new { ok = false, error = error.ToString() }); }
                }
            }
            finally { await session.DisposeAsync(); Dispatcher.UIThread.Post(lifetime.Cancel); }
        });
        try { Dispatcher.UIThread.MainLoop(lifetime.Token); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        worker.GetAwaiter().GetResult();
        return 0;
    }

    private static void Emit(object value) => Console.WriteLine("RESULT " + JsonSerializer.Serialize(value));
}

internal sealed class CheckApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
}

internal sealed class Session : IAsyncDisposable
{
    private readonly List<string> events = [];
    private readonly object gate = new();
    private ServerRelayService? server;
    private ClientRelayService? client;
    private ClientWindow? window;
    private MemoryPort? serverMidi;
    private MemoryPort? clientMidi;
    private IMidiDuplexPort? application;
    private IAsyncEnumerator<MidiPacket>? applicationReader;
    private CancellationTokenSource? clientLifetime;
    private int opened;
    private int released;
    public string[] PortsBefore { get; } = Inventory();

    private static string[] Inventory() =>
#if MACOS
        CoreMidiPortCatalog.GetNames().Order(StringComparer.Ordinal).ToArray();
#else
        [];
#endif

    private void Log(string role, string message)
    {
        lock (gate) { events.Add($"{DateTimeOffset.UtcNow:O} {role}: {message}"); }
    }

    public async Task<object?> ExecuteAsync(string op, JsonElement command, CancellationToken cancellation)
    {
        switch (op)
        {
            case "start-server":
                if (server is not null) throw new InvalidOperationException("Stop the server first.");
                opened = released = 0;
                Func<ServerSettings, bool> available = _ => true;
                Func<ServerSettings, IMidiDuplexPort> open = _ =>
                {
                    if (serverMidi is { IsDisposed: false }) throw new InvalidOperationException("Previous server MIDI was not released.");
                    Interlocked.Increment(ref opened);
                    return serverMidi = new MemoryPort(() => Interlocked.Increment(ref released));
                };
                server = (ServerRelayService)Activator.CreateInstance(typeof(ServerRelayService), BindingFlags.Instance | BindingFlags.NonPublic,
                    null, [available, open], null)!;
                server.LogMessage += (_, message) => Log("server", message);
                await server.StartAsync(new ServerSettings
                {
                    MidiEndpoint = "Takeover test device", MidiInput = "Takeover input", MidiOutput = "Takeover output",
                    ListenUrl = command.GetProperty("url").GetString()!, Token = "takeover-cross-host-test",
                    AllowTakeover = command.GetProperty("allow").GetBoolean(),
                }, cancellation);
                return new { listening = server.IsRunning };
            case "start-client":
                if (client is not null) throw new InvalidOperationException("Stop the client first.");
                clientLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                clientMidi = new MemoryPort();
#if MACOS
                client = new ClientRelayService();
#else
                Func<ClientSettings, IMidiDuplexPort> openClient = _ => clientMidi;
                client = (ClientRelayService)Activator.CreateInstance(typeof(ClientRelayService), BindingFlags.Instance | BindingFlags.NonPublic,
                    null, [openClient], null)!;
#endif
                client.LogMessage += (_, message) => Log("client", message);
                string address = command.GetProperty("url").GetString()!;
                window = await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var result = new ClientWindow(client);
                    var configuration = (Window)typeof(ClientWindow).GetField("_configuration", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(result)!;
                    configuration.FindControl<TextBox>("ServerUri")!.Text = address;
                    result.ShowMainWindow();
                    return result;
                });
                string name = "Takeover Check " + Guid.NewGuid().ToString("N")[..10];
                await client.StartAsync(new ClientSettings
                {
                    InterfaceName = name, MidiInput = "Takeover app input", MidiOutput = "Takeover app output",
                    UseAppleNetworkMidi = false, ServerUri = address, Token = "takeover-cross-host-test",
                });
#if MACOS
                application = CoreMidiDuplexPort.Open(name);
                applicationReader = application.ReadAllAsync(clientLifetime.Token).GetAsyncEnumerator(clientLifetime.Token);
#endif
                return new { started = true, name };
            case "status":
                object? prompt = await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    Window? dialog = window?.OwnedWindows.SingleOrDefault(w => w.Name == "TakeoverDialog");
                    return dialog is null ? null : new { title = dialog.Title, visible = dialog.IsVisible,
                        texts = dialog.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToArray(),
                        buttons = dialog.GetVisualDescendants().OfType<Button>().Select(b => new { b.Name, b.IsEnabled }).ToArray() };
                });
                string[] recent;
                lock (gate) { recent = events.TakeLast(20).ToArray(); }
                return new
                {
                    client = client is null ? null : new { client.IsConnected, client.IsPaused, client.ConnectionStatus, client.LastConnectionError },
                    server = server is null ? null : new { server.IsRunning, server.ConnectedClient, server.LastConnectionError, opened, released },
                    prompt, recent,
                };
            case "confirm":
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    Window dialog = window!.OwnedWindows.Single(w => w.Name == "TakeoverDialog");
                    string button = command.GetProperty("accept").GetBoolean() ? "ConfirmTakeoverButton" : "CancelTakeoverButton";
                    dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Name == button)
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                });
                return new { clicked = true };
            case "send-client":
                MidiPacket fromApp = Packet(command);
                if (application is not null) await application.SendAsync(fromApp, cancellation);
                else await clientMidi!.Input.Writer.WriteAsync(fromApp, cancellation);
                return new { sent = fromApp.Data.Length };
            case "send-server":
                await serverMidi!.Input.Writer.WriteAsync(Packet(command), cancellation);
                return new { sent = true };
            case "read-server":
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                {
                    deadline.CancelAfter(TimeSpan.FromSeconds(10));
                    return new { data = Convert.ToHexString((await serverMidi!.Output.Reader.ReadAsync(deadline.Token)).Data.Span) };
                }
            case "read-client":
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                {
                    deadline.CancelAfter(TimeSpan.FromSeconds(10));
                    MidiPacket received;
                    if (applicationReader is not null)
                    {
                        if (!await applicationReader.MoveNextAsync().AsTask().WaitAsync(deadline.Token)) throw new EndOfStreamException();
                        received = applicationReader.Current;
                    }
                    else received = await clientMidi!.Output.Reader.ReadAsync(deadline.Token);
                    return new { data = Convert.ToHexString(received.Data.Span) };
                }
            case "stop-client":
                await StopClientAsync();
                return new { stopped = true, portsRestored = PortsBefore.SequenceEqual(Inventory()) };
            case "stop-server":
                if (server is not null) await server.DisposeAsync();
                server = null;
                return new { stopped = true, opened, released };
            case "quit":
                await DisposeAsync();
                return new { stopped = true, portsRestored = PortsBefore.SequenceEqual(Inventory()), portsAfter = Inventory() };
            default: throw new ArgumentException("Unknown operation: " + op);
        }
    }

    private static MidiPacket Packet(JsonElement command) => new(MidiPacketFormat.Midi1, Convert.FromHexString(command.GetProperty("data").GetString()!));

    private async Task StopClientAsync()
    {
        if (client is not null) { await client.DisposeAsync(); client = null; }
        if (window is not null)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { window.AllowClose(); window.Close(); });
            window = null;
        }
        if (clientLifetime is not null) await clientLifetime.CancelAsync();
        if (applicationReader is not null) { await applicationReader.DisposeAsync(); applicationReader = null; }
        if (application is not null) { await application.DisposeAsync(); application = null; }
        clientLifetime?.Dispose(); clientLifetime = null;
        clientMidi = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopClientAsync();
        if (server is not null) { await server.DisposeAsync(); server = null; }
    }
}

internal sealed class MemoryPort(Action? onDispose = null) : IMidiDuplexPort
{
    public Channel<MidiPacket> Input { get; } = Channel.CreateUnbounded<MidiPacket>();
    public Channel<MidiPacket> Output { get; } = Channel.CreateUnbounded<MidiPacket>();
    public bool IsDisposed { get; private set; }
    public string InputName => "Takeover check input";
    public string OutputName => "Takeover check output";
    public IAsyncEnumerable<MidiPacket> ReadAllAsync(CancellationToken cancellationToken) => Input.Reader.ReadAllAsync(cancellationToken);
    public ValueTask SendAsync(MidiPacket packet, CancellationToken cancellationToken) => Output.Writer.WriteAsync(packet, cancellationToken);
    public ValueTask DisposeAsync()
    {
        if (!IsDisposed) { IsDisposed = true; onDispose?.Invoke(); }
        Input.Writer.TryComplete(); Output.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
