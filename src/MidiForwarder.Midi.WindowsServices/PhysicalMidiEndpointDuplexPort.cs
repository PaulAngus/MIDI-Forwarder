using System.Threading.Channels;
using MidiForwarder.Core;
using Windows.Devices.Midi2;
using Windows.Devices.Midi2.Enumeration;

namespace MidiForwarder.Midi.WindowsServices;

/// <summary>
/// A duplex connection to a real (physical or driver-provided) MIDI endpoint via Windows MIDI
/// Services. Unlike the legacy WinMM API, the service multiplexes concurrent client sessions onto
/// the same endpoint, so other applications can keep using the device at the same time.
/// </summary>
public sealed class PhysicalMidiEndpointDuplexPort : IMidiDuplexPort
{
    private readonly MidiSession _session;
    private readonly MidiEndpointConnection _connection;
    private readonly SysEx7Assembler _sysEx = new();
    private readonly Channel<ReadOnlyMemory<byte>> _messages = Channel.CreateUnbounded<ReadOnlyMemory<byte>>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly object _sendLock = new();
    private bool _disposed;

    private PhysicalMidiEndpointDuplexPort(string endpointName, MidiSession session, MidiEndpointConnection connection)
    {
        EndpointName = endpointName;
        _session = session;
        _connection = connection;
        _connection.MessageReceived += OnMessageReceived;
    }

    public string EndpointName { get; }

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
            if (!connection.Open())
            {
                throw new InvalidOperationException($"Windows MIDI Services could not activate endpoint: {endpointName}");
            }

            return new PhysicalMidiEndpointDuplexPort(device.Name, session, connection);
        }
        catch
        {
            session?.Dispose();
            throw;
        }
    }

    public IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAllAsync(CancellationToken cancellationToken) =>
        _messages.Reader.ReadAllAsync(cancellationToken);

    public ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<uint[]> packets = UmpMidi1Codec.Encode(message.Span);

        lock (_sendLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (uint[] packet in packets)
            {
                MidiSendMessageResults result = packet.Length switch
                {
                    1 => _connection.SendSingleMessageWords(0, packet[0]),
                    2 => _connection.SendSingleMessageWords(0, packet[0], packet[1]),
                    _ => throw new InvalidDataException("MIDI 1.0 conversion produced an unsupported UMP packet size."),
                };
                if (!MidiEndpointConnection.SendMessageSucceeded(result))
                {
                    throw new IOException($"Windows MIDI Services failed to send a message ({result}).");
                }
            }
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
        _sysEx.Dispose();
        _messages.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    private void OnMessageReceived(IMidiMessageReceivedEventSource sender, MidiMessageReceivedEventArgs args)
    {
        try
        {
            byte wordCount = args.FillWords(out uint word0, out uint word1, out uint word2, out uint word3);
            Span<uint> words = stackalloc uint[4] { word0, word1, word2, word3 };
            byte[]? message = UmpMidi1Codec.Decode(words[..wordCount], _sysEx);
            if (message is not null)
            {
                _messages.Writer.TryWrite(message);
            }
        }
        catch (InvalidDataException error)
        {
            _messages.Writer.TryComplete(error);
        }
    }
}
