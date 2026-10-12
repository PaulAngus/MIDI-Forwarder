using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using MidiForwarder.Core;
using MidiForwarder.Midi.WindowsServices;

namespace MidiForwarder.Server;

public sealed record GatewayState(IReadOnlyList<GatewayConnection> Connections, string DirectAccessStatus);

public sealed class ServerRelayService : IAsyncDisposable
{
    private readonly SemaphoreSlim _clientGate = new(8, 8);
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private bool _disposed;
    private readonly ConcurrentDictionary<long, Task> _clients = new();
    private CancellationTokenSource? _cancellation;
    private TcpListener? _listener;
    private Task? _runTask, _monitorTask;
    private MidiDeviceGateway? _gateway;
    private long _nextClient;
    private string _directAccessStatus = "Direct device access has not been checked.";
    private readonly object _statePublishLock = new();

    public event EventHandler<string>? LogMessage;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<GatewayState>? GatewayChanged;
    public bool IsRunning => _cancellation is not null;
    public GatewayState CurrentGatewayState
    {
        get
        {
            MidiDeviceGateway? gateway = _gateway;
            return new GatewayState(gateway?.Connections ?? [], _directAccessStatus);
        }
    }

    public async Task StartAsync(ServerSettings settings, CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await Task.Run(() => StartCoreAsync(settings, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }

    private async Task StartCoreAsync(ServerSettings settings, CancellationToken cancellationToken)
    {
        if (IsRunning) { return; }
        Validate(settings);
        Uri listenUri = TcpEndpoint.Parse(settings.ListenUrl);
        var listener = new TcpListener(TcpEndpoint.ParseListenAddress(listenUri.Host), listenUri.Port);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        MidiDeviceGateway? gateway = null;
        try
        {
            PhysicalMidiEndpointDuplexPort physical = PhysicalMidiEndpointDuplexPort.Open(settings.MidiEndpoint);
            gateway = new MidiDeviceGateway(physical, WriteLog,
                new TransportDiagnostics(DiagnosticLog.GetPath("server-gateway.jsonl")));
            foreach (string name in settings.LocalPortNames)
            {
                WindowsMidiServicesDuplexPort local = WindowsMidiServicesDuplexPort.Open(name.Trim());
                try { gateway.AddLocalPort("local-" + name.Trim(), name.Trim(), local); }
                catch { await local.DisposeAsync().ConfigureAwait(false); throw; }
                WriteLog($"Local gateway port created: {name.Trim()}. Select it for one app's MIDI input and output.");
            }
            listener.Start(8);
            _gateway = gateway; _listener = listener; _cancellation = cancellation;
            gateway.StateChanged += OnGatewayChanged;
            _runTask = AcceptClientsAsync(listener, settings.Token, gateway, cancellation.Token);
            _monitorTask = MonitorDirectAccessAsync(physical, cancellation.Token);
            WriteLog($"Gateway opened physical MIDI endpoint: {physical.EndpointName}. All gateway traffic uses one device writer.");
            SetStatus($"Listening on {settings.ListenUrl}");
            PublishGatewayState();
        }
        catch
        {
            listener.Stop(); cancellation.Dispose();
            if (gateway is not null) { await gateway.DisposeAsync().ConfigureAwait(false); }
            throw;
        }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try { await Task.Run(StopCoreAsync).ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
    }

    private async Task StopCoreAsync()
    {
        CancellationTokenSource? cancellation = Interlocked.Exchange(ref _cancellation, null);
        TcpListener? listener = Interlocked.Exchange(ref _listener, null);
        Task? runTask = Interlocked.Exchange(ref _runTask, null);
        Task? monitor = Interlocked.Exchange(ref _monitorTask, null);
        MidiDeviceGateway? gateway = Interlocked.Exchange(ref _gateway, null);
        if (cancellation is not null)
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            listener?.Stop();
            if (runTask is not null)
            {
                try { await runTask.ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                catch (SocketException) when (cancellation.IsCancellationRequested) { }
            }
            await Task.WhenAll(_clients.Values).ConfigureAwait(false);
            if (monitor is not null) { await monitor.ConfigureAwait(false); }
            if (gateway is not null)
            {
                gateway.StateChanged -= OnGatewayChanged;
                await gateway.DisposeAsync().ConfigureAwait(false);
            }
            cancellation.Dispose();
        }
        _directAccessStatus = "Direct device access has not been checked.";
        SetStatus("Stopped"); PublishGatewayState();
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) { return; }
            _disposed = true;
            await Task.Run(StopCoreAsync).ConfigureAwait(false);
            _clientGate.Dispose();
        }
        finally { _lifecycle.Release(); }
    }

    private async Task AcceptClientsAsync(TcpListener listener, string token, MidiDeviceGateway gateway, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            if (!await _clientGate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) { client.Dispose(); continue; }
            long id = Interlocked.Increment(ref _nextClient);
            Task task = HandleClientAsync(client, token, gateway, id, cancellationToken);
            _clients[id] = task;
            _ = ForgetClientAsync(id, task);
        }
    }

