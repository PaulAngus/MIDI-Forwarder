using System.Buffers.Binary;
using MidiForwarder.Core;

namespace MidiForwarder.Midi.CoreMidi;

/// <summary>RFC 6295 MIDI command lists used on the local Apple network MIDI connection.</summary>
internal sealed class RtpMidiCodec : IDisposable
{
    private readonly MemoryStream _sysEx = new();
    private ushort? _sequence;

    internal IReadOnlyList<byte[]> Decode(ReadOnlySpan<byte> packet, uint sender)
    {
        if (packet.Length < 13 || packet[0] != 0x80 || (packet[1] & 0x7f) != 0x61
            || BinaryPrimitives.ReadUInt32BigEndian(packet[8..]) != sender)
        {
            throw new InvalidDataException("Invalid local RTP-MIDI header.");
        }

        ushort sequence = BinaryPrimitives.ReadUInt16BigEndian(packet[2..]);
        if (_sequence is ushort previous)
        {
            ushort distance = unchecked((ushort)(sequence - previous));
            if (distance == 0 || distance > 0x8000) { return []; } // Duplicate or late packet.
            if (distance != 1) { throw new IOException("Local RTP-MIDI packet lost; restarting the MIDI session."); }
        }

        _sequence = sequence;
        int position = 12;
        byte flags = packet[position++];
        int length = flags & 15;
        if ((flags & 0x80) != 0)
        {
            if (position >= packet.Length) { throw new InvalidDataException("Truncated RTP-MIDI length."); }
            length = (length << 8) | packet[position++];
        }

        if (length > packet.Length - position) { throw new InvalidDataException("Truncated RTP-MIDI command list."); }
        ReadOnlySpan<byte> commands = packet.Slice(position, length);
        position = 0;
        bool delta = (flags & 0x20) != 0;
        byte runningStatus = 0;
        var messages = new List<byte[]>();
        while (position < commands.Length)
        {
            if (delta) { SkipDelta(commands, ref position); }
            delta = true;
            if (position == commands.Length) { break; } // Legal trailing delta time.
            byte status = commands[position] >= 128 ? commands[position++] : runningStatus;
            if (status is 0xf0 or 0xf7)
            {
                if (status == 0xf0)
                {
                    _sysEx.SetLength(0);
                    _sysEx.WriteByte(0xf0);
                }
                else if (_sysEx.Length == 0)
                {
                    throw new InvalidDataException("RTP-MIDI SysEx continuation has no start.");
                }

                while (position < commands.Length && commands[position] < 128)
                {
                    if (_sysEx.Length >= MidiFrame.MaximumLength - 1)
                    {
                        throw new InvalidDataException("RTP-MIDI SysEx exceeds the frame limit.");
                    }
                    _sysEx.WriteByte(commands[position++]);
                }

                if (position == commands.Length) { throw new InvalidDataException("Unterminated RTP-MIDI SysEx segment."); }
                switch (commands[position++])
                {
                    case 0xf7:
                    case 0xf5: // RFC 6295's dropped-EOX encoding.
                        _sysEx.WriteByte(0xf7);
                        messages.Add(_sysEx.ToArray());
                        _sysEx.SetLength(0);
                        break;
                    case 0xf4: // Cancellation deliberately emits no partial message.
                        _sysEx.SetLength(0);
                        break;
                    case 0xf0:
                        break;
                    default:
                        throw new InvalidDataException("Invalid RTP-MIDI SysEx segment terminator.");
                }
                runningStatus = 0;
            }
            else
            {
                int bytes = Midi1ShortMessageCodec.GetLength(status);
                if (bytes == 0 || status is 0xf9 or 0xfd || bytes - 1 > commands.Length - position)
                {
                    throw new InvalidDataException("Invalid RTP-MIDI short message.");
                }

                byte[] message = new byte[bytes];
                message[0] = status;
                for (int index = 1; index < bytes; index++)
                {
                    byte value = commands[position++];
                    if (value >= 128) { throw new InvalidDataException("Invalid RTP-MIDI data byte."); }
                    message[index] = value;
                }
                messages.Add(message);
                if (status < 0xf0) { runningStatus = status; }
                else if (status < 0xf8) { runningStatus = 0; }
            }
        }

        // Apple's recovery journal follows the command list. With no sequence loss,
        // journal data is redundant. A loss closes this session before any commands
        // from the affected packet are delivered, rather than guessing lost SysEx.
        return messages;
    }

