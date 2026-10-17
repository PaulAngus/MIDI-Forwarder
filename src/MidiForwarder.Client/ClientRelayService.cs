using System.Net.Sockets;
using MidiForwarder.Core;
#if MACOS
using WindowsMidiServicesDuplexPort = MightBeUseful.Midi.CoreMidi.CoreMidiDuplexPort;
using MightBeUseful.Midi.CoreMidi;
#else
using MightBeUseful.Midi.Windows;
#endif

namespace MidiForwarder.Client;

public sealed class ClientRelayService : IAsyncDisposable
{
    private CancellationTokenSource? _cancellation;
    private Task? _runTask;
    private IMidiDuplexPort? _midi;
#if !MACOS
    private readonly Func<ClientSettings, IMidiDuplexPort> _openMidi;

    public ClientRelayService() : this(static settings =>
        WindowsMidiBackend.OpenClient(settings.InterfaceName, settings.MidiInput, settings.MidiOutput))
    {
    }

    internal ClientRelayService(Func<ClientSettings, IMidiDuplexPort> openMidi)
    {
        ArgumentNullException.ThrowIfNull(openMidi);
        _openMidi = openMidi;
    }
#endif
#if MACOS
    private CoreMidiNetworkPort? _networkMidi;
#endif

    public event EventHandler<string>? LogMessage;

    public event EventHandler<string>? StatusChanged;

    public bool IsRunning => _cancellation is not null;

    public bool IsConnected { get; private set; }

    public string ConnectionStatus { get; private set; } = "Stopped";

    public string? LastConnectionError { get; private set; }

    public MidiTrafficMonitor Traffic { get; } = new();

    public Task StartAsync(ClientSettings settings)
    {
        if (IsRunning)
        {
            return Task.CompletedTask;
        }

        if (!settings.IsComplete)
        {
            throw new InvalidOperationException("Complete the MIDI settings and enter a valid tcp:// server address.");
        }

        Uri serverUri = TcpEndpoint.Parse(settings.ServerUri);
#if MACOS
        IMidiDuplexPort? midi = null;
        if (settings.UseAppleNetworkMidi)
        {
            _networkMidi = new CoreMidiNetworkPort(settings.NetworkMidiEndpoint, settings.NetworkMidiSessionPort, WriteLog);
            WriteLog($"Using Apple network MIDI: {settings.NetworkMidiEndpoint}. Local peer: 127.0.0.1:{CoreMidiNetworkPort.PeerPort}.");
        }
        else
        {
            midi = WindowsMidiServicesDuplexPort.OpenVirtual(settings.InterfaceName);
            WriteLog($"Created compatibility virtual MIDI input/output: {settings.InterfaceName}");
        }
#else
        IMidiDuplexPort midi = _openMidi(settings);
        WriteLog(WindowsMidiBackend.Capabilities.Description);
        WriteLog($"MIDI input: {midi.InputName}; output: {midi.OutputName}");
#endif
        var cancellation = new CancellationTokenSource();
        _midi = midi;
        _cancellation = cancellation;
        Traffic.Reset();
        LastConnectionError = null;
        SetStatus("Connecting");
        _runTask = RunReconnectLoopAsync(serverUri, settings.Token, midi, cancellation.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        IsConnected = false;
        CancellationTokenSource? cancellation = Interlocked.Exchange(ref _cancellation, null);
        Task? runTask = Interlocked.Exchange(ref _runTask, null);
        IMidiDuplexPort? midi = Interlocked.Exchange(ref _midi, null);
#if MACOS
        CoreMidiNetworkPort? networkMidi = Interlocked.Exchange(ref _networkMidi, null);
#endif
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

        if (midi is not null)
        {
            await midi.DisposeAsync().ConfigureAwait(false);
        }
#if MACOS
        if (networkMidi is not null) { await networkMidi.DisposeAsync().ConfigureAwait(false); }
#endif

        SetStatus("Stopped");
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task RunReconnectLoopAsync(
        Uri serverUri,
        string token,
        IMidiDuplexPort? midi,
        CancellationToken cancellationToken)
    {
        int retrySeconds = 1;
#if MACOS
        CoreMidiNetworkPort? networkMidi = _networkMidi;
#endif
        while (!cancellationToken.IsCancellationRequested)
        {
            using var client = new TcpClient { NoDelay = true };
            IMidiDuplexPort? connectionMidi = null;
            try
            {
#if MACOS
                if (networkMidi is not null)
                {
                    SetStatus("Waiting for Apple MIDI");
                    WriteLog("Connecting to Apple's local RTP-MIDI session.");
                    connectionMidi = await networkMidi.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                }
#endif
                connectionMidi ??= midi;
                if (connectionMidi is null) { throw new IOException("MIDI interface is unavailable."); }
                SetStatus("Connecting");
                WriteLog($"Connecting to {serverUri}");
                await client.ConnectAsync(serverUri.Host, serverUri.Port, cancellationToken).ConfigureAwait(false);
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                NetworkStream stream = client.GetStream();
                await TcpAuthentication.SendAsync(stream, token, cancellationToken).ConfigureAwait(false);
                retrySeconds = 1;
                Traffic.Reset();
                LastConnectionError = null;
                IsConnected = true;
                SetStatus("Connected");
                WriteLog("Connected to server using low-latency TCP (TCP_NODELAY).");
                await TcpMidiBridge.RunAsync(stream, connectionMidi, WriteLog, cancellationToken, Traffic).ConfigureAwait(false);
                if (!cancellationToken.IsCancellationRequested)
                {
                    LastConnectionError = "The server closed the connection. Check the server and shared token.";
                    WriteLog(LastConnectionError);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error) when (error is SocketException or IOException
#if !MACOS
                or NotSupportedException or NAudio.MmException
#endif
                )
            {
                LastConnectionError = error.Message;
                WriteLog($"Connection ended: {error.Message}");
            }
            finally
            {
                IsConnected = false;
                if (connectionMidi is not null && !ReferenceEquals(connectionMidi, midi))
                {
                    await connectionMidi.DisposeAsync().ConfigureAwait(false);
                }
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                SetStatus($"Reconnecting in {retrySeconds}s");
                await Task.Delay(TimeSpan.FromSeconds(retrySeconds), cancellationToken).ConfigureAwait(false);
                retrySeconds = Math.Min(retrySeconds * 2, 30);
            }
        }
    }

    private void WriteLog(string message) => LogMessage?.Invoke(this, message);

    private void SetStatus(string status)
    {
        ConnectionStatus = status;
        StatusChanged?.Invoke(this, status);
        WriteLog(status);
    }
}
