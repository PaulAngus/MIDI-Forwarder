using System.Net;
using System.Net.Sockets;
using MidiForwarder.Core;
using MidiForwarder.Midi.WindowsServices;

namespace MidiForwarder.Server;

public sealed class ServerRelayService : IAsyncDisposable
{
    private readonly SemaphoreSlim _clientGate = new(1, 1);
    private CancellationTokenSource? _cancellation;
    private TcpListener? _listener;
    private Task? _clientTask;
    private Task? _runTask;

    public event EventHandler<string>? LogMessage;

    public event EventHandler<string>? StatusChanged;

    public bool IsRunning => _cancellation is not null;

    public Task StartAsync(ServerSettings settings, CancellationToken cancellationToken = default)
    {
        if (IsRunning)
        {
            return Task.CompletedTask;
        }

        Validate(settings);
        Uri listenUri = TcpEndpoint.Parse(settings.ListenUrl);
        IPAddress listenAddress = TcpEndpoint.ParseListenAddress(listenUri.Host);
        var listener = new TcpListener(listenAddress, listenUri.Port);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            listener.Start(1);
            _listener = listener;
            _cancellation = cancellation;
            _runTask = AcceptClientsAsync(listener, settings.Token, settings.MidiEndpoint, cancellation.Token);
            SetStatus($"Listening on {settings.ListenUrl}");
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
        _clientGate.Dispose();
    }

    private async Task AcceptClientsAsync(
        TcpListener listener,
        string token,
        string midiEndpoint,
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

            _clientTask = HandleClientAsync(client, token, midiEndpoint, cancellationToken);
        }
    }

    private async Task HandleClientAsync(
        TcpClient client,
        string token,
        string midiEndpoint,
        CancellationToken cancellationToken)
    {
        using (client)
        {
            PhysicalMidiEndpointDuplexPort? midi = null;
            try
            {
                client.NoDelay = true;
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                NetworkStream stream = client.GetStream();
                using var authenticationTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                authenticationTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                bool authorized = await TcpAuthentication.ReceiveAndValidateAsync(
                    stream,
                    token,
                    authenticationTimeout.Token).ConfigureAwait(false);
                if (!authorized)
                {
                    WriteLog("Rejected a client with an invalid token or protocol.");
                    return;
                }

                WriteLog($"Client connected: {client.Client.RemoteEndPoint}");
                SetStatus("Client connected");

                // Windows MIDI Services shares the endpoint with other applications (e.g. the
                // device's own editor), so it can stay connected only for as long as needed.
                midi = PhysicalMidiEndpointDuplexPort.Open(midiEndpoint);
                WriteLog($"Connected to MIDI endpoint: {midi.EndpointName}");
                await TcpMidiBridge.RunAsync(stream, midi, null, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (OperationCanceledException error)
            {
                WriteLog($"Client connection ended: {error.Message}");
            }
            catch (Exception error) when (error is IOException or SocketException or EndOfStreamException or InvalidOperationException)
            {
                WriteLog($"Client connection ended: {error.Message}");
            }
            finally
            {
                if (midi is not null)
                {
                    await midi.DisposeAsync().ConfigureAwait(false);
                    WriteLog("MIDI endpoint connection released.");
                }

                _clientGate.Release();
                if (IsRunning)
                {
                    SetStatus("Listening");
                }
            }
        }
    }

    private static void Validate(ServerSettings settings)
    {
        if (!settings.IsComplete)
        {
            throw new InvalidOperationException("Select a physical MIDI endpoint and enter a valid TCP listen address.");
        }

        if (!PhysicalMidiEndpointCatalog.GetNames().Contains(settings.MidiEndpoint, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"MIDI endpoint not found: {settings.MidiEndpoint}");
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

    private void WriteLog(string message) => LogMessage?.Invoke(this, message);

    private void SetStatus(string status) => StatusChanged?.Invoke(this, status);
}
