using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Windows.System;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using Xdc.NativeGlass;

namespace Xdc.SingleWindowGlass;

internal sealed partial class SingleGlassWidget : NativeWindow, IDisposable
{
    private const int UseHostBackdropBrush = 17;
    private const int CornerPreference = 33;
    private const int SystemBackdropType = 38;

    private readonly DispatcherQueueController? queue = CompositionInterop.EnsureDispatcherQueue();
    private readonly Compositor compositor = new();
    private readonly CompositionSurfaceInterop.DeviceContext device;
    private readonly DesktopWindowTarget target;
    private readonly ContainerVisual root;
    private readonly CompositionRoundedRectangleGeometry clipShape;
    private readonly CompositionGeometricClip rootClip;
    private readonly CompositionBackdropBrush backdropBrush;
    private readonly CompositionEffectFactory blurFactory;
    private readonly CompositionEffectBrush blurBrush;
    private readonly SpriteVisual blurVisual;
    private readonly CompositionColorBrush tintBrush;
    private readonly SpriteVisual tintVisual;

    // Header + Dock chrome visuals
    private readonly SpriteVisual headerVisual;
    private readonly SpriteVisual dockVisual;

    // Viewport container with geometric clip + scrollable content container (GPU offset)
    private readonly ContainerVisual viewportVisual;
    private readonly InsetClip viewportClip;
    private readonly ContainerVisual scrollContentVisual;

    private readonly List<CourseRowItem> rows = [];
    private float radiusDip = 30f;
    private float scrollOffsetPx;
    private float maxScrollPx;
    private int hoveredRowIndex = -1;
    private int lastClickedRowIndex = -1;
    private int scrollCount;
    private Rectangle boundsPx;
    private uint dpi = 96;
    private bool blurEnabled = true;

    private sealed record CourseRowItem(
        int Index, string CourseId, string Title, string Detail, string Trail,
        Rectangle BoundsInContentPx, SpriteVisual Visual, CompositionDrawingSurface Surface);

    public int LastClickedRowIndex => lastClickedRowIndex;
    public int HoveredRowIndex => hoveredRowIndex;
    public float ScrollOffsetPx => scrollOffsetPx;
    public int ScrollEventCount => scrollCount;
    public bool BlurEnabled => blurEnabled;
    public CompositionEffectFactoryLoadStatus EffectStatus => blurFactory.LoadStatus;

    public SingleGlassWidget(Rectangle screenBounds, float radiusDip = 30f)
    {
        this.radiusDip = Math.Clamp(radiusDip, 16f, 36f);
        boundsPx = screenBounds;
        device = new CompositionSurfaceInterop.DeviceContext(compositor);

        CreateHandle(new CreateParams
        {
            Caption = "学习桌面单窗口合成原型",
            Style = unchecked((int)0x80000000), // WS_POPUP (visible after SetWindowPos)
            ExStyle = 0x00200000 | 0x08000000 | 0x00000080, // WS_EX_NOREDIRECTIONBITMAP | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
            X = screenBounds.X,
            Y = screenBounds.Y,
            Width = screenBounds.Width,
            Height = screenBounds.Height,
        });

        SetDwmAttribute(SystemBackdropType, 1); // DWMSBT_NONE
        SetDwmAttribute(CornerPreference, 1);   // DWMWCP_DONOTROUND
        SetDwmAttribute(UseHostBackdropBrush, 1);

        dpi = GetDpiForWindow(Handle);
        if (dpi == 0) dpi = 96;

        target = CompositionInterop.CreateWindowTarget(compositor, Handle);
        root = compositor.CreateContainerVisual();
        root.BorderMode = CompositionBorderMode.Soft;
        clipShape = compositor.CreateRoundedRectangleGeometry();
        rootClip = compositor.CreateGeometricClip(clipShape);
        root.Clip = rootClip;

        backdropBrush = compositor.CreateHostBackdropBrush();
        blurFactory = compositor.CreateEffectFactory(
            new GaussianBlurDescription(new CompositionEffectSourceParameter("Backdrop"), 18f));
        blurBrush = blurFactory.CreateBrush();
        blurBrush.SetSourceParameter("Backdrop", backdropBrush);
        blurVisual = compositor.CreateSpriteVisual();
        blurVisual.Brush = blurBrush;

        // Default dark theme 74% transparency -> 26% alpha (66/255) sheet rgb(32,33,36)
        tintBrush = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(66, 32, 33, 36));
        tintVisual = compositor.CreateSpriteVisual();
        tintVisual.Brush = tintBrush;

