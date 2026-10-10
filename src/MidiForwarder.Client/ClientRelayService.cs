using System.Net.Sockets;
using MidiForwarder.Core;
using MidiForwarder.Midi.WindowsServices;

namespace MidiForwarder.Client;

public sealed class ClientRelayService : IAsyncDisposable
{
    private CancellationTokenSource? _cancellation;
    private Task? _runTask;
    private List<WindowsMidiServicesDuplexPort>? _ports;
    private readonly object _statusLock = new();
    private readonly Dictionary<string, string> _portStates = [];

    public event EventHandler<string>? LogMessage;

    public event EventHandler<string>? StatusChanged;

    public bool IsRunning => _cancellation is not null;

    public async Task StartAsync(ClientSettings settings)
    {
        if (IsRunning)
        {
            return;
        }

        if (!settings.IsComplete)
        {
            throw new InvalidOperationException("Enter a virtual MIDI interface name and a valid tcp:// server address.");
        }

        Uri serverUri = TcpEndpoint.Parse(settings.ServerUri);
        string[] names = new[] { settings.InterfaceName }.Concat(settings.AdditionalInterfaceNames ?? []).Select(name => name?.Trim() ?? string.Empty).ToArray();
        if (names.Length > 8 || names.Any(name => string.IsNullOrWhiteSpace(name) || name.Length > 31 || name.Any(char.IsControl)) ||
            names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length)
        { throw new InvalidOperationException("Use up to eight different MIDI port names, each 1–31 characters long."); }
        var ports = new List<WindowsMidiServicesDuplexPort>();
        try
        {
            foreach (string name in names)
            {
                var midi = WindowsMidiServicesDuplexPort.Open(name);
                midi.EndSession(); // Each app gets a separate connection and offline discard buffer.
                ports.Add(midi);
                WriteLog($"Created virtual MIDI input/output: {midi.ApplicationPortName}");
            }
        }
        catch { foreach (var port in ports) { await port.DisposeAsync().ConfigureAwait(false); } throw; }
        var cancellation = new CancellationTokenSource();
        _ports = ports;
        _cancellation = cancellation;
        lock (_statusLock) { _portStates.Clear(); foreach (string name in names) { _portStates[name] = "Connecting"; } }
        _runTask = Task.WhenAll(ports.Select(midi => RunReconnectLoopAsync(serverUri, settings.Token, midi, cancellation.Token)));
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cancellation = Interlocked.Exchange(ref _cancellation, null);
        Task? runTask = Interlocked.Exchange(ref _runTask, null);
        List<WindowsMidiServicesDuplexPort>? ports = Interlocked.Exchange(ref _ports, null);
        if (cancellation is not null)
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            if (runTask is not null)
            {
                try
                {
                    await runTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                }
            }

            cancellation.Dispose();
        }

        if (ports is not null) { foreach (var midi in ports) { await midi.DisposeAsync().ConfigureAwait(false); } }
        lock (_statusLock) { _portStates.Clear(); }

        SetStatus("Stopped");
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task RunReconnectLoopAsync(
        Uri serverUri,
        string token,
        WindowsMidiServicesDuplexPort midi,
        CancellationToken cancellationToken)
    {
        int retrySeconds = 1;
        while (!cancellationToken.IsCancellationRequested)
        {
            using var client = new TcpClient { NoDelay = true };
            bool waitingForAccess = false;
            try
            {
                SetPortStatus(midi.ApplicationPortName, "Connecting");
                WriteLog($"{midi.ApplicationPortName}: connecting to {serverUri}");
                await client.ConnectAsync(serverUri.Host, serverUri.Port, cancellationToken).ConfigureAwait(false);
                TcpSocketSettings.Configure(client.Client);
                NetworkStream stream = client.GetStream();
                using var authenticationTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                authenticationTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                await TcpAuthentication.AuthenticateAsync(stream, token, midi.ApplicationPortName, authenticationTimeout.Token).ConfigureAwait(false);
                retrySeconds = 1;
                SetPortStatus(midi.ApplicationPortName, "Connected");
                WriteLog($"{midi.ApplicationPortName}: connected to server using low-latency TCP (TCP_NODELAY).");
                await using var diagnostics = new TransportDiagnostics(DiagnosticLog.GetPath("client-transport.jsonl"));
                await TcpMidiBridge.RunAsync(stream, midi, WriteLog, cancellationToken, diagnostics).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (GatewayUnavailableException)
            {
                waitingForAccess = true;
                WriteLog($"{midi.ApplicationPortName}: device reserved for another connection. Waiting for access; offline MIDI is discarded.");
            }
            catch (Exception error) when (error is SocketException or IOException)
            {
                WriteLog($"Connection ended: {error.Message}");
            }
            catch (OperationCanceledException)
            {
                WriteLog("Authentication timed out; check that both applications use protocol version 3.");
            }
            catch (InvalidDataException error)
            {
                WriteLog($"Invalid MIDI data ended the session: {error.Message}");
            }
            finally { midi.EndSession(); }

            if (!cancellationToken.IsCancellationRequested)
            {
                SetPortStatus(midi.ApplicationPortName, waitingForAccess ? $"Waiting for device access; retry in {retrySeconds}s" : $"Reconnecting in {retrySeconds}s");
                await Task.Delay(TimeSpan.FromSeconds(retrySeconds), cancellationToken).ConfigureAwait(false);
                retrySeconds = Math.Min(retrySeconds * 2, 30);
            }
        }
    }

    private void WriteLog(string message) => LogMessage?.Invoke(this, message);

    private void SetStatus(string status) => StatusChanged?.Invoke(this, status);

    private void SetPortStatus(string name, string status)
    {
        string summary;
        lock (_statusLock)
        {
            _portStates[name] = status;
            summary = _portStates.Count == 1 ? $"{name}: {status}" :
                string.Join(Environment.NewLine, _portStates.Select(port => $"{port.Key}: {port.Value}"));
            SetStatus(summary);
        }
    }
}
