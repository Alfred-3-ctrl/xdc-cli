namespace Xdc.NativeDesktop;

internal sealed class GlassListRow : GlassButton
{
    private readonly string title;
    private readonly string detail;
    private readonly string trail;
    private readonly Color secondary;
    private Font? strongFont;
    private Font? smallFont;

    public GlassListRow(string title, string? detail, string? trail, Color secondary)
    {
        this.title = OneLine(title);
        this.detail = OneLine(detail);
        this.trail = OneLine(trail);
        this.secondary = secondary;

        Text = string.IsNullOrEmpty(detail) ? title : title + Environment.NewLine + detail;
        if (!string.IsNullOrEmpty(trail)) Text += "   " + trail;
        AccessibleName = title;
        AccessibleDescription = string.Join("，", new[] { this.detail, this.trail }.Where(text => text.Length > 0));
    }

    internal override void PaintText(Graphics graphics, Rectangle bounds, bool keyed)
    {
        var content = Rectangle.FromLTRB(bounds.Left + Padding.Left, bounds.Top + Padding.Top,
            bounds.Right - Padding.Right, bounds.Bottom - Padding.Bottom);
        if (content.Width <= 0 || content.Height <= 0) return;

        Font heading = Font.Bold || !Font.FontFamily.IsStyleAvailable(FontStyle.Bold)
            ? Font
            : strongFont ??= new Font(Font, Font.Style | FontStyle.Bold);
        Font caption = smallFont ??= new Font(Font.FontFamily, Font.Size * .86f, Font.Style, Font.Unit);
        Color titleInk = Enabled ? ForeColor : ControlPaint.Dark(ForeColor, .24f);
        Color detailInk = Enabled ? secondary : ControlPaint.Dark(secondary, .24f);

        if (detail.Length == 0)
        {
            DrawInline(graphics, content, title, heading, titleInk, trail, caption, detailInk, keyed);
            return;
        }

        int headingHeight = (int)Math.Ceiling(heading.GetHeight(graphics));
        int captionHeight = (int)Math.Ceiling(caption.GetHeight(graphics));
        int spacing = Math.Max(1, headingHeight / 10);
        int y = content.Top + Math.Max(0, (content.Height - headingHeight - captionHeight - spacing) / 2);
        DrawInline(graphics, new Rectangle(content.X, y, content.Width, headingHeight),
            title, heading, titleInk, "", caption, detailInk, keyed);
        y += headingHeight + spacing;
        DrawInline(graphics, new Rectangle(content.X, y, content.Width, captionHeight),
            detail, caption, detailInk, trail, caption, detailInk, keyed);
    }

    private void DrawInline(Graphics graphics, Rectangle line, string text, Font font, Color ink,
        string suffix, Font suffixFont, Color suffixInk, bool keyed)
    {
        bool rightToLeft = RightToLeft == RightToLeft.Yes;
        var alignment = rightToLeft ? ContentAlignment.MiddleRight : ContentAlignment.MiddleLeft;
        if (suffix.Length == 0 || line.Width < 6)
        {
            GlassControlPainting.DrawText(graphics, text, font, ink, line, Padding.Empty, alignment,
                ellipsis: true, keyed, rightToLeft);
            return;
        }

        int suffixWidth = Math.Clamp((int)Math.Ceiling(graphics.MeasureString(suffix, suffixFont).Width),
            1, line.Width / 3);
        int gap = Math.Min(line.Width / 6, Math.Max(2, (int)Math.Round(suffixFont.GetHeight(graphics) * .35f)));
        int textWidth = Math.Clamp((int)Math.Ceiling(graphics.MeasureString(text, font).Width),
            1, line.Width - suffixWidth - gap);
        var textBounds = new Rectangle(rightToLeft ? line.Right - textWidth : line.Left,
            line.Top, textWidth, line.Height);
        var suffixBounds = new Rectangle(rightToLeft ? textBounds.Left - gap - suffixWidth : textBounds.Right + gap,
            line.Top, suffixWidth, line.Height);

        GlassControlPainting.DrawText(graphics, text, font, ink, textBounds, Padding.Empty, alignment,
            ellipsis: true, keyed, rightToLeft);
        GlassControlPainting.DrawText(graphics, suffix, suffixFont, suffixInk, suffixBounds, Padding.Empty, alignment,
            ellipsis: true, keyed, rightToLeft);
    }

    private static string OneLine(string? value) => value?.ReplaceLineEndings(" ").Trim() ?? "";

    protected override void OnFontChanged(EventArgs e)
    {
        ReleaseFonts();
        base.OnFontChanged(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) ReleaseFonts();
        base.Dispose(disposing);
    }

    private void ReleaseFonts()
    {
        strongFont?.Dispose();
        smallFont?.Dispose();
        strongFont = smallFont = null;
    }
}
