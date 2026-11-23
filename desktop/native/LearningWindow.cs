using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text.Json;
using Windows.UI.Composition;
using Xdc.NativeGlass;
using Timer = System.Windows.Forms.Timer;

namespace Xdc.NativeDesktop;

internal sealed partial class LearningWindow : Form
{
    private static readonly Color CanvasKey = Color.Magenta;
    private readonly NativeOptions options;
    private readonly BusinessClient service;
    private readonly DesktopLifecycle lifecycle;
    private readonly GlassComposition composition;
    private readonly Panel header = new() { BackColor = CanvasKey };
    private readonly FlowLayoutPanel body = new() { BackColor = CanvasKey, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
    private readonly FlowLayoutPanel dock = new() { BackColor = CanvasKey, WrapContents = false };
    private readonly Label status = new() { AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Visible = false };
    private readonly Timer idleTimer = new() { Interval = 20000 };
    private readonly Timer refreshTimer = new() { Interval = 30 * 60 * 1000 };
    private readonly Timer noticeTimer = new() { Interval = 4200 };
    private readonly Stack<Page> history = new();
    private readonly Dictionary<string, string> route = new();
    private readonly List<Control> wide = [];
    private readonly Dictionary<Control, Font> ownedFonts = [];
    private Page? page;
    private GlassWindow? glass;
    private DesktopPreferences saved;
    private DesktopPreferences prefs;
    private Func<Task>? pageAfterStart;
    private Timer? reportTimer;
    private bool shownOnce, closing, allowClose, quiet, inLayout, readyToPaint;
    private int pageVersion;
    private Point? resizeOrigin;
    private Size resizeSize;
    private Task quietTransition = Task.CompletedTask;

    private sealed record Page(Func<Task> Show, int Scroll = 0);
    public int PageVersion => pageVersion;
    private bool Dark => prefs.Look.Scheme == "dark" ||
        (prefs.Look.Scheme == "system" && !SystemUsesLightTheme());
    internal Color Surface => Dark ? Color.FromArgb(37, 47, 60) : Color.FromArgb(242, 245, 249);
    internal Color Foreground => Dark ? Color.FromArgb(244, 247, 251) : Color.FromArgb(25, 34, 46);
    internal Color Secondary => Dark ? Color.FromArgb(195, 206, 218) : Color.FromArgb(75, 87, 102);
    internal Color Accent => ColorTranslator.FromHtml(prefs.Look.Accent);

    public LearningWindow(NativeOptions options)
    {
        this.options = options;
        saved = prefs = DesktopPreferences.Load(options.Profile);
        service = new BusinessClient(options);
        lifecycle = new DesktopLifecycle(this, options.Managed);
        composition = new GlassComposition();
        Text = "学习桌面";
        StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        MaximizeBox = MinimizeBox = false;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = TransparencyKey = CanvasKey;
        Bounds = SavedPixelBounds();
        Controls.Add(body);
        Controls.Add(header);
        Controls.Add(dock);
        Controls.Add(status);
        status.BringToFront();
        header.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) BeginDrag(); };
        lifecycle.LayoutToggleRequested += ToggleLayout;
        lifecycle.LayoutEnvironmentChanged += RestorePlacement;
        lifecycle.UuChanged += ChangeQuietMode;
        service.Changed += OnBusinessEvent;
        idleTimer.Tick += async (_, _) =>
        {
            if (closing) return;
            try { await service.StopAsync(onlyIfIdle: true); }
            catch (IOException) { }
            catch (InvalidOperationException) { }
            catch (TimeoutException) { }
        };
        refreshTimer.Tick += async (_, _) =>
        {
            if (!closing && !quiet && Visible) await Run(RefreshOverviewFromTimer);
        };
        noticeTimer.Tick += (_, _) => { status.Hide(); noticeTimer.Stop(); };
        pageAfterStart = RestoreOrOpenHome;
        readyToPaint = true;
        LayoutContent();
        UpdateNavigation();
    }

    protected override bool ShowWithoutActivation => true;

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (shownOnce) return;
        shownOnce = true;
        lifecycle.Start();
        if (!lifecycle.ApplyShortcut(prefs.Shortcut)) Notice("布局快捷键已被占用");
        EnsureGlass();
        if (options.Managed && glass is not null)
        {
            try { DesktopStartup.Install(Environment.ProcessPath!, LaunchArguments(), options.Desktop, saved.Startup); }
            catch (Exception error) { Notice("无法更新 Windows 启动入口：" + error.Message); }
        }
        idleTimer.Start();
        refreshTimer.Start();
        var first = pageAfterStart;
        pageAfterStart = null;
        if (options.Report is not null) StartReport();
        if (!quiet && first is not null) await Run(first);
    }

    private async Task RestoreOrOpenHome()
    {
        var view = ReadLastView();
        if (view.ValueKind == JsonValueKind.Object)
        {
            await RestoreBusinessPage(view);
            if (view.TryGetProperty("scroll", out var scroll) && scroll.TryGetInt32(out int offset))
                body.AutoScrollPosition = new Point(0, Math.Max(0, offset));
        }
        else await ChangePage(ShowOverview);
    }

    private void EnsureGlass()
    {
        if (closing || quiet || !Visible || !IsHandleCreated || glass is not null) return;
        glass = new GlassWindow(0, passive: true, shared: composition);
        lifecycle.SetBackdrop(glass.Handle);
        ApplyGlass();
        AlignGlass();
        lifecycle.PlaceOnDesktop();
    }

    private void AlignGlass()
    {
        if (glass is null || !IsHandleCreated) return;
        var bounds = Visible && !quiet && WindowState != FormWindowState.Minimized
            ? RectangleToScreen(ClientRectangle) : Rectangle.Empty;
        glass.PlaceBehind(bounds, bounds.IsEmpty ? 0 : Handle);
    }

    private void ApplyGlass()
    {
        if (glass is null) return;
        var rgb = Dark ? Color.FromArgb(22, 31, 46) : Color.FromArgb(227, 236, 249);
        int alpha = (int)Math.Round((100 - prefs.Look.Transparency) * 2.55);
        glass.SetTint(Color.FromArgb(Math.Clamp(alpha, 0, 255), rgb));
        glass.SetRadius(prefs.Look.Radius);
    }

    private void ReleaseGlass()
    {
        lifecycle.SetBackdrop(0);
        glass?.Dispose();
        glass = null;
    }

    private void ChangeQuietMode(bool active)
    {
        if (closing || quiet == active) return;
        quiet = active;
        if (active)
        {
            SaveLastView();
            ReleaseGlass();
            Hide();
        }
        quietTransition = QuietTransitionAfter(quietTransition, active);
    }

    private async Task QuietTransitionAfter(Task previous, bool active)
    {
        await previous;
        if (closing || quiet != active) return;
        try
        {
            if (service.ProcessId is not null)
            {
                if (active)
                {
                    var info = await CallAsync("service.hasActiveJob");
                    if (info.TryGetProperty("active", out var busy) && busy.ValueKind == JsonValueKind.True)
                        NotifyFinishingDownload();
                    await CallAsync("service.quietPause");
                }
                else await CallAsync("service.resume");
            }
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException) { }
        if (!active && !quiet && !closing)
        {
            Show();
            EnsureGlass();
            if (page is null) await Run(RestoreOrOpenHome);
        }
    }

    private void NotifyFinishingDownload()
    {
        var notice = new NotifyIcon { Visible = true, Icon = SystemIcons.Information, Text = "学习桌面" };
        notice.ShowBalloonTip(4500, "学习桌面", "当前文件仍在处理，剩余队列将在 UU 退出后继续。", ToolTipIcon.Info);
        var timer = new Timer { Interval = 5000 };
        timer.Tick += (_, _) => { timer.Dispose(); notice.Dispose(); };
        timer.Start();
        Disposed += (_, _) => { timer.Dispose(); notice.Dispose(); };
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        if (!readyToPaint) return;
        AlignGlass();
        RefreshLayoutDimensions();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (!readyToPaint) return;
        LayoutContent();
        AlignGlass();
        RefreshLayoutDimensions();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (readyToPaint && shownOnce && !closing)
        {
            lifecycle.PlaceOnDesktop();
            AlignGlass();
        }
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        AlignGlass();
        if (readyToPaint && prefs.Look.Scheme == "system") ApplyGlass();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        AlignGlass();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        if (readyToPaint) BeginInvoke(RestorePlacement);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (allowClose || e.CloseReason is CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing)
        {
            SaveLastView();
            base.OnFormClosing(e);
            return;
        }
        e.Cancel = true;
        BeginInvoke(() => _ = CloseDesktop());
    }

    private async Task CloseDesktop()
    {
        if (closing) return;
        closing = true;
        SaveLastView();
        ReleaseGlass();
        Hide();
        try { await quietTransition; await service.StopAsync(onlyIfIdle: false); }
        catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException) { }
        allowClose = true;
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && readyToPaint)
        {
            readyToPaint = false;
            idleTimer.Dispose();
            refreshTimer.Dispose();
            noticeTimer.Dispose();
            reportTimer?.Dispose();
            ReleaseGlass();
            lifecycle.Dispose();
            service.Dispose();
            composition.Dispose();
        }
        base.Dispose(disposing);
    }

    internal int Dip(double size) => Math.Max(1, (int)Math.Round(size * DeviceDpi / 96.0 * prefs.Look.Scale / 100.0));

    private void LayoutContent()
    {
        if (inLayout) return;
        inLayout = true;
        try
        {
            int margin = Dip(18);
            header.SetBounds(margin, Dip(13), Math.Max(1, ClientSize.Width - 2 * margin), Dip(38));
            int top = header.Bottom + Dip(9);
            const int navigationCount = 3;
            int dockWidth = Math.Min(ClientSize.Width - 2 * margin, Dip(prefs.Look.NavStyle == "icons" ? 48 : 90) * navigationCount);
            int dockX = prefs.Look.NavAlign switch
            {
                "left" => margin,
                "right" => ClientSize.Width - dockWidth - margin,
                _ => (ClientSize.Width - dockWidth) / 2,
            };
            dock.SetBounds(dockX, Math.Max(top, ClientSize.Height - Dip(57)), dockWidth, Dip(44));
            status.SetBounds(margin, Math.Max(top, dock.Top - Dip(32)), ClientSize.Width - 2 * margin, Dip(31));
            body.SetBounds(margin, top, ClientSize.Width - 2 * margin, Math.Max(1, dock.Top - top - Dip(8)));
            foreach (var item in wide) item.Width = ContentWidth;
            foreach (Control item in dock.Controls) item.Width = Math.Max(1, dock.ClientSize.Width / navigationCount);
            if (header.Controls.Count > 0) header.Controls[^1].Left = header.ClientSize.Width - header.Controls[^1].Width;
        }
        finally { inLayout = false; }
    }

    private int ContentWidth => Math.Max(Dip(40), body.ClientSize.Width - SystemInformation.VerticalScrollBarWidth);

    private void UpdateNavigation()
    {
        ClearControls(header);
        ClearControls(dock);
        var back = ActionButton("‹", () => _ = GoBack());
        back.AccessibleName = "返回";
        back.Visible = history.Count != 0;
        back.Location = Point.Empty;
        back.Width = Dip(39);
        header.Controls.Add(back);
        var settings = ActionButton("⚙", () => _ = OpenSettings());
        settings.AccessibleName = "设置";
        settings.Width = Dip(38);
        var refresh = ActionButton("↻", () => _ = Run(RefreshPage));
        refresh.AccessibleName = "刷新";
        refresh.Width = Dip(36);
        refresh.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        refresh.Left = Math.Max(0, header.ClientSize.Width - Dip(80));
        refresh.Visible = route.TryGetValue("name", out var screen) && screen is not ("settings" or "appearance" or "session");
        header.Controls.Add(refresh);
        if (!prefs.Locked)
        {
            var drag = ActionButton("⠿", () => { });
            drag.AccessibleName = "拖动组件";
            drag.Width = Dip(35);
            drag.Location = new Point(Dip(44), 0);
            drag.Cursor = Cursors.SizeAll;
            drag.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) BeginDrag(); };
            header.Controls.Add(drag);
            var grip = ActionButton("↘", () => { });
            grip.AccessibleName = "调整窗口尺寸";
            grip.Width = Dip(34);
            grip.Location = new Point(Dip(82), 0);
            grip.Cursor = Cursors.SizeNWSE;
            grip.MouseDown += (_, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                resizeOrigin = Cursor.Position;
                resizeSize = Size;
                grip.Capture = true;
            };
            grip.MouseMove += (_, _) =>
            {
                if (resizeOrigin is not { } origin) return;
                int minWidth = (int)Math.Round(320 * DeviceDpi / 96.0);
                int minHeight = (int)Math.Round(520 * DeviceDpi / 96.0);
                Size = new Size(Math.Max(minWidth, resizeSize.Width + Cursor.Position.X - origin.X),
                    Math.Max(minHeight, resizeSize.Height + Cursor.Position.Y - origin.Y));
            };
            grip.MouseUp += (_, _) => { resizeOrigin = null; grip.Capture = false; };
            grip.MouseCaptureChanged += (_, _) => resizeOrigin = null;
            header.Controls.Add(grip);
        }
        header.Controls.Add(settings);
        AddDock("⌂", "总览", "overview", ShowOverview);
        AddDock("▤", "我的课程", "courses", ShowCourses);
        AddDock("↓", "下载记录", "downloads", ShowDownloads);
        LayoutContent();
    }

    private void AddDock(string icon, string text, string name, Func<Task> action)
    {
        string label = prefs.Look.NavStyle switch { "icons" => icon, "text" => text, _ => icon + " " + text };
        var button = ActionButton(label, () => _ = NavigateDock(action),
            route.TryGetValue("name", out var view) && view == name);
        button.Text = label;
        button.AccessibleName = text;
        button.Margin = Padding.Empty;
        button.Height = Dip(37);
        dock.Controls.Add(button);
    }

    private Task NavigateDock(Func<Task> destination)
    {
        history.Clear();
        page = null;
        return ChangePage(destination);
    }

    internal async Task ChangePage(Func<Task> view)
    {
        if (closing) return;
        if (page is not null) history.Push(page with { Scroll = -body.AutoScrollPosition.Y });
        page = new Page(view);
        pageVersion++;
        await Run(view);
    }

    internal async Task GoBack()
    {
        if (!history.TryPop(out var destination)) return;
        page = destination;
        pageVersion++;
        int version = pageVersion;
        await Run(destination.Show);
        if (version == pageVersion) body.AutoScrollPosition = new Point(0, destination.Scroll);
    }

    private async Task RefreshPage()
    {
        if (await RefreshBusinessPage()) return;
        if (page is not null) await page.Show();
    }

    internal void DescribeRoute(string name, string? courseId = null, string? assignmentId = null, string? tab = null)
    {
        route.Clear();
        route["name"] = name;
        if (courseId is not null) route["courseId"] = courseId;
        if (assignmentId is not null) route["assignmentId"] = assignmentId;
        if (tab is not null) route["tab"] = tab;
    }

    private void SaveLastView()
    {
        if (!route.TryGetValue("name", out var name) || name is not ("overview" or "courses" or "downloads" or "course" or "assignment"))
            return;
        var path = Path.Combine(options.Profile, "last-view.json");
        var view = new Dictionary<string, object>(route.ToDictionary(pair => pair.Key, pair => (object)pair.Value))
            { ["scroll"] = -body.AutoScrollPosition.Y };
        try
        {
            Directory.CreateDirectory(options.Profile);
            var temporary = path + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { savedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), view }));
            File.Move(temporary, path, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private JsonElement ReadLastView()
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(options.Profile, "last-view.json")));
            var root = json.RootElement;
            if (root.TryGetProperty("savedAt", out var time) && time.TryGetInt64(out long savedAt) &&
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - savedAt < 300000 &&
                root.TryGetProperty("view", out var view)) return view.Clone();
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (JsonException) { }
        return default;
    }

    private async Task Run(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            Notice(error.Message);
        }
    }

    private void OnBusinessEvent(JsonElement value)
    {
        if (closing || !IsHandleCreated) return;
        try
        {
            BeginInvoke(() =>
            {
                if (!closing && value.TryGetProperty("type", out var kind) && kind.GetString() == "download.changed")
                    RefreshDownloads(value);
            });
        }
        catch (InvalidOperationException) { }
    }

    internal Task<JsonElement> CallAsync(string method, object? payload = null) => service.CallAsync(method, payload);

    internal void BeginPage(string title, string? subtitle = null)
    {
        body.SuspendLayout();
        try
        {
            ClearControls(body);
            wide.Clear();
            body.AutoScrollPosition = Point.Empty;
            AddLine(title, 23, Foreground, FontStyle.Bold);
            if (!string.IsNullOrEmpty(subtitle)) AddLine(subtitle, 12, Secondary, FontStyle.Regular);
        }
        finally { body.ResumeLayout(); }
        UpdateNavigation();
    }

    internal void Section(string title, string? subtitle = null)
    {
        AddLine(title, 15, Foreground, FontStyle.Bold, 8);
        if (!string.IsNullOrWhiteSpace(subtitle)) AddLine(subtitle, 11, Secondary, FontStyle.Regular);
    }

    private void AddLine(string text, int size, Color color, FontStyle style, int top = 0)
    {
        var label = new Label
        {
            Text = text,
            AutoSize = true,
            MaximumSize = new Size(ContentWidth, 0),
            BackColor = CanvasKey,
            ForeColor = color,
            Margin = new Padding(Dip(4), top == 0 ? 0 : Dip(top), 0, Dip(5)),
        };
        SetOwnedFont(label, TextFont(size, style));
        body.Controls.Add(label);
    }

    private Font TextFont(int size, FontStyle style = FontStyle.Regular) =>
        new("Microsoft YaHei UI", Dip(size), style, GraphicsUnit.Pixel);

    private void SetOwnedFont(Control control, Font font)
    {
        if (!ownedFonts.TryGetValue(control, out var previous)) control.Disposed += ReleaseControlFont;
        control.Font = font;
        ownedFonts[control] = font;
        previous?.Dispose();
    }

    private void ReleaseControlFont(object? sender, EventArgs args)
    {
        if (sender is Control control && ownedFonts.Remove(control, out var font)) font.Dispose();
    }

    internal void Row(string title, string? subtitle = null, Action? onClick = null, string? trail = null)
    {
        var item = ActionButton(string.IsNullOrEmpty(subtitle) ? title : title + Environment.NewLine + subtitle,
            onClick ?? (() => { }));
        item.TextAlign = ContentAlignment.MiddleLeft;
        item.Padding = new Padding(Dip(9), 0, Dip(5), 0);
        item.Height = Dip(prefs.Look.Density switch { "compact" => 39, "comfortable" => 59, _ => 49 });
        item.AccessibleName = title;
        item.Cursor = onClick is null ? Cursors.Default : Cursors.Hand;
        item.AutoEllipsis = true;
        if (!string.IsNullOrEmpty(trail)) item.Text += "   " + trail;
        AddContent(item);
    }

    internal void AddContent(Control control)
    {
        control.Width = ContentWidth;
        wide.Add(control);
        body.Controls.Add(control);
    }

    internal Button ActionButton(string text, Action onClick, bool primary = false)
    {
        Color fill = primary ? Color.FromArgb(255, Accent.R, Accent.G, Accent.B) : Surface;
        var button = new Button
        {
            Text = text,
            ForeColor = primary ? (fill.GetBrightness() > .56f ? Color.Black : Color.White) : Foreground,
            BackColor = fill,
            FlatStyle = FlatStyle.Flat,
            UseVisualStyleBackColor = false,
            Height = Dip(34),
            Margin = new Padding(0, 0, 0, Dip(4)),
        };
        button.FlatAppearance.BorderSize = 0;
        button.Click += (_, _) => onClick();
        SetOwnedFont(button, TextFont(13));
        button.Resize += (_, _) => RoundButton(button);
        return button;
    }

    private void RoundButton(Button button)
    {
        if (button.Width < 8 || button.Height < 8 || button.IsDisposed) return;
        int corner = Math.Min(Dip(12), Math.Min(button.Width, button.Height));
        using var path = new GraphicsPath();
        path.AddArc(0, 0, corner, corner, 180, 90);
        path.AddArc(button.Width - corner, 0, corner, corner, 270, 90);
        path.AddArc(button.Width - corner, button.Height - corner, corner, corner, 0, 90);
        path.AddArc(0, button.Height - corner, corner, corner, 90, 90);
        path.CloseFigure();
        var previous = button.Region;
        button.Region = new Region(path);
        previous?.Dispose();
    }

    internal TextBox Input(string accessibleName, string text = "", bool password = false, bool multiline = false)
    {
        var input = new TextBox
        {
            AccessibleName = accessibleName,
            Text = text,
            BackColor = Surface,
            ForeColor = Foreground,
            UseSystemPasswordChar = password,
            Multiline = multiline,
            Height = Dip(multiline ? 57 : 28),
            Margin = new Padding(0, 0, 0, Dip(5)),
        };
        SetOwnedFont(input, TextFont(13));
        return input;
    }

    internal string? ChooseFolder()
    {
        using var folder = new FolderBrowserDialog
        {
            Description = "选择课件保存位置",
            SelectedPath = Directory.Exists(prefs.DownloadDirectory) ? prefs.DownloadDirectory : string.Empty,
        };
        return folder.ShowDialog(this) == DialogResult.OK ? folder.SelectedPath : null;
    }

    internal void Notice(string message)
    {
        if (IsDisposed || closing) return;
        status.Text = "  " + message;
        status.BackColor = Surface;
        status.ForeColor = Foreground;
        status.Show();
        noticeTimer.Stop();
        noticeTimer.Start();
    }

    private static void ClearControls(Control parent)
    {
        while (parent.Controls.Count > 0) parent.Controls[0].Dispose();
    }

    private void StartReport()
    {
        var deadline = Environment.TickCount64 + 5000;
        var timer = new Timer { Interval = 100 };
        reportTimer = timer;
        timer.Tick += (_, _) =>
        {
            var load = glass?.EffectLoadStatus;
            if ((load is null || load == CompositionEffectFactoryLoadStatus.Pending) && Environment.TickCount64 < deadline) return;
            timer.Stop();
            File.WriteAllText(options.Report!, JsonSerializer.Serialize(new
            {
                ready = load == CompositionEffectFactoryLoadStatus.Success,
                pid = Environment.ProcessId,
                windowHandle = Handle.ToInt64(),
                glassHandle = glass?.Handle.ToInt64(),
                servicePid = service.ProcessId,
                radius = prefs.Look.Radius,
                status = load?.ToString(),
            }));
            timer.Dispose();
            reportTimer = null;
        };
        timer.Start();
    }

    private string LaunchArguments() => "--desktop \"" + options.Desktop + "\"";
    private Rectangle SavedPixelBounds() => prefs.Geometry.Normalize().ToPixels();

    private void RestorePlacement()
    {
        prefs = prefs with { Geometry = DesktopGeometry.FromPixels(Bounds).Normalize() };
        Bounds = SavedPixelBounds();
        ApplyGlass();
        AlignGlass();
        if (!closing && !quiet && !Visible) Show();
    }

    private static bool SystemUsesLightTheme() =>
        Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "AppsUseLightTheme", 0) as int? == 1;

    private void BeginDrag()
    {
        if (prefs.Locked) return;
        ReleaseCapture();
        SendMessage(Handle, 0x00A1, 2, 0);
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReleaseCapture();

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial nint SendMessage(nint window, uint message, nuint wParam, nint lParam);
}
