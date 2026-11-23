using System.Drawing;
using System.Runtime.InteropServices;
using Windows.Graphics.DirectX;
using Windows.UI.Composition;
using WinRT;

namespace Xdc.SingleWindowGlass;

internal static unsafe partial class CompositionSurfaceInterop
{
    private const uint D3D11_SDK_VERSION = 7;
    private const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
    private const int D3D_DRIVER_TYPE_HARDWARE = 1;
    private const uint DXGI_MAP_WRITE = 2;

    private static readonly Guid IID_IDXGIDevice = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
    private static readonly Guid IID_ICompositorInterop = new("25297D5C-3AD4-4C9C-B5CF-E36A38512330");
    private static readonly Guid IID_ICompositionDrawingSurfaceInterop = new("FD04E6E3-FE0C-4C3C-AB19-A07601A576EE");
    private static readonly Guid IID_IDXGISurface = new("CAFCB56C-6AC3-4889-BF47-9E23BBD260EC");
    private static readonly Guid IID_ID3D11Texture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_TEXTURE2D_DESC
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public uint Format;
        public uint SampleCount;
        public uint SampleQuality;
        public uint Usage;
        public uint BindFlags;
        public uint CPUAccessFlags;
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_MAPPED_RECT
    {
        public int Pitch;
        public byte* pBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X, Y;
    }

    public sealed class DeviceContext : IDisposable
    {
        public nint D3DDevice { get; private set; }
        public nint D3DContext { get; private set; }
        public CompositionGraphicsDevice GraphicsDevice { get; }

        public DeviceContext(Compositor compositor)
        {
            Marshal.ThrowExceptionForHR(D3D11CreateDevice(
                0, D3D_DRIVER_TYPE_HARDWARE, 0, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                0, 0, D3D11_SDK_VERSION, out nint d3dDevice, out _, out nint d3dContext));
            D3DDevice = d3dDevice;
            D3DContext = d3dContext;

            Guid dxgiIid = IID_IDXGIDevice;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(d3dDevice, in dxgiIid, out nint dxgiDevice));
            try
            {
                var compRef = ((IWinRTObject)compositor).NativeObject;
                Guid interopIid = IID_ICompositorInterop;
                Marshal.ThrowExceptionForHR(Marshal.QueryInterface(compRef.ThisPtr, in interopIid, out nint compInterop));
                nint graphicsDeviceAbi = 0;
                try
                {
                    // ICompositorInterop::CreateGraphicsDevice is slot 5 (after IUnknown 0..2, CreateCompositionSurfaceForHandle 3, CreateCompositionSurfaceForSwapChain 4)
                    var createGraphicsDevice = (delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)(*(nint**)compInterop)[5];
                    Marshal.ThrowExceptionForHR(createGraphicsDevice(compInterop, dxgiDevice, &graphicsDeviceAbi));
                    GraphicsDevice = MarshalInterface<CompositionGraphicsDevice>.FromAbi(graphicsDeviceAbi);
                }
                finally
                {
                    if (graphicsDeviceAbi != 0) Marshal.Release(graphicsDeviceAbi);
                    Marshal.Release(compInterop);
                }
            }
            finally
            {
                Marshal.Release(dxgiDevice);
            }
        }

        public CompositionDrawingSurface CreateAndUploadPremultipliedBgra(Size size, byte[] premultipliedBgra)
        {
            var surface = GraphicsDevice.CreateDrawingSurface(
                new Windows.Foundation.Size(size.Width, size.Height),
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                DirectXAlphaMode.Premultiplied);
            UploadPremultipliedBgra(surface, size, premultipliedBgra);
            return surface;
        }

        public void UploadPremultipliedBgra(CompositionDrawingSurface surface, Size size, byte[] premultipliedBgra)
        {
            var surfRef = ((IWinRTObject)surface).NativeObject;
            Guid interopIid = IID_ICompositionDrawingSurfaceInterop;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(surfRef.ThisPtr, in interopIid, out nint surfInterop));
            nint tex2D = 0;
            try
            {
                NativeRect updateRect = new() { Left = 0, Top = 0, Right = size.Width, Bottom = size.Height };
                Guid texIid = IID_ID3D11Texture2D;
                NativePoint offset;
                // ICompositionDrawingSurfaceInterop::BeginDraw is slot 3, EndDraw is slot 4
                var beginDraw = (delegate* unmanaged[Stdcall]<nint, NativeRect*, Guid*, nint*, NativePoint*, int>)(*(nint**)surfInterop)[3];
                var endDraw = (delegate* unmanaged[Stdcall]<nint, int>)(*(nint**)surfInterop)[4];
                Marshal.ThrowExceptionForHR(beginDraw(surfInterop, &updateRect, &texIid, &tex2D, &offset));
                try
                {
                    // ID3D11DeviceContext::UpdateSubresource is slot 48 on ID3D11DeviceContext vtable
                    var updateSubresource = (delegate* unmanaged[Stdcall]<nint, nint, uint, NativeBox*, void*, uint, uint, void>)(*(nint**)D3DContext)[48];
                    NativeBox box = new()
                    {
                        Left = (uint)offset.X,
                        Top = (uint)offset.Y,
                        Front = 0,
                        Right = (uint)(offset.X + size.Width),
                        Bottom = (uint)(offset.Y + size.Height),
                        Back = 1,
                    };
                    fixed (byte* pSrc = premultipliedBgra)
                    {
                        updateSubresource(D3DContext, tex2D, 0, &box, pSrc, (uint)(size.Width * 4), (uint)premultipliedBgra.Length);
                    }
                }
                finally
                {
                    _ = endDraw(surfInterop);
                }
            }
            finally
            {
                if (tex2D != 0) Marshal.Release(tex2D);
                Marshal.Release(surfInterop);
            }
        }

        public void Dispose()
        {
            GraphicsDevice?.Dispose();
            if (D3DContext != 0) { Marshal.Release(D3DContext); D3DContext = 0; }
            if (D3DDevice != 0) { Marshal.Release(D3DDevice); D3DDevice = 0; }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBox
    {
        public uint Left, Top, Front, Right, Bottom, Back;
    }

    [LibraryImport("d3d11.dll")]
    private static partial int D3D11CreateDevice(
        nint adapter, int driverType, nint software, uint flags,
        nint featureLevels, uint featureLevelCount, uint sdkVersion,
        out nint device, out int featureLevel, out nint immediateContext);
}
