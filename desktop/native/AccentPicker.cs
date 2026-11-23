using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace Xdc.NativeDesktop;

internal sealed class AccentPicker : Form
{
    private readonly Action<Color> preview;
    private readonly Panel chip;
    private readonly ColorPlane plane;
    private readonly HueStrip hue;
    private readonly TextBox hex;
    private readonly TextBox red;
    private readonly TextBox green;
    private readonly TextBox blue;
    private readonly Label redLabel;
    private readonly Label greenLabel;
    private readonly Label blueLabel;
    private Hsv picker;
    private Color selected;
    private bool syncing;
    private bool useHsl;

    public static void Show(Control anchor, Color initial, Color surface, Color foreground, Action<Color> preview)
    {
        if (anchor.IsDisposed || !anchor.IsHandleCreated) return;

        using var popup = new AccentPicker(initial, surface, foreground, anchor.DeviceDpi, preview);
        Rectangle swatch = anchor.RectangleToScreen(anchor.ClientRectangle);
        Rectangle workArea = Screen.FromRectangle(swatch).WorkingArea;
        int gap = Math.Max(1, anchor.DeviceDpi * 5 / 96);
        int x = Math.Clamp(swatch.Right - popup.Width, workArea.Left, Math.Max(workArea.Left, workArea.Right - popup.Width));
        int y = swatch.Bottom + gap;
        if (y + popup.Height > workArea.Bottom) y = swatch.Top - gap - popup.Height;
        popup.Location = new Point(x, Math.Clamp(y, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - popup.Height)));