    private async Task ForgetClientAsync(long id, Task task)
    {
        try { await task.ConfigureAwait(false); }
        finally { _clients.TryRemove(id, out _); }
    }

    private async Task HandleClientAsync(TcpClient client, string token, MidiDeviceGateway gateway, long id, CancellationToken cancellationToken)
    {
        using (client)
        {
            IMidiDuplexPort? route = null;
            try
            {
                TcpSocketSettings.Configure(client.Client);
                NetworkStream stream = client.GetStream();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                string? interfaceName = await TcpAuthentication.ReceiveRequestAsync(stream, token, timeout.Token).ConfigureAwait(false);
                if (interfaceName is null)
                { WriteLog("Rejected a client with an invalid token or protocol."); return; }
                string name = string.IsNullOrEmpty(interfaceName) ? $"Remote connection {id}" : $"Remote: {interfaceName}";
                route = gateway.AddNetworkConnection("remote-" + id, $"{name} ({client.Client.RemoteEndPoint})");
                await TcpAuthentication.AcceptAsync(stream, timeout.Token).ConfigureAwait(false);
                WriteLog($"{route.InputName} connected to the gateway.");
                await using var diagnostics = new TransportDiagnostics(DiagnosticLog.GetPath("server-transport.jsonl"));
                await TcpMidiBridge.RunAsync(stream, route, WriteLog, cancellationToken, diagnostics).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (OperationCanceledException) { WriteLog("Client authentication timed out."); }
            catch (Exception error) when (error is IOException or SocketException or InvalidOperationException or InvalidDataException)
            { WriteLog($"Client connection ended: {error.Message}"); }
            finally
            {
                if (route is not null) { await route.DisposeAsync().ConfigureAwait(false); }
                _clientGate.Release();
            }
        }
    }

    private async Task MonitorDirectAccessAsync(PhysicalMidiEndpointDuplexPort physical, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            do
            {
                string status;
                try
                {
                    IReadOnlyList<string> sessions = physical.GetOtherSessionNames();
                    status = sessions.Count == 0 ? "No other Windows MIDI Services sessions detected on the physical device." :
                        $"Direct device access: {string.Join(", ", sessions)}. MIDI forwarding remains shared.";
                }
                catch (Exception error) when (error is COMException or InvalidOperationException or NotSupportedException)
                { status = "Other apps' direct device access could not be checked on this Windows MIDI Services version."; }
                if (status != _directAccessStatus)
                { _directAccessStatus = status; WriteLog(status); PublishGatewayState(); }
            } while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void OnGatewayChanged(object? sender, EventArgs args) => PublishGatewayState();
    private void PublishGatewayState()
    {
        lock (_statePublishLock) { GatewayChanged?.Invoke(this, CurrentGatewayState); }
    }
    private void WriteLog(string message) => LogMessage?.Invoke(this, message);
    private void SetStatus(string status) => StatusChanged?.Invoke(this, status);

    private static void Validate(ServerSettings settings)
    {
        if (!settings.IsComplete) { throw new InvalidOperationException("Select a physical MIDI endpoint and enter a valid TCP listen address."); }
        if (!PhysicalMidiEndpointCatalog.GetNames().Contains(settings.MidiEndpoint, StringComparer.OrdinalIgnoreCase))
        { throw new InvalidOperationException($"MIDI endpoint not found: {settings.MidiEndpoint}"); }
        if (settings.LocalPortNames is null || settings.LocalPortNames.Length > 8 ||
            settings.LocalPortNames.Any(name => string.IsNullOrWhiteSpace(name) || name.Trim().Length > 31 || name.Any(char.IsControl)) ||
            settings.LocalPortNames.Select(name => name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != settings.LocalPortNames.Length)
        { throw new InvalidOperationException("Use up to eight different local port names, each 1–31 characters long."); }
        if (settings.LocalPortNames.Any(name => name.Trim().Equals(settings.MidiEndpoint, StringComparison.OrdinalIgnoreCase)))
        { throw new InvalidOperationException("Give virtual ports different names from the physical device."); }
        Uri uri = TcpEndpoint.Parse(settings.ListenUrl);
        bool loopback = uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) || uri.Host.Trim('[', ']').Equals("::1", StringComparison.OrdinalIgnoreCase);
        if (!loopback && string.IsNullOrWhiteSpace(settings.Token))
        { throw new InvalidOperationException("Enter a shared token when listening beyond this computer."); }
    }
}
