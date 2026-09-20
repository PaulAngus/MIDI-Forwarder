using System.Runtime.InteropServices;

namespace MidiForwarder.Midi.WinMM;

internal sealed class WinMmMidiInput : IDisposable
{
    private const uint CallbackFunction = 0x00030000;
    private const uint MimData = 0x03C3;
    private const uint MimLongData = 0x03C4;
    private const uint MimError = 0x03C5;
    private const uint MimLongError = 0x03C6;
    private const uint MimMoreData = 0x03CC;
    private const int BufferCount = 4;
    private const int BufferSize = 64 * 1024;

    private readonly Action<int> _shortMessageReceived;
    private readonly Action<byte[]> _sysexMessageReceived;
    private readonly Action<Exception> _errorReceived;
    private readonly MidiInProc _callback;
    private readonly List<NativeBuffer> _buffers = [];
    private readonly ManualResetEventSlim _callbacksFinished = new(initialState: true);
    private IntPtr _handle;
    private int _activeCallbacks;
    private volatile bool _stopping;
    private bool _disposed;

    public WinMmMidiInput(
        int deviceIndex,
        Action<int> shortMessageReceived,
        Action<byte[]> sysexMessageReceived,
        Action<Exception> errorReceived)
    {
        _shortMessageReceived = shortMessageReceived;
        _sysexMessageReceived = sysexMessageReceived;
        _errorReceived = errorReceived;
        _callback = Callback;

        CheckResult(
            MidiInOpen(out IntPtr handle, checked((uint)deviceIndex), _callback, 0, CallbackFunction),
            "midiInOpen");
        _handle = handle;

        try
        {
            for (int index = 0; index < BufferCount; index++)
            {
                _buffers.Add(CreateAndQueueBuffer());
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CheckResult(MidiInStart(_handle), "midiInStart");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopping = true;

        if (_handle != IntPtr.Zero)
        {
            _ = MidiInStop(_handle);
            _ = MidiInReset(_handle);
            _callbacksFinished.Wait(TimeSpan.FromSeconds(2));

            foreach (NativeBuffer buffer in _buffers)
            {
                if (buffer.Prepared)
                {
                    _ = MidiInUnprepareHeader(_handle, buffer.Header, checked((uint)Marshal.SizeOf<MidiHeader>()));
                }
            }

            _ = MidiInClose(_handle);
            _handle = IntPtr.Zero;
        }

        foreach (NativeBuffer buffer in _buffers)
        {
            Marshal.FreeHGlobal(buffer.Header);
            Marshal.FreeHGlobal(buffer.Data);
        }

        _buffers.Clear();
    }

    private NativeBuffer CreateAndQueueBuffer()
    {
        IntPtr data = Marshal.AllocHGlobal(BufferSize);
        IntPtr header = Marshal.AllocHGlobal(Marshal.SizeOf<MidiHeader>());
        var buffer = new NativeBuffer(data, header);

        try
        {
            var midiHeader = new MidiHeader
            {
                Data = data,
                BufferLength = BufferSize,
                Reserved = new nuint[8],
            };
            Marshal.StructureToPtr(midiHeader, header, fDeleteOld: false);

            CheckResult(MidiInPrepareHeader(_handle, header, checked((uint)Marshal.SizeOf<MidiHeader>())), "midiInPrepareHeader");
            buffer.Prepared = true;
            CheckResult(MidiInAddBuffer(_handle, header, checked((uint)Marshal.SizeOf<MidiHeader>())), "midiInAddBuffer");
            return buffer;
        }
        catch
        {
            if (buffer.Prepared)
            {
                _ = MidiInUnprepareHeader(_handle, header, checked((uint)Marshal.SizeOf<MidiHeader>()));
            }

            Marshal.FreeHGlobal(header);
            Marshal.FreeHGlobal(data);
            throw;
        }
    }

    private void Callback(IntPtr midiInHandle, uint message, nuint instance, nuint parameter1, nuint parameter2)
    {
        _callbacksFinished.Reset();
        Interlocked.Increment(ref _activeCallbacks);

        try
        {
            if (_stopping)
            {
                return;
            }

            switch (message)
            {
                case MimData:
                case MimMoreData:
                    // WinMM stores the packed MIDI message in the low 32 bits. Converting
                    // an x64 callback pointer with IntPtr.ToInt32 can throw OverflowException.
                    _shortMessageReceived(unchecked((int)parameter1));
                    break;

                case MimLongData:
                    ReceiveLongMessage((IntPtr)parameter1, isError: false);
                    break;

                case MimError:
                    SafeReportError(new IOException($"MIDI input error: 0x{unchecked((int)parameter1):X8}"));
                    break;

                case MimLongError:
                    ReceiveLongMessage((IntPtr)parameter1, isError: true);
                    break;
            }
        }
        catch (Exception error)
        {
            // Exceptions must never escape a native callback; doing so terminates the process.
            SafeReportError(error);
        }
        finally
        {
            if (Interlocked.Decrement(ref _activeCallbacks) == 0)
            {
                _callbacksFinished.Set();
            }
        }
    }

    private void ReceiveLongMessage(IntPtr headerPointer, bool isError)
    {
        MidiHeader header = Marshal.PtrToStructure<MidiHeader>(headerPointer);
        int bytesRecorded = checked((int)header.BytesRecorded);

        if (isError)
        {
            SafeReportError(new IOException($"Invalid MIDI SysEx input ({bytesRecorded} bytes)."));
        }
        else if (bytesRecorded > 0)
        {
            var message = new byte[bytesRecorded];
            Marshal.Copy(header.Data, message, 0, bytesRecorded);
            _sysexMessageReceived(message);
        }

        if (!_stopping)
        {
            uint result = MidiInAddBuffer(_handle, headerPointer, checked((uint)Marshal.SizeOf<MidiHeader>()));
            if (result != 0)
            {
                SafeReportError(new IOException($"midiInAddBuffer failed with WinMM error {result}."));
            }
        }
    }

    private void SafeReportError(Exception error)
    {
        try
        {
            _errorReceived(error);
        }
        catch
        {
            // The native callback boundary must remain exception-free.
        }
    }

    private static void CheckResult(uint result, string operation)
    {
        if (result != 0)
        {
            throw new IOException($"{operation} failed with WinMM error {result}.");
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void MidiInProc(IntPtr midiInHandle, uint message, nuint instance, nuint parameter1, nuint parameter2);

    [StructLayout(LayoutKind.Sequential)]
    private struct MidiHeader
    {
        public IntPtr Data;
        public uint BufferLength;
        public uint BytesRecorded;
        public nuint User;
        public uint Flags;
        public IntPtr Next;
        public nuint ReservedForDriver;
        public uint Offset;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public nuint[] Reserved;
    }

    private sealed class NativeBuffer(IntPtr data, IntPtr header)
    {
        public IntPtr Data { get; } = data;
        public IntPtr Header { get; } = header;
        public bool Prepared { get; set; }
    }

    [DllImport("winmm.dll", EntryPoint = "midiInOpen")]
    private static extern uint MidiInOpen(
        out IntPtr handle,
        uint deviceId,
        MidiInProc callback,
        nuint instance,
        uint flags);

    [DllImport("winmm.dll", EntryPoint = "midiInStart")]
    private static extern uint MidiInStart(IntPtr handle);

    [DllImport("winmm.dll", EntryPoint = "midiInStop")]
    private static extern uint MidiInStop(IntPtr handle);

    [DllImport("winmm.dll", EntryPoint = "midiInReset")]
    private static extern uint MidiInReset(IntPtr handle);

    [DllImport("winmm.dll", EntryPoint = "midiInClose")]
    private static extern uint MidiInClose(IntPtr handle);

    [DllImport("winmm.dll", EntryPoint = "midiInPrepareHeader")]
    private static extern uint MidiInPrepareHeader(IntPtr handle, IntPtr header, uint headerSize);

    [DllImport("winmm.dll", EntryPoint = "midiInUnprepareHeader")]
    private static extern uint MidiInUnprepareHeader(IntPtr handle, IntPtr header, uint headerSize);

    [DllImport("winmm.dll", EntryPoint = "midiInAddBuffer")]
    private static extern uint MidiInAddBuffer(IntPtr handle, IntPtr header, uint headerSize);
}
