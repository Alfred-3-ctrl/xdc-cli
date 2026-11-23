using System.Runtime.InteropServices;

namespace Xdc.NativeDesktop;

// Store the same screen DIP rectangle as Electron. WinForms reads physical pixels
// once its PerMonitorV2 DPI awareness has been selected.
internal readonly record struct DesktopGeometry(int X, int Y, int Width, int Height)
{
    public static DesktopGeometry Default
    {
        get
        {
            Rectangle area = DesktopDisplays.Current[0].DipWorkArea;
            return new(area.Right - 484, area.Top + 28, 460, 700);
        }
    }

    public DesktopGeometry Normalize()
    {
        var geometry = LimitSize(this);
        var display = DesktopDisplays.ForDip(geometry.Rectangle);
        return Fit(geometry, display.DipWorkArea);
    }

    public Rectangle ToPixels()
    {
        var geometry = LimitSize(this);
        var display = DesktopDisplays.ForDip(geometry.Rectangle);
        var fitted = Fit(geometry, display.DipWorkArea);
        Rectangle area = display.PixelWorkArea;
        int width = Math.Min(area.Width, Round(fitted.Width * display.Scale));
        int height = Math.Min(area.Height, Round(fitted.Height * display.Scale));
        return new Rectangle(
            Math.Clamp(display.Pixels.X + Round((fitted.X - display.DipBounds.X) * display.Scale),
                area.Left, area.Right - width),
            Math.Clamp(display.Pixels.Y + Round((fitted.Y - display.DipBounds.Y) * display.Scale),
                area.Top, area.Bottom - height),
            width, height);
    }

    public static DesktopGeometry FromPixels(Rectangle pixels)
    {
        var display = DesktopDisplays.ForPixels(pixels);
        return Fit(LimitSize(new DesktopGeometry(
            display.DipBounds.X + Round((pixels.X - display.Pixels.X) / display.Scale),
            display.DipBounds.Y + Round((pixels.Y - display.Pixels.Y) / display.Scale),
            Round(pixels.Width / display.Scale), Round(pixels.Height / display.Scale))), display.DipWorkArea);
    }

    private Rectangle Rectangle => new(X, Y, Width, Height);

    private static DesktopGeometry LimitSize(DesktopGeometry geometry) => new(
        Math.Clamp(geometry.X, -100000, 100000), Math.Clamp(geometry.Y, -100000, 100000),
        Math.Clamp(geometry.Width, 320, 820), Math.Clamp(geometry.Height, 520, 1100));

    private static DesktopGeometry Fit(DesktopGeometry geometry, Rectangle area)
    {
        int width = Math.Min(geometry.Width, area.Width);
        int height = Math.Min(geometry.Height, area.Height);
        return new(Math.Clamp(geometry.X, area.Left, area.Right - width),
            Math.Clamp(geometry.Y, area.Top, area.Bottom - height), width, height);
    }

    private static int Round(double value) => (int)Math.Floor(value + .5);
}

internal static class DesktopDisplays
{
    private static DesktopDisplay[]? current;

    public static DesktopDisplay[] Current => current ??= Read();
    public static void Invalidate() => current = null;
    public static DesktopDisplay ForDip(Rectangle value) => Match(value, display => display.DipBounds);
    public static DesktopDisplay ForPixels(Rectangle value) => Match(value, display => display.Pixels);

    private static DesktopDisplay Match(Rectangle value, Func<DesktopDisplay, Rectangle> bounds)
    {
        return Current.OrderBy(display =>
        {
            Rectangle other = bounds(display);
            Rectangle overlap = Rectangle.Intersect(value, other);
            if (overlap.Width > 0 && overlap.Height > 0)
                return -(long)overlap.Width * overlap.Height;
            int x = Math.Max(0, Math.Max(other.Left - value.Right, value.Left - other.Right));
            int y = Math.Max(0, Math.Max(other.Top - value.Bottom, value.Top - other.Bottom));
            return (long)x * x + (long)y * y;
        }).First();
    }

