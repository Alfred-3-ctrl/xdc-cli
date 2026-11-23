using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Xdc.NativeDesktop;

internal sealed partial class ContentScrollPanel : Panel
{
    private const int VerticalScrollbarWidthMetric = 2;
    private readonly ScrollThumb thumb;

    internal ContentScrollPanel(Color canvas)
    {
        BackColor = canvas;
        Body = new FlowLayoutPanel
        {
            AutoScroll = true,
            BackColor = canvas,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
        };
        thumb = new ScrollThumb(Body) { BackColor = canvas };
        Controls.Add(Body);
        Controls.Add(thumb);
        thumb.BringToFront();

        Body.Layout += (_, _) => thumb.Synchronize();
        Body.Scroll += (_, _) => thumb.Synchronize();
        Body.MouseWheel += (_, _) => thumb.Synchronize();
        // WinForms also moves the viewport to reveal focused controls and restore saved positions.
        Body.Paint += (_, _) => thumb.Synchronize();
        LayoutViewport();
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal FlowLayoutPanel Body { get; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Rectangle ViewportBounds => new(0, 0, Math.Max(0, ClientSize.Width - TrackWidth), ClientSize.Height);

    internal Control? ScrollbarAt(Point client) => thumb.Visible && thumb.Bounds.Contains(client) ? thumb : null;

    private int TrackWidth => Math.Max(1, (int)Math.Round(12 * DeviceDpi / 96.0));

    internal void SetThumbColors(Color normal, Color focused)
    {
        thumb.ForeColor = normal;
        thumb.SetFocusColor(focused);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        LayoutViewport();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        LayoutViewport();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        LayoutViewport();
    }

    private void LayoutViewport()
    {
        if (thumb is null) return;
        // Clip the native scrollbar outside this panel while WinForms retains its scrolling behavior.
        int nativeWidth = GetSystemMetricsForDpi(VerticalScrollbarWidthMetric, (uint)DeviceDpi);
        Body.SetBounds(0, 0, ClientSize.Width + nativeWidth, ClientSize.Height);
        thumb.SetBounds(ViewportBounds.Right, 0, TrackWidth, ClientSize.Height);
        thumb.Synchronize();
    }

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetricsForDpi(int index, uint dpi);

    private sealed class ScrollThumb : Control
    {
        private readonly FlowLayoutPanel body;
        private Rectangle position;
        private Color focusColor;
        private int? dragOffset;

        public ScrollThumb(FlowLayoutPanel body)
        {
            this.body = body;
            AccessibleName = "内容滚动条";
            AccessibleRole = AccessibleRole.ScrollBar;
            TabStop = true;
            Visible = false;
            SetStyle(ControlStyles.Selectable | ControlStyles.UserPaint |
                ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        private int Maximum => Math.Max(0, body.DisplayRectangle.Height - body.ClientSize.Height);
        private int Offset => -body.AutoScrollPosition.Y;
        private int ThumbWidth => Math.Max(1, (int)Math.Round(4 * DeviceDpi / 96.0));

        public void SetFocusColor(Color color)
        {
            if (focusColor == color) return;
            focusColor = color;
            Invalidate();
        }

        public void Synchronize()
        {
            int maximum = Maximum;
            var area = ClientRectangle;
            if (maximum == 0 || area.Width <= 0 || area.Height <= 0)
            {
                if (position.IsEmpty) return;
                position = Rectangle.Empty;
                Visible = false;
                return;
            }

            int minHeight = Math.Min(area.Height, (int)Math.Round(22 * DeviceDpi / 96.0));
            var height = Math.Clamp((int)Math.Round(area.Height * (double)body.ClientSize.Height /
                body.DisplayRectangle.Height), minHeight, area.Height);
            var top = (int)Math.Round((area.Height - height) * (Offset / (double)maximum));
            var next = new Rectangle((area.Width - ThumbWidth) / 2, top, ThumbWidth, height);
            if (position == next) return;
            position = next;
            Visible = true;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (position.IsEmpty) return;
            using var canvas = new SolidBrush(BackColor);
            e.Graphics.FillRectangle(canvas, e.ClipRectangle);
            e.Graphics.SmoothingMode = SmoothingMode.None;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
            using var path = GlassControlPainting.RoundedPath(position, position.Width / 2);
            using var ink = new SolidBrush(Focused && ShowFocusCues ? focusColor : ForeColor);
            e.Graphics.FillPath(ink, path);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || position.IsEmpty) return;
            Focus();
            if (position.Contains(e.Location))
            {
                Capture = true;
                dragOffset = e.Y - position.Top;
            }
            else ScrollTo(Offset + Math.Sign(e.Y - position.Top) * body.ClientSize.Height);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int travel = ClientSize.Height - position.Height;
            if (dragOffset is int start && travel > 0)
                ScrollTo((int)Math.Round(Math.Clamp((e.Y - start) / (double)travel, 0, 1) * Maximum));
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left) Capture = false;
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture) dragOffset = null;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            ScrollTo(Offset - e.Delta);
            if (e is HandledMouseEventArgs handled) handled.Handled = true;
            base.OnMouseWheel(e);
        }

        protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is
            Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            int line = (int)Math.Round(28 * DeviceDpi / 96.0);
            int? next = e.KeyCode switch
            {
                Keys.Up => Offset - line,
                Keys.Down => Offset + line,
                Keys.PageUp => Offset - body.ClientSize.Height,
                Keys.PageDown => Offset + body.ClientSize.Height,
                Keys.Home => 0,
                Keys.End => Maximum,
                _ => null,
            };
            if (next is int target)
            {
                ScrollTo(target);
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            Invalidate();
        }

        private void ScrollTo(int y)
        {
            body.AutoScrollPosition = new Point(0, Math.Clamp(y, 0, Maximum));
            Synchronize();
        }
    }
}
