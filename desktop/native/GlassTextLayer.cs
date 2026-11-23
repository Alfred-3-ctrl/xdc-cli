using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace Xdc.NativeDesktop;

internal sealed partial class GlassTextLayer : NativeWindow, IDisposable
{
    private const uint PreviousWindow = 3;
    private const int ExtendedStyleIndex = -20;
    private const int TopmostStyle = 0x00000008;
    private const uint KeepSize = 0x0001;
    private const uint KeepPosition = 0x0002;
    private const uint KeepZOrder = 0x0004;
    private const uint KeepActivationAndOwnerOrder = 0x0210;
    private const uint ShowWithoutActivation = 0x0040;
    private const uint PerPixelAlpha = 0x00000002;
    private readonly nint owner;
    private nint dc;
    private nint dib;
    private nint originalBitmap;
    private Bitmap? surface;
    private Rectangle bounds;
    private bool visible;
    private bool disposed;
    public GlassTextLayer(nint owner)
    {
        this.owner = owner;
        try
        {
            CreateHandle(new CreateParams
            {
                Parent = owner,
                Caption = "学习桌面文字",
                Style = unchecked((int)0x80000000), // WS_POPUP
                ExStyle = 0x080800A0, // NOACTIVATE, LAYERED, TOOLWINDOW, TRANSPARENT
                Width = 1,
                Height = 1,
            });
            dc = CreateCompatibleDC(0);
            if (dc == 0) throw LastError("无法创建文字图层绘图设备。");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Draw(Rectangle screenBounds, Action<Graphics> paint)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (screenBounds.Width <= 0 || screenBounds.Height <= 0)
        {
            Hide();
            return;
        }

        var bitmap = GetSurface(screenBounds.Size);
        uint dpi = GetDpiForWindow(owner);
        if (dpi != 0 && (bitmap.HorizontalResolution != dpi || bitmap.VerticalResolution != dpi))
            bitmap.SetResolution(dpi, dpi);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.PageUnit = GraphicsUnit.Pixel;
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.Clear(Color.FromArgb(0, 0, 0, 0));
            graphics.CompositingMode = CompositingMode.SourceOver;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            paint(graphics);
            graphics.Flush(FlushIntention.Sync);
        }

        var destination = new NativePoint { X = screenBounds.X, Y = screenBounds.Y };
        var dimensions = new NativeSize { Width = screenBounds.Width, Height = screenBounds.Height };
        var origin = new NativePoint();
        var blend = new BlendFunction { SourceAlpha = 255, Format = 1 }; // AC_SRC_OVER, AC_SRC_ALPHA
        if (!UpdateLayeredWindow(Handle, 0, in destination, in dimensions, dc, in origin, 0, in blend, PerPixelAlpha))
            throw LastError("无法呈现文字图层。");
        bounds = screenBounds;
        Position(screenBounds);
    }

    public void Position(Rectangle screenBounds)
    {
        if (disposed) return;
        if (bounds.IsEmpty || screenBounds.Size != bounds.Size)
        {
            Hide();
            return;
        }

        nint aboveOwner = GetWindow(owner, PreviousWindow);
        bool reorder = aboveOwner != Handle;
        bool move = screenBounds.Location != bounds.Location;
        if (visible && !move && !reorder) return;
        // Follow the owner without crossing into the topmost band or covering an owned dialog.
        if (reorder && aboveOwner != 0 && (GetWindowLong(aboveOwner, ExtendedStyleIndex) & TopmostStyle) != 0)
            aboveOwner = 0;
        uint flags = KeepActivationAndOwnerOrder | KeepSize;
        if (!move) flags |= KeepPosition;
        if (!reorder) flags |= KeepZOrder;
        if (!visible) flags |= ShowWithoutActivation;
        if (!SetWindowPos(Handle, aboveOwner, screenBounds.X, screenBounds.Y, 0, 0, flags))
            throw LastError("无法定位文字图层。");
        bounds = screenBounds;
        visible = true;
    }

    public void Hide()
    {
        if (visible && Handle != 0) _ = ShowWindow(Handle, 0); // SW_HIDE
        visible = false;
    }

    private Bitmap GetSurface(Size size)
    {
        if (surface is { } current && current.Size == size) return current;
        ReleaseSurface();

        var info = new BitmapInfo
        {
            Header = new BitmapHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapHeader>(),
                Width = size.Width,
                Height = -size.Height,
                Planes = 1,
                BitCount = 32,
            },
        };
        try
        {
            dib = CreateDIBSection(dc, in info, 0, out nint pixels, 0, 0);
            if (dib == 0 || pixels == 0) throw LastError("无法分配文字图层像素。");
            nint previous = SelectObject(dc, dib);
            if (previous == 0 || previous == -1) throw LastError("无法设置文字图层绘图表面。");
            originalBitmap = previous;
            surface = new Bitmap(size.Width, size.Height, checked(size.Width * 4), PixelFormat.Format32bppPArgb, pixels);
            return surface;
        }
        catch
        {
            ReleaseSurface();
            throw;
        }
    }

    private void ReleaseSurface()
    {
        surface?.Dispose();
        surface = null;
        if (originalBitmap != 0)
        {
            _ = SelectObject(dc, originalBitmap);
            originalBitmap = 0;
        }
        if (dib != 0)
        {
            _ = DeleteObject(dib);
            dib = 0;
        }
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0084) // WM_NCHITTEST
        {
            message.Result = -1; // HTTRANSPARENT
            return;
        }
        base.WndProc(ref message);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            if (Handle != 0) DestroyHandle();
        }
        finally
        {
            ReleaseSurface();
            if (dc != 0)
            {
                _ = DeleteDC(dc);
                dc = 0;
            }
        }
    }

    private static Win32Exception LastError(string message) => new(Marshal.GetLastPInvokeError(), message);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Width;
        public int Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlendFunction
    {
        public byte Operation;
        public byte Flags;
        public byte SourceAlpha;
        public byte Format;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapHeader Header;
        public uint FirstColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPixelsPerMeter;
        public int YPixelsPerMeter;
        public uint ColorsUsed;
        public uint ColorsImportant;
    }

    [LibraryImport("gdi32.dll", SetLastError = true)]
    private static partial nint CreateCompatibleDC(nint source);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    private static partial nint CreateDIBSection(nint dc, in BitmapInfo info, uint usage, out nint pixels,
        nint section, uint offset);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    private static partial nint SelectObject(nint dc, nint item);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint item);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(nint dc);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateLayeredWindow(nint window, nint destinationDc, in NativePoint destination,
        in NativeSize size, nint sourceDc, in NativePoint source, uint colorKey, in BlendFunction blend, uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint window, int command);

    [LibraryImport("user32.dll")]
    private static partial nint GetWindow(nint window, uint command);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static partial int GetWindowLong(nint window, int index);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint window);
}
