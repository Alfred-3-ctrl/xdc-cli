using System.Numerics;
using System.Runtime.InteropServices;
using Windows.System;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;

namespace Xdc.NativeGlass;

internal sealed partial class GlassWindow : NativeWindow, IDisposable
{
    private const int UseHostBackdropBrush = 17;
    private const int CornerPreference = 33;
    private const int SystemBackdropType = 38;
    private const uint KeepActivationAndOwnerOrder = 0x0210;
    private const uint KeepZOrder = 0x0004;
    private const uint ShowWithoutActivation = 0x0040;
    private const uint NextWindow = 2;

    private readonly bool passive;
    private readonly DispatcherQueueController? queue;
    private readonly Compositor compositor;
    private readonly DesktopWindowTarget target;
    private readonly ContainerVisual panel;
    private readonly CompositionRoundedRectangleGeometry shape;
    private readonly CompositionGeometricClip clip;
    private readonly CompositionBackdropBrush backdropBrush;
    private readonly CompositionEffectFactory blurFactory;
    private readonly CompositionEffectBrush blurBrush;
    private readonly CompositionColorBrush tintBrush;
    private readonly SpriteVisual blur;
    private readonly SpriteVisual tint;
    private float radius = 30f;
    private Rectangle bounds;
    private (Size Size, float Radius, uint Dpi) geometry;
    private bool visible;
    private bool disposed;

    public GlassWindow(nint owner, bool passive = false)
    {
        this.passive = passive;
        queue = CompositionInterop.EnsureDispatcherQueue();
        compositor = new Compositor();
        try
        {
            CreateHandle(new CreateParams
            {
                Parent = owner,
                Caption = passive ? "学习桌面原生玻璃" : null,
                Style = unchecked((int)0x80000000) | (passive ? 0x08000000 : 0), // WS_POPUP, WS_DISABLED
                ExStyle = 0x00200000 | 0x08000000 | 0x00000080, // NOREDIRECTIONBITMAP, NOACTIVATE, TOOLWINDOW
                Width = 1,
                Height = 1,
            });

            // DWM supplies the sample, but it does not paint the HWND's bounds.
            SetDwmAttribute(SystemBackdropType, 1); // DWMSBT_NONE
            SetDwmAttribute(CornerPreference, 1); // DWMWCP_DONOTROUND
            SetDwmAttribute(UseHostBackdropBrush, 1);

            target = CompositionInterop.CreateWindowTarget(compositor, Handle);
            panel = compositor.CreateContainerVisual();
            panel.BorderMode = CompositionBorderMode.Soft;
            shape = compositor.CreateRoundedRectangleGeometry();
            clip = compositor.CreateGeometricClip(shape);
            panel.Clip = clip;

            backdropBrush = compositor.CreateHostBackdropBrush();
            var description = new GaussianBlurDescription(new CompositionEffectSourceParameter("Backdrop"), 18f);
            blurFactory = compositor.CreateEffectFactory(description);
            blurBrush = blurFactory.CreateBrush();
            blurBrush.SetSourceParameter("Backdrop", backdropBrush);
            blur = compositor.CreateSpriteVisual();
            blur.Brush = blurBrush;

            tintBrush = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(24, 21, 30, 43));
            tint = compositor.CreateSpriteVisual();
            tint.Brush = tintBrush;
            panel.Children.InsertAtTop(blur);
            panel.Children.InsertAtTop(tint);
            target.Root = panel;
        }
        catch
        {
            if (Handle != 0) DestroyHandle();
            compositor.Dispose();
            throw;
        }
    }

    public void SetRadius(float value)
    {
        radius = Math.Clamp(value, 16f, 36f);
        ApplyGeometry();
    }

    public void SetEnabled(bool enabled)
    {
        blur.IsVisible = enabled;
        tint.IsVisible = enabled;
    }

    public string? ReadEffectStatus()
    {
        var status = blurFactory.LoadStatus;
        if (status == CompositionEffectFactoryLoadStatus.Pending) return null;
        if (status == CompositionEffectFactoryLoadStatus.Success) return "系统模糊已加载。";
        SetEnabled(false);
        return $"无法加载系统模糊（{status}）。\r\nHRESULT 0x{blurFactory.ExtendedError?.HResult:X8}";
    }

    public CompositionEffectFactoryLoadStatus EffectLoadStatus => blurFactory.LoadStatus;
    public int? EffectError => blurFactory.ExtendedError?.HResult;

    public void Place(Rectangle screenBounds) => PlaceBehind(screenBounds, 0);

    public void PlaceBehind(Rectangle screenBounds, nint front)
    {
        if (screenBounds.IsEmpty)
        {
            if (visible) ShowWindow(Handle, 0); // SW_HIDE
            visible = false;
            return;
        }

        bool reorder = front != 0 && GetWindow(front, NextWindow) != Handle;
        if (!visible || bounds != screenBounds || reorder)
        {
            uint flags = KeepActivationAndOwnerOrder;
            if (!reorder) flags |= KeepZOrder;
            if (!visible) flags |= ShowWithoutActivation;
            if (!SetWindowPos(Handle, front, screenBounds.X, screenBounds.Y,
                    screenBounds.Width, screenBounds.Height, flags))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
            bounds = screenBounds;
        }
        ApplyGeometry();
        visible = true;
    }

    private void ApplyGeometry()
    {
        if (bounds.IsEmpty) return;
        uint dpi = GetDpiForWindow(Handle);
        var next = (bounds.Size, radius, dpi);
        if (geometry == next) return;
        var pixels = new Vector2(bounds.Width, bounds.Height);
        float physicalRadius = Math.Min(radius * dpi / 96f, Math.Min(pixels.X, pixels.Y) / 2);
        shape.Size = pixels;
        shape.CornerRadius = new Vector2(physicalRadius);
        panel.Size = pixels;
        blur.Size = pixels;
        tint.Size = pixels;
        if (passive)
        {
            // The visual provides antialiasing. The slightly wider native region keeps
            // rounded corners transparent to input even across process boundaries.
            int diameter = Math.Max(1, 2 * ((int)physicalRadius - 1));
            nint region = CreateRoundRectRgn(0, 0, bounds.Width, bounds.Height, diameter, diameter);
            if (region == 0) throw new InvalidOperationException("Cannot create the glass hit-test region.");
            if (SetWindowRgn(Handle, region, true) == 0)
            {
                DeleteObject(region);
                throw new InvalidOperationException("Cannot apply the glass hit-test region.");
            }
        }
        geometry = next;
    }

    private void SetDwmAttribute(int property, int value) =>
        Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(Handle, property, in value, sizeof(int)));

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0084) // WM_NCHITTEST
        {
            message.Result = -1; // HTTRANSPARENT, test controls belong to the backdrop form.
            return;
        }
        base.WndProc(ref message);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        target.Root = null;
        tint.Dispose();
        tintBrush.Dispose();
        blur.Dispose();
        blurBrush.Dispose();
        blurFactory.Dispose();
        backdropBrush.Dispose();
        clip.Dispose();
        shape.Dispose();
        panel.Dispose();
        target.Dispose();
        compositor.Dispose();
        if (Handle != 0) DestroyHandle();
        GC.KeepAlive(queue);
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, in int value, uint size);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint hwnd, int command);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial nint GetWindow(nint hwnd, uint command);

    [LibraryImport("user32.dll")]
    private static partial int SetWindowRgn(nint hwnd, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint graphicsObject);
}
