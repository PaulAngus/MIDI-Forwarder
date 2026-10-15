using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using MidiForwarder.Core;

namespace MidiForwarder.Midi.CoreMidi;

public sealed class CoreMidiDuplexPort : IMidiDuplexPort
{
    private readonly Channel<MidiPacket> _messages = Channel.CreateUnbounded<MidiPacket>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly object _gate = new();
    private readonly RtMidiNative.ReceiveCallback _callback;
    private readonly Midi1PacketDecoder _decoder = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly CoreMidiSourcePacer _sourcePacer = new();
    private CoreMidiVirtualSource? _virtualOutput;
    private IntPtr _input;
    private IntPtr _output;
    private bool _disposed;

    private CoreMidiDuplexPort(string name, bool virtualPort)
    {
        InputName = OutputName = name;
        _callback = OnMessage;
        try
        {
            _input = RtMidiNative.Create(true);
            if (!virtualPort) { _output = RtMidiNative.Create(false); }
            RtMidiNative.IgnoreTypes(_input, false, false, false);
            RtMidiNative.Check(_input);
            RtMidiNative.SetCallback(_input, _callback, IntPtr.Zero);
            RtMidiNative.Check(_input);
            if (virtualPort)
            {
                RtMidiNative.OpenVirtualPort(_input, name);
                RtMidiNative.Check(_input);
                _virtualOutput = new CoreMidiVirtualSource(name);
            }
            else
            {
                RtMidiNative.OpenPort(_input, FindPort(_input, name), "MIDI Forwarder Receive");
                RtMidiNative.Check(_input);
                RtMidiNative.OpenPort(_output, FindPort(_output, name), "MIDI Forwarder Send");
            }

            if (!virtualPort) { RtMidiNative.Check(_output); }
        }
        catch
        {
            DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    public string InputName { get; }

    public string OutputName { get; }

    public string ApplicationPortName => InputName;

    public string EndpointName => InputName;

    public static CoreMidiDuplexPort Open(string endpointName) => new(endpointName, false);

    public static CoreMidiDuplexPort OpenVirtual(string interfaceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(interfaceName);
        return new CoreMidiDuplexPort(interfaceName.Trim(), true);
    }

    public IAsyncEnumerable<MidiPacket> ReadAllAsync(CancellationToken cancellationToken) =>
        _messages.Reader.ReadAllAsync(cancellationToken);

    public async ValueTask SendAsync(MidiPacket packet, CancellationToken cancellationToken)
    {
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte[]? message;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                message = _decoder.Decode(packet);
            }

            if (message is null) { return; }
            if (_virtualOutput is not null)
            {
                bool sysex = message[0] == 0xf0;
                int offset = 0;
                try
                {
                    while (offset < message.Length)
                    {
                        int length = Math.Min(CoreMidiVirtualSource.MaximumPacketBytes, message.Length - offset);
                        int umpBytes = sysex ? ((length + 5) / 6) * 8 : 4;
                        await _sourcePacer.WaitAsync(umpBytes, cancellationToken).ConfigureAwait(false);
                        lock (_gate)
                        {
                            ObjectDisposedException.ThrowIf(_disposed, this);
                            _virtualOutput.Send(message.AsSpan(offset, length));
                        }

                        offset += length;
                    }
                }
                catch (OperationCanceledException) when (sysex && offset > 0 && offset < message.Length)
                {
                    // End an interrupted native SysEx stream before a later session sends
                    // another message through the persistent virtual endpoint.
                    lock (_gate)
                    {
                        if (!_disposed) { _virtualOutput.Send([0xf7]); }
                    }

                    throw;
                }

                return;
            }

            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (RtMidiNative.SendMessage(_output, message, message.Length) < 0)
                {
                    RtMidiNative.Check(_output);
                    throw new IOException("CoreMIDI could not send the MIDI message.");
                }

                RtMidiNative.Check(_output);
            }
        }
        finally { _sendGate.Release(); }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
            // Close the input before releasing the managed callback or its native context.
            if (_input != IntPtr.Zero)
            {
                RtMidiNative.ClosePort(_input);
                RtMidiNative.CancelCallback(_input);
            }

            RtMidiNative.Free(_input, true);
            RtMidiNative.Free(_output, false);
            _virtualOutput?.Dispose();
            _input = _output = IntPtr.Zero;
            GC.KeepAlive(_callback);
            _decoder.Dispose();
            _messages.Writer.TryComplete();
        }

        return ValueTask.CompletedTask;
    }

    [SuppressMessage("Design", "CA1031", Justification = "Exceptions must not escape a native MIDI callback; the reader receives the failure.")]
    private void OnMessage(double timestamp, IntPtr message, nuint length, IntPtr context)
    {
        try
        {
            if (length == 0)
            {
                return;
            }

            if (length > MidiFrame.MaximumLength)
            {
                throw new InvalidDataException("CoreMIDI message exceeds the frame limit.");
            }

            var bytes = new byte[(int)length];
            Marshal.Copy(message, bytes, 0, bytes.Length);
            _messages.Writer.TryWrite(new MidiPacket(MidiPacketFormat.Midi1, bytes));
        }
        catch (Exception error)
        {
            _messages.Writer.TryComplete(error);
        }
    }

    private static uint FindPort(IntPtr handle, string name)
    {
        IReadOnlyList<string> names = RtMidiNative.GetNames(handle);
        for (int index = 0; index < names.Count; index++)
        {
            if (names[index].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return (uint)index;
            }
        }

        throw new IOException($"CoreMIDI port not found: {name}");
    }
}
