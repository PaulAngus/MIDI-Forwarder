using System.Net;
using System.Net.Sockets;
using MidiForwarder.Core;
#if MACOS
using PhysicalMidiEndpointDuplexPort = MightBeUseful.Midi.CoreMidi.CoreMidiDuplexPort;
using PhysicalMidiEndpointCatalog = MightBeUseful.Midi.CoreMidi.CoreMidiPortCatalog;
#else
using MightBeUseful.Midi.Windows;
#endif

namespace MidiForwarder.Server;

public sealed class ServerRelayService : IAsyncDisposable
{
    private readonly Func<ServerSettings, bool> _midiPortsAvailable;
    private readonly Func<ServerSettings, IMidiDuplexPort> _openMidi;
    private readonly SemaphoreSlim _clientGate = new(1, 1);
    private readonly SemaphoreSlim _connectionSlots = new(16, 16);
    private readonly List<Task> _connections = [];
    private ClientSession? _activeSession;
    private CancellationTokenSource? _cancellation;
    private TcpListener? _listener;
    private Task? _runTask;

    public ServerRelayService() : this(AreMidiPortsAvailable)
    {
    }

    internal ServerRelayService(Func<ServerSettings, bool> midiPortsAvailable,
        Func<ServerSettings, IMidiDuplexPort>? openMidi = null)
    {
        ArgumentNullException.ThrowIfNull(midiPortsAvailable);
        _midiPortsAvailable = midiPortsAvailable;
        _openMidi = openMidi ?? OpenMidi;
    }

    public event EventHandler<string>? LogMessage;

    public event EventHandler<string>? StatusChanged;

    public bool IsRunning => _cancellation is not null;

    public MidiTrafficMonitor Traffic { get; } = new();

    public MidiCapture Capture { get; } = new();

    public string? ConnectedClient { get; private set; }

    public string? LastConnectionError { get; private set; }

    public Task StartAsync(ServerSettings settings, CancellationToken cancellationToken = default)
    {
        if (IsRunning)
        {
            return Task.CompletedTask;
        }

        ValidateSettings(settings);
        Uri listenUri = TcpEndpoint.Parse(settings.ListenUrl);
        IPAddress listenAddress = TcpEndpoint.ParseListenAddress(listenUri.Host);
        var listener = new TcpListener(listenAddress, listenUri.Port);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            listener.Start(1);
            Traffic.Reset();
            LastConnectionError = null;
            _listener = listener;
            _cancellation = cancellation;
            _runTask = AcceptClientsAsync(listener, settings, cancellation.Token);
#if !MACOS
            WriteLog(WindowsMidiBackend.Capabilities.Description);
            WriteLog($"Selected MIDI input: {settings.EffectiveMidiInput}; output: {settings.EffectiveMidiOutput}");
#else
            WriteLog($"Selected MIDI endpoint: {settings.MidiEndpoint}");
#endif
            WriteLog("The MIDI endpoint opens when a client connects.");
            SetStatus($"Listening on {settings.ListenUrl}; waiting for a client");
            return Task.CompletedTask;
        }
        catch
        {
            listener.Stop();
            cancellation.Dispose();
            throw;
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cancellation = Interlocked.Exchange(ref _cancellation, null);
        TcpListener? listener = Interlocked.Exchange(ref _listener, null);
        Task? runTask = Interlocked.Exchange(ref _runTask, null);

        if (cancellation is not null)
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            listener?.Stop();
            if (runTask is not null)
            {
                try
                {
                    await runTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                }
                catch (SocketException) when (cancellation.IsCancellationRequested)
                {
                }
            }

            await Task.WhenAll(_connections).ConfigureAwait(false);
            _connections.Clear();
            _activeSession?.Cancellation.Dispose();
            _activeSession = null;

            cancellation.Dispose();
        }

