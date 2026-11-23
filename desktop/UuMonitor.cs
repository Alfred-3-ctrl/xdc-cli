using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

internal static class UuMonitor
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string title);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr window, int show);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, uint attribute, out int value, int size);

    private const uint GW_HWNDPREV = 3;
    private const uint DWMWA_CLOAKED = 14;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int SW_SHOWNOACTIVATE = 4;
    private static readonly IntPtr HWND_TOP = IntPtr.Zero;
    private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOOWNERZORDER = 0x0200;
    private const uint SWP_ASYNCWINDOWPOS = 0x4000;

    private static volatile bool armed;
    private static volatile bool disconnected;
    private static int parentId;
    private static int sessionId;
    private static string monitoredProcess;
    private static long desktopWindow;

    private static void Send(string message)
    {
        try { Console.WriteLine(message); }
        catch (IOException) { disconnected = true; }
    }

    private static bool IsCloaked(IntPtr window)
    {
        int flags;
        return DwmGetWindowAttribute(window, DWMWA_CLOAKED, out flags, sizeof(int)) == 0 && flags != 0;
    }

    private static bool HasDesktopIcons(IntPtr window)
    {
        // Explorer can hide its icon view while keeping the desktop host visible.
        return FindWindowEx(window, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero;
    }

    private static IntPtr FindDesktopSurface(IntPtr shell)
    {
        uint shellProcess;
        if (shell == IntPtr.Zero || GetWindowThreadProcessId(shell, out shellProcess) == 0)
            return IntPtr.Zero;
        if (IsWindowVisible(shell) && HasDesktopIcons(shell) && !IsCloaked(shell)) return shell;

        // Explorer can host the desktop icons in either Progman or a WorkerW.
        // Other WorkerW windows can serve unrelated Explorer features.
        IntPtr worker = IntPtr.Zero;
        for (int i = 0; i < 100; ++i)
        {
            worker = FindWindowEx(IntPtr.Zero, worker, "WorkerW", null);
            if (worker == IntPtr.Zero) break;
            uint process;
            if (GetWindowThreadProcessId(worker, out process) != 0 && process == shellProcess &&
                IsWindowVisible(worker) && HasDesktopIcons(worker) && !IsCloaked(worker))
                return worker;
        }
        return IsWindowVisible(shell) && !IsCloaked(shell) ? shell : IntPtr.Zero;
    }

    private static void PlaceOnDesktop()
    {
        IntPtr window = new IntPtr(Interlocked.Read(ref desktopWindow));
        if (window == IntPtr.Zero) return;
        uint process;
        if (GetWindowThreadProcessId(window, out process) == 0 || process != (uint)parentId) return;

        IntPtr foreground = GetForegroundWindow();
        if (foreground == window || IsCloaked(window)) return;
        IntPtr shell = GetShellWindow();
        IntPtr surface = FindDesktopSurface(shell);
        if (surface == IntPtr.Zero || IsTopmost(surface)) return;

        if (IsIconic(window) || !IsWindowVisible(window))
        {
            // Only recover a registered window when the desktop itself has focus.
            if (foreground != IntPtr.Zero &&
                (foreground == shell || foreground == surface || foreground == GetDesktopWindow()))
                ShowWindowAsync(window, SW_SHOWNOACTIVATE);
            return;
        }

        // Insert immediately above Explorer's desktop surface. HWND_BOTTOM can
        // put the widget behind the surface. Avoid crossing into the topmost band.
        IntPtr above = GetWindow(surface, GW_HWNDPREV);
        if (above == window) return;
        if (above == IntPtr.Zero || IsTopmost(above))
        {
            // Explorer is first among normal windows. HWND_TOP takes a normal
            // window to the top of that same group without making it topmost.
            above = IsTopmost(window) ? HWND_NOTOPMOST : HWND_TOP;
        }
        SetWindowPos(window, above, 0, 0, 0, 0,
            SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_ASYNCWINDOWPOS);
    }

    private static bool IsTopmost(IntPtr window)
    {
        return (GetWindowLong(window, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;
    }

    private static void ReadCommands()
    {
        string command;
        while ((command = Console.ReadLine()) != null)
        {
            if (command.StartsWith("arm ", StringComparison.Ordinal))
            {
                long sequence;
                if (!long.TryParse(command.Substring(4), out sequence)) continue;
                armed = IsRunning(monitoredProcess);
                Send((armed ? "armed " : "arm-aborted ") + sequence);
            }
            else if (command == "disarm")
            {
                armed = false;
            }
            else if (command.StartsWith("desktop ", StringComparison.Ordinal))
            {
                long handle;
                if (long.TryParse(command.Substring(8), out handle))
                {
                    Interlocked.Exchange(ref desktopWindow, handle);
                    PlaceOnDesktop();
                }
            }
        }
        disconnected = true;
    }

    private static bool IsRunning(string processName)
    {
        Process[] processes = Process.GetProcessesByName(processName);
        bool running = false;
        foreach (Process process in processes)
        {
            using (process)
            {
                try { if (process.SessionId == sessionId) running = true; }
                catch (InvalidOperationException) {}
                catch (Win32Exception) {}
            }
        }
        return running;
    }

    private static int Main(string[] args)
    {
        if (args.Length != 4) return 2;
        if (!int.TryParse(args[2], out parentId)) return 2;
        using (Process current = Process.GetCurrentProcess()) sessionId = current.SessionId;
        monitoredProcess = args[3];
        new Thread(ReadCommands) { IsBackground = true }.Start();
        bool? previous = null;
        for (;;)
        {
            if (disconnected && !armed) return 0;
            bool active = IsRunning(args[3]);
            if (!disconnected && previous != active) Send(active ? "uu on" : "uu off");
            if (!disconnected && !active) PlaceOnDesktop();
            if (disconnected && !active)
            {
                try
                {
                    using (Process parent = Process.GetProcessById(parentId))
                    {
                        if (!parent.WaitForExit(3000)) continue;
                    }
                }
                catch (ArgumentException) {}
                // Project path is a trusted app-owned directory and cannot contain quotes.
                Process.Start(new ProcessStartInfo
                {
                    FileName = args[0],
                    Arguments = "\"" + args[1] + "\" --resume-desktop",
                    WorkingDirectory = args[1],
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                return 0;
            }
            previous = active;
            Thread.Sleep(disconnected ? 2200 : 1500);
        }
    }
}
