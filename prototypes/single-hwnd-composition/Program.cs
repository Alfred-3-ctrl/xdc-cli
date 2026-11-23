using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using Windows.UI.Composition;
using Timer = System.Windows.Forms.Timer;

namespace Xdc.SingleWindowGlass;

internal static partial class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string? reportPath = null;
        string? rowPngPath = null;
        bool benchmarkExit = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--report" && i + 1 < args.Length) reportPath = args[++i];
            else if (args[i] == "--dump-row" && i + 1 < args.Length) rowPngPath = args[++i];
            else if (args[i] == "--self-test") benchmarkExit = true;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        uint dpi = GetDpiForSystem();
        double scale = dpi / 96.0;
        var rect = new Rectangle((int)Math.Round(120 * scale), (int)Math.Round(60 * scale),
            (int)Math.Round(460 * scale), (int)Math.Round(700 * scale));

        using var widget = new SingleGlassWidget(rect, 30f);
        if (rowPngPath is not null)
        {
            var bytes = widget.DumpFirstRowPremultipliedBgra(out var size);
            using var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
            var lockData = bmp.LockBits(new Rectangle(Point.Empty, size), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            Marshal.Copy(bytes, 0, lockData.Scan0, bytes.Length);
            bmp.UnlockBits(lockData);
            bmp.Save(rowPngPath, ImageFormat.Png);
        }

        using var timer = new Timer { Interval = 80 };
        int exitCode = 0;
        long deadline = Environment.TickCount64 + 4000;
        timer.Tick += (_, _) =>
        {
            var status = widget.EffectStatus;
            if (status == CompositionEffectFactoryLoadStatus.Pending && Environment.TickCount64 < deadline) return;
            timer.Stop();

            // The self-test dispatches input to this HWND only. It measures CPU dispatch, not presented frames.
            object? selfTest = null;
            if (benchmarkExit)
            {
                // 1. Blank area click on row 0 (x = 75% width, y = first row center)
                int clickX = (int)Math.Round(rect.Width * 0.75);
                int clickY = (int)Math.Round(100 * scale);
                nint packed = (nint)((clickX & 0xFFFF) | ((clickY & 0xFFFF) << 16));
                _ = SendMessage(widget.Handle, 0x0200, 0, packed); // WM_MOUSEMOVE (hover)
                int hovered = widget.HoveredRowIndex;
                _ = SendMessage(widget.Handle, 0x0201, 1, packed); // WM_LBUTTONDOWN
                _ = SendMessage(widget.Handle, 0x0202, 0, packed); // WM_LBUTTONUP
                int clicked = widget.LastClickedRowIndex;

                // Alternate directions so every wheel dispatch changes the visual offset.
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < 60; i++)
                {
                    short delta = i % 2 == 0 ? (short)-120 : (short)120;
                    nint wParam = unchecked((ushort)delta) << 16;
                    _ = SendMessage(widget.Handle, 0x020A, wParam, packed);
                }
                sw.Stop();
                _ = SendMessage(widget.Handle, 0x020A, (nint)(unchecked((ushort)-120) << 16), packed);
                float offsetAfterDown = widget.ScrollOffsetPx;

                // 3. Pure-transparent degradation toggle (disable live blur, keep tint)
                widget.SetBlurEnabled(false);
                bool blurOff = !widget.BlurEnabled;
                widget.SetBlurEnabled(true);
                bool passed = status == CompositionEffectFactoryLoadStatus.Success &&
                    hovered == 0 && clicked == 0 && offsetAfterDown > 0 && blurOff;
                exitCode = passed ? 0 : 1;

                selfTest = new
                {
                    passed,
                    hoveredRowOnBlankArea = hovered,
                    clickedRowOnBlankArea = clicked,
                    wheelDispatch60TotalMs = Math.Round(sw.Elapsed.TotalMilliseconds, 3),
                    wheelDispatchAverageUs = Math.Round(sw.Elapsed.TotalMicroseconds / 60.0, 2),
                    scrollOffsetAfterDownPx = offsetAfterDown,
                    pureTransparentFallbackToggled = blurOff,
                };
            }

            if (reportPath is not null)
            {
                File.WriteAllText(reportPath, JsonSerializer.Serialize(new
                {
                    ready = status == CompositionEffectFactoryLoadStatus.Success,
                    pid = Environment.ProcessId,
                    windowHandle = widget.Handle.ToInt64(),
                    dpi,
                    physicalBounds = new[] { rect.X, rect.Y, rect.Width, rect.Height },
                    status = status.ToString(),
                    selfTest,
                }, new JsonSerializerOptions { WriteIndented = true }));
            }

            if (benchmarkExit) Application.ExitThread();
        };
        timer.Start();
        Application.Run();
        return exitCode;
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForSystem();

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial nint SendMessage(nint hwnd, uint msg, nint wParam, nint lParam);
}
