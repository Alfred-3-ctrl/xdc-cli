using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Xdc.NativeDesktop;

internal sealed partial class LearningWindow
{
    private GlassTextLayer? contentTextLayer;
    private Size renderedTextSize;
    private bool contentTextQueued;
    private bool contentTextPlacementQueued;

    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        if (message.Msg != 0x0047 || contentTextLayer is null || contentTextPlacementQueued ||
            closing || !IsHandleCreated) return; // WM_WINDOWPOSCHANGED

        // Desktop pinning can reorder the front window without moving or repainting it.
        contentTextPlacementQueued = true;
        BeginInvoke(() =>
        {
            contentTextPlacementQueued = false;
            AlignContentText();
        });
    }

    private void StartContentText()
    {
        try
        {
            contentTextLayer = new GlassTextLayer(Handle);
            QueueContentText();
        }
        catch (Exception error) when (error is ExternalException or ArgumentException or OutOfMemoryException)
        {
            UseKeyedText(error);
        }
    }

    private void ReleaseContentText()
    {
        contentTextLayer?.Dispose();
        contentTextLayer = null;
        renderedTextSize = Size.Empty;
    }

    private void UseKeyedText(Exception error)
    {
        Trace.TraceWarning("Cannot present the antialiased content layer: {0}", error.Message);
        ReleaseContentText();
        body.Invalidate(invalidateChildren: true);
    }

    private Rectangle ContentTextBounds => Visible && content.Visible && !quiet &&
        WindowState != FormWindowState.Minimized ? content.RectangleToScreen(content.ViewportBounds) : Rectangle.Empty;

    private void AlignContentText()
    {
        if (contentTextLayer is not { } layer) return;
        var area = ContentTextBounds;
        if (area.IsEmpty)
        {
            layer.Hide();
            renderedTextSize = Size.Empty;
            return;
        }
        if (area.Size != renderedTextSize)
        {
            layer.Hide();
            QueueContentText();
            return;
        }

        try { layer.Position(area); }
        catch (Exception error) when (error is ExternalException or ArgumentException or OutOfMemoryException)
        {
            UseKeyedText(error);
        }
    }

    internal bool DeferContentText(Control control)
    {
        if (contentTextLayer is null || !body.Contains(control)) return false;
        QueueContentText();
        return true;
    }

    private void QueueContentText()
    {
        if (contentTextLayer is null || contentTextQueued || !IsHandleCreated || closing) return;
        contentTextQueued = true;
        BeginInvoke(RenderContentText);
    }

    private void RenderContentText()
    {
        contentTextQueued = false;
        if (closing || contentTextLayer is not { } layer) return;
        var area = ContentTextBounds;
        if (area.IsEmpty) return;

        try
        {
            layer.Draw(area, graphics =>
            {
                if (status.Visible)
                {
                    var notice = status.RectangleToScreen(status.ClientRectangle);
                    notice.Offset(-area.X, -area.Y);
                    graphics.ExcludeClip(notice);
                }
                PaintContentText(body, graphics, Point.Empty, new Rectangle(Point.Empty, area.Size));
            });
            renderedTextSize = area.Size;
        }
        catch (Exception error) when (error is ExternalException or ArgumentException or OutOfMemoryException)
        {
            UseKeyedText(error);
        }
    }

    private static void PaintContentText(Control parent, Graphics graphics, Point origin, Rectangle clip)
    {
        for (int i = parent.Controls.Count - 1; i >= 0; i--)
        {
            Control control = parent.Controls[i];
            if (!control.Visible) continue;
            Rectangle bounds = control.ClientRectangle;
            bounds.Offset(origin.X + control.Left, origin.Y + control.Top);
            Rectangle childClip = Rectangle.Intersect(clip, bounds);
            if (childClip.Width <= 0 || childClip.Height <= 0) continue;

            switch (control)
            {
                case GlassLabel label when GlassControlPainting.IsColorKey(label.BackColor):
                {
                    var state = graphics.Save();
                    graphics.SetClip(childClip, CombineMode.Intersect);
                    label.PaintText(graphics, bounds, keyed: false);
                    graphics.Restore(state);
                    break;
                }
                case GlassButton button when button.UseGlassBackground:
                {
                    var state = graphics.Save();
                    graphics.SetClip(childClip, CombineMode.Intersect);
                    button.PaintGlassHighlight(graphics, bounds);
                    button.PaintText(graphics, bounds, keyed: false);
                    graphics.Restore(state);
                    break;
                }
                default:
                    if (control.HasChildren) PaintContentText(control, graphics, bounds.Location, childClip);
                    break;
            }
        }
    }
}
