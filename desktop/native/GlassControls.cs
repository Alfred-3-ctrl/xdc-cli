using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace Xdc.NativeDesktop;

internal sealed class GlassLabel : Label
{
    public GlassLabel()
    {
        UseMnemonic = false;
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (!GlassControlPainting.IsColorKey(BackColor))
        {
            base.OnPaint(e);
            return;
        }

        GlassControlPainting.DrawText(e.Graphics, Text, Font, ForeColor, ClientRectangle,
            Padding, TextAlign, AutoEllipsis, keyed: true, RightToLeft == RightToLeft.Yes);
    }
}

internal sealed class GlassButton : Button
{
    private bool hovered;
    private bool mousePressed;
    private bool keyPressed;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Color CanvasColor { get; set; } = Color.Magenta;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Color BorderColor { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Color HoverColor { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Color PressedColor { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Color FocusColor { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal int CornerRadius { get; set; } = 8;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool UseGlassBackground { get; set; }

    public GlassButton()
    {
        UseVisualStyleBackColor = false;
        UseMnemonic = false;
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width < 2 || Height < 2) return;
        // ButtonBase paints as an opaque control, so initialize its buffer here.
        using (var canvas = new SolidBrush(CanvasColor)) e.Graphics.FillRectangle(canvas, ClientRectangle);
        var surface = ClientRectangle;
        surface.Width--;
        surface.Height--;
        bool keyedCanvas = GlassControlPainting.IsColorKey(CanvasColor);
        e.Graphics.SmoothingMode = keyedCanvas ? SmoothingMode.None : SmoothingMode.AntiAlias;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;

        Color fill = BackColor;
        if (Enabled && (mousePressed || keyPressed) && !PressedColor.IsEmpty) fill = PressedColor;
        else if (Enabled && hovered && !HoverColor.IsEmpty) fill = HoverColor;

        using var shape = GlassControlPainting.RoundedPath(surface, CornerRadius);
        if (!UseGlassBackground)
        {
            using var brush = new SolidBrush(GlassControlPainting.VisibleColor(fill));
            e.Graphics.FillPath(brush, shape);
        }

        var outline = Focused && ShowFocusCues && !FocusColor.IsEmpty ? FocusColor : BorderColor;
        if (!outline.IsEmpty)
        {
            using var pen = new Pen(GlassControlPainting.VisibleColor(outline));
            e.Graphics.DrawPath(pen, shape);
        }

        Color ink = Enabled ? ForeColor : ControlPaint.Dark(ForeColor, 0.24f);
        var state = e.Graphics.Save();
        e.Graphics.SetClip(shape, CombineMode.Intersect);
        GlassControlPainting.DrawText(e.Graphics, Text, Font, ink, ClientRectangle,
            Padding, TextAlign, AutoEllipsis, keyedCanvas, RightToLeft == RightToLeft.Yes);
        e.Graphics.Restore(state);
    }

    protected override void WndProc(ref Message message)
    {
        if (!UseGlassBackground)
        {
            base.WndProc(ref message);
            return;
        }

        // A keyed pixel can hit the glass HWND during release even though the pointer
        // is still inside this button. Use its bounds for the captured click.
        switch (message.Msg)
        {
            case 0x0201: // WM_LBUTTONDOWN
            case 0x0203: // WM_LBUTTONDBLCLK
                if (Enabled)
                {
                    FindForm()?.Activate();
                    Focus();
                    Capture = true;
                    mousePressed = true;
                }
                return;
            case 0x0202: // WM_LBUTTONUP
                var cursor = GlassContentInput.MessagePoint(message.LParam);
                bool clicked = mousePressed && ClientRectangle.Contains(cursor) &&
                    (Parent is null || Parent.ClientRectangle.Contains(Parent.PointToClient(PointToScreen(cursor))));
                mousePressed = false;
                Capture = false;
                if (clicked) PerformClick();
                return;
            default:
                base.WndProc(ref message);
                break;
        }
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        if (UseGlassBackground && !Capture) mousePressed = false;
        base.OnMouseCaptureChanged(e);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        hovered = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        hovered = false;
        if (!UseGlassBackground || !Capture) mousePressed = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        mousePressed = true;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        mousePressed = false;
        Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode != Keys.Space) return;
        keyPressed = true;
        Invalidate();
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.KeyCode != Keys.Space) return;
        keyPressed = false;
        Invalidate();
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        keyPressed = false;
        Invalidate();
    }
}

internal sealed partial class GlassTrackBar : TrackBar
{
    private const int ReflectedNotify = 0x204e;
    private const int CustomDraw = -12;
    private const uint PrePaint = 0x00000001;
    private const uint ItemPrePaint = 0x00010001;
    private const nuint ThumbPart = 2;
    private const nuint ChannelPart = 3;
    private const uint GetThumbRectangle = 0x0419;
    private const uint GetChannelRectangle = 0x041a;
    private const int SkipDefault = 0x00000004;
    private const int NotifyItems = 0x00000020;
    private Color trackColor = SystemColors.Highlight;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Color RailColor { get; set; } = SystemColors.ControlDark;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Color TrackColor
    {
        get => trackColor;
        set
        {
            if (trackColor == value) return;
            trackColor = value;
            Invalidate();
        }
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg != ReflectedNotify || message.LParam == 0 ||
            Orientation != Orientation.Horizontal ||
            Marshal.PtrToStructure<NotifyHeader>(message.LParam).Code != CustomDraw)
        {
            base.WndProc(ref message);
            return;
        }

        var draw = Marshal.PtrToStructure<CustomDrawMessage>(message.LParam);
        if (draw.Stage == PrePaint)
        {
            message.Result = NotifyItems;
            return;
        }