    private static DesktopDisplay[] Read()
    {
        var screens = Screen.AllScreens.OrderByDescending(screen => screen.Primary).ToArray();
        if (screens.Length == 0)
        {
            var defaultBounds = new Rectangle(0, 0, 1920, 1080);
            return [new(defaultBounds, defaultBounds, 1, Point.Empty)];
        }

        var pending = screens.Select(ScreenGeometry).ToList();
        var placed = new List<DesktopDisplay> { pending[0] };
        pending.RemoveAt(0);
        // Chromium lays out touching displays relative to their parent, starting
        // at the primary display. Use the same coordinate system for old files.
        var parents = new Stack<DesktopDisplay>();
        parents.Push(placed[0]);
        while (parents.TryPop(out DesktopDisplay parent))
        {
            for (int index = pending.Count - 1; index >= 0; index--)
            {
                DesktopDisplay child = pending[index];
                if (!TryAttach(parent, child, out var placedChild)) continue;
                placed.Add(placedChild);
                parents.Push(placedChild);
                pending.RemoveAt(index);
            }
        }
        placed.AddRange(pending);
        return [.. placed];
    }

    private static DesktopDisplay ScreenGeometry(Screen screen)
    {
        Rectangle bounds = screen.Bounds;
        var center = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        nint monitor = MonitorFromPoint(center, 2);
        double scale = GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 1;
        return new(bounds, screen.WorkingArea, scale,
            new Point((int)Math.Floor(bounds.X / scale), (int)Math.Floor(bounds.Y / scale)));
    }

    private static bool TryAttach(DesktopDisplay parent, DesktopDisplay child, out DesktopDisplay placed)
    {
        Rectangle a = parent.Pixels;
        Rectangle b = child.Pixels;
        Rectangle dip = parent.DipBounds;
        var x = dip.X + AxisOffset(a.Left, a.Right, b.Left, b.Right, parent.Scale, child.Scale,
            dip.Width, child.DipBounds.Width);
        var y = dip.Y + AxisOffset(a.Top, a.Bottom, b.Top, b.Bottom, parent.Scale, child.Scale,
            dip.Height, child.DipBounds.Height);

        if (a.Bottom == b.Top && a.Left <= b.Right && b.Left <= a.Right)
            placed = child with { DipOrigin = new Point(x, dip.Bottom) };
        else if (a.Top == b.Bottom && a.Left <= b.Right && b.Left <= a.Right)
            placed = child with { DipOrigin = new Point(x, dip.Top - child.DipBounds.Height) };
        else if (a.Right == b.Left && a.Top <= b.Bottom && b.Top <= a.Bottom)
            placed = child with { DipOrigin = new Point(dip.Right, y) };
        else if (a.Left == b.Right && a.Top <= b.Bottom && b.Top <= a.Bottom)
            placed = child with { DipOrigin = new Point(dip.Left - child.DipBounds.Width, y) };
        else
        {
            placed = default;
            return false;
        }
        return true;
    }

    private static int AxisOffset(int start, int end, int childStart, int childEnd,
        double scale, double childScale, int length, int childLength)
    {
        if (start != childStart && childEnd == end) return length - childLength;
        if (childStart >= start) return (int)Math.Floor((childStart - start) / scale);
        if (childEnd <= end)
            return length - childLength - (int)Math.Floor((end - childEnd) / scale);
        return (int)Math.Floor((childStart - start) / childScale);
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint MonitorFromPoint(Point point, uint flags);

    [DllImport("shcore.dll", ExactSpelling = true)]
    private static extern int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);
}

internal readonly record struct DesktopDisplay(Rectangle Pixels, Rectangle PixelWorkArea, double Scale, Point DipOrigin)
{
    public Rectangle DipBounds => new(DipOrigin, new Size(
        CoveringLength(Pixels.Left, Pixels.Right), CoveringLength(Pixels.Top, Pixels.Bottom)));

    public Rectangle DipWorkArea => Rectangle.FromLTRB(
        DipOrigin.X + Floor(PixelWorkArea.Left - Pixels.Left),
        DipOrigin.Y + Floor(PixelWorkArea.Top - Pixels.Top),
        DipOrigin.X + Ceiling(PixelWorkArea.Right - Pixels.Left),
        DipOrigin.Y + Ceiling(PixelWorkArea.Bottom - Pixels.Top));

    private int Floor(int value) => (int)Math.Floor(value / Scale);
    private int Ceiling(int value) => (int)Math.Ceiling(value / Scale);
    private int CoveringLength(int lower, int upper) => Ceiling(upper) - Floor(lower);
}
