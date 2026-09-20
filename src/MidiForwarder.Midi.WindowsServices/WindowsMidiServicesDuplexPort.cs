using System.Threading.Channels;
using MidiForwarder.Core;
using Windows.Devices.Midi2;
using Windows.Devices.Midi2.Transports.Loopback;

namespace MidiForwarder.Midi.WindowsServices;

public sealed class WindowsMidiServicesDuplexPort : IMidiDuplexPort
{
    private const string RelaySuffix = " (Relay)";
    private const string ApplicationSuffix = " (App)";
    private const int MaximumWinMmPortNameLength = 31;
    private readonly MidiSession _session;
    private readonly MidiEndpointConnection _connection;
    private readonly Guid _associationId;
    private readonly SysEx7Assembler _sysEx = new();
    private readonly Channel<ReadOnlyMemory<byte>> _messages = Channel.CreateUnbounded<ReadOnlyMemory<byte>>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly object _sendLock = new();
    private bool _disposed;

    private WindowsMidiServicesDuplexPort(
        string applicationPortName,
        MidiSession session,
        MidiEndpointConnection connection,
        Guid associationId)
    {
        ApplicationPortName = applicationPortName;
        _session = session;
        _connection = connection;
        _associationId = associationId;
        _connection.MessageReceived += OnMessageReceived;
    }

    public string ApplicationPortName { get; }

    public string InputName => ApplicationPortName;

    public string OutputName => ApplicationPortName;

    public static WindowsMidiServicesDuplexPort Open(string interfaceName)
    {
        string rootName = ValidateAndNormalizeName(interfaceName);
        string relayName = rootName + RelaySuffix;
        string applicationName = rootName + ApplicationSuffix;
        MidiSession? session = null;
        MidiEndpointConnection? connection = null;
        Guid associationId = Guid.Empty;

        try
        {
            if (!MidiApi.EnsureServiceAvailable())
            {
                throw new InvalidOperationException("Windows MIDI Services is unavailable. Install or enable Windows MIDI Services and try again.");
            }

            if (!MidiLoopbackManager.IsTransportAvailable)
            {
                throw new InvalidOperationException("The Windows MIDI Services loopback transport is unavailable on this PC.");
            }

            session = MidiSession.Create("MIDI Forwarder Client")
                ?? throw new InvalidOperationException("Windows MIDI Services could not create a MIDI session.");

            string uniqueId = Guid.NewGuid().ToString("N");
            var definitionA = new MidiLoopbackEndpointDefinition
            {
                Name = relayName,
                Description = "Private relay endpoint owned by MIDI Forwarder Client",
                UniqueId = uniqueId + "a",
            };
            var definitionB = new MidiLoopbackEndpointDefinition
            {
                Name = applicationName,
                Description = "Virtual MIDI interface published by MIDI Forwarder Client",
                UniqueId = uniqueId + "b",
            };
            var creationConfig = new MidiLoopbackCreationConfig(definitionA, definitionB);

            MidiLoopbackCreationResponse creationResult = MidiLoopbackManager.CreateTransientLoopback(creationConfig);
            if (!creationResult.Success)
            {
                throw new InvalidOperationException("Windows MIDI Services could not create the virtual MIDI interface.");
            }

            associationId = creationResult.CreatedLoopbackEntry.AssociationId;
            connection = session.CreateEndpointConnection(creationResult.CreatedLoopbackEntry.EndpointA.EndpointDeviceId)
                ?? throw new InvalidOperationException("Windows MIDI Services could not open the relay side of the virtual interface.");
            if (!connection.Open())
            {
                throw new InvalidOperationException("Windows MIDI Services could not activate the relay side of the virtual interface.");
            }

            return new WindowsMidiServicesDuplexPort(applicationName, session, connection, associationId);
        }
        catch
        {
            session?.Dispose();
            if (associationId != Guid.Empty)
            {
                TryRemoveLoopback(associationId);
            }

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
        TryRemoveLoopback(_associationId);
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

    private static string ValidateAndNormalizeName(string interfaceName)
    {
        string name = interfaceName.Trim();
        if (name.Length == 0)
        {
            throw new InvalidOperationException("Enter a name for the virtual MIDI interface.");
        }

        int maximumRootLength = MaximumWinMmPortNameLength - RelaySuffix.Length;
        if (name.Length > maximumRootLength)
        {
            throw new InvalidOperationException($"The virtual MIDI interface name must be {maximumRootLength} characters or fewer.");
        }

        return name;
    }

    private static void TryRemoveLoopback(Guid associationId)
    {
        try
        {
            MidiLoopbackManager.RemoveTransientLoopback(new MidiLoopbackRemovalConfig(associationId));
        }
        catch
        {
            // The service also removes transient endpoints when it restarts.
        }
    }
}
