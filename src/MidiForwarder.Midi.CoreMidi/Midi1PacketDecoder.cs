using MidiForwarder.Core;
using MidiForwarder.Midi.WindowsServices;

namespace MidiForwarder.Midi.CoreMidi;

public sealed class Midi1PacketDecoder : IDisposable
{
    private readonly SysEx7Assembler[] _sysEx = Enumerable.Range(0, 16).Select(_ => new SysEx7Assembler()).ToArray();

    public byte[]? Decode(MidiPacket packet)
    {
        packet.Validate();
        if (packet.Format == MidiPacketFormat.Midi1)
        {
            return packet.Data.ToArray();
        }

        uint[] words = packet.ToUmpWords();
        uint messageType = words[0] >> 28;
        if (messageType == 0)
        {
            return null;
        }

        if (messageType is not (1 or 2 or 3))
        {
            throw new IOException($"The macOS MIDI 1.0 backend cannot send UMP message type {messageType}.");
        }

        int group = (int)((words[0] >> 24) & 0xf);
        return UmpMidi1Codec.Decode(words, _sysEx[group]);
    }

    public void Dispose()
    {
        foreach (SysEx7Assembler assembler in _sysEx)
        {
            assembler.Dispose();
        }
    }
}
