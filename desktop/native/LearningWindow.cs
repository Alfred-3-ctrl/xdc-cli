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
    internal Color Surface => Dark ? Color.FromArgb(39, 40, 43) : Color.FromArgb(253, 253, 254);
    internal Color Foreground => Dark ? Color.FromArgb(242, 242, 244) : Color.FromArgb(35, 36, 38);
    internal Color Secondary => Mix(
        Dark ? Color.FromArgb(170, 170, 177) : Color.FromArgb(101, 103, 108),
        Dark ? Color.FromArgb(199, 207, 221) : Color.FromArgb(57, 60, 67),
        Math.Clamp((prefs.Look.Transparency - 30) / 65d, 0, 1));
    internal Color Accent => ColorTranslator.FromHtml(prefs.Look.Accent);
    private Color Sheet => Dark ? Color.FromArgb(32, 33, 36) : Color.FromArgb(247, 247, 248);
    private Color Chrome => Dark ? Color.FromArgb(69, 71, 80) : Color.FromArgb(248, 249, 251);
    private Color Rule => Dark ? Color.FromArgb(104, 108, 119) : Color.FromArgb(205, 208, 214);
    private Color AccentInk
    {
        get
        {
            var baseTone = Luminance(Sheet);
            for (int step = 0; step <= 10; step++)
            {
                var tone = Mix(Accent, Foreground, step / 10d);
                double ink = Luminance(tone);
                if ((Math.Max(baseTone, ink) + .05) / (Math.Min(baseTone, ink) + .05) >= 4.5) return tone;
            }
            return Foreground;
        }
    }

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
        dock.Paint += (_, e) => PaintDockOutline(e.Graphics);
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
        int alpha = (int)Math.Round((100 - prefs.Look.Transparency) * 2.55);
        glass.SetTint(Color.FromArgb(Math.Clamp(alpha, 0, 255), Sheet));
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
            int margin = Dip(15);
            int gutter = Math.Clamp((int)Math.Round(ClientSize.Width * .06), Dip(20), Dip(29));
            header.SetBounds(margin, Dip(11), Math.Max(1, ClientSize.Width - 2 * margin), Dip(36));
            int top = Dip(58);
            int dockPadding = Dip(4);
            int dockHeight = Dip(38) + 2 * dockPadding;
            dock.Padding = new Padding(dockPadding);
            var tools = dock.Controls.Cast<Control>().ToArray();
            int[] desiredWidths = tools.Select(item =>
                Math.Max(Dip(38), TextRenderer.MeasureText(item.Text, item.Font).Width + 2 * Dip(11))).ToArray();
            int itemSpace = Math.Max(1, ClientSize.Width - 2 * margin - 2 * dockPadding - tools.Length * Dip(2));
            int itemsWidth = desiredWidths.Sum();
            int dockWidth = Math.Min(itemsWidth, itemSpace) + 2 * dockPadding + tools.Length * Dip(2);
            int dockX = prefs.Look.NavAlign switch
            {
                "left" => margin,
                "right" => ClientSize.Width - dockWidth - margin,
                _ => (ClientSize.Width - dockWidth) / 2,
            };
            var dockBounds = new Rectangle(dockX, Math.Max(top, ClientSize.Height - dockHeight - Dip(13)), dockWidth, dockHeight);
            if (dock.Bounds.Size != dockBounds.Size || dock.Region is null)
            {
                using var outline = CapsulePath(new(0, 0, dockWidth, dockHeight), dockHeight / 2);
                var previous = dock.Region;
                dock.Region = new Region(outline);
                previous?.Dispose();
            }
            dock.Bounds = dockBounds;
            status.SetBounds(gutter, Math.Max(top, dock.Top - Dip(32)), ClientSize.Width - 2 * gutter, Dip(31));
            body.SetBounds(gutter, top, ClientSize.Width - 2 * gutter, Math.Max(1, dock.Top - top - Dip(12)));
            foreach (var item in wide) item.Width = ContentWidth;
            for (int i = 0; i < tools.Length; i++)
                tools[i].Width = itemsWidth <= itemSpace ? desiredWidths[i] : Math.Max(1, desiredWidths[i] * itemSpace / itemsWidth);
            if (header.Controls.Count > 0) header.Controls[^1].Left = header.ClientSize.Width - header.Controls[^1].Width;
        }
        finally { inLayout = false; }
    }

    private int ContentWidth => Math.Max(Dip(40), body.ClientSize.Width - SystemInformation.VerticalScrollBarWidth);

    private void UpdateNavigation()
    {
        ClearControls(header);
        ClearControls(dock);
        dock.BackColor = Chrome;
        var back = ToolButton("‹", () => _ = GoBack());
        back.AccessibleName = "返回";
        back.Visible = history.Count != 0;
        back.Location = Point.Empty;
        header.Controls.Add(back);
        var settings = ToolButton("⚙", () => _ = OpenSettings());
        settings.AccessibleName = "设置";
        var refresh = ToolButton("↻", () => _ = Run(RefreshPage));
        refresh.AccessibleName = "刷新";
        refresh.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        refresh.Left = Math.Max(0, header.ClientSize.Width - Dip(80));
        refresh.Visible = route.TryGetValue("name", out var screen) && screen is not ("settings" or "appearance" or "session");
        header.Controls.Add(refresh);
        if (!prefs.Locked)
        {
            var drag = ToolButton("⠿", () => { });
            drag.AccessibleName = "拖动组件";
            drag.Location = new Point(Dip(44), 0);
            drag.Cursor = Cursors.SizeAll;
            drag.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) BeginDrag(); };
            header.Controls.Add(drag);
            var grip = ToolButton("↘", () => { });
            grip.AccessibleName = "调整窗口尺寸";
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
        bool selected = route.TryGetValue("name", out var view) && view == name;
        var button = (GlassButton)ActionButton(label, () => _ = NavigateDock(action));
        button.CanvasColor = Chrome;
        button.BackColor = selected ? Mix(Chrome, Accent, .22) : Chrome;
        button.HoverColor = selected ? button.BackColor : Mix(Chrome, Foreground, .08);
        button.ForeColor = Foreground;
        button.AccessibleName = text;
        button.Margin = new Padding(Dip(1), 0, Dip(1), 0);
        button.Height = Dip(38);
        button.CornerRadius = button.Height / 2;
        SetOwnedFont(button, TextFont(13, selected ? FontStyle.Bold : FontStyle.Regular));
        dock.Controls.Add(button);
    }

    private Button ToolButton(string text, Action click)
    {
        var button = (GlassButton)ActionButton(text, click);
        button.Size = new Size(Dip(36), Dip(36));
        button.BackColor = Chrome;
        button.BorderColor = Rule;
        button.HoverColor = Mix(Chrome, Foreground, .12);
        button.CornerRadius = button.Height / 2;
        SetOwnedFont(button, TextFont(text == "‹" ? 23 : 16));
        return button;
    }

    private void PaintDockOutline(Graphics graphics)
    {
        if (dock.Width < 3 || dock.Height < 3) return;
        using var border = new Pen(Rule);
        using var outline = CapsulePath(new(1, 1, dock.Width - 2, dock.Height - 2), dock.Height / 2);
        graphics.DrawPath(border, outline);
    }

    private static GraphicsPath CapsulePath(Rectangle rectangle, int radius)
    {
        int size = Math.Min(2 * radius, Math.Min(rectangle.Width, rectangle.Height));
        var path = new GraphicsPath();
        if (size <= 1) { path.AddRectangle(rectangle); return path; }
        path.AddArc(rectangle.Left, rectangle.Top, size, size, 180, 90);
        path.AddArc(rectangle.Right - size, rectangle.Top, size, size, 270, 90);
        path.AddArc(rectangle.Right - size, rectangle.Bottom - size, size, size, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - size, size, size, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static Color Mix(Color bottom, Color top, double weight)
    {
        int Channel(int from, int to) => (int)Math.Round(from + (to - from) * weight);
        return Color.FromArgb(Channel(bottom.R, top.R), Channel(bottom.G, top.G), Channel(bottom.B, top.B));
    }

    private static double Luminance(Color color)
    {
        static double Linear(double value) => value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
        return .2126 * Linear(color.R / 255d) + .7152 * Linear(color.G / 255d) + .0722 * Linear(color.B / 255d);
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
            AddLine(title, 29, Foreground, FontStyle.Bold, bottom: string.IsNullOrEmpty(subtitle) ? 26 : 4);
            if (!string.IsNullOrEmpty(subtitle)) AddLine(subtitle, 13, Secondary, FontStyle.Regular, bottom: 26);
        }
        finally { body.ResumeLayout(); }
        UpdateNavigation();
    }

    internal void Section(string title, string? subtitle = null)
    {
        if (string.IsNullOrWhiteSpace(subtitle))
        {
            AddLine(title, 13, Foreground, FontStyle.Bold, 12);
            return;
        }
        var heading = new Panel { BackColor = CanvasKey, Height = Dip(28), Margin = new Padding(0, Dip(12), 0, 0) };
        var label = new GlassLabel { Text = title, BackColor = CanvasKey, ForeColor = Foreground, Dock = DockStyle.Left, AutoSize = true };
        var detail = new GlassLabel { Text = subtitle, BackColor = CanvasKey, ForeColor = Secondary, Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.TopRight, AutoEllipsis = true };
        SetOwnedFont(label, TextFont(13, FontStyle.Bold));
        SetOwnedFont(detail, TextFont(12));
        heading.Controls.Add(detail);
        heading.Controls.Add(label);
        AddContent(heading);
    }

    private void AddLine(string text, int size, Color color, FontStyle style, int top = 0, int bottom = 5)
    {
        var label = new GlassLabel
        {
            Text = text,
            AutoSize = true,
            MaximumSize = new Size(ContentWidth, 0),
            BackColor = CanvasKey,
            ForeColor = color,
            Margin = new Padding(0, top == 0 ? 0 : Dip(top), 0, Dip(bottom)),
        };
        SetOwnedFont(label, TextFont(size, style));
        body.Controls.Add(label);
    }

    private Font TextFont(int size, FontStyle style = FontStyle.Regular) =>
        new("Microsoft YaHei UI", Dip(size), style, GraphicsUnit.Pixel);

    private void SetOwnedFont(Control control, Font font)
    {
        if (ownedFonts.TryGetValue(control, out var previous))
        {
            // WinForms can keep the installed font when the new font has equal properties.
            if (ReferenceEquals(previous, font)) return;
            if (previous.Equals(font))
            {
                font.Dispose();
                return;
            }
        }
        else control.Disposed += ReleaseControlFont;
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
        if (onClick is null)
        {
            AddLine(title, 13, Secondary, FontStyle.Regular, top: 4, bottom: 12);
            if (!string.IsNullOrEmpty(subtitle)) AddLine(subtitle, 12, Secondary, FontStyle.Regular, bottom: 12);
            return;
        }
        var item = (GlassButton)ActionButton(string.IsNullOrEmpty(subtitle) ? title : title + Environment.NewLine + subtitle,
            onClick);
        item.CornerRadius = 0;
        item.BackColor = Mix(Sheet, Surface, .45);
        item.TextAlign = ContentAlignment.MiddleLeft;
        item.Padding = new Padding(Dip(9), 0, Dip(5), 0);
        item.Height = Dip(prefs.Look.Density switch { "compact" => 44, "comfortable" => 66, _ => 54 });
        item.AccessibleName = title;
        item.Cursor = Cursors.Hand;
        item.AutoEllipsis = true;
        if (!string.IsNullOrEmpty(trail)) item.Text += "   " + trail;
        item.Margin = Padding.Empty;
        AddContent(item);
        AddContent(new Panel { Height = 1, BackColor = Rule, Margin = Padding.Empty });
    }

    internal void AddContent(Control control)
    {
        control.Width = ContentWidth;
        wide.Add(control);
        body.Controls.Add(control);
    }

    internal Button ActionButton(string text, Action onClick, bool primary = false)
    {
        var button = new GlassButton
        {
            Text = text,
            ForeColor = primary ? AccentInk : Foreground,
            BackColor = Surface,
            HoverColor = Mix(Surface, Foreground, .1),
            FocusColor = AccentInk,
            CornerRadius = Dip(8),
            FlatStyle = FlatStyle.Flat,
            UseVisualStyleBackColor = false,
            Height = Dip(34),
            Margin = new Padding(0, 0, 0, Dip(4)),
        };
        button.FlatAppearance.BorderSize = 0;
        button.Click += (_, _) => onClick();
        SetOwnedFont(button, TextFont(13));
        return button;
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
