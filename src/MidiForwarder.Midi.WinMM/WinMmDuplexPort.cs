using System.Threading.Channels;
using MidiForwarder.Core;
using NAudio.Midi;

namespace MidiForwarder.Midi.WinMM;

public sealed class WinMmDuplexPort : IMidiDuplexPort
{
    private readonly WinMmMidiInput _input;
    private readonly MidiOut _output;
    private readonly Channel<ReadOnlyMemory<byte>> _messages = Channel.CreateUnbounded<ReadOnlyMemory<byte>>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly object _outputLock = new();
    private bool _disposed;

    private WinMmDuplexPort(string inputName, int inputIndex, string outputName, MidiOut output)
    {
        InputName = inputName;
        OutputName = outputName;
        _output = output;
        _input = new WinMmMidiInput(inputIndex, OnShortMessage, OnSysexMessage, OnInputError);

        try
        {
            _input.Start();
        }
        catch
        {
            _input.Dispose();
            throw;
        }
    }

    public string InputName { get; }

    public string OutputName { get; }

    public static WinMmDuplexPort Open(string inputName, string outputName)
    {
        int inputIndex = FindInput(inputName);
        int outputIndex = FindOutput(outputName);
        MidiOut? output = null;

        try
        {
            output = new MidiOut(outputIndex);
            return new WinMmDuplexPort(inputName, inputIndex, outputName, output);
        }
        catch
        {
            output?.Dispose();
            throw;
        }
    }

    public IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAllAsync(CancellationToken cancellationToken) =>
        _messages.Reader.ReadAllAsync(cancellationToken);

    public ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MidiFrame.Validate(message.Span);

        lock (_outputLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (message.Span[0] == 0xf0)
            {
                _output.SendBuffer(message.ToArray());
            }
            else
            {
                _output.Send(Midi1ShortMessageCodec.Encode(message.Span));
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
        _input.Dispose();
        _output.Dispose();
        _messages.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    private static void OnShortMessage(int rawMessage, ChannelWriter<ReadOnlyMemory<byte>> writer)
    {
        try
        {
            writer.TryWrite(Midi1ShortMessageCodec.Decode(rawMessage));
        }
        catch (InvalidDataException error)
        {
            writer.TryComplete(error);
        }
    }

    private void OnShortMessage(int rawMessage) => OnShortMessage(rawMessage, _messages.Writer);

    private void OnSysexMessage(byte[] message) => _messages.Writer.TryWrite(message);

    private void OnInputError(Exception error) => _messages.Writer.TryComplete(error);

    private static int FindInput(string name)
    {
        for (int index = 0; index < MidiIn.NumberOfDevices; index++)
        {
            if (string.Equals(MidiIn.DeviceInfo(index).ProductName, name, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        throw new IOException($"MIDI input not found: {name}");
    }

    private static int FindOutput(string name)
    {
        for (int index = 0; index < MidiOut.NumberOfDevices; index++)
        {
            if (string.Equals(MidiOut.DeviceInfo(index).ProductName, name, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        throw new IOException($"MIDI output not found: {name}");
    }
}