        root.Children.InsertAtTop(blurVisual);
        root.Children.InsertAtTop(tintVisual);

        headerVisual = compositor.CreateSpriteVisual();
        viewportVisual = compositor.CreateContainerVisual();
        viewportClip = compositor.CreateInsetClip();
        viewportVisual.Clip = viewportClip;
        scrollContentVisual = compositor.CreateContainerVisual();
        viewportVisual.Children.InsertAtTop(scrollContentVisual);
        dockVisual = compositor.CreateSpriteVisual();

        root.Children.InsertAtTop(headerVisual);
        root.Children.InsertAtTop(viewportVisual);
        root.Children.InsertAtTop(dockVisual);
        target.Root = root;

        ApplyGeometryAndBuildScene();
        _ = SetWindowPos(Handle, 0, screenBounds.X, screenBounds.Y, screenBounds.Width, screenBounds.Height, 0x0040 | 0x0010); // SWP_SHOWWINDOW | SWP_NOACTIVATE
    }

    private int Px(double dip) => Math.Max(1, (int)Math.Round(dip * dpi / 96.0));

    public void SetBlurEnabled(bool enabled)
    {
        blurEnabled = enabled;
        blurVisual.IsVisible = enabled;
    }

    public void SetRadius(float value)
    {
        radiusDip = Math.Clamp(value, 16f, 36f);
        UpdateClipAndRegion();
    }

    private void UpdateClipAndRegion()
    {
        var sizeVec = new Vector2(boundsPx.Width, boundsPx.Height);
        float rPx = Math.Min(radiusDip * dpi / 96f, Math.Min(sizeVec.X, sizeVec.Y) / 2f);
        clipShape.Size = sizeVec;
        clipShape.CornerRadius = new Vector2(rPx);
        root.Size = sizeVec;
        blurVisual.Size = sizeVec;
        tintVisual.Size = sizeVec;

        int diameter = Math.Max(1, 2 * ((int)rPx - 1));
        nint rgn = CreateRoundRectRgn(0, 0, boundsPx.Width, boundsPx.Height, diameter, diameter);
        if (rgn != 0 && SetWindowRgn(Handle, rgn, true) == 0) DeleteObject(rgn);
    }

    private void ApplyGeometryAndBuildScene()
    {
        UpdateClipAndRegion();
        int gutter = Px(24);
        int headerTop = Px(12);
        int headerH = Px(74);
        int headerW = boundsPx.Width - 2 * gutter;

        // Render Header ("我的课程" + "30 门模拟课程 · 单窗口 Composition")
        headerVisual.Offset = new Vector3(gutter, headerTop, 0);
        headerVisual.Size = new Vector2(headerW, headerH);
        var headerBgra = RenderPremultipliedSurface(new Size(headerW, headerH), g =>
        {
            using var hFont = new Font("Microsoft YaHei UI", Px(26), FontStyle.Bold, GraphicsUnit.Pixel);
            using var sFont = new Font("Microsoft YaHei UI", Px(12), FontStyle.Regular, GraphicsUnit.Pixel);
            using var fg = new SolidBrush(Color.FromArgb(245, 246, 250));
            using var sub = new SolidBrush(Color.FromArgb(200, 206, 218));
            g.DrawString("我的课程", hFont, fg, new PointF(0, Px(4)));
            g.DrawString("30 门模拟课程 · 单窗口 Composition 原型", sFont, sub, new PointF(Px(2), Px(44)));
        });
        var headerSurf = device.CreateAndUploadPremultipliedBgra(new Size(headerW, headerH), headerBgra);
        headerVisual.Brush = compositor.CreateSurfaceBrush(headerSurf);

        // Render Bottom Dock pill
        int dockW = Px(286);
        int dockH = Px(44);
        int dockX = (boundsPx.Width - dockW) / 2;
        int dockY = boundsPx.Height - dockH - Px(14);
        dockVisual.Offset = new Vector3(dockX, dockY, 0);
        dockVisual.Size = new Vector2(dockW, dockH);
        var dockBgra = RenderPremultipliedSurface(new Size(dockW, dockH), g =>
        {
            var pill = new Rectangle(1, 1, dockW - 2, dockH - 2);
            using var path = GlassControlPaintingLite.Capsule(pill);
            using var fill = new SolidBrush(Color.FromArgb(190, 52, 54, 62));
            using var border = new Pen(Color.FromArgb(90, 255, 255, 255), 1f);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.FillPath(fill, path);
            g.DrawPath(border, path);
            using var dFont = new Font("Microsoft YaHei UI", Px(13), FontStyle.Bold, GraphicsUnit.Pixel);
            using var fg = new SolidBrush(Color.FromArgb(245, 246, 250));
            using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("⌂ 总览      ▤ 我的课程      ↓ 下载记录", dFont, fg, new RectangleF(0, 0, dockW, dockH), fmt);
        });
        var dockSurf = device.CreateAndUploadPremultipliedBgra(new Size(dockW, dockH), dockBgra);
        dockVisual.Brush = compositor.CreateSurfaceBrush(dockSurf);

        // Viewport for 30 scrollable rows
        int viewTop = headerTop + headerH + Px(6);
        int viewH = Math.Max(Px(100), dockY - viewTop - Px(12));
        int viewW = boundsPx.Width - 2 * gutter;
        viewportVisual.Offset = new Vector3(gutter, viewTop, 0);
        viewportVisual.Size = new Vector2(viewW, viewH);

        int rowH = Px(54);
        rows.Clear();
        scrollContentVisual.Children.RemoveAll();
        for (int i = 0; i < 30; i++)
        {
            string cid = (101 + i).ToString();
            string title = i == 0 ? "验收示例课程" : i == 1 ? "另一门示例课程" : $"单窗口合成课程 {i + 1:00}";
            string detail = $"示例教师 · 2026–2027 秋冬";
            var rowRect = new Rectangle(0, i * rowH, viewW, rowH);
            var bgra = RenderRowBgra(new Size(viewW, rowH), title, detail, "›", hovered: false);
            var surf = device.CreateAndUploadPremultipliedBgra(new Size(viewW, rowH), bgra);
            var sprite = compositor.CreateSpriteVisual();
            sprite.Offset = new Vector3(0, rowRect.Y, 0);
            sprite.Size = new Vector2(viewW, rowH);
            sprite.Brush = compositor.CreateSurfaceBrush(surf);
            scrollContentVisual.Children.InsertAtTop(sprite);
            rows.Add(new CourseRowItem(i, cid, title, detail, "›", rowRect, sprite, surf));
        }

        maxScrollPx = Math.Max(0, 30 * rowH - viewH);
        scrollOffsetPx = 0;
        scrollContentVisual.Offset = Vector3.Zero;
    }

    private byte[] RenderRowBgra(Size size, string title, string detail, string trail, bool hovered)
    {
        return RenderPremultipliedSurface(size, g =>
        {
            if (hovered)
            {
                using var hoverFill = new SolidBrush(Color.FromArgb(24, 255, 255, 255));
                g.FillRectangle(hoverFill, 0, 0, size.Width, size.Height - 1);
            }
            using var rulePen = new Pen(Color.FromArgb(42, 255, 255, 255), 1f);
            g.DrawLine(rulePen, 0, size.Height - 1, size.Width, size.Height - 1);

            using var tFont = new Font("Microsoft YaHei UI", Px(14), FontStyle.Bold, GraphicsUnit.Pixel);
            using var dFont = new Font("Microsoft YaHei UI", Px(12), FontStyle.Regular, GraphicsUnit.Pixel);
            using var tBrush = new SolidBrush(Color.FromArgb(244, 245, 249));
            using var dBrush = new SolidBrush(Color.FromArgb(196, 202, 214));

            int padX = Px(8);
            g.DrawString(title, tFont, tBrush, new PointF(padX, Px(7)));
            g.DrawString(detail, dFont, dBrush, new PointF(padX, Px(29)));
            using var rightFmt = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
            g.DrawString(trail, tFont, dBrush, new RectangleF(0, 0, size.Width - padX, size.Height), rightFmt);
        });
    }

    public byte[] DumpFirstRowPremultipliedBgra(out Size size)
    {
        var r = rows[0];
        size = r.BoundsInContentPx.Size;
        return RenderRowBgra(size, r.Title, r.Detail, r.Trail, hovered: false);
    }

    private static unsafe byte[] RenderPremultipliedSurface(Size size, Action<Graphics> draw)
    {
        using var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.Clear(Color.FromArgb(0, 0, 0, 0));
            g.CompositingMode = CompositingMode.SourceOver;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            draw(g);
            g.Flush(FlushIntention.Sync);
        }
        var data = bmp.LockBits(new Rectangle(Point.Empty, size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            byte[] bytes = new byte[size.Width * size.Height * 4];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    private int HitRowAtClientPoint(Point clientPt)
    {
        int viewX = (int)viewportVisual.Offset.X;
        int viewY = (int)viewportVisual.Offset.Y;
        int viewW = (int)viewportVisual.Size.X;
        int viewH = (int)viewportVisual.Size.Y;
        if (clientPt.X < viewX || clientPt.X >= viewX + viewW || clientPt.Y < viewY || clientPt.Y >= viewY + viewH)
            return -1;
        float contentY = clientPt.Y - viewY + scrollOffsetPx;
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i].BoundsInContentPx;
            if (contentY >= r.Top && contentY < r.Bottom) return i;
        }
        return -1;
    }

    private void UpdateHoveredRow(int nextIndex)
    {
        if (hoveredRowIndex == nextIndex) return;
        int prev = hoveredRowIndex;
        hoveredRowIndex = nextIndex;
        if (prev >= 0 && prev < rows.Count)
        {
            var r = rows[prev];
            device.UploadPremultipliedBgra(r.Surface, r.BoundsInContentPx.Size,
                RenderRowBgra(r.BoundsInContentPx.Size, r.Title, r.Detail, r.Trail, hovered: false));
        }
        if (nextIndex >= 0 && nextIndex < rows.Count)
        {
            var r = rows[nextIndex];
            device.UploadPremultipliedBgra(r.Surface, r.BoundsInContentPx.Size,
                RenderRowBgra(r.BoundsInContentPx.Size, r.Title, r.Detail, r.Trail, hovered: true));
        }
    }

    public void ScrollBy(float deltaPx)
    {
        // GPU-only scroll: only changes Visual.Offset, zero glyph redraw!
        scrollOffsetPx = Math.Clamp(scrollOffsetPx + deltaPx, 0f, maxScrollPx);
        scrollContentVisual.Offset = new Vector3(0f, -scrollOffsetPx, 0f);
        scrollCount++;
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case 0x0084: // WM_NCHITTEST -> single HWND handles all hits inside its rounded region!
                m.Result = 1; // HTCLIENT
                return;
            case 0x0021: // WM_MOUSEACTIVATE
                m.Result = 3; // MA_NOACTIVATE
                return;
            case 0x0010: // WM_CLOSE
            case 0x0205: // WM_RBUTTONUP
                Application.ExitThread();
                m.Result = 0;
                return;
            case 0x0200: // WM_MOUSEMOVE
            {
                var pt = UnpackPoint(m.LParam);
                UpdateHoveredRow(HitRowAtClientPoint(pt));
                m.Result = 0;
                return;
            }
            case 0x0201: // WM_LBUTTONDOWN
            case 0x0202: // WM_LBUTTONUP
            {
                var pt = UnpackPoint(m.LParam);
                int idx = HitRowAtClientPoint(pt);
                if (m.Msg == 0x0202 && idx >= 0) lastClickedRowIndex = idx;
                m.Result = 0;
                return;
            }
            case 0x020A: // WM_MOUSEWHEEL
            {
                short wheelDelta = unchecked((short)((long)m.WParam >> 16));
                ScrollBy(-wheelDelta * (Px(54) / 120f));
                m.Result = 0;
                return;
            }
        }
        base.WndProc(ref m);
    }

    private static Point UnpackPoint(nint lParam) =>
        new(unchecked((short)lParam), unchecked((short)(lParam >> 16)));

    private void SetDwmAttribute(int property, int value) =>
        Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(Handle, property, in value, sizeof(int)));

    public void Dispose()
    {
        if (target is not null) target.Root = null;
        foreach (var r in rows)
        {
            r.Visual.Dispose();
            r.Surface.Dispose();
        }
        scrollContentVisual?.Dispose();
        viewportClip?.Dispose();
        viewportVisual?.Dispose();
        headerVisual?.Dispose();
        dockVisual?.Dispose();
        tintVisual?.Dispose();
        tintBrush?.Dispose();
        blurVisual?.Dispose();
        blurBrush?.Dispose();
        backdropBrush?.Dispose();
        blurFactory?.Dispose();
        rootClip?.Dispose();
        clipShape?.Dispose();
        root?.Dispose();
        target?.Dispose();
        device?.Dispose();
        if (Handle != 0) DestroyHandle();
        compositor?.Dispose();
        GC.KeepAlive(queue);
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, in int value, uint size);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial int SetWindowRgn(nint hwnd, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint graphicsObject);
}

internal static class GlassControlPaintingLite
{
    public static GraphicsPath Capsule(Rectangle r)
    {
        var path = new GraphicsPath();
        int d = Math.Min(r.Width, r.Height);
        path.AddArc(r.Left, r.Top, d, d, 90, 180);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 180);
        path.CloseFigure();
        return path;
    }
}
