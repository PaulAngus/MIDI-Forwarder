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
    private readonly SemaphoreSlim _clientGate = new(1, 1);
    private CancellationTokenSource? _cancellation;
    private TcpListener? _listener;
    private Task? _clientTask;
    private Task? _runTask;

    public ServerRelayService() : this(AreMidiPortsAvailable)
    {
    }

    internal ServerRelayService(Func<ServerSettings, bool> midiPortsAvailable)
    {
        ArgumentNullException.ThrowIfNull(midiPortsAvailable);
        _midiPortsAvailable = midiPortsAvailable;
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

            Task? clientTask = Interlocked.Exchange(ref _clientTask, null);
            if (clientTask is not null)
            {
                try
                {
                    await clientTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                }
            }

            cancellation.Dispose();
        }

        SetStatus("Stopped");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await Capture.DisposeAsync().ConfigureAwait(false);
        _clientGate.Dispose();
    }

    private async Task AcceptClientsAsync(
        TcpListener listener,
        ServerSettings settings,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            if (!await _clientGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                client.Dispose();
                continue;
            }

            _clientTask = HandleClientAsync(client, settings, cancellationToken);
        }
    }

    private async Task HandleClientAsync(
        TcpClient client,
        ServerSettings settings,
        CancellationToken cancellationToken)
    {
        using (client)
        {
#if MACOS
        IMidiDuplexPort? midi = null;
#else
            IMidiDuplexPort? midi = null;
#endif
            try
            {
                client.NoDelay = true;
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                NetworkStream stream = client.GetStream();
                using var authenticationTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                authenticationTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                bool authorized = await TcpAuthentication.ReceiveAndValidateAsync(
                    stream,
                    settings.Token,
                    authenticationTimeout.Token).ConfigureAwait(false);
                if (!authorized)
                {
                    LastConnectionError = "Client authentication failed. Check that both ends use the same shared token.";
                    WriteLog("Rejected a client with an invalid token or protocol.");
                    return;
                }

                WriteLog($"Client connected: {client.Client.RemoteEndPoint}");
                SetStatus("Client authenticated; opening MIDI endpoint");

                // Release hardware between connections, including exclusive WinMM drivers.
#if MACOS
                midi = PhysicalMidiEndpointDuplexPort.Open(settings.MidiEndpoint);
#else
                midi = WindowsMidiBackend.OpenPhysical(settings.MidiEndpoint, settings.EffectiveMidiInput, settings.EffectiveMidiOutput);
#endif
                WriteLog($"MIDI input: {midi.InputName}; output: {midi.OutputName}");
                Traffic.Reset();
                LastConnectionError = null;
                ConnectedClient = client.Client.RemoteEndPoint?.ToString() ?? "Network client";
                SetStatus("Client connected; MIDI endpoint open");
                await TcpMidiBridge.RunAsync(stream, midi, WriteLog, cancellationToken, Traffic, Capture).ConfigureAwait(false);
                WriteLog("Client disconnected.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (OperationCanceledException error)
            {
                LastConnectionError = error.Message;
                WriteLog($"Client connection ended: {error.Message}");
            }
            catch (Exception error) when (error is IOException or SocketException or EndOfStreamException or InvalidOperationException or NotSupportedException
#if !MACOS
                or NAudio.MmException
#endif
                )
            {
                LastConnectionError = error.Message;
                WriteLog($"Client connection ended: {error.Message}");
            }
            finally
            {
                ConnectedClient = null;
                if (midi is not null)
                {
                    await midi.DisposeAsync().ConfigureAwait(false);
                    WriteLog("MIDI endpoint connection released.");
                }

                _clientGate.Release();
                if (IsRunning)
                {
                    SetStatus($"Listening on {settings.ListenUrl}; waiting for a client");
                }
            }
        }
    }

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
