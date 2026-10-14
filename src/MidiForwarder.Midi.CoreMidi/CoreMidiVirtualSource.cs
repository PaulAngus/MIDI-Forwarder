using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace MidiForwarder.Midi.CoreMidi;

// Own the virtual source directly so large SysEx can be paced in fragments. RtMidi's
// sendMessage API rejects fragments that do not begin with a MIDI status byte.
[SuppressMessage("Security", "CA2101", Justification = "CoreFoundation explicitly receives a UTF-8 C string.")]
internal sealed class CoreMidiVirtualSource : IDisposable
{
    internal const int MaximumPacketBytes = 4096;
    private const int PacketListBytes = MaximumPacketBytes + 64;
    private const string MidiLibrary = "/System/Library/Frameworks/CoreMIDI.framework/CoreMIDI";
    private const string FoundationLibrary = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private uint _client;
    private uint _source;
    private IntPtr _packetList;

    public CoreMidiVirtualSource(string name)
    {
        IntPtr nativeName = CFStringCreateWithCString(IntPtr.Zero, name, 0x08000100);
        if (nativeName == IntPtr.Zero)
        {
            throw new IOException("CoreMIDI could not allocate the virtual port name.");
        }

        try
        {
            Check(MIDIClientCreate(nativeName, IntPtr.Zero, IntPtr.Zero, out _client), "create client");
            Check(MIDISourceCreate(_client, nativeName, out _source), "create virtual source");
            _packetList = Marshal.AllocHGlobal(PacketListBytes);
        }
        catch
        {
            Dispose();
            throw;
        }
        finally
        {
            CFRelease(nativeName);
        }
    }

    public void Send(ReadOnlySpan<byte> bytes)
    {
        ObjectDisposedException.ThrowIf(_packetList == IntPtr.Zero, this);
        IntPtr packet = MIDIPacketListInit(_packetList);
        if (MIDIPacketListAdd(_packetList, PacketListBytes, packet, 0, (nuint)bytes.Length, bytes.ToArray()) == IntPtr.Zero)
        {
            throw new IOException("CoreMIDI could not allocate a MIDI packet.");
        }

        Check(MIDIReceived(_source, _packetList), "publish MIDI packet");
    }

    public void Dispose()
    {
        if (_source != 0) { _ = MIDIEndpointDispose(_source); _source = 0; }
        if (_client != 0) { _ = MIDIClientDispose(_client); _client = 0; }
        Marshal.FreeHGlobal(_packetList);
        _packetList = IntPtr.Zero;
    }

    private static void Check(int status, string operation)
    {
        if (status != 0) { throw new IOException($"CoreMIDI could not {operation} (OSStatus {status})."); }
    }

    [DllImport(FoundationLibrary)] private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, uint encoding);
    [DllImport(FoundationLibrary)] private static extern void CFRelease(IntPtr value);
    [DllImport(MidiLibrary)] private static extern int MIDIClientCreate(IntPtr name, IntPtr callback, IntPtr context, out uint client);
    [DllImport(MidiLibrary)] private static extern int MIDISourceCreate(uint client, IntPtr name, out uint source);
    [DllImport(MidiLibrary)] private static extern IntPtr MIDIPacketListInit(IntPtr list);
    [DllImport(MidiLibrary)] private static extern IntPtr MIDIPacketListAdd(IntPtr list, nuint size, IntPtr packet, ulong timestamp, nuint count, byte[] bytes);
    [DllImport(MidiLibrary)] private static extern int MIDIReceived(uint source, IntPtr list);
    [DllImport(MidiLibrary)] private static extern int MIDIEndpointDispose(uint source);
    [DllImport(MidiLibrary)] private static extern int MIDIClientDispose(uint client);
}