        SetStatus("Stopped");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await Capture.DisposeAsync().ConfigureAwait(false);
        _clientGate.Dispose();
        _connectionSlots.Dispose();
    }

    private async Task AcceptClientsAsync(
        TcpListener listener,
        ServerSettings settings,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            // Once accepted, HandleClientAsync owns cleanup even if shutdown has just begun.
            if (!await _connectionSlots.WaitAsync(0, CancellationToken.None).ConfigureAwait(false))
            {
                client.Dispose();
                continue;
            }

            _connections.RemoveAll(task => task.IsCompleted);
            _connections.Add(HandleClientAsync(client, settings, cancellationToken));
        }
    }

    private async Task HandleClientAsync(
        TcpClient client,
        ServerSettings settings,
        CancellationToken cancellationToken)
    {
        using (client)
        {
            IMidiDuplexPort? midi = null;
            ClientSession? session = null;
            bool negotiateSession = false;
            bool acknowledged = false;
            try
            {
                client.NoDelay = true;
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                NetworkStream stream = client.GetStream();
                using var authenticationTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                authenticationTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                (bool authorized, negotiateSession) = await TcpAuthentication.ReceiveRequestAsync(
                    stream,
                    settings.Token,
                    authenticationTimeout.Token).ConfigureAwait(false);
                if (!authorized)
                {
                    if (ConnectedClient is null)
                    {
                        LastConnectionError = "Client authentication failed. Check that both ends use the same shared token.";
                    }
                    WriteLog("Rejected a client with an invalid token or protocol.");
                    if (negotiateSession)
                    {
                        await TcpSession.WriteStatusAsync(stream, TcpSessionStatus.Unauthorized, authenticationTimeout.Token).ConfigureAwait(false);
                    }
                    return;
                }

                // Never hold the admission gate while the incoming user considers the prompt.
                ClientSession? offeredSession;
                await _clientGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    offeredSession = _activeSession is { Completion.Task.IsCompleted: false } ? _activeSession : null;
                    if (offeredSession is null)
                    {
                        _activeSession?.Cancellation.Dispose();
                        session = new ClientSession(cancellationToken);
                        _activeSession = session;
                    }
                }
                finally { _clientGate.Release(); }

                if (session is null)
                {
                    if (!negotiateSession) { return; }
                    using var decisionTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    decisionTimeout.CancelAfter(TimeSpan.FromMinutes(2));
                    await TcpSession.WriteStatusAsync(stream,
                        settings.AllowTakeover ? TcpSessionStatus.TakeoverAvailable : TcpSessionStatus.Busy,
                        decisionTimeout.Token).ConfigureAwait(false);
                    if (!settings.AllowTakeover
                        || !await TcpSession.ReadDecisionAsync(stream, decisionTimeout.Token).ConfigureAwait(false)) { return; }

                    await _clientGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        ClientSession? current = _activeSession;
                        if (current is not null && !current.Completion.Task.IsCompleted)
                        {
                            if (!ReferenceEquals(current, offeredSession))
                            {
                                await TcpSession.WriteStatusAsync(stream, TcpSessionStatus.ConnectionChanged, decisionTimeout.Token).ConfigureAwait(false);
                                return;
                            }
                            WriteLog("An authenticated client confirmed takeover; releasing the previous connection.");
                            current.Replaced = true;
                            await current.Cancellation.CancelAsync().ConfigureAwait(false);
                            // The previous bridge and MIDI port must fully close before reopening exclusive drivers.
                            await current.Completion.Task.ConfigureAwait(false);
                        }
                        cancellationToken.ThrowIfCancellationRequested();
                        current?.Cancellation.Dispose();
                        session = new ClientSession(cancellationToken);
                        _activeSession = session;
                    }
                    finally { _clientGate.Release(); }
                }

                WriteLog($"Client connected: {client.Client.RemoteEndPoint}");
                SetStatus("Client authenticated; opening MIDI endpoint");

                // Release hardware between connections, including exclusive WinMM drivers.
                midi = _openMidi(settings);
                WriteLog($"MIDI input: {midi.InputName}; output: {midi.OutputName}");
                Traffic.Reset();
                LastConnectionError = null;
                ConnectedClient = client.Client.RemoteEndPoint?.ToString() ?? "Network client";
                if (negotiateSession)
                {
                    await TcpSession.WriteStatusAsync(stream, TcpSessionStatus.Connected, session.Cancellation.Token).ConfigureAwait(false);
                }
                acknowledged = true;
                SetStatus("Client connected; MIDI endpoint open");
                await TcpMidiBridge.RunAsync(stream, midi, WriteLog, session.Cancellation.Token, Traffic, Capture).ConfigureAwait(false);
                WriteLog("Client disconnected.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || session?.Replaced == true)
            {
            }
            catch (OperationCanceledException error)
            {
                if (session is not null || ConnectedClient is null) { LastConnectionError = error.Message; }
                WriteLog($"Client connection ended: {error.Message}");
            }
            catch (Exception error) when (error is IOException or SocketException or EndOfStreamException or InvalidOperationException or NotSupportedException
#if !MACOS
                or NAudio.MmException
#endif
                )
            {
                if (session is not null || ConnectedClient is null) { LastConnectionError = error.Message; }
                WriteLog($"Client connection ended: {error.Message}");
            }
            finally
            {
                try
                {
                    if (session is not null)
                    {
                        ConnectedClient = null;
                        if (midi is not null)
                        {
                            await midi.DisposeAsync().ConfigureAwait(false);
                            WriteLog("MIDI endpoint connection released.");
                        }
                        if (negotiateSession && !cancellationToken.IsCancellationRequested
                            && (session.Replaced || !acknowledged))
                        {
                            // Both bridge pumps have stopped, so this cannot interleave with a MIDI frame.
                            using var noticeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                            try
                            {
                                await TcpSession.WriteStatusAsync(client.GetStream(),
                                    session.Replaced ? TcpSessionStatus.Replaced : TcpSessionStatus.Unavailable,
                                    noticeTimeout.Token).ConfigureAwait(false);
                            }
                            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException) { }
                        }
                        if (IsRunning) { SetStatus($"Listening on {settings.ListenUrl}; waiting for a client"); }
                    }
                }
                finally
                {
                    session?.Completion.TrySetResult();
                    _connectionSlots.Release();
                }
            }
        }
    }

    private sealed class ClientSession(CancellationToken cancellationToken)
    {
        public CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Replaced { get; set; }
    }

    private static IMidiDuplexPort OpenMidi(ServerSettings settings) =>
