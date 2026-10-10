using MidiForwarder.Core;
using Windows.Devices.Midi2;
using System.Runtime.InteropServices;

namespace MidiForwarder.Midi.WindowsServices;

/// <summary>Uses the largest supported transmissions, retaining whole UMPs and stopping on the first failure.</summary>
public static class UmpBatchSender
{
    public static void Send(MidiEndpointConnection connection, uint[] words)
    {
        try
        {
            Send(words, connection.GetSupportedMaxMidiWordsPerTransmission(),
                (offset, count) => MidiEndpointConnection.SendMessageSucceeded(
                    connection.SendMultipleMessagesWordArray(0, offset, count, words)));
        }
        catch (COMException error) { throw new IOException("Windows MIDI Services transmission failed.", error); }
    }

    public static void Send(uint[] words, uint maximumWords, Func<uint, uint, bool> transmit)
    {
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(transmit);
        for (int index = 0; index < words.Length;)
        {
            int size = UmpSysExAssembler.WordCount((byte)(words[index] >> 24));
            if (index + size > words.Length) { throw new InvalidDataException("Truncated UMP transmission."); }
            index += size;
        }
        int offset = 0;
        while (offset < words.Length)
        {
            int count = 0;
            while (offset + count < words.Length)
            {
                int size = UmpSysExAssembler.WordCount((byte)(words[offset + count] >> 24));
                if ((uint)(count + size) > maximumWords) { break; }
                count += size;
            }
            if (count == 0) { throw new IOException("The endpoint transmission limit cannot fit a complete UMP."); }
            if (!transmit((uint)offset, (uint)count))
            { throw new IOException("Windows MIDI Services rejected a transmission; remaining SysEx was not sent."); }
            offset += count;
        }
    }
}
