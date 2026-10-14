using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using MidiForwarder.Core;

namespace MidiForwarder.Midi.CoreMidi;

/// <summary>
/// A loopback Apple RTP-MIDI peer. An Audio MIDI Setup network session
/// supplies the native, driver-owned source and destination used by MIDI apps.
/// Each negotiated connection has its own MIDI queues, sequence and decoders.
/// </summary>
public sealed class CoreMidiNetworkPort : IAsyncDisposable
{
    public const int PeerPort = 55006;
    private readonly object _gate = new();
    private readonly UdpClient _control;
    private readonly UdpClient _data;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task[] _tasks;
    private readonly string _endpointName;
    private readonly int _driverPort;
    private readonly Action<string>? _log;
    private TaskCompletionSource _changed = NewSignal();
    private Session? _session;
    private IOException? _failure;
    private bool _disposed;
    private bool _policyWarning;

    public CoreMidiNetworkPort(string endpointName, int driverPort = 5004, Action<string>? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointName);
        if (driverPort is < 1 or > 65534 || driverPort is PeerPort - 1 or PeerPort or PeerPort + 1)
        {
            throw new ArgumentOutOfRangeException(nameof(driverPort), "Choose a separate Apple RTP session port between 1 and 65534.");
        }
        if (!CoreMidiPortCatalog.GetNames().Contains(endpointName, StringComparer.Ordinal))
        {
            throw new IOException($"Enable '{endpointName}' in Audio MIDI Setup's network settings, then connect again.");
        }