        DialogResult result = anchor.FindForm() is { } owner ? popup.ShowDialog(owner) : popup.ShowDialog();
        if (result != DialogResult.OK && popup.ChangedFrom(initial) && !anchor.IsDisposed)
            preview(initial);
    }

    private AccentPicker(Color initial, Color surface, Color foreground, int dpi, Action<Color> preview)
    {
        this.preview = preview;
        selected = initial;
        picker = Hsv.FromColor(initial);
        int Px(int dip) => (int)Math.Round(dip * dpi / 96.0);
        Rectangle At(int x, int y, int w, int h) => new(Px(x), Px(y), Px(w), Px(h));

        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        ShowInTaskbar = ShowIcon = ControlBox = false;
        AccessibleName = "强调色选择器";
        BackColor = surface;
        ForeColor = foreground;
        ClientSize = new Size(Px(252), Px(232));
        Color fieldSurface = Blend(surface, foreground, 0.12);

        plane = new ColorPlane
        {
            Bounds = At(11, 11, 230, 112),
            AccessibleName = "饱和度与明度",
            AccessibleDescription = "按 ← 或 → 调整饱和度，按 ↑ 或 ↓ 调整明度。",
        };
        plane.Changed += (saturation, value) => Apply(picker with { Saturation = saturation, Value = value });
        Controls.Add(plane);

        chip = new Panel { Bounds = At(11, 134, 27, 22), AccessibleName = "当前颜色", BorderStyle = BorderStyle.FixedSingle };
        Controls.Add(chip);
        hue = new HueStrip
        {
            Bounds = At(46, 134, 195, 22),
            AccessibleName = "色相",
            AccessibleDescription = "按 ← 或 → 调整色相。",
        };
        hue.Changed += value => Apply(picker with { Hue = value });
        Controls.Add(hue);

        hex = AddInput("HEX", 11, 79, 7, out _);
        red = AddInput("R", 98, 42, 3, out redLabel);
        green = AddInput("G", 148, 42, 3, out greenLabel);
        blue = AddInput("B", 198, 43, 3, out blueLabel);
        hex.TextChanged += (_, _) =>
        {
            string token = hex.Text.Trim().TrimStart('#');
            if (syncing || token.Length != 6 || !int.TryParse(token, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int value))
                return;
            var color = Color.FromArgb(255, (value >> 16) & 255, (value >> 8) & 255, value & 255);
            Apply(Hsv.FromColor(color, picker.Hue), hex, color);
        };
        foreach (TextBox field in new[] { red, green, blue })
        {
            field.TextChanged += (_, _) =>
            {
                if (syncing || !TryChannel(red, useHsl ? 360 : 255, out int r) ||
                    !TryChannel(green, useHsl ? 100 : 255, out int g) || !TryChannel(blue, useHsl ? 100 : 255, out int b))
                    return;
                if (useHsl)
                {
                    double light = b / 100d;
                    double value = light + g / 100d * Math.Min(light, 1 - light);
                    double saturation = value == 0 ? 0 : 2 * (1 - light / value);
                    Apply(new Hsv(r % 360, saturation, value), field);
                }
                else
                {
                    var color = Color.FromArgb(r, g, b);
                    Apply(Hsv.FromColor(color, picker.Hue), field, color);
                }
            };
        }

        var format = new ComboBox
        {
            Bounds = At(11, 199, 82, 25),
            AccessibleName = "颜色格式",
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = fieldSurface,
            ForeColor = foreground,
        };
        format.Items.AddRange(["RGB", "HSL"]);
        format.SelectedIndex = 0;
        format.SelectedIndexChanged += (_, _) =>
        {
            useHsl = format.SelectedIndex == 1;
            red.AccessibleName = redLabel.Text = useHsl ? "H" : "R";
            green.AccessibleName = greenLabel.Text = useHsl ? "S %" : "G";
            blue.AccessibleName = blueLabel.Text = useHsl ? "L %" : "B";
            Synchronize();
        };
        Controls.Add(format);

        var eyedropper = new Button
        {
            Text = "吸管",
            AccessibleName = "屏幕取色",
            AccessibleDescription = "点击屏幕上的颜色，按 Esc 取消。",
            Bounds = At(100, 199, 66, 25),
            ForeColor = foreground,
            BackColor = fieldSurface,
            FlatStyle = FlatStyle.Flat,
            UseVisualStyleBackColor = false,
        };
        eyedropper.FlatAppearance.BorderColor = Blend(surface, foreground, 0.26);
        eyedropper.Click += (_, _) =>
        {
            if (ScreenColorPicker.Pick(this) is { } color)
                Apply(Hsv.FromColor(color, picker.Hue), exact: color);
        };
        Controls.Add(eyedropper);

        var done = new Button
        {
            Text = "完成",
            Bounds = At(179, 199, 62, 25),
            ForeColor = foreground,
            BackColor = fieldSurface,
            FlatStyle = FlatStyle.Flat,
            DialogResult = DialogResult.OK,
            UseVisualStyleBackColor = false,
        };
        done.FlatAppearance.BorderColor = Blend(surface, foreground, 0.26);
        Controls.Add(done);
        AcceptButton = done;
        ActiveControl = plane;
        AttachRightClickCancel(this);
        Synchronize();

        TextBox AddInput(string label, int left, int width, int length, out Label caption)
        {
            caption = new Label
            {
                Bounds = At(left, 160, width, 13),
                Text = label,
                TextAlign = ContentAlignment.MiddleCenter,
            };
            Controls.Add(caption);
            var field = new TextBox
            {
                Bounds = At(left, 175, width, 22),
                AccessibleName = label,
                MaxLength = length,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = fieldSurface,
                ForeColor = foreground,
                TextAlign = HorizontalAlignment.Center,
            };
            field.Leave += (_, _) => Synchronize();
            Controls.Add(field);
            return field;
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData != Keys.Escape) return base.ProcessCmdKey(ref msg, keyData);
        DialogResult = DialogResult.Cancel;
        Close();
        return true;
    }

    private void AttachRightClickCancel(Control root)
    {
        root.MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            DialogResult = DialogResult.Cancel;
            Close();
        };
        foreach (Control child in root.Controls)
        {
            if (child is not TextBox) AttachRightClickCancel(child);
        }
    }

    private static bool TryChannel(TextBox field, int max, out int channel) =>
        int.TryParse(field.Text, NumberStyles.None, CultureInfo.InvariantCulture, out channel) && channel <= max;

    private bool ChangedFrom(Color initial) => selected.ToArgb() != initial.ToArgb();

    private void Apply(Hsv value, TextBox? edited = null, Color? exact = null)
    {
        picker = value;
        Color next = exact ?? value.ToColor();
        bool changed = next.ToArgb() != selected.ToArgb();
        selected = next;
        Synchronize(edited);
        if (changed) preview(next);
    }

    private void Synchronize(TextBox? edited = null)
    {
        syncing = true;
        try
        {
            if (edited != hex) hex.Text = $"#{selected.R:X2}{selected.G:X2}{selected.B:X2}";
            if (edited != red) red.Text = Channel(useHsl ? (int)Math.Round(picker.Hue) : selected.R);
            if (edited != green) green.Text = Channel(useHsl ? (int)Math.Round(selected.GetSaturation() * 100) : selected.G);
            if (edited != blue) blue.Text = Channel(useHsl ? (int)Math.Round(selected.GetBrightness() * 100) : selected.B);
        }
        finally { syncing = false; }

        chip.BackColor = selected;
        plane.Value = picker;
        hue.Hue = picker.Hue;
        plane.Invalidate();
        hue.Invalidate();
    }

    private static string Channel(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static Color Blend(Color from, Color to, double amount) => Color.FromArgb(
        (int)Math.Round(from.R + (to.R - from.R) * amount),
        (int)Math.Round(from.G + (to.G - from.G) * amount),
        (int)Math.Round(from.B + (to.B - from.B) * amount));

    private readonly record struct Hsv(double Hue, double Saturation, double Value)
    {
        public static Hsv FromColor(Color color, double previousHue = 0)
        {
            double max = Math.Max(color.R, Math.Max(color.G, color.B));
            double min = Math.Min(color.R, Math.Min(color.G, color.B));
            double saturation = max > 0 ? (max - min) / max : 0;
            return new Hsv(saturation > 0 ? color.GetHue() : previousHue, saturation, max / 255);
        }

        public Color ToColor()
        {
            double section = Hue / 60;
            double chroma = Value * Saturation;
            double secondary = chroma * (1 - Math.Abs(section % 2 - 1));
            double gray = Value - chroma;
            var (r, g, b) = ((int)section % 6) switch
            {
                0 => (chroma, secondary, 0.0),
                1 => (secondary, chroma, 0.0),
                2 => (0.0, chroma, secondary),
                3 => (0.0, secondary, chroma),
                4 => (secondary, 0.0, chroma),
                _ => (chroma, 0.0, secondary),
            };
            int Byte(double channel) => Math.Clamp((int)Math.Round((channel + gray) * 255), 0, 255);
            return Color.FromArgb(Byte(r), Byte(g), Byte(b));
        }
    }

    private abstract class DragControl : Control
    {
        protected DragControl()
        {
            SetStyle(ControlStyles.Selectable | ControlStyles.ResizeRedraw, true);
            DoubleBuffered = true;
            TabStop = true;
            AccessibleRole = AccessibleRole.Slider;
        }

        protected abstract void UpdateFromPoint(Point at);

        protected double Horizontal(Point point) => Math.Clamp(point.X / (double)Math.Max(1, Width - 1), 0, 1);
        protected double Vertical(Point point) => Math.Clamp(point.Y / (double)Math.Max(1, Height - 1), 0, 1);

        protected override bool IsInputKey(Keys keyData) =>
            (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Focus();
            Capture = true;
            UpdateFromPoint(e.Location);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (Capture && e.Button == MouseButtons.Left) UpdateFromPoint(e.Location);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left) Capture = false;
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            Invalidate();
        }

        protected void Border(Graphics g)
        {
            if (Focused) ControlPaint.DrawFocusRectangle(g, ClientRectangle);
            else g.DrawRectangle(Pens.Gray, 0, 0, Width - 1, Height - 1);
        }
    }

    private sealed class ColorPlane : DragControl
    {
        public event Action<double, double>? Changed;
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        internal Hsv Value { get; set; }

        protected override void UpdateFromPoint(Point at) => Changed?.Invoke(Horizontal(at), 1 - Vertical(at));

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            double step = e.Shift ? 0.05 : 0.01;
            double saturation = Value.Saturation;
            double value = Value.Value;
            switch (e.KeyCode)
            {
                case Keys.Left: saturation -= step; break;
                case Keys.Right: saturation += step; break;
                case Keys.Up: value += step; break;
                case Keys.Down: value -= step; break;
                default: return;
            }
            e.Handled = true;
            Changed?.Invoke(Math.Clamp(saturation, 0, 1), Math.Clamp(value, 0, 1));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Rectangle bounds = ClientRectangle;
            Graphics g = e.Graphics;
            using var colors = new LinearGradientBrush(bounds, Color.White, new Hsv(Value.Hue, 1, 1).ToColor(), LinearGradientMode.Horizontal);
            using var shades = new LinearGradientBrush(bounds, Color.FromArgb(0, Color.Black), Color.Black, LinearGradientMode.Vertical);
            g.FillRectangle(colors, bounds);
            g.FillRectangle(shades, bounds);
            float x = (float)(Value.Saturation * (Width - 1));
            float y = (float)((1 - Value.Value) * (Height - 1));
            float radius = 4 * DeviceDpi / 96f;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var outline = new Pen(Color.Black, 3 * DeviceDpi / 96f);
            using var highlight = new Pen(Color.White, 1.5f * DeviceDpi / 96f);
            foreach (Pen pen in new[] { outline, highlight })
                g.DrawEllipse(pen, x - radius, y - radius, 2 * radius, 2 * radius);
            g.SmoothingMode = SmoothingMode.None;
            Border(g);
        }
    }

    private sealed class HueStrip : DragControl
    {
        private const double LastHue = 359.999;
        private static readonly ColorBlend Spectrum = new()
        {
            Colors = [Color.Red, Color.Yellow, Color.Lime, Color.Cyan, Color.Blue, Color.Magenta, Color.Red],
            Positions = [0, 1f / 6, 2f / 6, 3f / 6, 4f / 6, 5f / 6, 1],
        };
        public event Action<double>? Changed;
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        internal double Hue { get; set; }

        protected override void UpdateFromPoint(Point at) => Changed?.Invoke(Horizontal(at) * LastHue);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            int move;
            switch (e.KeyCode)
            {
                case Keys.Left:
                case Keys.Down: move = -1; break;
                case Keys.Right:
                case Keys.Up: move = 1; break;
                default: return;
            }
            e.Handled = true;
            Changed?.Invoke(Math.Clamp(Hue + move * (e.Shift ? 10 : 1), 0, LastHue));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Rectangle bounds = ClientRectangle;
            Graphics g = e.Graphics;
            using var brush = new LinearGradientBrush(bounds, Color.Red, Color.Red, LinearGradientMode.Horizontal)
                { InterpolationColors = Spectrum };
            g.FillRectangle(brush, bounds);
            float x = (float)(Hue / LastHue * (Width - 1));
            using var outline = new Pen(Color.Black, 4 * DeviceDpi / 96f);
            using var highlight = new Pen(Color.White, 2 * DeviceDpi / 96f);
            g.DrawLine(outline, x, 1, x, Height - 2);
            g.DrawLine(highlight, x, 1, x, Height - 2);
            Border(g);
        }
    }
}
