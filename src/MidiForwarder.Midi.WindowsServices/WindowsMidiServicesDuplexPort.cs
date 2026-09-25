using System.Buffers.Binary;
using System.Threading.Channels;
using MidiForwarder.Core;
using Windows.Devices.Midi2;
using Windows.Devices.Midi2.Transports.Loopback;

namespace MidiForwarder.Midi.WindowsServices;

public sealed class WindowsMidiServicesDuplexPort : IMidiDuplexPort
{
    private const string RelaySuffix = " (Relay)";
    private const int MaximumWinMmPortNameLength = 31;
    private readonly MidiSession _session;
    private readonly MidiEndpointConnection _connection;
    private readonly Guid _associationId;
    private readonly Channel<MidiPacket> _messages = Channel.CreateUnbounded<MidiPacket>(
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
        string applicationName = rootName;
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
                // Keep the relay accessible to this UMP connection, but out of MIDI 1.0 selectors.
                CreateOnlyUmpEndpoint = true,
            };
            var definitionB = new MidiLoopbackEndpointDefinition
            {
                Name = applicationName,
                Description = "Virtual MIDI interface published by MIDI Forwarder Client",
                UniqueId = uniqueId + "b",
                CreateOnlyUmpEndpoint = false,
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

    public IAsyncEnumerable<MidiPacket> ReadAllAsync(CancellationToken cancellationToken) =>
        _messages.Reader.ReadAllAsync(cancellationToken);

    public ValueTask SendAsync(MidiPacket packet, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        packet.Validate();
        if (packet.Format == MidiPacketFormat.UniversalMidiPacket)
        {
            lock (_sendLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                SendUmpPacket(packet.Data.Span);
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
            foreach (uint[] umpPacket in packets)
            {
                MidiSendMessageResults result = umpPacket.Length switch
                {
                    1 => _connection.SendSingleMessageWords(0, umpPacket[0]),
                    2 => _connection.SendSingleMessageWords(0, umpPacket[0], umpPacket[1]),
                    3 => _connection.SendSingleMessageWords(0, umpPacket[0], umpPacket[1], umpPacket[2]),
                    4 => _connection.SendSingleMessageWords(0, umpPacket[0], umpPacket[1], umpPacket[2], umpPacket[3]),
                    _ => throw new InvalidDataException("UMP packet has an unsupported word count."),
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
        _messages.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    private void OnMessageReceived(IMidiMessageReceivedEventSource sender, MidiMessageReceivedEventArgs args)
    {
        try
        {
            byte wordCount = args.FillWords(out uint word0, out uint word1, out uint word2, out uint word3);
            Span<uint> words = stackalloc uint[4] { word0, word1, word2, word3 };
            _messages.Writer.TryWrite(MidiPacket.FromUmpWords(words[..wordCount]));
        }
        catch (InvalidDataException error)
        {
            _messages.Writer.TryComplete(error);
        }
    }

    private void SendUmpPacket(ReadOnlySpan<byte> umpPacket)
    {
        uint word0 = BinaryPrimitives.ReadUInt32BigEndian(umpPacket);
        MidiSendMessageResults result = umpPacket.Length switch
        {
            4 => _connection.SendSingleMessageWords(0, word0),
            8 => _connection.SendSingleMessageWords(0, word0, BinaryPrimitives.ReadUInt32BigEndian(umpPacket[4..])),
            12 => _connection.SendSingleMessageWords(0, word0, BinaryPrimitives.ReadUInt32BigEndian(umpPacket[4..]), BinaryPrimitives.ReadUInt32BigEndian(umpPacket[8..])),
            16 => _connection.SendSingleMessageWords(0, word0, BinaryPrimitives.ReadUInt32BigEndian(umpPacket[4..]), BinaryPrimitives.ReadUInt32BigEndian(umpPacket[8..]), BinaryPrimitives.ReadUInt32BigEndian(umpPacket[12..])),
            _ => throw new InvalidDataException("UMP packet has an unsupported word count."),
        };
        if (!MidiEndpointConnection.SendMessageSucceeded(result))
        {
            throw new IOException($"Windows MIDI Services failed to send a message ({result}).");
        }
    }

    private static string ValidateAndNormalizeName(string interfaceName)
    {
        string name = interfaceName.Trim();
        if (name.Length == 0)
        {
            throw new InvalidOperationException("Enter a name for the virtual MIDI interface.");
        }

        int maximumRootLength = MaximumWinMmPortNameLength;
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