        _endpointName = endpointName;
        _driverPort = driverPort;
        _log = log;
        _control = new UdpClient(new IPEndPoint(IPAddress.Loopback, PeerPort));
        UdpClient? data = null;
        try
        {
            data = new UdpClient(new IPEndPoint(IPAddress.Loopback, PeerPort + 1));
            data.Client.ReceiveBufferSize = Math.Max(data.Client.ReceiveBufferSize, 262144);
            data.Client.SendBufferSize = Math.Max(data.Client.SendBufferSize, 262144);
            _data = data;
        }
        catch
        {
            _control.Dispose();
            data?.Dispose();
            _stop.Dispose();
            throw;
        }
        _tasks = [PumpAsync(_control, false), PumpAsync(_data, true), MonitorAsync()];
    }

    public async Task<IMidiDuplexPort> WaitForConnectionAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_failure is not null) { throw new IOException("The local MIDI listener stopped. Disconnect and connect the client again.", _failure); }
                if (_session is { Ready: true, Closed: false }) { return _session; }
                changed = _changed.Task;
            }
            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void Signal()
    {
        TaskCompletionSource previous = _changed;
        _changed = NewSignal();
        previous.TrySetResult();
    }

    private static ulong Clock() => (ulong)(Stopwatch.GetTimestamp() * 10000.0 / Stopwatch.Frequency);

    private async Task PumpAsync(UdpClient socket, bool dataPort)
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                UdpReceiveResult received = await socket.ReceiveAsync(_stop.Token).ConfigureAwait(false);
                if (!IPAddress.IsLoopback(received.RemoteEndPoint.Address)
                    || received.RemoteEndPoint.Port != _driverPort + (dataPort ? 1 : 0)) { continue; }
                byte[]? response = null;
                Session? failed = null;
                lock (_gate)
                {
                    if (_disposed) { return; }
                    try
                    {
                        response = Handle(received.Buffer, dataPort);
                    }
                    catch (IOException error)
                    {
                        failed = _session;
                        if (failed is not null) { End(failed, error); }
                        _log?.Invoke(error.Message);
                    }
                }
                if (response is not null) { await socket.SendAsync(response, received.RemoteEndPoint, _stop.Token).ConfigureAwait(false); }
                if (failed is not null) { await SendExitAsync(failed).ConfigureAwait(false); }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (SocketException error) when (!_stop.IsCancellationRequested) { Fail(error); }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
    }

    // Called under _gate by both receive pumps. Malformed local input ends the
    // session and its TCP bridge, preventing partial or stale MIDI from escaping.
    private byte[]? Handle(byte[] packet, bool dataPort)
    {
        if (packet.Length >= 4 && packet[0] == 255 && packet[1] == 255)
        {
            ushort command = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2));
            if (command is 0x4f4b or 0x4e4f && packet.Length >= 16 // OK / NO for our invitation.
                && _session is { Closed: false, Initiator: true } invited
                && BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(8)) == invited.InvitationToken)
            {
                if (command == 0x4e4f)
                {
                    End(invited, new IOException("Apple MIDI declined the local connection."));
                    if (!_policyWarning)
                    {
                        _policyWarning = true;
                        _log?.Invoke("In Audio MIDI Setup, allow computers in the directory and add 127.0.0.1:55006. The app will retry automatically.");
                    }
                    return null;
                }
                if (BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4)) != 2) { return null; }
                uint remoteSender = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(12));
                if (invited.RemoteSender != 0 && invited.RemoteSender != remoteSender)
                {
                    throw new InvalidDataException("Apple MIDI changed its session identifier during negotiation.");
                }
                invited.RemoteSender = remoteSender;
                invited.LastSeen = Stopwatch.GetTimestamp();
                if (dataPort) { invited.DataToken = invited.InvitationToken; }
                else { invited.ControlToken = invited.InvitationToken; }
                return null;
            }
            if (command == 0x494e) // IN
            {
                if (packet.Length < 16 || BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4)) != 2)
                {
                    throw new InvalidDataException("Unsupported local Apple MIDI invitation.");
                }
                uint token = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(8));
                uint sender = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(12));
                uint? previousToken = dataPort ? _session?.DataToken : _session?.ControlToken;
                if (_session is null || _session.Closed || _session.RemoteSender != sender
                    || (previousToken.HasValue && previousToken.Value != token))
                {
                    if (_session is not null) { End(_session, new IOException("Apple MIDI replaced its connection.")); }
                    _session = new Session(this, sender);
                    _log?.Invoke("Apple network MIDI connected; calibrating its clock.");
                }
                Session current = _session;
                current.LastSeen = Stopwatch.GetTimestamp();
                if (dataPort) { current.DataToken = token; }
                else { current.ControlToken = token; }
                byte[] response = ControlPacket(0x4f4b, token, current.Sender, true); // OK
                return response;
            }

            if (_session is not { Closed: false } session) { return null; }
            if (command == 0x434b && dataPort && packet.Length == 36 // CK
                && BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4)) == session.RemoteSender
                && session.ControlToken.HasValue && session.DataToken.HasValue)
            {
                session.LastSeen = Stopwatch.GetTimestamp();
                if (session.Initiator && packet[8] == 1
                    && BinaryPrimitives.ReadUInt64BigEndian(packet.AsSpan(12)) == session.PendingClock)
                {
                    byte[] response = packet.ToArray();
                    BinaryPrimitives.WriteUInt32BigEndian(response.AsSpan(4), session.Sender);
                    response[8] = 2;
                    BinaryPrimitives.WriteUInt64BigEndian(response.AsSpan(28), Clock());
                    session.PendingClock = 0;
                    MarkSynchronized(session);
                    return response;
                }
                if (packet[8] == 0)
                {
                    byte[] response = packet.ToArray();
                    BinaryPrimitives.WriteUInt32BigEndian(response.AsSpan(4), session.Sender);
                    response[8] = 1;
                    BinaryPrimitives.WriteUInt64BigEndian(response.AsSpan(20), Clock());
                    return response;
                }
                if (!session.Initiator && packet[8] == 2)
                {
                    MarkSynchronized(session);
                }
            }
            else if (command == 0x4259 && packet.Length >= 16 // BY
                && BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(12)) == session.RemoteSender)
            {
                End(session, new IOException("Apple network MIDI disconnected."));
                _log?.Invoke("Apple network MIDI disconnected; waiting for its persistent peer to reconnect.");
            }
            return null; // RS feedback needs no journal retention on this loopback sender.
        }

        if (dataPort && _session is { Closed: false } active)
        {
            active.LastSeen = Stopwatch.GetTimestamp();
            IReadOnlyList<byte[]> messages = active.Input.Decode(packet, active.RemoteSender);
            if (active.Ready)
            {
                foreach (byte[] message in messages) { active.Enqueue(message); }
            }
            // Feedback belongs on the control socket, so send it from that socket.
            byte[] feedback = new byte[12];
            feedback[0] = feedback[1] = 255; feedback[2] = (byte)'R'; feedback[3] = (byte)'S';
            BinaryPrimitives.WriteUInt32BigEndian(feedback.AsSpan(4), active.Sender);
            packet.AsSpan(2, 2).CopyTo(feedback.AsSpan(8));
            _control.Send(feedback, new IPEndPoint(IPAddress.Loopback, _driverPort));
        }
        return null;
    }

    private void MarkSynchronized(Session session)
    {
        if (++session.SyncExchanges >= 6 && !session.Ready)
        {
            session.Ready = true;
            Signal();
            _log?.Invoke($"Apple network MIDI ready: {_endpointName}");
        }
    }

    private static byte[] ControlPacket(ushort command, uint token, uint sender, bool name = false)
    {
        byte[] label = name ? Encoding.UTF8.GetBytes("MIDI Forwarder\0") : [];
        byte[] packet = new byte[16 + label.Length]; packet[0] = packet[1] = 255;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), command);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), token);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(12), sender);
        label.CopyTo(packet, 16);
        return packet;
    }

    private void End(Session session, IOException? error)
    {
        if (session.Closed) { return; }
        session.Closed = true;
        session.Messages.Writer.TryComplete(error);
        session.Input.Dispose();
        Signal();
    }

    private void Fail(SocketException error)
    {
        lock (_gate)
        {
            if (_disposed) { return; }
            _failure = new IOException("Local Apple MIDI socket failed.", error);
            if (_session is not null) { End(_session, _failure); }
            Signal();
        }
        _log?.Invoke(_failure.Message);
    }

    private async Task MonitorAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                Session? expired = null;
                byte[]? initiate = null;
                bool dataPort = false;
                lock (_gate)
                {
                    if (_disposed) { return; }
                    if (_session is null || _session.Closed)
                    {
                        _session = new Session(this, 0) { Initiator = true };
                    }
                    if (_session is { Closed: false } current
                        && Stopwatch.GetElapsedTime(current.LastSeen) > TimeSpan.FromSeconds(75))
                    {
                        expired = current;
                        End(current, new IOException("Apple network MIDI clock timed out."));
                    }
                    else if (_session is { Closed: false, Initiator: true } active)
                    {
                        if (!active.ControlToken.HasValue || !active.DataToken.HasValue)
                        {
                            dataPort = active.ControlToken.HasValue;
                            initiate = ControlPacket(0x494e, active.InvitationToken, active.Sender, true);
                        }
                        else if (!active.Ready || Stopwatch.GetElapsedTime(active.LastSync) > TimeSpan.FromSeconds(10))
                        {
                            active.LastSync = Stopwatch.GetTimestamp();
                            active.PendingClock = Clock();
                            dataPort = true;
                            initiate = new byte[36]; initiate[0] = initiate[1] = 255;
                            initiate[2] = (byte)'C'; initiate[3] = (byte)'K';
                            BinaryPrimitives.WriteUInt32BigEndian(initiate.AsSpan(4), active.Sender);
                            BinaryPrimitives.WriteUInt64BigEndian(initiate.AsSpan(12), active.PendingClock);
                        }
                    }
                }
                if (initiate is not null)
                {
                    await (dataPort ? _data : _control).SendAsync(initiate,
                        new IPEndPoint(IPAddress.Loopback, _driverPort + (dataPort ? 1 : 0)), _stop.Token).ConfigureAwait(false);
                }
                if (expired is not null)
                {
                    _log?.Invoke("Apple network MIDI clock timed out; restarting its connection.");
                    await SendExitAsync(expired).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (SocketException error) when (!_stop.IsCancellationRequested) { Fail(error); }
    }

    private async Task SendExitAsync(Session session)
    {
        try
        {
            if (session.ControlToken is uint token)
            {
                await _control.SendAsync(ControlPacket(0x4259, token, session.Sender),
                    new IPEndPoint(IPAddress.Loopback, _driverPort)).ConfigureAwait(false);
            }
        }
        catch (SocketException) { /* Peer or local socket is already gone. */ }
        catch (ObjectDisposedException) { /* Listener shutdown already closed its socket. */ }
    }

    public async ValueTask DisposeAsync()
    {
        Session? session;
        lock (_gate)
        {
            if (_disposed) { return; }
            _disposed = true;
            session = _session;
            if (session is not null) { End(session, null); }
            Signal();
        }
        await _stop.CancelAsync().ConfigureAwait(false);
        if (session is not null)
        {
            await SendExitAsync(session).ConfigureAwait(false);
            await session.DisposeAsync().ConfigureAwait(false);
        }
        await Task.WhenAll(_tasks).ConfigureAwait(false);
        _control.Dispose(); _data.Dispose(); _stop.Dispose();
    }

    private sealed class Session(CoreMidiNetworkPort owner, uint remoteSender) : IMidiDuplexPort
    {
        private readonly SemaphoreSlim _sendGate = new(1, 1);
        private readonly Midi1PacketDecoder _output = new();
        private ushort _sequence = (ushort)Random.Shared.Next(ushort.MaxValue + 1);
        private int _queuedBytes;
        private bool _disposed;
        internal readonly Channel<MidiPacket> Messages = Channel.CreateBounded<MidiPacket>(256);
        internal readonly RtpMidiCodec Input = new();
        internal uint RemoteSender = remoteSender;
        internal readonly uint Sender = (uint)Random.Shared.Next(1, int.MaxValue);
        internal readonly uint InvitationToken = (uint)Random.Shared.Next(1, int.MaxValue);
        internal uint? ControlToken;
        internal uint? DataToken;
        internal int SyncExchanges;
        internal bool Ready;
        internal bool Closed;
        internal bool Initiator;
        internal long LastSync;
        internal ulong PendingClock;
        internal long LastSeen = Stopwatch.GetTimestamp();
        public string InputName => owner._endpointName;
        public string OutputName => owner._endpointName;

        internal void Enqueue(byte[] bytes)
        {
            if (Interlocked.Add(ref _queuedBytes, bytes.Length) > 4 * MidiFrame.MaximumLength
                || !Messages.Writer.TryWrite(new MidiPacket(MidiPacketFormat.Midi1, bytes)))
            {
                throw new IOException("Local MIDI receive queue is full; restarting the session without dropping messages silently.");
            }
        }

        public async IAsyncEnumerable<MidiPacket> ReadAllAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (MidiPacket message in Messages.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                Interlocked.Add(ref _queuedBytes, -message.Data.Length);
                yield return message;
            }
        }

        public async ValueTask SendAsync(MidiPacket packet, CancellationToken cancellationToken)
        {
            await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            bool partialSysEx = false;
            try
            {
                lock (owner._gate)
                {
                    if (Closed) { throw new IOException("Apple MIDI connection ended."); }
                }
                byte[]? message = _output.Decode(packet);
                if (message is null) { return; }
                IReadOnlyList<byte[]> commands = RtpMidiCodec.Encode(message);
                int index = 0;
                foreach (byte[] command in commands)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    lock (owner._gate)
                    {
                        if (Closed) { throw new IOException("Apple MIDI connection ended."); }
                    }
                    await SendCommandAsync(command, cancellationToken).ConfigureAwait(false);
                    if (command[0] is 0xf0 or 0xf7) { partialSysEx = command[^1] == 0xf0; }
                    // Allow the native receiver to drain very large bursts. This is
                    // scheduling fairness, not the virtual-source rate restriction.
                    if (++index % 32 == 0) { await Task.Delay(1, cancellationToken).ConfigureAwait(false); }
                }
            }
            catch (OperationCanceledException) when (partialSysEx)
            {
                try { await SendCommandAsync([0xf7, 0xf4], CancellationToken.None).ConfigureAwait(false); }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
                throw;
            }
            finally { _sendGate.Release(); }
        }

        private async Task SendCommandAsync(byte[] command, CancellationToken cancellationToken)
        {
            byte[] packet = RtpMidiCodec.Packet(command, _sequence++, (uint)Clock(), Sender);
            await owner._data.SendAsync(packet, new IPEndPoint(IPAddress.Loopback, owner._driverPort + 1),
                cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            lock (owner._gate) { owner.End(this, null); }
            await owner.SendExitAsync(this).ConfigureAwait(false);
            await _sendGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed) { return; }
                _disposed = true;
                _output.Dispose();
            }
            finally { _sendGate.Release(); }
        }
    }
}
