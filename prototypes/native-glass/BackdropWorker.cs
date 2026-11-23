using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Windows.UI.Composition;

namespace Xdc.NativeGlass;

internal static partial class BackdropWorker
{
    private const int DwmCloaked = 14;
    private static readonly Guid VirtualDesktopManagerClass = new("AA509086-5CA9-4C25-8F95-589D3C07B48A");

    private sealed class Input
    {
        public volatile bool Closed;
        public volatile float Radius = 30f;

        public async Task ReadAsync()
        {
            try
            {
                using var reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8, false);
                while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    try
                    {
                        using var payload = JsonDocument.Parse(line);
                        if (payload.RootElement.ValueKind == JsonValueKind.Object &&
                            payload.RootElement.TryGetProperty("radius", out var value) &&
                            value.ValueKind == JsonValueKind.Number &&
                            value.TryGetSingle(out float requested) && float.IsFinite(requested))
                            Radius = Math.Clamp(requested, 16f, 36f);
                    }
                    catch (JsonException) { }
                }
            }
            catch (IOException) { }
            finally
            {
                Closed = true;
            }
        }
    }

    public static int Run(string[] args)
    {
        using var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
        {
            AutoFlush = true,
            NewLine = "\n",
        };
        if (args.Length != 3 ||
            !nint.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out nint front) || front <= 0 ||
            !int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out int parentPid) || parentPid <= 0)
            return Error(stdout, "invalid-arguments");
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
            return Error(stdout, "unsupported-windows");
        if (!Matches(front, parentPid)) return Error(stdout, "invalid-target");

        try
        {
            using var parent = Process.GetProcessById(parentPid);
            using var lifetime = new ApplicationContext();
            using var glass = new GlassWindow(0, passive: true);
            using var timer = new System.Windows.Forms.Timer { Interval = 40 };
            using var desktop = new DesktopAlignment();
            var input = new Input();
            _ = Task.Run(input.ReadAsync);

            int exitCode = 0;
            bool ready = false;
            float radius = 30f;
            var startup = Stopwatch.StartNew();
            timer.Tick += (_, _) =>
            {
                try
                {
                    if (input.Closed || parent.HasExited || !Matches(front, parentPid))
                    {
                        lifetime.ExitThread();
                        return;
                    }

                    float requestedRadius = input.Radius;
                    if (requestedRadius != radius) glass.SetRadius(radius = requestedRadius);
                    Rectangle bounds = ClientBounds(front);
                    if (!bounds.IsEmpty && !desktop.Align(front, glass.Handle))
                        bounds = Rectangle.Empty;
                    glass.PlaceBehind(bounds, front);

                    if (!ready)
                    {
                        switch (glass.EffectLoadStatus)
                        {
                            case CompositionEffectFactoryLoadStatus.Success:
                                stdout.WriteLine("backdrop " + glass.Handle.ToInt64().ToString(CultureInfo.InvariantCulture));
                                stdout.WriteLine("ready");
                                ready = true;
                                break;
                            case CompositionEffectFactoryLoadStatus.Pending when startup.Elapsed < TimeSpan.FromSeconds(5):
                                break;
                            default:
                                exitCode = Error(stdout, $"effect-{glass.EffectLoadStatus} 0x{glass.EffectError:X8}");
                                lifetime.ExitThread();
                                break;
                        }
                    }
                }
                catch (Exception error)
                {
                    exitCode = Error(stdout, $"worker 0x{error.HResult:X8}");
                    lifetime.ExitThread();
                }
            };
            timer.Start();
            Application.Run(lifetime);
            return exitCode;
        }
        catch (Exception error)
        {
            return Error(stdout, $"initialize 0x{error.HResult:X8}");
        }
    }

    private static int Error(TextWriter output, string detail)
    {
        try { output.WriteLine("error " + detail); } catch (IOException) { }
        return 1;
    }

    private static bool Matches(nint hwnd, int pid) =>
        GetWindowThreadProcessId(hwnd, out uint owner) != 0 && owner == (uint)pid && GetAncestor(hwnd, 2) == hwnd;

    private static bool Cloaked(nint hwnd) => DwmGetWindowAttribute(hwnd, DwmCloaked, out int state, sizeof(int)) < 0 || state != 0;

    private static Rectangle ClientBounds(nint hwnd)
    {
        if (!IsWindowVisible(hwnd) || IsIconic(hwnd) || Cloaked(hwnd)) return Rectangle.Empty;
        // The PerMonitorV2 worker reads the foreign client rectangle in screen coordinates.
        // This avoids deriving monitor scaling from dimensions exposed by Electron.
        var info = new WindowInfo { Size = (uint)Marshal.SizeOf<WindowInfo>() };
        if (!GetWindowInfo(hwnd, ref info)) return Rectangle.Empty;
        var rect = info.Client;
        return rect.Right > rect.Left && rect.Bottom > rect.Top
            ? Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom)
            : Rectangle.Empty;
    }

    private sealed class DesktopAlignment : IDisposable
    {
        private readonly IVirtualDesktopManager? manager;
        private long retryAt;

        public DesktopAlignment()
        {
            try
            {
                var type = Type.GetTypeFromCLSID(VirtualDesktopManagerClass);
                if (type != null) manager = Activator.CreateInstance(type) as IVirtualDesktopManager;
            }
            catch (COMException) { }
        }

        public bool Align(nint front, nint backdrop)
        {
            if (!Cloaked(backdrop)) return true;
            // Current-desktop switching is driven by the user. Only move our own window,
            // and only when the front window is visible on the destination desktop.
            if (manager == null || Environment.TickCount64 < retryAt) return false;
            retryAt = Environment.TickCount64 + 1000;
            try
            {
                if (manager.GetWindowDesktopId(front, out Guid target) >= 0 &&
                    manager.GetWindowDesktopId(backdrop, out Guid current) >= 0 && target != current)
                    manager.MoveWindowToDesktop(backdrop, in target);
            }
            catch (COMException) { }
            return !Cloaked(backdrop);
        }

        public void Dispose()
        {
            if (manager != null) Marshal.ReleaseComObject(manager);
        }
    }

    [ComImport, Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopManager
    {
        [PreserveSig]
        int IsWindowOnCurrentVirtualDesktop(nint hwnd, [MarshalAs(UnmanagedType.Bool)] out bool current);
        [PreserveSig]
        int GetWindowDesktopId(nint hwnd, out Guid desktopId);
        [PreserveSig]
        int MoveWindowToDesktop(nint hwnd, in Guid desktopId);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowInfo
    {
        public uint Size;
        public NativeRect Window;
        public NativeRect Client;
        public uint Style, ExStyle, Status, BorderX, BorderY;
        public ushort WindowType, CreatorVersion;
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [LibraryImport("user32.dll")]
    private static partial nint GetAncestor(nint hwnd, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowInfo(nint hwnd, ref WindowInfo info);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, uint size);
}
