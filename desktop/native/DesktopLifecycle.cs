using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Timer = System.Windows.Forms.Timer;

namespace Xdc.NativeDesktop;

internal readonly record struct LayoutShortcut(string Name, uint Modifiers, uint Key)
{
    public static LayoutShortcut? Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var parts = input.Where(c => !char.IsWhiteSpace(c)).Select(char.ToLowerInvariant).ToArray();
        string[] tokens = new string(parts).Split('+');
        if (tokens.Length < 2) return null;

        uint flags = 0;
        for (int index = 0; index < tokens.Length - 1; index++)
        {
            uint bit = tokens[index] switch { "ctrl" => 2u, "alt" => 1u, "shift" => 4u, _ => 0 };
            if (bit == 0 || (flags & bit) != 0) return null;
            flags |= bit;
        }

        string key = tokens[^1];
        uint virtualKey;
        if (key.Length == 1 && (char.IsAsciiLetter(key[0]) || char.IsAsciiDigit(key[0])))
            virtualKey = char.ToUpperInvariant(key[0]);
        else if (key.Length is 2 or 3 && key[0] == 'f' &&
            uint.TryParse(key.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out uint function) &&
            function is >= 1 and <= 12 && (key[1] != '0'))
            virtualKey = 0x70 + function - 1;
        else
            return null;

        string name = ((flags & 2) != 0 ? "Ctrl+" : "") + ((flags & 1) != 0 ? "Alt+" : "") +
            ((flags & 4) != 0 ? "Shift+" : "") + key.ToUpperInvariant();
        return new(name, flags | 0x4000, virtualKey);
    }
}

internal sealed class DesktopLifecycle : NativeWindow, IDisposable
{
    private const uint PreviousWindow = 3;
    private const uint PlacementFlags = 0x0001 | 0x0002 | 0x0010 | 0x0200;
    private const int WindowTopmost = 0x00000008;

    private readonly Form window;
    private readonly bool managed;
    private readonly Timer? timer;
    private readonly int sessionId;
    private nint backdrop;
    private int hotkeyId;
    private LayoutShortcut? registeredShortcut;
    private LayoutShortcut? requestedShortcut;
    private bool layoutQueued;
    private bool started;
    private bool disposed;
    private bool hasShownWindow;

    public event Action<bool>? UuChanged;
    public event Action? LayoutToggleRequested;
    public event Action? LayoutEnvironmentChanged;
    public bool UuActive { get; private set; }

    public DesktopLifecycle(Form window, bool managed)
    {
        this.window = window;
        this.managed = managed;
        window.HandleCreated += Attach;
        window.HandleDestroyed += Detach;
        if (window.IsHandleCreated) Attach(window, EventArgs.Empty);
        if (!managed) return;

        using var process = Process.GetCurrentProcess();
        sessionId = process.SessionId;
        timer = new Timer { Interval = 1500 };
        timer.Tick += Tick;
        window.Deactivate += OnWindowDeactivated;
    }

    public void Start()
    {
        if (!managed || started || disposed) return;
        started = true;
        Tick(this, EventArgs.Empty);
        timer!.Start();
    }

    public bool ApplyShortcut(string shortcut)
    {
        var parsed = LayoutShortcut.Parse(shortcut);
        if (parsed is null) return false;
        if (!managed) return true;
        if (Handle != 0 && !Register(parsed.Value)) return false;
        requestedShortcut = parsed;
        return true;
    }

    private bool Register(LayoutShortcut shortcut)
    {
        if (registeredShortcut == shortcut) return true;
        int nextId = hotkeyId == 1 ? 2 : 1;
        if (!RegisterHotKey(Handle, nextId, shortcut.Modifiers, shortcut.Key)) return false;
        if (hotkeyId != 0) _ = UnregisterHotKey(Handle, hotkeyId);
        hotkeyId = nextId;
        registeredShortcut = shortcut;
        return true;
    }

    public void SetBackdrop(nint handle)
    {
        backdrop = handle;
        PlaceOnDesktop();
    }

    public void PlaceOnDesktop()
    {
        nint front = Handle;
        if (!managed || !started || UuActive || disposed || front == 0 || IsCloaked(front)) return;
        bool visible = !IsIconic(front) && IsWindowVisible(front);
        if (visible) hasShownWindow = true;
        nint foreground = GetForegroundWindow();
        if (foreground == front || (foreground != 0 && GetAncestor(foreground, 3) == front)) return;

        nint shell = GetShellWindow();
        nint surface = FindDesktopSurface(shell);
        if (surface == 0 || IsTopmost(surface)) return;

        if (!visible)
        {
            if (hasShownWindow && foreground != 0 &&
                (foreground == shell || foreground == surface || foreground == GetDesktopWindow()))
                _ = ShowWindowAsync(front, 4); // SW_SHOWNOACTIVATE
            return;
        }

        nint back = backdrop;
        if (back != 0 && (GetWindowThreadProcessId(back, out uint pid) == 0 ||
            pid != (uint)Environment.ProcessId || !IsWindowVisible(back) || IsCloaked(back)))
            back = 0;

        nint above = GetWindow(surface, PreviousWindow);
        if (back == 0 && above == front) return;
        if (back != 0 && above == back && GetWindow(back, PreviousWindow) == front) return;
        // Ignore our pair when selecting the predecessor of the Explorer band.
        if (above == front || (back != 0 && above == back)) above = GetWindow(above, PreviousWindow);
        if (above == front || (back != 0 && above == back)) above = GetWindow(above, PreviousWindow);
        if (above == 0 || IsTopmost(above)) above = IsTopmost(front) ? new nint(-2) : 0;
        if (SetWindowPos(front, above, 0, 0, 0, 0, PlacementFlags) && back != 0)
            _ = SetWindowPos(back, front, 0, 0, 0, 0, PlacementFlags);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0312 && message.WParam == hotkeyId && hotkeyId != 0)
        {
            if (!UuActive && window.Visible) LayoutToggleRequested?.Invoke();
            return;
        }