        if (draw.Stage != ItemPrePaint || (draw.Item != ChannelPart && draw.Item != ThumbPart))
        {
            base.WndProc(ref message);
            return;
        }

        var thumb = GetBounds(GetThumbRectangle);
        var channel = GetBounds(GetChannelRectangle);
        if (thumb.IsEmpty || channel.IsEmpty)
        {
            base.WndProc(ref message);
            return;
        }

        using var graphics = Graphics.FromHdc(draw.DeviceContext);
        graphics.SmoothingMode = GlassControlPainting.IsColorKey(BackColor) ? SmoothingMode.None : SmoothingMode.AntiAlias;
        var color = Enabled ? TrackColor : SystemColors.GrayText;
        int centerX = thumb.Left + thumb.Width / 2;
        int centerY = thumb.Top + thumb.Height / 2;
        if (draw.Item == ChannelPart)
        {
            int thickness = Math.Max(2, (int)Math.Round(4 * DeviceDpi / 96.0));
            var rail = new Rectangle(channel.Left, centerY - thickness / 2, channel.Width, thickness);
            FillRail(graphics, rail, RailColor);

            bool rightToLeft = RightToLeft == RightToLeft.Yes && RightToLeftLayout;
            int current = Math.Clamp(centerX, rail.Left, rail.Right);
            var selected = rightToLeft
                ? Rectangle.FromLTRB(current, rail.Top, rail.Right, rail.Bottom)
                : Rectangle.FromLTRB(rail.Left, rail.Top, current, rail.Bottom);
            FillRail(graphics, selected, color);
        }
        else
        {
            int diameter = Math.Max(1, Math.Min(thumb.Width, thumb.Height));
            var bounds = new Rectangle(centerX - diameter / 2, centerY - diameter / 2, diameter, diameter);
            using var brush = new SolidBrush(GlassControlPainting.VisibleColor(color));
            graphics.FillEllipse(brush, bounds);
            if (Focused && ShowFocusCues && diameter > 4)
            {
                bounds.Inflate(-1, -1);
                using var outline = new Pen(ForeColor);
                graphics.DrawEllipse(outline, bounds);
            }
        }
        message.Result = SkipDefault;
    }

    private Rectangle GetBounds(uint message)
    {
        _ = SendMessage(Handle, message, 0, out var bounds);
        return Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
    }

    private static void FillRail(Graphics graphics, Rectangle bounds, Color color)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        using var path = GlassControlPainting.RoundedPath(bounds, bounds.Height / 2);
        using var brush = new SolidBrush(GlassControlPainting.VisibleColor(color));
        graphics.FillPath(brush, path);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyHeader
    {
        public nint Window;
        public nuint Identifier;
        public int Code;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CustomDrawMessage
    {
        public NotifyHeader Header;
        public uint Stage;
        public nint DeviceContext;
        public NativeRectangle Bounds;
        public nuint Item;
        public uint State;
        public nint Parameter;
    }

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial nint SendMessage(nint hwnd, uint message, nuint parameter, out NativeRectangle bounds);
}

internal static class GlassControlPainting
{
    public static bool IsColorKey(Color color) => color.ToArgb() == Color.Magenta.ToArgb();

    // The exact key color cannot be displayed, including when chosen as an accent.
    public static Color VisibleColor(Color color) => IsColorKey(color) ? Color.FromArgb(254, 0, 255) : color;

    public static GraphicsPath RoundedPath(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        int diameter = Math.Clamp(radius, 0, Math.Min(bounds.Width, bounds.Height) / 2) * 2;
        if (diameter < 2)
        {
            path.AddRectangle(bounds);
            return path;
        }
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void DrawText(Graphics graphics, string text, Font font, Color color, Rectangle bounds,
        Padding padding, ContentAlignment alignment, bool ellipsis, bool keyed, bool rightToLeft)
    {
        var textBounds = RectangleF.FromLTRB(bounds.Left + padding.Left, bounds.Top + padding.Top,
            bounds.Right - padding.Right, bounds.Bottom - padding.Bottom);
        if (string.IsNullOrEmpty(text) || textBounds.Width <= 0 || textBounds.Height <= 0) return;

        using var format = new StringFormat
        {
            Alignment = alignment switch
            {
                ContentAlignment.TopCenter or ContentAlignment.MiddleCenter or ContentAlignment.BottomCenter => StringAlignment.Center,
                ContentAlignment.TopRight or ContentAlignment.MiddleRight or ContentAlignment.BottomRight => StringAlignment.Far,
                _ => StringAlignment.Near,
            },
            LineAlignment = alignment switch
            {
                ContentAlignment.MiddleLeft or ContentAlignment.MiddleCenter or ContentAlignment.MiddleRight => StringAlignment.Center,
                ContentAlignment.BottomLeft or ContentAlignment.BottomCenter or ContentAlignment.BottomRight => StringAlignment.Far,
                _ => StringAlignment.Near,
            },
            Trimming = ellipsis ? StringTrimming.EllipsisCharacter : StringTrimming.None,
        };
        if (rightToLeft) format.FormatFlags |= StringFormatFlags.DirectionRightToLeft;

        // A color-keyed pixel becomes transparent only when all RGB channels equal the key.
        // Blending glyph edges against that key would leave opaque magenta fringes.
        graphics.TextRenderingHint = keyed ? TextRenderingHint.SingleBitPerPixelGridFit : TextRenderingHint.AntiAliasGridFit;
        using var ink = new SolidBrush(VisibleColor(color));
        graphics.DrawString(text, font, ink, textBounds, format);
    }
}
