using System.Runtime.InteropServices;

namespace Xdc.NativeDesktop;

// Color-keyed content pixels pass through the front HWND. The glass directly behind
// it can route those clicks and wheel events inside the content viewport.
internal sealed partial class GlassContentInput : NativeWindow, IDisposable
{
    private readonly ContentScrollPanel content;
    private GlassButton? lastHovered;
    private bool trackingLeave;

    public GlassContentInput(nint backdrop, ContentScrollPanel content)
    {
        this.content = content;
        AssignHandle(backdrop);
        _ = EnableWindow(backdrop, true);
    }

    public void Dispose()
    {
        SetHovered(null);
        if (Handle != 0) ReleaseHandle();
    }

    protected override void WndProc(ref Message message)
    {
        switch (message.Msg)
        {
            case 0x0084: // WM_NCHITTEST
                if (ContentContains(MessagePoint(message.LParam)))
                {
                    message.Result = 1; // HTCLIENT
                    return;
                }
                break;
            case 0x0021: // WM_MOUSEACTIVATE
                message.Result = 3; // MA_NOACTIVATE
                return;
            case 0x0020: // WM_SETCURSOR
                if (ControlAt(Cursor.Position) is { } hovered)
                {
                    _ = SetCursor(hovered.Cursor.Handle);
                    message.Result = 1;
                    return;
                }
                break;
            case 0x0200: // WM_MOUSEMOVE
            {
                if (!trackingLeave)
                {
                    var tracking = new MouseTracking { Size = (uint)Marshal.SizeOf<MouseTracking>(), Flags = 2, Window = Handle };
                    trackingLeave = TrackMouseEvent(ref tracking);
                }
                var moveLocal = MessagePoint(message.LParam);
                var moveScreen = new NativePoint { X = moveLocal.X, Y = moveLocal.Y };
                if (!ClientToScreen(Handle, ref moveScreen)) break;
                SetHovered(ControlAt(new Point(moveScreen.X, moveScreen.Y)) as GlassButton);
                break;
            }
            case 0x02A3: // WM_MOUSELEAVE
                trackingLeave = false;
                SetHovered(null);
                break;
            case 0x0201: // WM_LBUTTONDOWN
            case 0x0202: // WM_LBUTTONUP
            case 0x0203: // WM_LBUTTONDBLCLK
            case 0x0204: // WM_RBUTTONDOWN
            case 0x0205: // WM_RBUTTONUP
                var local = MessagePoint(message.LParam);
                var screen = new NativePoint { X = local.X, Y = local.Y };
                if (!ClientToScreen(Handle, ref screen)) break;
                var point = new Point(screen.X, screen.Y);
                if (ControlAt(point) is { } target)
                {
                    if (target is not GlassButton && message.Msg == 0x0201) content.FindForm()?.Activate();
                    _ = SendMessage(target.Handle, (uint)message.Msg, message.WParam,
                        PackPoint(target.PointToClient(point)));
                    return;
                }
                break;
            case 0x020A: // WM_MOUSEWHEEL
                if (ContentContains(MessagePoint(message.LParam)))
                {
                    _ = SendMessage(content.Body.Handle, (uint)message.Msg, message.WParam, message.LParam);
                    return;
                }
                break;
        }
        base.WndProc(ref message);
    }

    private void SetHovered(GlassButton? next)
    {
        if (lastHovered == next) return;
        if (lastHovered is { IsDisposed: false } previous) previous.SetGlassHover(false);
        lastHovered = next;
        next?.SetGlassHover(true);
    }

    private bool ContentContains(Point point) => content.IsHandleCreated && content.Visible && content.Enabled &&
        content.FindForm() is { IsHandleCreated: true } form && IsWindowEnabled(form.Handle) &&
        content.RectangleToScreen(content.ClientRectangle).Contains(point);

    private Control? ControlAt(Point point)
    {
        if (!ContentContains(point)) return null;
        Point local = content.PointToClient(point);
        if (!content.ViewportBounds.Contains(local)) return content.ScrollbarAt(local);
        var body = content.Body;
        return body.GetChildAtPoint(body.PointToClient(point), GetChildAtPointSkip.Invisible) is
            GlassButton { UseGlassBackground: true, Enabled: true } button ? button : null;
    }

    internal static Point MessagePoint(nint value) =>
        new(unchecked((short)value), unchecked((short)(value >> 16)));

    private static nint PackPoint(Point point) => (nint)(unchecked((ushort)point.X) |
        (unchecked((ushort)point.Y) << 16));

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseTracking
    {
        public uint Size;
        public uint Flags;
        public nint Window;
        public uint HoverTime;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TrackMouseEvent(ref MouseTracking tracking);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnableWindow(nint window, [MarshalAs(UnmanagedType.Bool)] bool enabled);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowEnabled(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClientToScreen(nint window, ref NativePoint point);

    [LibraryImport("user32.dll")]
    private static partial nint SetCursor(nint cursor);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial nint SendMessage(nint window, uint message, nint wParam, nint lParam);
}