        if (message.Msg is 0x007E or 0x001A // WM_DISPLAYCHANGE / WM_SETTINGCHANGE
            || (message.Msg == 0x0218 && message.WParam is 7 or 18) // resume
            || (message.Msg == 0x02B1 && message.WParam == 8)) // session unlock
            QueueLayoutUpdate();
        base.WndProc(ref message);
    }

    private void QueueLayoutUpdate()
    {
        DesktopDisplays.Invalidate();
        if (disposed || layoutQueued || !window.IsHandleCreated) return;
        layoutQueued = true;
        window.BeginInvoke(() =>
        {
            layoutQueued = false;
            if (disposed) return;
            LayoutEnvironmentChanged?.Invoke();
            PlaceOnDesktop();
        });
    }

    private void Attach(object? sender, EventArgs args)
    {
        if (disposed || Handle != 0) return;
        AssignHandle(window.Handle);
        if (managed)
        {
            _ = WTSRegisterSessionNotification(Handle, 0);
            if (requestedShortcut is LayoutShortcut shortcut) _ = Register(shortcut);
        }
    }

    private void Detach(object? sender, EventArgs args)
    {
        if (Handle != 0)
        {
            if (managed)
            {
                if (hotkeyId != 0) _ = UnregisterHotKey(Handle, hotkeyId);
                _ = WTSUnRegisterSessionNotification(Handle);
            }
            ReleaseHandle();
        }
        hotkeyId = 0;
        registeredShortcut = null;
        layoutQueued = false;
    }

    private void Tick(object? sender, EventArgs args)
    {
        var processes = Process.GetProcessesByName("uu");
        bool active = false;
        foreach (Process process in processes)
        {
            using (process)
            {
                try { if (process.SessionId == sessionId) active = true; }
                catch (Exception failure) when (failure is InvalidOperationException or Win32Exception) { }
            }
        }
        if (active != UuActive)
        {
            UuActive = active;
            UuChanged?.Invoke(active);
        }
        PlaceOnDesktop();
    }

    private void OnWindowDeactivated(object? sender, EventArgs args) => PlaceOnDesktop();

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        timer?.Dispose();
        window.HandleCreated -= Attach;
        window.HandleDestroyed -= Detach;
        window.Deactivate -= OnWindowDeactivated;
        Detach(this, EventArgs.Empty);
    }

    private static nint FindDesktopSurface(nint shell)
    {
        if (shell == 0 || GetWindowThreadProcessId(shell, out uint shellProcess) == 0) return 0;
        if (ContainsDesktopIcons(shell)) return shell;
        nint candidate = 0;
        while ((candidate = FindWindowEx(0, candidate, "WorkerW", null)) != 0)
            if (GetWindowThreadProcessId(candidate, out uint pid) != 0 && pid == shellProcess &&
                ContainsDesktopIcons(candidate))
                return candidate;
        return IsWindowVisible(shell) && !IsCloaked(shell) ? shell : 0;
    }

    private static bool ContainsDesktopIcons(nint window) =>
        IsWindowVisible(window) && !IsCloaked(window) && FindWindowEx(window, 0, "SHELLDLL_DefView", null) != 0;

    private static bool IsCloaked(nint window) =>
        DwmGetWindowAttribute(window, 14, out int flags, sizeof(int)) == 0 && flags != 0;

    private static bool IsTopmost(nint window) => (GetWindowLong(window, -20) & WindowTopmost) != 0;

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool UnregisterHotKey(nint window, int id);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetShellWindow();
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetDesktopWindow();
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetAncestor(nint window, uint flag);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowEx(nint parent, nint after, string className, string? title);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool IsIconic(nint window);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool ShowWindowAsync(nint window, int command);
    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmGetWindowAttribute(nint window, uint attribute, out int value, int valueSize);
    [DllImport("wtsapi32.dll", ExactSpelling = true)]
    private static extern bool WTSRegisterSessionNotification(nint window, uint flags);
    [DllImport("wtsapi32.dll", ExactSpelling = true)]
    private static extern bool WTSUnRegisterSessionNotification(nint window);
}
