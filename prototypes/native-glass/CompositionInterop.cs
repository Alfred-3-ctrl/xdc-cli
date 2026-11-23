using System.Runtime.InteropServices;
using Windows.System;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using WinRT;

namespace Xdc.NativeGlass;

internal static partial class CompositionInterop
{
    private static readonly Guid DesktopInteropId = new("29E691FA-4567-4DCA-B319-D0F207EB6807");

    public static DispatcherQueueController? EnsureDispatcherQueue()
    {
        if (DispatcherQueue.GetForCurrentThread() is not null) return null;

        // WinForms already owns this STA. The queue runs on the same message loop.
        var options = new DispatcherQueueOptions { Size = 12, ThreadType = 2, ApartmentType = 0 };
        Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(options, out nint pointer));
        try
        {
            return MarshalInterface<DispatcherQueueController>.FromAbi(pointer);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    public static unsafe DesktopWindowTarget CreateWindowTarget(Compositor compositor, nint hwnd)
    {
        // ICompositorDesktopInterop::CreateDesktopWindowTarget is the first IUnknown method.
        var reference = ((IWinRTObject)compositor).NativeObject;
        Guid iid = DesktopInteropId;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(reference.ThisPtr, in iid, out nint interop));
        nint result = 0;
        try
        {
            var create = (delegate* unmanaged[Stdcall]<nint, nint, int, nint*, int>)(*(nint**)interop)[3];
            Marshal.ThrowExceptionForHR(create(interop, hwnd, 0, &result));
            return MarshalInterface<DesktopWindowTarget>.FromAbi(result);
        }
        finally
        {
            if (result != 0) Marshal.Release(result);
            Marshal.Release(interop);
            GC.KeepAlive(reference);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        public uint Size;
        public int ThreadType;
        public int ApartmentType;
    }

    [LibraryImport("CoreMessaging.dll")]
    private static partial int CreateDispatcherQueueController(DispatcherQueueOptions options, out nint instance);
}
