using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WallhackTerminal.Hid;

public sealed unsafe class HidConnection : IDisposable
{
    readonly SafeFileHandle _handle;

    public HidInterfaceInfo Info { get; }

    HidConnection(SafeFileHandle handle, HidInterfaceInfo info)
    {
        _handle = handle;
        Info = info;
    }

    public static HidConnection? TryOpen(HidInterfaceInfo info, bool write)
    {
        uint access = write ? Native.GENERIC_READ | Native.GENERIC_WRITE : Native.GENERIC_READ;
        var handle = Native.CreateFile(info.Path, access, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
            IntPtr.Zero, Native.OPEN_EXISTING, Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }
        Native.HidD_SetNumInputBuffers(handle, 64);
        return new HidConnection(handle, info);
    }

    public int Read(byte[] buffer, CancellationToken cancel)
    {
        using var done = new ManualResetEvent(false);
        var overlapped = new NativeOverlapped { EventHandle = done.SafeWaitHandle.DangerousGetHandle() };
        fixed (byte* p = buffer)
        {
            if (!Native.ReadFile(_handle, p, buffer.Length, IntPtr.Zero, &overlapped))
            {
                int error = Marshal.GetLastWin32Error();
                if (error != Native.ERROR_IO_PENDING) throw new Win32Exception(error);
                if (WaitHandle.WaitAny([done, cancel.WaitHandle]) == 1)
                {
                    Native.CancelIoEx(_handle, &overlapped);
                    Native.GetOverlappedResult(_handle, &overlapped, out _, true);
                    throw new OperationCanceledException(cancel);
                }
            }
            if (!Native.GetOverlappedResult(_handle, &overlapped, out int read, true))
            {
                int error = Marshal.GetLastWin32Error();
                if (error == Native.ERROR_OPERATION_ABORTED && cancel.IsCancellationRequested)
                    throw new OperationCanceledException(cancel);
                throw new Win32Exception(error);
            }
            return read;
        }
    }

    public void Write(byte[] buffer, int timeoutMs = 1000)
    {
        using var done = new ManualResetEvent(false);
        var overlapped = new NativeOverlapped { EventHandle = done.SafeWaitHandle.DangerousGetHandle() };
        fixed (byte* p = buffer)
        {
            if (!Native.WriteFile(_handle, p, buffer.Length, IntPtr.Zero, &overlapped))
            {
                int error = Marshal.GetLastWin32Error();
                if (error != Native.ERROR_IO_PENDING) throw new Win32Exception(error);
                if (!done.WaitOne(timeoutMs))
                {
                    Native.CancelIoEx(_handle, &overlapped);
                    Native.GetOverlappedResult(_handle, &overlapped, out _, true);
                    throw new TimeoutException("HID write timed out");
                }
            }
            if (!Native.GetOverlappedResult(_handle, &overlapped, out _, true))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    public void Dispose() => _handle.Dispose();
}