    internal static IReadOnlyList<byte[]> Encode(ReadOnlySpan<byte> message)
    {
        MidiFrame.Validate(message);
        var commands = new List<byte[]>();
        int position = 0;
        byte runningStatus = 0;
        while (position < message.Length)
        {
            byte status = message[position] >= 128 ? message[position++] : runningStatus;
            if (status == 0xf0)
            {
                var fragment = new List<byte>(1002) { 0xf0 };
                bool ended = false;
                while (position < message.Length)
                {
                    byte value = message[position++];
                    if (value == 0xf7)
                    {
                        fragment.Add(0xf7); commands.Add(fragment.ToArray()); ended = true; break;
                    }
                    if (value >= 0xf8 && value is not (0xf9 or 0xfd))
                    {
                        if (fragment.Count > 1)
                        {
                            fragment.Add(0xf0); commands.Add(fragment.ToArray()); fragment = [0xf7];
                        }
                        commands.Add([value]);
                    }
                    else
                    {
                        if (value >= 128) { throw new InvalidDataException("Invalid MIDI SysEx data."); }
                        fragment.Add(value);
                        if (fragment.Count == 1001)
                        {
                            fragment.Add(0xf0); commands.Add(fragment.ToArray()); fragment = [0xf7];
                        }
                    }
                }
                if (!ended) { throw new InvalidDataException("Incomplete MIDI SysEx frame."); }
                runningStatus = 0;
            }
            else if (status == 0xf7)
            {
                runningStatus = 0; // Unpaired EOX has no RTP encoding or MIDI payload.
            }
            else
            {
                int bytes = Midi1ShortMessageCodec.GetLength(status);
                if (bytes == 0 || status is 0xf9 or 0xfd || bytes - 1 > message.Length - position)
                {
                    throw new InvalidDataException("Unsupported or incomplete MIDI short message.");
                }
                byte[] command = new byte[bytes]; command[0] = status;
                for (int index = 1; index < bytes; index++)
                {
                    byte value = message[position++];
                    if (value >= 128) { throw new InvalidDataException("Invalid MIDI data byte."); }
                    command[index] = value;
                }
                commands.Add(command);
                if (status < 0xf0) { runningStatus = status; }
                else if (status < 0xf8) { runningStatus = 0; }
            }
        }
        return commands;
    }

    internal static byte[] Packet(ReadOnlySpan<byte> command, ushort sequence, uint timestamp, uint sender)
    {
        int header = command.Length > 15 ? 2 : 1;
        byte[] packet = new byte[12 + header + command.Length];
        packet[0] = 0x80; packet[1] = 0x61;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), sender);
        if (header == 1) { packet[12] = checked((byte)command.Length); }
        else { packet[12] = (byte)(0x80 | (command.Length >> 8)); packet[13] = (byte)command.Length; }
        command.CopyTo(packet.AsSpan(12 + header));
        return packet;
    }

    private static void SkipDelta(ReadOnlySpan<byte> commands, ref int position)
    {
        for (int index = 0; index < 4; index++)
        {
            if (position >= commands.Length) { throw new InvalidDataException("Truncated RTP-MIDI delta time."); }
            if ((commands[position++] & 0x80) == 0) { return; }
        }
        throw new InvalidDataException("RTP-MIDI delta time exceeds four bytes.");
    }

    public void Dispose() => _sysEx.Dispose();
}
