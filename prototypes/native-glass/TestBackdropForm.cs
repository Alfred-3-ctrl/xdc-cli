using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

namespace Xdc.NativeGlass
{
    internal sealed class TestBackdropForm : Form
    {
        private readonly TestCanvas canvas;
        private readonly TrackBar radius;
        private readonly CheckBox effect;
        private readonly CheckBox animate;
        private readonly Timer timer;
        private readonly Label status;
        private Rectangle publishedBounds;

        public event Action<float>? RadiusChanged;
        public event Action<bool>? EffectChanged;
        public event Action<Rectangle>? GlassBoundsChanged;

        public TestBackdropForm()
        {
            Text = "实时玻璃对照";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(840, 560);
            MinimumSize = new Size(640, 420);
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Microsoft YaHei UI", 9F);
            BackColor = Color.FromArgb(245, 247, 248);
            ForeColor = Color.FromArgb(26, 35, 40);

            var layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.Padding = new Padding(16);
            layout.ColumnCount = 2;
            layout.RowCount = 1;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 236));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(layout);

            canvas = new TestCanvas();
            canvas.Dock = DockStyle.Fill;
            canvas.Margin = Padding.Empty;
            canvas.SizeChanged += delegate { PublishGlassBounds(); };
            canvas.LocationChanged += delegate { PublishGlassBounds(); };
            layout.Controls.Add(canvas, 0, 0);

            var panel = new TableLayoutPanel();
            panel.Dock = DockStyle.Top;
            panel.AutoSize = true;
            panel.Padding = new Padding(18, 8, 0, 0);
            panel.ColumnCount = 1;
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.Controls.Add(panel, 1, 0);

            var heading = LabelOf("背景对照", new Padding(0, 0, 0, 10));
            heading.Font = new Font(Font.FontFamily, 15F, FontStyle.Bold);
            AddRow(panel, heading);
            var guide = LabelOf("观察玻璃内外的移动图案，对照模糊是否实时更新，并检查四个角。", new Padding(0, 0, 8, 20));
            guide.MaximumSize = new Size(190, 0);
            AddRow(panel, guide);

            var radiusText = LabelOf("圆角半径 30 DIP", new Padding(0, 0, 0, 4));
            AddRow(panel, radiusText);
            radius = new TrackBar();
            radius.AccessibleName = "圆角半径";
            radius.Minimum = 16;
            radius.Maximum = 36;
            radius.Value = 30;
            radius.TickFrequency = 4;
            radius.Dock = DockStyle.Fill;
            radius.Margin = new Padding(0, 0, 4, 12);
            radius.ValueChanged += delegate
            {
                radiusText.Text = $"圆角半径 {radius.Value} DIP";
                RadiusChanged?.Invoke(radius.Value);
            };
            AddRow(panel, radius);

            effect = new CheckBox();
            effect.AutoSize = true;
            effect.Margin = new Padding(0, 0, 0, 10);
            effect.Text = "实时模糊";
            effect.Checked = true;
            effect.CheckedChanged += delegate
            {
                EffectChanged?.Invoke(effect.Checked);
            };
            AddRow(panel, effect);

            animate = new CheckBox();
            animate.AutoSize = true;
            animate.Margin = new Padding(0, 0, 0, 12);
            animate.Text = "移动测试图案";
            animate.Checked = true;
            AddRow(panel, animate);

            var pattern = new Button();
            pattern.AutoSize = true;
            pattern.Margin = new Padding(0);
            pattern.Text = "切换图案";
            pattern.Click += delegate { canvas.SwitchPattern(); };
            AddRow(panel, pattern);

            status = LabelOf(String.Empty, new Padding(0, 14, 0, 0));
            status.MaximumSize = new Size(190, 0);
            status.Visible = false;
            AddRow(panel, status);

