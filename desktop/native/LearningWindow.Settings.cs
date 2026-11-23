using System.Text.RegularExpressions;

namespace Xdc.NativeDesktop;

internal sealed partial class LearningWindow
{
    private string appearanceTab = "look";
    private NumericUpDown? widthControl;
    private NumericUpDown? heightControl;
    private Label? dimensions;
    private Label? changes;
    private Button? saveAppearance;
    private bool previewingBounds;

    private Task OpenSettings() => route.GetValueOrDefault("name") == "settings" ? GoBack() : ChangePage(ShowSettings);

    private Task ShowSettings()
    {
        DescribeRoute("settings");
        BeginPage("设置");
        Section("桌面");
        Row("外观与布局", onClick: () =>
        {
            appearanceTab = "look";
            _ = ChangePage(ShowAppearance);
        }, trail: "›");
        var startup = new CheckBox
        {
            Text = "登录 Windows 时启动",
            AccessibleName = "登录 Windows 时启动",
            Checked = prefs.Startup,
            Enabled = options.Managed,
            BackColor = Surface,
            ForeColor = Foreground,
            Height = Dip(35),
            Padding = new Padding(Dip(8), 0, 0, 0),
        };
        startup.CheckedChanged += (_, _) =>
        {
            bool requested = startup.Checked;
            try
            {
                if (requested == saved.Startup) return;
                DesktopStartup.SetEnabled(requested, Environment.ProcessPath!, LaunchArguments(), options.Desktop);
                var updated = saved with { Startup = requested };
                try { updated.Save(options.Profile); }
                catch
                {
                    DesktopStartup.SetEnabled(saved.Startup, Environment.ProcessPath!, LaunchArguments(), options.Desktop);
                    throw;
                }
                saved = updated;
                prefs = prefs with { Startup = requested };
            }
            catch (Exception error)
            {
                startup.Checked = saved.Startup;
                Notice("启动设置未保存：" + error.Message);
            }
        };
        AddContent(startup);
        Section("浙大账号");
        Row("账号与会话", onClick: () => _ = ChangePage(ShowAccount), trail: "›");
        Section("下载");
        Row("保存位置", prefs.DownloadDirectory, () =>
        {
            if (ChooseFolder() is not { } folder) return;
            try
            {
                var updated = saved with { DownloadDirectory = folder };
                updated.Save(options.Profile);
                saved = updated;
                prefs = prefs with { DownloadDirectory = folder };
                _ = ShowSettings();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Notice(error.Message); }
        });
        Row("UU 运行时", "完成当前文件后暂停");
        AddContent(ActionButton("关闭组件", () => _ = CloseDesktop()));
        return Task.CompletedTask;
    }

    private Task ShowAppearance()
    {
        DescribeRoute("appearance", tab: appearanceTab);
        dimensions = null;
        widthControl = heightControl = null;
        changes = null;
        saveAppearance = null;
        BeginPage("外观与布局");
        var tabs = new FlowLayoutPanel { Height = Dip(40), WrapContents = false, BackColor = CanvasKey };
        foreach (var item in new[] { (Key: "look", Text: "外观"), (Key: "layout", Text: "布局") })
            tabs.Controls.Add(ActionButton(item.Text, () =>
            {
                appearanceTab = item.Key;
                _ = ShowAppearance();
            }, appearanceTab == item.Key));
        AddContent(tabs);
        if (appearanceTab == "look")
        {
            Choice("主题", prefs.Look.Scheme, [("system", "跟随系统"), ("light", "浅色"), ("dark", "深色")],
                value => PreviewLook(prefs.Look with { Scheme = value }, redraw: true));
            AccentSetting();
            Range("玻璃透明度", 0, 95, prefs.Look.Transparency, "%", value =>
                PreviewLook(prefs.Look with { Transparency = value }));
            Range("界面缩放", 85, 125, prefs.Look.Scale, "%", value =>
                PreviewLook(prefs.Look with { Scale = value }));
        }
        else
        {
            dimensions = SettingLabel("");
            AddContent(dimensions);
            AddContent(ActionButton(prefs.Locked ? "解锁布局" : "锁定布局", () =>
            {
                prefs = prefs with { Locked = !prefs.Locked };
                _ = ShowAppearance();
            }));
            if (!prefs.Locked)
            {
                widthControl = GeometryInput("宽度", prefs.Geometry.Width, 320, 620, value =>
                    PreviewBounds(prefs.Geometry with { Width = value }));
                heightControl = GeometryInput("高度", prefs.Geometry.Height, 520, 820, value =>
                    PreviewBounds(prefs.Geometry with { Height = value }));
            }
            AddContent(SettingLabel("布局快捷键"));
            var shortcut = Input("布局快捷键", prefs.Shortcut);
            shortcut.MaxLength = 28;
            shortcut.TextChanged += (_, _) =>
            {
                prefs = prefs with { Shortcut = shortcut.Text };
                UpdateAppearanceFeedback();
            };
            AddContent(shortcut);
            Choice("列表密度", prefs.Look.Density, [("compact", "紧凑"), ("regular", "普通"), ("comfortable", "宽松")],
                value => PreviewLook(prefs.Look with { Density = value }));
            Choice("底栏显示", prefs.Look.NavStyle, [("both", "图标与文字"), ("text", "文字"), ("icons", "图标")],
                value => PreviewLook(prefs.Look with { NavStyle = value }));
            Choice("底栏位置", prefs.Look.NavAlign, [("left", "靠左"), ("center", "居中"), ("right", "靠右")],
                value => PreviewLook(prefs.Look with { NavAlign = value }));
            Range("窗口圆角", 16, 36, prefs.Look.Radius, " px", value =>
                PreviewLook(prefs.Look with { Radius = value }));
        }
        var actions = new FlowLayoutPanel { Height = Dip(42), BackColor = CanvasKey, WrapContents = false };
        actions.Controls.Add(ActionButton("恢复默认", ResetAppearance));
        saveAppearance = ActionButton("保存", SaveAppearance, primary: true);
        actions.Controls.Add(saveAppearance);
        changes = SettingLabel("");
        changes.AutoSize = true;
        actions.Controls.Add(changes);
        AddContent(actions);
        RefreshLayoutDimensions();
        UpdateAppearanceFeedback();
        return Task.CompletedTask;
    }

