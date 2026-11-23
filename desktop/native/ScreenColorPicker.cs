using System.Runtime.InteropServices;

namespace Xdc.NativeDesktop;

internal sealed partial class ScreenColorPicker : Form
{
    private Point pickedPoint;

    public static Color? Pick(IWin32Window owner)
    {
        Point position;
        using (var overlay = new ScreenColorPicker())
        {
            if (overlay.ShowDialog(owner) != DialogResult.OK) return null;
            position = overlay.pickedPoint;
        }

        // Wait for the input overlay to leave the composition before sampling the point behind it.
        _ = DwmFlush();
        if (ReadPixel(position) is { } color) return color;

        MessageBox.Show(owner, "这个位置的颜色无法读取。", "屏幕取色",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
        return null;
    }

    private ScreenColorPicker()
    {
        Text = AccessibleName = "屏幕取色";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        ShowInTaskbar = false;
        TopMost = true;
        Cursor = Cursors.Cross;
        BackColor = Color.Black;
        // Nonzero alpha keeps the overlay clickable without dimming the screen appreciably.
        Opacity = 1d / 255d;
        Bounds = SystemInformation.VirtualScreen;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Right)
        {
            DialogResult = DialogResult.Cancel;
        }
        else if (e.Button == MouseButtons.Left)
        {
            pickedPoint = PointToScreen(e.Location);
            DialogResult = DialogResult.OK;
        }
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keys)
    {
        if (keys != Keys.Escape) return base.ProcessCmdKey(ref message, keys);
        DialogResult = DialogResult.Cancel;
        return true;
    }

    protected override void OnDeactivate(EventArgs e)
    {
        if (DialogResult == DialogResult.None) DialogResult = DialogResult.Cancel;
        base.OnDeactivate(e);
    }

    private static Color? ReadPixel(Point position)
    {
        nint screen = GetDC(0);
        if (screen == 0) return null;
        try
        {
            uint rgb = GetPixel(screen, position.X, position.Y);
            return rgb == uint.MaxValue ? null :
                Color.FromArgb((int)(rgb & 255), (int)((rgb >> 8) & 255), (int)((rgb >> 16) & 255));
        }
        finally { _ = ReleaseDC(0, screen); }
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint window);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint window, nint deviceContext);

    [LibraryImport("gdi32.dll")]
    private static partial uint GetPixel(nint deviceContext, int x, int y);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmFlush();
}
