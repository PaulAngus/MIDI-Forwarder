using System.Diagnostics;

namespace MidiForwarder.Core;

public sealed record GatewayConnection(string Id, string Name, bool IsNetwork);

/// <summary>One physical connection and writer, with a separate receive buffer for each application.</summary>
public sealed class MidiDeviceGateway : IAsyncDisposable
{
    private readonly IMidiDuplexPort _device;
    private readonly TransportDiagnostics? _diagnostics;
    private readonly Action<string>? _log;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, GatewayPort> _ports = [];
    private readonly List<(IMidiDuplexPort Port, Task Pump)> _locals = [];
    private readonly Task _deviceReader;
    private Exception? _failure;
    private bool _disposed;

    public MidiDeviceGateway(IMidiDuplexPort device, Action<string>? log = null, TransportDiagnostics? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        _device = device; _log = log; _diagnostics = diagnostics;
        (device as IMidiSessionPort)?.BeginSession();
        _deviceReader = ReadDeviceAsync();
    }

    public event EventHandler? StateChanged;
    public IReadOnlyList<GatewayConnection> Connections
    {
        get
        {
            lock (_sync)
            {
                return _ports.Values.Select(port => new GatewayConnection(port.Id, port.InputName, port.IsNetwork)).ToArray();
            }
        }
    }

    public void AddLocalPort(string id, string name, IMidiDuplexPort applicationPort)
    {
        ArgumentNullException.ThrowIfNull(applicationPort);
        GatewayPort route;
        lock (_sync)
        {
            EnsureRunning();
            route = new GatewayPort(this, id, name, false);
            _ports.Add(id, route);
            (applicationPort as IMidiSessionPort)?.BeginSession();
            _locals.Add((applicationPort, RunLocalAsync(route, applicationPort)));
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public IMidiDuplexPort AddNetworkConnection(string id, string name)
    {
        GatewayPort route;
        lock (_sync)
        {
            EnsureRunning();
            route = new GatewayPort(this, id, name, true);
            _ports.Add(id, route);
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
        return route;
    }

    private async ValueTask SendAsync(GatewayPort source, MidiPacket packet, CancellationToken token)
    {
        packet.Validate();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
        await _writer.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                EnsureRunning();
                if (!_ports.TryGetValue(source.Id, out GatewayPort? current) || !ReferenceEquals(current, source))
                { throw new IOException("Gateway connection ended."); }
            }
            long started = Stopwatch.GetTimestamp();
            try { await _device.SendAsync(packet, linked.Token).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException)
            {
                _diagnostics?.Failure("gateway-to-device", packet, Stopwatch.GetElapsedTime(started).TotalMilliseconds, error);
                Fail(error);
                throw;
            }
            _diagnostics?.Record("gateway-to-device", packet, Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                (_device as IMidiSessionPort)?.ReceiveStatistics);
        }
        finally { _writer.Release(); }
    }

    private async Task ReadDeviceAsync()
    {
        try
        {
            await foreach (MidiPacket packet in _device.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                long started = Stopwatch.GetTimestamp();
                lock (_sync)
                {
                    foreach (GatewayPort port in _ports.Values)
                    { port.Receive(packet); }
                }
                _diagnostics?.Record("device-to-gateway", packet, Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    (_device as IMidiSessionPort)?.ReceiveStatistics);
            }
            if (!_stop.IsCancellationRequested) { Fail(new IOException("The device receive connection ended.")); }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException)
        { Fail(error); }
    }

    private async Task RunLocalAsync(GatewayPort route, IMidiDuplexPort local)
    {
        using var token = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        try
        {
            Task incoming = ForwardAsync(local, route, token.Token);
            Task outgoing = ForwardAsync(route, local, token.Token);
            await Task.WhenAny(incoming, outgoing).ConfigureAwait(false);
            await token.CancelAsync().ConfigureAwait(false);
            try { await Task.WhenAll(incoming, outgoing).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException)
        { _log?.Invoke($"Local gateway port '{route.InputName}' ended: {error.GetType().Name}"); }
        finally
        {
            (local as IMidiSessionPort)?.EndSession();
            await RemoveAsync(route).ConfigureAwait(false);
        }
    }

    private static async Task ForwardAsync(IMidiDuplexPort input, IMidiDuplexPort output, CancellationToken token)
    {
        await foreach (MidiPacket packet in input.ReadAllAsync(token).ConfigureAwait(false))
        {
            await output.SendAsync(packet, token).ConfigureAwait(false);
        }
    }

    private async ValueTask RemoveAsync(GatewayPort port)
    {
        await _writer.WaitAsync().ConfigureAwait(false);
        bool changed;
        try
        {
            lock (_sync)
            {
                changed = _ports.TryGetValue(port.Id, out var current) && ReferenceEquals(current, port);
                if (changed) { _ports.Remove(port.Id); }
                port.Close();
            }
        }
        finally { _writer.Release(); }
        if (changed) { StateChanged?.Invoke(this, EventArgs.Empty); }
    }

    private void Fail(Exception error)
    {
        lock (_sync)
        {
            if (_failure is not null || _disposed) { return; }
            _failure = error;
            foreach (GatewayPort port in _ports.Values) { port.Close(); }
        }
        _stop.Cancel();
        _log?.Invoke($"Gateway device connection failed: {error.GetType().Name}. Stop and restart the server.");
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void EnsureRunning()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_failure is not null) { throw new IOException("Gateway device connection failed.", _failure); }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed) { return; }
            _disposed = true;
            foreach (GatewayPort port in _ports.Values) { port.Close(); }
        }
        await _stop.CancelAsync().ConfigureAwait(false);
        await _deviceReader.ConfigureAwait(false);
        await Task.WhenAll(_locals.Select(local => local.Pump)).ConfigureAwait(false);
        await _writer.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var local in _locals) { await local.Port.DisposeAsync().ConfigureAwait(false); }
            _diagnostics?.Finish(_failure, (_device as IMidiSessionPort)?.ReceiveStatistics);
            await _device.DisposeAsync().ConfigureAwait(false);
            if (_diagnostics is not null) { await _diagnostics.DisposeAsync().ConfigureAwait(false); }
        }
        finally { _writer.Release(); }
        // Network session cleanup can still call RemoveAsync after the device reader ends.
        _stop.Dispose();
    }

    private sealed class GatewayPort(MidiDeviceGateway gateway, string id, string name, bool isNetwork) : IMidiDuplexPort, IMidiSessionPort
    {
        private readonly MidiReceiveBuffer _receive = new();
        public string Id { get; } = id;
        public bool IsNetwork { get; } = isNetwork;
        public string InputName { get; } = name;
        public string OutputName => InputName;
        public MidiQueueStatistics ReceiveStatistics => _receive.Statistics;
        public void BeginSession() => _receive.BeginSession();
        public void EndSession() => _receive.EndSession();
        public void Receive(MidiPacket packet) => _receive.Publish(packet);
        public void Close() => _receive.Close();
        public IAsyncEnumerable<MidiPacket> ReadAllAsync(CancellationToken token) => _receive.ReadAllAsync(token);
        public ValueTask SendAsync(MidiPacket packet, CancellationToken token) => gateway.SendAsync(this, packet, token);
        public ValueTask DisposeAsync() => gateway.RemoveAsync(this);
    }
}
