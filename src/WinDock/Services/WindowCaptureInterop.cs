using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;

namespace WinDock.Services;

// Documented HWND / DXGI bridges. These declarations avoid a capture library,
// private compositor interfaces and desktop-wide screenshot fallbacks.
static class WindowCaptureInterop
{
    [DllImport("d3d11.dll")]
    static extern int D3D11CreateDevice(nint adapter, int type, nint software, uint flags,
        nint levels, uint count, uint sdk, out nint device, out int level, out nint context);
    [DllImport("d3d11.dll")]
    static extern int CreateDirect3D11DeviceFromDXGIDevice(nint device, out nint inspectable);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    static extern int WindowsCreateString(string text, int length, out nint value);
    [DllImport("combase.dll")]
    static extern int WindowsDeleteString(nint value);
    [DllImport("combase.dll")]
    static extern int RoGetActivationFactory(nint name, in Guid iid, out nint factory);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int CreateForWindow(nint factory, nint window, in Guid iid, out nint item);

    internal static IDirect3DDevice CreateDevice()
    {
        nint device = 0, context = 0, dxgi = 0, projected = 0;
        try
        {
            Marshal.ThrowExceptionForHR(D3D11CreateDevice(0, 1 /* HARDWARE */, 0, 0x20 /* BGRA_SUPPORT */,
                0, 0, 7 /* D3D11_SDK_VERSION */, out device, out _, out context));
            var iid = new Guid("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(device, in iid, out dxgi));
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out projected));
            return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(projected);
        }
        finally
        {
            if (projected != 0) Marshal.Release(projected);
            if (dxgi != 0) Marshal.Release(dxgi);
            if (context != 0) Marshal.Release(context);
            if (device != 0) Marshal.Release(device);
        }
    }

    internal static GraphicsCaptureItem CreateItem(nint window)
    {
        const string name = "Windows.Graphics.Capture.GraphicsCaptureItem";
        nint text = 0, factory = 0, item = 0;
        try
        {
            Marshal.ThrowExceptionForHR(WindowsCreateString(name, name.Length, out text));
            var interop = new Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(text, in interop, out factory));
            // IGraphicsCaptureItemInterop inherits IUnknown; CreateForWindow is
            // the first method after the three standard IUnknown entries.
            var table = Marshal.ReadIntPtr(factory);
            var create = Marshal.GetDelegateForFunctionPointer<CreateForWindow>(Marshal.ReadIntPtr(table, 3 * nint.Size));
            var itemId = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
            Marshal.ThrowExceptionForHR(create(factory, window, in itemId, out item));
            return GraphicsCaptureItem.FromAbi(item);
        }
        finally
        {
            if (item != 0) Marshal.Release(item);
            if (factory != 0) Marshal.Release(factory);
            if (text != 0) WindowsDeleteString(text);
        }
    }
}