    private Label SettingLabel(string text) => new()
    {
        Text = text,
        ForeColor = Foreground,
        BackColor = Surface,
        TextAlign = ContentAlignment.MiddleLeft,
        Height = Dip(25),
        Padding = new Padding(Dip(7), 0, Dip(7), 0),
        Margin = new Padding(0, Dip(2), 0, 0),
    };

    private void Choice(string label, string selected, (string Key, string Text)[] options, Action<string> change)
    {
        AddContent(SettingLabel(label));
        var select = new ComboBox
        {
            AccessibleName = label, DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Surface, ForeColor = Foreground,
        };
        select.Items.AddRange(options.Select(value => value.Text).ToArray());
        select.SelectedIndex = Array.FindIndex(options, option => option.Key == selected);
        select.SelectedIndexChanged += (_, _) =>
        {
            if (select.SelectedIndex >= 0) change(options[select.SelectedIndex].Key);
        };
        AddContent(select);
    }

    private void Range(string label, int min, int max, int value, string unit, Action<int> change)
    {
        var readout = SettingLabel(label + " " + value + unit);
        AddContent(readout);
        var slider = new TrackBar
        {
            AccessibleName = label,
            Name = label,
            BackColor = Surface,
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(value, min, max),
            TickStyle = TickStyle.None,
            AutoSize = false,
            Height = Dip(30),
        };
        slider.ValueChanged += (_, _) =>
        {
            readout.Text = label + " " + slider.Value + unit;
            change(slider.Value);
        };
        AddContent(slider);
    }

    private void AccentSetting()
    {
        var field = Input("强调色十六进制", prefs.Look.Accent.ToUpperInvariant());
        field.MaxLength = 7;
        field.TextChanged += (_, _) =>
        {
            if (Regex.IsMatch(field.Text, "^#[a-fA-F0-9]{6}$"))
                PreviewLook(prefs.Look with { Accent = field.Text.ToLowerInvariant() });
        };
        AddContent(ActionButton("选择强调色", () =>
        {
            using var chooser = new ColorDialog { FullOpen = true, Color = Accent };
            if (chooser.ShowDialog(this) == DialogResult.OK)
                field.Text = $"#{chooser.Color.R:X2}{chooser.Color.G:X2}{chooser.Color.B:X2}";
        }, primary: true));
        AddContent(field);
    }

