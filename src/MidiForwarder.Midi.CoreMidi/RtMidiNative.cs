using System.Runtime.InteropServices;
using System.Text;
using System.Diagnostics.CodeAnalysis;

namespace MidiForwarder.Midi.CoreMidi;

[SuppressMessage("Security", "CA2101", Justification = "RtMidi takes UTF-8 char pointers; LPUTF8Str is explicitly specified on every string parameter.")]
internal static class RtMidiNative
{
    private const string Library = "librtmidi.7.dylib";
    private const int CoreMidiApi = 1;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void ReceiveCallback(double timestamp, IntPtr message, nuint length, IntPtr context);

    internal static IntPtr Create(bool input)
    {
        IntPtr handle = input
            ? CreateInput(CoreMidiApi, "MIDI Forwarder", 1024)
            : CreateOutput(CoreMidiApi, "MIDI Forwarder");
        try
        {
            Check(handle);
            return handle;
        }
        catch
        {
            Free(handle, input);
            throw;
        }
    }

    internal static void Check(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            throw new IOException("CoreMIDI could not create a MIDI client.");
        }

        // RtMidiWrapper has two pointers, a C bool, then an aligned error pointer.
        if (Marshal.ReadByte(handle, 2 * IntPtr.Size) == 0)
        {
            string? message = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(handle, 3 * IntPtr.Size));
            throw new IOException(message ?? "CoreMIDI operation failed.");
        }
    }

    internal static void Free(IntPtr handle, bool input)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }

        if (input)
        {
            FreeInput(handle);
        }
        else
        {
            FreeOutput(handle);
        }
    }

    internal static IReadOnlyList<string> GetNames(IntPtr handle)
    {
        uint count = GetPortCount(handle);
        Check(handle);
        var names = new List<string>();
        for (uint index = 0; index < count; index++)
        {
            int length = 0;
            if (GetPortName(handle, index, null, ref length) < 0)
            {
                throw new IOException("CoreMIDI could not read a port name length.");
            }
            Check(handle);
            var buffer = new byte[length];
            if (GetPortName(handle, index, buffer, ref length) < 0)
            {
                throw new IOException("CoreMIDI could not read a port name.");
            }

            Check(handle);
            names.Add(Encoding.UTF8.GetString(buffer).TrimEnd('\0'));
        }

        return names;
    }

    [DllImport(Library, EntryPoint = "rtmidi_in_create", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr CreateInput(int api, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, uint queueSize);

    [DllImport(Library, EntryPoint = "rtmidi_out_create", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr CreateOutput(int api, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(Library, EntryPoint = "rtmidi_in_free", CallingConvention = CallingConvention.Cdecl)]
    private static extern void FreeInput(IntPtr handle);

    [DllImport(Library, EntryPoint = "rtmidi_out_free", CallingConvention = CallingConvention.Cdecl)]
    private static extern void FreeOutput(IntPtr handle);

    [DllImport(Library, EntryPoint = "rtmidi_get_port_count", CallingConvention = CallingConvention.Cdecl)]
    private static extern uint GetPortCount(IntPtr handle);

    [DllImport(Library, EntryPoint = "rtmidi_get_port_name", CallingConvention = CallingConvention.Cdecl)]
    private static extern int GetPortName(IntPtr handle, uint index, [Out] byte[]? buffer, ref int length);

    [DllImport(Library, EntryPoint = "rtmidi_open_port", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void OpenPort(IntPtr handle, uint index, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(Library, EntryPoint = "rtmidi_open_virtual_port", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void OpenVirtualPort(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(Library, EntryPoint = "rtmidi_in_ignore_types", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void IgnoreTypes(IntPtr handle, [MarshalAs(UnmanagedType.I1)] bool sysex,
        [MarshalAs(UnmanagedType.I1)] bool timing, [MarshalAs(UnmanagedType.I1)] bool sensing);

    [DllImport(Library, EntryPoint = "rtmidi_in_set_callback", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SetCallback(IntPtr handle, ReceiveCallback callback, IntPtr context);

    [DllImport(Library, EntryPoint = "rtmidi_close_port", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ClosePort(IntPtr handle);

    [DllImport(Library, EntryPoint = "rtmidi_in_cancel_callback", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void CancelCallback(IntPtr handle);

    [DllImport(Library, EntryPoint = "rtmidi_out_send_message", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SendMessage(IntPtr handle, byte[] message, int length);
}
