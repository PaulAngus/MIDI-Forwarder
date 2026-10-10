using MidiForwarder.Core;
using Windows.Devices.Midi2;
using Windows.Devices.Midi2.Enumeration;
using Windows.Devices.Midi2.Reporting;

namespace MidiForwarder.Midi.WindowsServices;

/// <summary>
/// A duplex connection to a real (physical or driver-provided) MIDI endpoint via Windows MIDI
/// Services. Unlike the legacy WinMM API, the service multiplexes concurrent client sessions onto
/// the same endpoint, so other applications can keep using the device at the same time.
/// </summary>
public sealed class PhysicalMidiEndpointDuplexPort : IMidiDuplexPort, IMidiSessionPort
{
    private readonly MidiSession _session;
    private readonly MidiEndpointConnection _connection;
    private readonly MidiReceiveBuffer _messages = new();
    private readonly object _sendLock = new();
    private bool _disposed;

    private PhysicalMidiEndpointDuplexPort(string endpointName, string endpointId, MidiSession session, MidiEndpointConnection connection)
    {
        EndpointName = endpointName;
        EndpointId = endpointId;
        _session = session;
        _connection = connection;
        _connection.MessageReceived += OnMessageReceived;
    }

    public string EndpointName { get; }
    public string EndpointId { get; }

    public IReadOnlyList<string> GetOtherSessionNames() => MidiReporting
        .FindAllSessionsWithMatchingOpenUmpEndpoint(EndpointId, true)
        .Where(session => session.ProcessId != (ulong)Environment.ProcessId)
        .Select(session => $"{session.SessionName} (PID {session.ProcessId})")
        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    public string InputName => EndpointName;

    public string OutputName => EndpointName;

    public static PhysicalMidiEndpointDuplexPort Open(string endpointName)
    {
        if (string.IsNullOrWhiteSpace(endpointName))
        {
            throw new InvalidOperationException("Enter the name of a physical MIDI endpoint.");
        }

        if (!MidiApi.EnsureServiceAvailable())
        {
            throw new InvalidOperationException("Windows MIDI Services is unavailable. Install or enable Windows MIDI Services and try again.");
        }

        MidiEndpointDeviceInformation? device = MidiEndpointDeviceInformation
            .FindAll(MidiEndpointDeviceInformationSortOrder.Name, MidiEndpointDeviceInformationFilters.AllStandardEndpoints)
            .FirstOrDefault(candidate => string.Equals(candidate.Name, endpointName, StringComparison.OrdinalIgnoreCase));
        if (device is null)
        {
            throw new IOException($"MIDI endpoint not found: {endpointName}");
        }

        MidiSession? session = null;
        try
        {
            session = MidiSession.Create("MIDI Forwarder Server")
                ?? throw new InvalidOperationException("Windows MIDI Services could not create a MIDI session.");
            MidiEndpointConnection connection = session.CreateEndpointConnection(device.EndpointDeviceId)
                ?? throw new InvalidOperationException($"Windows MIDI Services could not open endpoint: {endpointName}");
            var port = new PhysicalMidiEndpointDuplexPort(device.Name, device.EndpointDeviceId, session, connection);
            if (!connection.Open())
            {
                throw new InvalidOperationException($"Windows MIDI Services could not activate endpoint: {endpointName}");
            }

            return port;
        }
        catch
        {
            session?.Dispose();
            throw;
        }
    }

    public IAsyncEnumerable<MidiPacket> ReadAllAsync(CancellationToken cancellationToken) =>
        _messages.ReadAllAsync(cancellationToken);

    public void BeginSession() => _messages.BeginSession();
    public void EndSession() => _messages.EndSession();
    public MidiQueueStatistics ReceiveStatistics => _messages.Statistics;

    public ValueTask SendAsync(MidiPacket packet, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        packet.Validate();
        if (packet.Format is MidiPacketFormat.UniversalMidiPacket or MidiPacketFormat.UniversalMidiPacketBatch)
        {
            lock (_sendLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                UmpBatchSender.Send(_connection, packet.ToUmpWords());
            }

            return ValueTask.CompletedTask;
        }

        if (packet.Format != MidiPacketFormat.Midi1)
        {
            throw new InvalidDataException($"Unsupported MIDI packet format: {(byte)packet.Format}.");
        }

        IReadOnlyList<uint[]> packets = UmpMidi1Codec.Encode(packet.Data.Span);

        lock (_sendLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            UmpBatchSender.Send(_connection, packets.SelectMany(packet => packet).ToArray());
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _connection.MessageReceived -= OnMessageReceived;
        _session.DisconnectEndpointConnection(_connection.ConnectionId);
        _session.Dispose();
        _messages.Close();
        return ValueTask.CompletedTask;
    }

    private void OnMessageReceived(IMidiMessageReceivedEventSource sender, MidiMessageReceivedEventArgs args)
    {
        try
        {
            byte wordCount = args.FillWords(out uint word0, out uint word1, out uint word2, out uint word3);
            Span<uint> words = stackalloc uint[4] { word0, word1, word2, word3 };
            _messages.Publish(MidiPacket.FromUmpWords(words[..wordCount]));
        }
        catch (InvalidDataException error)
        {
            _messages.Fail(error);
        }
    }

}