            timer = new Timer();
            timer.Interval = 40;
            timer.Tick += delegate { canvas.Advance(); };
            animate.CheckedChanged += delegate { UpdateAnimation(); };
        }

        private Rectangle CanvasBounds
        {
            get
            {
                if (canvas == null || !Visible || WindowState == FormWindowState.Minimized)
                    return Rectangle.Empty;
                return canvas.RectangleToScreen(canvas.ClientRectangle);
            }
        }

        private Rectangle GlassBounds
        {
            get
            {
                Rectangle canvasBounds = CanvasBounds;
                if (canvasBounds.Width < 32 || canvasBounds.Height < 32) return Rectangle.Empty;
                int width = canvasBounds.Width * 7 / 10;
                int height = canvasBounds.Height * 3 / 4;
                return new Rectangle(
                    canvasBounds.Left + (canvasBounds.Width - width) / 2,
                    canvasBounds.Top + (canvasBounds.Height - height) / 2,
                    width,
                    height
                );
            }
        }

        public void SetStatus(string message)
        {
            status.Text = message ?? String.Empty;
            status.Visible = !String.IsNullOrWhiteSpace(message);
        }

        protected override void OnLocationChanged(EventArgs e)
        {
            base.OnLocationChanged(e);
            PublishGlassBounds();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            PublishGlassBounds();
            UpdateAnimation();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            PublishGlassBounds();
            UpdateAnimation();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            PublishGlassBounds();
            UpdateAnimation();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && timer != null) timer.Dispose();
            base.Dispose(disposing);
        }

        private void UpdateAnimation()
        {
            if (timer != null && !IsDisposed && !Disposing)
                timer.Enabled = Visible && WindowState != FormWindowState.Minimized && animate.Checked;
        }

        private void PublishGlassBounds()
        {
            if (IsDisposed || Disposing) return;
            Rectangle bounds = GlassBounds;
            if (bounds == publishedBounds) return;
            publishedBounds = bounds;
            GlassBoundsChanged?.Invoke(bounds);
        }

        private static Label LabelOf(string text, Padding margin)
        {
            return new Label { Text = text, AutoSize = true, Margin = margin };
        }

        private static void AddRow(TableLayoutPanel panel, Control control)
        {
            int row = panel.RowCount++;
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.Controls.Add(control, 0, row);
        }

        private sealed class TestCanvas : Control
        {
            private static readonly Color[] Palette = {
                Color.FromArgb(20, 46, 62),
                Color.FromArgb(123, 221, 206),
                Color.FromArgb(245, 170, 95),
                Color.FromArgb(82, 76, 134),
            };
            private bool stripes;
            private int offset;

            public TestCanvas()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                    ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Opaque, true);
            }

            public void SwitchPattern()
            {
                stripes = !stripes;
                Invalidate();
            }

            public void Advance()
            {
                offset = (offset + 2) % 100000;
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                int cell = stripes ? Math.Clamp(Width / 58, 6, 14) : Math.Clamp(Width / 6, 36, 96);
                // Shift the pattern across the stationary glass window.
                int period = cell * Palette.Length;
                int shift = offset % period;
                g.Clear(Palette[0]);
                using (var brush = new SolidBrush(Color.Black))
                {
                    if (stripes)
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        int stripe = 0;
                        for (int x = -period + shift - Height; x < Width; x += cell, stripe++)
                        {
                            brush.Color = Palette[stripe % Palette.Length];
                            g.FillPolygon(brush, new[] {
                                new Point(x, 0), new Point(x + cell, 0),
                                new Point(x + cell + Height, Height), new Point(x + Height, Height)
                            });
                        }
                        return;
                    }

                    for (int row = 0, y = 0; y < Height; y += cell, row++)
                    {
                        for (int col = 0, x = -period + shift; x < Width; x += cell, col++)
                        {
                            brush.Color = Palette[(row + col) % Palette.Length];
                            g.FillRectangle(brush, x, y, cell, cell);
                            brush.Color = (row + col) % 2 == 0 ? Color.White : Color.FromArgb(26, 35, 40);
                            g.FillRectangle(brush, x + cell / 2 - 1, y + cell / 2 - 7, 2, 14);
                            g.FillRectangle(brush, x + cell / 2 - 7, y + cell / 2 - 1, 14, 2);
                        }
                    }
                }
            }
        }
    }
}
