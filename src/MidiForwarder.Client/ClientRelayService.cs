using System.Net.Sockets;
using MidiForwarder.Core;
using MidiForwarder.Midi.WindowsServices;

namespace MidiForwarder.Client;

public sealed class ClientRelayService : IAsyncDisposable
{
    private CancellationTokenSource? _cancellation;
    private Task? _runTask;
    private WindowsMidiServicesDuplexPort? _midi;

    public event EventHandler<string>? LogMessage;

    public event EventHandler<string>? StatusChanged;

    public bool IsRunning => _cancellation is not null;

    public Task StartAsync(ClientSettings settings)
    {
        if (IsRunning)
        {
            return Task.CompletedTask;
        }

        if (!settings.IsComplete)
        {
            throw new InvalidOperationException("Enter a virtual MIDI interface name and a valid tcp:// server address.");
        }

        Uri serverUri = TcpEndpoint.Parse(settings.ServerUri);
        WindowsMidiServicesDuplexPort midi = WindowsMidiServicesDuplexPort.Open(settings.InterfaceName);
        var cancellation = new CancellationTokenSource();
        _midi = midi;
        _cancellation = cancellation;
        _runTask = RunReconnectLoopAsync(serverUri, settings.Token, midi, cancellation.Token);
        WriteLog($"Created virtual MIDI input/output: {midi.ApplicationPortName}");
        SetStatus("Connecting");
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cancellation = Interlocked.Exchange(ref _cancellation, null);
        Task? runTask = Interlocked.Exchange(ref _runTask, null);
        WindowsMidiServicesDuplexPort? midi = Interlocked.Exchange(ref _midi, null);
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
            try
            {
                SetStatus("Connecting");
                WriteLog($"Connecting to {serverUri}");
                await client.ConnectAsync(serverUri.Host, serverUri.Port, cancellationToken).ConfigureAwait(false);
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                NetworkStream stream = client.GetStream();
                await TcpAuthentication.SendAsync(stream, token, cancellationToken).ConfigureAwait(false);
                retrySeconds = 1;
                SetStatus("Connected");
                WriteLog("Connected to server using low-latency TCP (TCP_NODELAY).");
                await TcpMidiBridge.RunAsync(stream, midi, null, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error) when (error is SocketException or IOException)
            {
                WriteLog($"Connection ended: {error.Message}");
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

    private void SetStatus(string status) => StatusChanged?.Invoke(this, status);
}