#if MACOS
        PhysicalMidiEndpointDuplexPort.Open(settings.MidiEndpoint);
#else
        WindowsMidiBackend.OpenPhysical(settings.MidiEndpoint, settings.EffectiveMidiInput, settings.EffectiveMidiOutput);
#endif

    internal void ValidateSettings(ServerSettings settings, bool requireAvailableMidi = true)
    {
        if (!settings.IsComplete)
        {
            throw new InvalidOperationException("Select a physical MIDI endpoint and enter a valid TCP listen address.");
        }

        if (requireAvailableMidi && !_midiPortsAvailable(settings))
        {
            throw new InvalidOperationException("The selected MIDI endpoint or input/output ports are unavailable. Refresh MIDI ports and check the selection.");
        }

        Uri uri = TcpEndpoint.Parse(settings.ListenUrl);
        bool loopback = uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("[::1]", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("::1", StringComparison.OrdinalIgnoreCase);
        if (!loopback && string.IsNullOrWhiteSpace(settings.Token))
        {
            throw new InvalidOperationException("Enter a shared token when listening beyond this computer.");
        }
    }

    private static bool AreMidiPortsAvailable(ServerSettings settings) =>
#if MACOS
        PhysicalMidiEndpointCatalog.GetNames().Contains(settings.MidiEndpoint, StringComparer.OrdinalIgnoreCase);
#else
        WindowsMidiBackend.Capabilities.UseMidiServices
            ? WindowsMidiBackend.GetEndpointNames().Contains(settings.MidiEndpoint, StringComparer.OrdinalIgnoreCase)
            : LegacyMidiPortCatalog.GetInputNames().Contains(settings.EffectiveMidiInput, StringComparer.OrdinalIgnoreCase)
                && LegacyMidiPortCatalog.GetOutputNames().Contains(settings.EffectiveMidiOutput, StringComparer.OrdinalIgnoreCase);
#endif

    private void WriteLog(string message) => LogMessage?.Invoke(this, message);

    private void SetStatus(string status)
    {
        StatusChanged?.Invoke(this, status);
        WriteLog(status);
    }
}
