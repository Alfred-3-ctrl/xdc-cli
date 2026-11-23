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
        var tabs = new FlowLayoutPanel { Height = Dip(38), WrapContents = false, BackColor = CanvasKey };
        foreach (var item in new[] { (Key: "look", Text: "外观"), (Key: "layout", Text: "布局") })
        {
            bool selected = appearanceTab == item.Key;
            var button = (GlassButton)ActionButton(item.Text, () =>
            {
                appearanceTab = item.Key;
                _ = ShowAppearance();
            });
            button.Width = Dip(52);
            button.Height = Dip(29);
            button.BackColor = selected ? Chrome : Surface;
            button.ForeColor = selected ? Foreground : Secondary;
            tabs.Controls.Add(button);
        }
        AddContent(tabs);
        AddContent(new Panel { Height = 1, BackColor = Rule, Margin = new Padding(0, 0, 0, Dip(6)) });
        if (appearanceTab == "look")
        {
            Choice("主题", prefs.Look.Scheme, [("system", "跟随系统"), ("light", "浅色"), ("dark", "深色")],
                value => PreviewLook(prefs.Look with { Scheme = value }, redraw: true));
            AccentSetting();
            Range("玻璃透明度", 0, 95, prefs.Look.Transparency, "%", value =>
                PreviewLook(prefs.Look with { Transparency = value }));
            Choice("玻璃效果", prefs.Look.Blur ? "blur" : "tint", [("blur", "实时模糊"), ("tint", "纯透明（降级）")],
                value => PreviewLook(prefs.Look with { Blur = value == "blur" }));
            Range("界面缩放", 85, 125, prefs.Look.Scale, "%", value =>
                PreviewLook(prefs.Look with { Scale = value }));
        }
        else
        {
            dimensions = SettingLabel("");
            var lockButton = ActionButton(prefs.Locked ? "解锁布局" : "锁定布局", () =>
            {
                prefs = prefs with { Locked = !prefs.Locked };
                _ = ShowAppearance();
            });
            SettingRow(dimensions, lockButton, 93);
            if (!prefs.Locked)
            {
                widthControl = GeometryInput("宽度", prefs.Geometry.Width, 320, 620, value =>
                    PreviewBounds(prefs.Geometry with { Width = value }));
                heightControl = GeometryInput("高度", prefs.Geometry.Height, 520, 820, value =>
                    PreviewBounds(prefs.Geometry with { Height = value }));
            }
            var shortcut = Input("布局快捷键", prefs.Shortcut);
            shortcut.MaxLength = 28;
            shortcut.TextChanged += (_, _) =>
            {
                prefs = prefs with { Shortcut = shortcut.Text };
                UpdateAppearanceFeedback();
            };
            SettingRow(SettingLabel("布局快捷键"), shortcut, 133);
            Choice("列表密度", prefs.Look.Density, [("compact", "紧凑"), ("regular", "标准"), ("comfortable", "宽松")],
                value => PreviewLook(prefs.Look with { Density = value }));
            Choice("底栏显示", prefs.Look.NavStyle, [("both", "图标与文字"), ("text", "仅文字"), ("icons", "仅图标")],
                value => PreviewLook(prefs.Look with { NavStyle = value }));
            Choice("底栏位置", prefs.Look.NavAlign, [("left", "靠左"), ("center", "居中"), ("right", "靠右")],
                value => PreviewLook(prefs.Look with { NavAlign = value }));
            Range("窗口圆角", 16, 36, prefs.Look.Radius, " px", value =>
                PreviewLook(prefs.Look with { Radius = value }));
        }
        var actions = new Panel { Height = Dip(44), BackColor = CanvasKey, Margin = new Padding(0, Dip(14), 0, 0) };
        var reset = ActionButton("恢复默认", ResetAppearance);
        saveAppearance = ActionButton("保存", SaveAppearance, primary: true);
        changes = SettingLabel("");
        var save = saveAppearance;
        var feedback = changes;
        feedback.ForeColor = Secondary;
        feedback.TextAlign = ContentAlignment.MiddleRight;
        void PlaceActions()
        {
            reset.Width = Dip(84);
            save.Width = Dip(56);
            save.Left = actions.ClientSize.Width - save.Width;
            feedback.SetBounds(Math.Max(reset.Right, save.Left - Dip(66)), 0, Dip(57), save.Height);
        }
        actions.Controls.Add(reset);
        actions.Controls.Add(save);
        actions.Controls.Add(changes);
        actions.Resize += (_, _) => PlaceActions();
        AddContent(actions);
        PlaceActions();
        RefreshLayoutDimensions();
        UpdateAppearanceFeedback();
        return Task.CompletedTask;
    }

    private Label SettingLabel(string text)
    {
        var label = new GlassLabel
        {
            Text = text,
            ForeColor = Foreground,
            BackColor = CanvasKey,
            TextAlign = ContentAlignment.MiddleLeft,
            Height = Dip(25),
        };
        SetOwnedFont(label, TextFont(13));
        return label;
    }

    private void SettingRow(Label title, Control control, int logicalWidth, bool stackWhenNarrow = false)
    {
        var row = new Panel { Height = Dip(49), BackColor = CanvasKey, Margin = Padding.Empty };
        var divider = new Panel { BackColor = Rule };
        void PlaceControls()
        {
            int available = Math.Max(1, row.ClientSize.Width - Dip(80));
            bool stacked = stackWhenNarrow && available < Dip(logicalWidth);
            if (stackWhenNarrow) row.Height = Dip(stacked ? 66 : 49);
            int width = Math.Min(Dip(logicalWidth), stacked ? row.ClientSize.Width : available);
            int top = stacked ? Dip(28) : (row.ClientSize.Height - control.Height) / 2;
            control.SetBounds(row.ClientSize.Width - width, top, width, control.Height);
            title.SetBounds(0, 0, stacked ? row.ClientSize.Width : Math.Max(1, control.Left - Dip(9)),
                stacked ? top : row.ClientSize.Height - 1);
            divider.SetBounds(0, row.ClientSize.Height - 1, row.ClientSize.Width, 1);
        }
        row.Controls.Add(title);
        row.Controls.Add(control);
        row.Controls.Add(divider);
        row.Resize += (_, _) => PlaceControls();
        AddContent(row);
        PlaceControls();
    }

    private void Choice(string label, string selected, (string Key, string Text)[] options, Action<string> change)
    {
        var select = new ComboBox
        {
            AccessibleName = label, DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            BackColor = Surface, ForeColor = Foreground,
        };
        select.Items.AddRange(options.Select(value => value.Text).ToArray());
        select.SelectedIndex = Array.FindIndex(options, option => option.Key == selected);
        select.SelectedIndexChanged += (_, _) =>
        {
            if (select.SelectedIndex >= 0) change(options[select.SelectedIndex].Key);
        };
        SetOwnedFont(select, TextFont(13));
        SettingRow(SettingLabel(label), select, 140);
    }

    private void Range(string label, int min, int max, int value, string unit, Action<int> change)
    {
        var readout = SettingLabel(value + unit);
        readout.TextAlign = ContentAlignment.MiddleRight;
        readout.ForeColor = Secondary;
        var slider = new GlassTrackBar
        {
            AccessibleName = label,
            Name = label,
            BackColor = CanvasKey,
            ForeColor = Foreground,
            RailColor = Rule,
            TrackColor = AccentInk,
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(value, min, max),
            TickStyle = TickStyle.None,
            AutoSize = false,
            Height = Dip(30),
        };
        slider.ValueChanged += (_, _) =>
        {
            readout.Text = slider.Value + unit;
            change(slider.Value);
        };
        var group = new Panel { BackColor = CanvasKey, Height = Dip(33) };
        void PlaceRange()
        {
            int readoutWidth = Dip(38);
            readout.SetBounds(Math.Max(1, group.Width - readoutWidth), 0, readoutWidth, group.Height);
            slider.SetBounds(0, (group.Height - slider.Height) / 2, Math.Max(1, readout.Left - Dip(5)), slider.Height);
        }
        group.Controls.Add(slider);
        group.Controls.Add(readout);
        group.Resize += (_, _) => PlaceRange();
        SettingRow(SettingLabel(label), group, 163);
        PlaceRange();
    }

    private static readonly string[] PresetAccents =
        ["#0a5152", "#245dd1", "#7a4fd6", "#c2410c", "#be185d", "#3f6212", "#57534e"];

    private void AccentSetting()
    {
        var field = Input("强调色十六进制", prefs.Look.Accent.ToUpperInvariant());
        field.MaxLength = 7;
        field.BorderStyle = BorderStyle.FixedSingle;
        var swatch = new GlassButton
        {
            AccessibleName = "选择强调色",
            Name = "AccentSwatch",
            BackColor = Accent,
            BorderColor = Rule,
            CornerRadius = Dip(5),
            Size = new Size(Dip(35), Dip(29)),
            TabStop = true,
        };
        var presetButtons = new List<(string Hex, GlassButton Button)>();
        void SyncPresetBorders()
        {
            string current = prefs.Look.Accent.ToLowerInvariant();
            foreach (var (hex, btn) in presetButtons)
            {
                btn.BorderColor = current == hex ? Foreground : Rule;
                btn.Invalidate();
            }
        }
        field.TextChanged += (_, _) =>
        {
            if (Regex.IsMatch(field.Text, "^#[a-fA-F0-9]{6}$"))
            {
                PreviewLook(prefs.Look with { Accent = field.Text.ToLowerInvariant() });
                swatch.BackColor = Accent;
                SyncPresetBorders();
            }
        };
        field.Leave += (_, _) =>
        {
            if (!Regex.IsMatch(field.Text, "^#[a-fA-F0-9]{6}$")) field.Text = prefs.Look.Accent.ToUpperInvariant();
        };
        swatch.Click += (_, _) => AccentPicker.Show(swatch, Accent, Surface, Foreground, color =>
        {
            if (!field.IsDisposed) field.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        });
        foreach (string hex in PresetAccents)
        {
            var presetColor = ColorTranslator.FromHtml(hex);
            var preset = new GlassButton
            {
                AccessibleName = "预设强调色 " + hex.ToUpperInvariant(),
                BackColor = presetColor,
                BorderColor = Rule,
                CornerRadius = Dip(9),
                Size = new Size(Dip(18), Dip(18)),
                TabStop = true,
            };
            preset.Click += (_, _) =>
            {
                if (!field.IsDisposed) field.Text = hex.ToUpperInvariant();
            };
            presetButtons.Add((hex, preset));
        }
        SyncPresetBorders();
        var group = new Panel { Height = Dip(32), BackColor = CanvasKey };
        void PlaceSwatch()
        {
            int x = 0;
            int dotGap = Dip(3);
            int fieldWidth = Dip(79);
            int swatchWidth = Dip(28);
            int remaining = group.Width - fieldWidth - swatchWidth - Dip(8) - presetButtons.Count * dotGap;
            int dotSize = Math.Clamp(remaining / presetButtons.Count, 1, Dip(18));
            foreach (var (_, btn) in presetButtons)
            {
                btn.Size = new Size(dotSize, dotSize);
                btn.CornerRadius = dotSize / 2;
                btn.Location = new Point(x, (group.Height - dotSize) / 2);
                x += dotSize + dotGap;
            }
            x += Dip(2);
            swatch.Size = new Size(swatchWidth, Dip(26));
            swatch.Location = new Point(x, (group.Height - swatch.Height) / 2);
            field.SetBounds(swatch.Right + Dip(6), (group.Height - field.Height) / 2,
                Math.Max(1, group.Width - swatch.Right - Dip(6)), field.Height);
        }
        foreach (var (_, btn) in presetButtons) group.Controls.Add(btn);
        group.Controls.Add(swatch);
        group.Controls.Add(field);
        group.Resize += (_, _) => PlaceSwatch();
        SettingRow(SettingLabel("强调色"), group, 260, stackWhenNarrow: true);
        PlaceSwatch();
    }

    private NumericUpDown GeometryInput(string label, int value, int min, int max, Action<int> change)
    {
        var field = new NumericUpDown
        {
            AccessibleName = label,
            Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max),
            BackColor = Surface, ForeColor = Foreground,
        };
        field.ValueChanged += (_, _) => { if (!previewingBounds) change((int)field.Value); };
        SetOwnedFont(field, TextFont(13));
        SettingRow(SettingLabel(label), field, 76);
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
        UpdateAccentSliders(body);
    }

    private void UpdateAccentSliders(Control parent)
    {
        foreach (Control control in parent.Controls)
        {
            if (control is GlassTrackBar slider) slider.TrackColor = AccentInk;
            else if (control.HasChildren) UpdateAccentSliders(control);
        }
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
            if (control is TrackBar or Button or Panel { Height: > 1 } or Label { AutoSize: false })
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
        if (saveAppearance is { IsDisposed: false })
        {
            saveAppearance.Enabled = changed;
            saveAppearance.ForeColor = changed ? AccentInk : Secondary;
        }
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
