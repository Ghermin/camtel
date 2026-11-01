using System.Runtime.InteropServices;

namespace CamTel;

/// <summary>Sender side of the CamTel Camera virtual webcam (softcam API).</summary>
public sealed unsafe class VirtualCamera : IDisposable
{
    private static IntPtr _lib;
    private static delegate* unmanaged[Cdecl]<int, int, float, IntPtr> _create;
    private static delegate* unmanaged[Cdecl]<IntPtr, void> _delete;
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> _send;
    private static delegate* unmanaged[Cdecl]<IntPtr, byte> _isConnected;

    private IntPtr _handle;

    public int Width { get; }
    public int Height { get; }

    private VirtualCamera(IntPtr handle, int width, int height)
    {
        _handle = handle;
        Width = width;
        Height = height;
    }

    /// <summary>Creates the camera, or returns null if the driver is missing or another sender is active.</summary>
    public static VirtualCamera? Create(int width, int height, float fps)
    {
        if (!EnsureLoaded()) return null;
        var h = _create(width, height, fps);
        return h == IntPtr.Zero ? null : new VirtualCamera(h, width, height);
    }

    /// <summary>Sends a top-down BGR24 frame of exactly Width*Height*3 bytes.</summary>
    public void Send(IntPtr bgr)
    {
        if (_handle != IntPtr.Zero) _send(_handle, bgr);
    }

    /// <summary>True once a camera application has opened the virtual camera.</summary>
    public bool IsConnected => _handle != IntPtr.Zero && _isConnected(_handle) != 0;

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            _delete(_handle);
            _handle = IntPtr.Zero;
        }
    }

    private static bool EnsureLoaded()
    {
        if (_lib != IntPtr.Zero) return true;
        var path = DriverInstaller.RegisteredDll64();
        if (path == null || !NativeLibrary.TryLoad(path, out var lib)) return false;
        _lib = lib;
        _create = (delegate* unmanaged[Cdecl]<int, int, float, IntPtr>)NativeLibrary.GetExport(lib, "scCreateCamera");
        _delete = (delegate* unmanaged[Cdecl]<IntPtr, void>)NativeLibrary.GetExport(lib, "scDeleteCamera");
        _send = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)NativeLibrary.GetExport(lib, "scSendFrame");
        _isConnected = (delegate* unmanaged[Cdecl]<IntPtr, byte>)NativeLibrary.GetExport(lib, "scIsConnected");
        return true;
    }
}