    private NumericUpDown GeometryInput(string label, int value, int min, int max, Action<int> change)
    {
        AddContent(SettingLabel(label));
        var field = new NumericUpDown
        {
            AccessibleName = label,
            Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max),
            BackColor = Surface, ForeColor = Foreground,
        };
        field.ValueChanged += (_, _) => { if (!previewingBounds) change((int)field.Value); };
        AddContent(field);
        return field;
    }

    private void PreviewLook(DesktopLook look, bool redraw = false)
    {
        int originalScale = prefs.Look.Scale;
        prefs = prefs with { Look = look.Normalize() };
        ApplyGlass();
        if (redraw)
        {
            BeginInvoke(() =>
            {
                if (!closing && route.GetValueOrDefault("name") == "appearance") _ = ShowAppearance();
            });
        }
        else
        {
            if (originalScale != prefs.Look.Scale)
            {
                float ratio = (float)prefs.Look.Scale / originalScale;
                body.SuspendLayout();
                try { ScalePageControls(body.Controls, ratio); }
                finally { body.ResumeLayout(); }
            }
            UpdateNavigation();
        }
        UpdateAppearanceFeedback();
    }

    private void ScalePageControls(Control.ControlCollection controls, float ratio)
    {
        foreach (Control control in controls)
        {
            if (control is Label or Button or TextBox or ComboBox or NumericUpDown or CheckBox)
            {
                var font = control.Font;
                SetOwnedFont(control, new Font(font.FontFamily, font.Size * ratio, font.Style, font.Unit));
            }
            if (control is TrackBar or Button or FlowLayoutPanel or Label { AutoSize: false })
                control.Height = Math.Max(1, (int)Math.Round(control.Height * ratio));
            if (control.Controls.Count != 0) ScalePageControls(control.Controls, ratio);
        }
    }

    private void PreviewBounds(DesktopGeometry geometry)
    {
        prefs = prefs with { Geometry = geometry.Normalize() };
        previewingBounds = true;
        try { Bounds = SavedPixelBounds(); }
        finally { previewingBounds = false; }
        RefreshLayoutDimensions();
    }

    private void RefreshLayoutDimensions()
    {
        if (!readyToPaint || previewingBounds) return;
        prefs = prefs with { Geometry = DesktopGeometry.FromPixels(Bounds) };
        if (dimensions is { IsDisposed: false })
        {
            dimensions.Text = $"{prefs.Geometry.Width} × {prefs.Geometry.Height}";
            previewingBounds = true;
            try
            {
                if (widthControl is { IsDisposed: false, Focused: false })
                    widthControl.Value = Math.Clamp(prefs.Geometry.Width, widthControl.Minimum, widthControl.Maximum);
                if (heightControl is { IsDisposed: false, Focused: false })
                    heightControl.Value = Math.Clamp(prefs.Geometry.Height, heightControl.Minimum, heightControl.Maximum);
            }
            finally { previewingBounds = false; }
        }
        UpdateAppearanceFeedback();
    }

    private void UpdateAppearanceFeedback()
    {
        bool changed = prefs != saved;
        if (changes is { IsDisposed: false }) changes.Text = changed ? "未保存" : "";
        if (saveAppearance is { IsDisposed: false }) saveAppearance.Enabled = changed;
    }

    private void ResetAppearance()
    {
        var defaults = DesktopGeometry.Default;
        prefs = prefs with { Look = DesktopLook.Default, Shortcut = DesktopPreferences.DefaultShortcut, Locked = true };
        PreviewBounds(prefs.Geometry with { Width = defaults.Width, Height = defaults.Height });
        ApplyGlass();
        _ = ShowAppearance();
    }

    private void SaveAppearance()
    {
        string? shortcut = LayoutShortcut.Parse(prefs.Shortcut)?.Name;
        if (shortcut is null || !lifecycle.ApplyShortcut(shortcut))
        {
            Notice("该布局快捷键无效或已被其他程序占用。");
            return;
        }
        var next = prefs with { Shortcut = shortcut, Geometry = DesktopGeometry.FromPixels(Bounds).Normalize() };
        try { next.Save(options.Profile); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            lifecycle.ApplyShortcut(saved.Shortcut);
            Notice("无法保存外观：" + error.Message);
            return;
        }
        saved = prefs = next;
        UpdateAppearanceFeedback();
        Notice("已保存外观和布局");
    }

    private void ToggleLayout()
    {
        if (closing || quiet || !Visible) return;
        bool locked = !prefs.Locked;
        var next = saved with { Locked = locked, Geometry = DesktopGeometry.FromPixels(Bounds).Normalize() };
        try
        {
            next.Save(options.Profile);
            saved = next;
            prefs = prefs with { Locked = locked, Geometry = next.Geometry };
            if (route.GetValueOrDefault("name") == "appearance") _ = ShowAppearance();
            else UpdateNavigation();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Notice(error.Message); }
    }

}
