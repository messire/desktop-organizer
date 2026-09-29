using System.Drawing.Drawing2D;
using DesktopOrganizer.Services.Abstractions;

// Color/Pen/Point/Size/FontFamily are ambiguous between System.Drawing and System.Windows(.Media) — both are
// global usings in this project (GlobalUsings.cs). This file only ever deals with the GDI/WinForms types.
using Color = System.Drawing.Color;
using FontFamily = System.Drawing.FontFamily;
using FontStyle = System.Drawing.FontStyle;
using Pen = System.Drawing.Pen;
using Point = System.Drawing.Point;
using PointF = System.Drawing.PointF;
using Size = System.Drawing.Size;

namespace DesktopOrganizer.Services.Providers;

/// <summary>
/// Windows 11-styled owner-drawn renderer of the tray context menu (design system 1.x,
/// docs/PLANS/design-system-1x.md §2.6/§2.9 variant A, ADR-047 D-10): flat <see cref="TrayMenuPalette.Background"/>
/// surface with a 1 px border, no Office gradient image column, a rounded highlight inset from the popup
/// edge, a checkmark glyph without a box, a chevron submenu arrow, and 1 px separators. Every metric is a
/// DIP constant scaled by the owning strip's <see cref="Control.DeviceDpi"/> at paint time (per-monitor
/// DPI); text is drawn with GDI (<see cref="TextRenderer"/>), like the stock WinForms renderers.
/// </summary>
/// <remarks>
/// A renderer instance is assigned by <see cref="TrayIconManager.SetMenuPalette"/> to the root strip AND to
/// every submenu <see cref="ToolStripDropDown"/> — WinForms does not propagate a custom renderer to
/// drop-downs. The only GDI resources it keeps between paints are the icon-font glyph <see cref="Font"/>s,
/// cached per pixel size (review L-7: two sizes per DPI, so the cache stays tiny); brushes and pens are
/// created and disposed per paint call. The owner disposes a renderer once it is replaced
/// (<see cref="TrayIconManager"/> on every palette change and on its own disposal).
/// </remarks>
internal sealed class TrayMenuRenderer : ToolStripRenderer, IDisposable
{
    /// <summary>Menu item height (§2.6: 30 DIP).</summary>
    internal const double ItemHeightDip = 30;

    /// <summary>Horizontal inset of the rounded highlight from the item bounds (§2.6: 4 DIP).</summary>
    internal const double HighlightInsetDip = 4;

    /// <summary>Horizontal inset of a separator line from the popup edge (§2.6: 4 DIP).</summary>
    internal const double SeparatorInsetDip = 4;

    /// <summary>Checkmark glyph size (Segoe icon font, pixels at 96 DPI).</summary>
    internal const double CheckGlyphSizeDip = 12;

    /// <summary>Submenu chevron glyph size (Segoe icon font, pixels at 96 DPI).</summary>
    internal const double ArrowGlyphSizeDip = 10;

    /// <summary>The Segoe icon-font checkmark (<c>U+E73E</c>) — same glyph as the WPF menus' <c>Icon.Glyph.Checkmark</c>.</summary>
    internal const string CheckmarkGlyph = "";

    /// <summary>The Segoe icon-font chevron (<c>U+E76C</c>) — same glyph as the WPF menus' <c>Icon.Glyph.SubmenuArrow</c>.</summary>
    internal const string ChevronGlyph = "";

    private const int BaseDpi = 96;

    private const TextFormatFlags GlyphFormat =
        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
        TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

    // Windows 11 ships "Segoe Fluent Icons", Windows 10 "Segoe MDL2 Assets" (same code points for both
    // glyphs used here). Resolved once per process; null = neither installed → vector fallback.
    private static readonly Lazy<string?> IconFontFamilyName = new(ResolveIconFontFamily);

    // Glyph fonts keyed by pixel size (UI thread only — painting is single-threaded). Emptied by Dispose.
    private readonly Dictionary<int, Font> _glyphFonts = [];
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrayMenuRenderer"/> class.
    /// </summary>
    /// <param name="palette">The colours to draw with.</param>
    public TrayMenuRenderer(TrayMenuPalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);

        Palette = palette;
        BackgroundColor = ToColor(palette.Background);
        BorderColor = ToColor(palette.Border);
        TextColor = ToColor(palette.Text);
        TextDisabledColor = ToColor(palette.TextDisabled);
        HoverFillColor = ToColor(palette.HoverFill);
        SeparatorColor = ToColor(palette.Separator);
        GlyphColor = ToColor(palette.Glyph);
    }

    /// <summary>Gets the palette this renderer draws with.</summary>
    internal TrayMenuPalette Palette { get; }

    /// <summary>Gets the menu surface colour.</summary>
    internal Color BackgroundColor { get; }

    /// <summary>Gets the popup border colour.</summary>
    internal Color BorderColor { get; }

    /// <summary>Gets the enabled item text colour.</summary>
    internal Color TextColor { get; }

    /// <summary>Gets the disabled item text (and disabled glyph) colour.</summary>
    internal Color TextDisabledColor { get; }

    /// <summary>Gets the highlight fill colour.</summary>
    internal Color HoverFillColor { get; }

    /// <summary>Gets the separator colour.</summary>
    internal Color SeparatorColor { get; }

    /// <summary>Gets the enabled glyph (checkmark/chevron) colour.</summary>
    internal Color GlyphColor { get; }

    /// <summary>Gets the number of glyph fonts currently cached (test seam; 0 after <see cref="Dispose"/>).</summary>
    internal int CachedGlyphFontCount => _glyphFonts.Count;

    /// <summary>Gets a value indicating whether <see cref="Dispose"/> has run (test seam).</summary>
    internal bool IsDisposed => _disposed;

    /// <summary>
    /// Releases the cached glyph fonts. Idempotent. A strip that still paints with a disposed renderer (it
    /// should not — the owner reassigns every strip first) keeps working with per-paint fonts.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var font in _glyphFonts.Values)
        {
            font.Dispose();
        }

        _glyphFonts.Clear();
    }

    /// <summary>Converts a palette ARGB value (<c>0xAARRGGBB</c>) into a GDI colour.</summary>
    /// <param name="argb">The palette value.</param>
    /// <returns>The GDI colour.</returns>
    internal static Color ToColor(uint argb) => Color.FromArgb(unchecked((int)argb));

    /// <summary>Scales a DIP length to whole device pixels at <paramref name="dpi"/> (≤ 0 = 96).</summary>
    /// <param name="dip">The length in DIPs.</param>
    /// <param name="dpi">The device DPI.</param>
    /// <returns>The length in pixels, rounded to the nearest pixel.</returns>
    internal static int ScaleToPixels(double dip, int dpi)
        => (int)Math.Round(dip * (dpi > 0 ? dpi : BaseDpi) / BaseDpi, MidpointRounding.AwayFromZero);

    /// <summary>
    /// The vertical item padding that makes a menu item <see cref="ItemHeightDip"/> tall at
    /// <paramref name="dpi"/>, given its content height (the taller of the text line and the 16-DIP
    /// check/image column). Horizontal padding is kept from <paramref name="current"/>.
    /// </summary>
    /// <param name="dpi">The owning strip's device DPI.</param>
    /// <param name="textHeightPx">The height of one text line of the item font, in device pixels.</param>
    /// <param name="current">The item's current padding (its left/right are preserved).</param>
    /// <returns>The padding to assign.</returns>
    internal static Padding ItemPadding(int dpi, int textHeightPx, Padding current)
    {
        var content = Math.Max(textHeightPx, ScaleToPixels(16, dpi));
        var free = Math.Max(0, ScaleToPixels(ItemHeightDip, dpi) - content);
        var top = free / 2;
        return new Padding(current.Left, top, current.Right, free - top);
    }

    /// <summary>
    /// Applies the §2.6 row metrics (<see cref="ItemPadding"/>) to every menu item of <paramref name="strip"/>
    /// at its current <see cref="Control.DeviceDpi"/>. Idempotent; called before every show (the DPI can
    /// change with the monitor the taskbar is on).
    /// </summary>
    /// <param name="strip">The strip (root context menu or a submenu drop-down).</param>
    internal static void ApplyLayoutMetrics(ToolStrip strip)
    {
        ArgumentNullException.ThrowIfNull(strip);

        var dpi = strip.DeviceDpi;
        var textHeight = TextRenderer.MeasureText("Ag", strip.Font).Height;
        foreach (ToolStripItem item in strip.Items)
        {
            if (item is not ToolStripMenuItem) continue;

            var padding = ItemPadding(dpi, textHeight, item.Padding);
            if (item.Padding != padding)
            {
                item.Padding = padding;
            }
        }
    }

    /// <summary>
    /// The rounded highlight rectangle of an item of <paramref name="itemSize"/>, in item coordinates:
    /// inset <see cref="HighlightInsetDip"/> horizontally, full height (rows are 30 DIP, §2.6).
    /// </summary>
    /// <param name="itemSize">The item size in pixels.</param>
    /// <param name="dpi">The device DPI.</param>
    /// <returns>The highlight rectangle; empty when the item is too narrow.</returns>
    internal static Rectangle HighlightBounds(Size itemSize, int dpi)
    {
        var inset = ScaleToPixels(HighlightInsetDip, dpi);
        var width = itemSize.Width - (2 * inset);
        return width <= 0 || itemSize.Height <= 0 ? Rectangle.Empty : new Rectangle(inset, 0, width, itemSize.Height);
    }

    /// <summary>
    /// Whether <paramref name="item"/> is painted with the highlight: enabled and hovered/keyboard-selected,
    /// or the owner of the currently open submenu (<see cref="ToolStripItem.Pressed"/>).
    /// </summary>
    /// <param name="item">The item being painted.</param>
    /// <returns><see langword="true"/> when the highlight is drawn.</returns>
    internal static bool ShouldHighlight(ToolStripItem item) => item.Enabled && (item.Selected || item.Pressed);

    /// <summary>The text colour of <paramref name="item"/> (disabled items use <see cref="TextDisabledColor"/>).</summary>
    /// <param name="item">The item.</param>
    /// <returns>The colour.</returns>
    internal Color ItemTextColor(ToolStripItem item) => item.Enabled ? TextColor : TextDisabledColor;

    /// <summary>The glyph colour of <paramref name="item"/> (disabled items use <see cref="TextDisabledColor"/>).</summary>
    /// <param name="item">The item.</param>
    /// <returns>The colour.</returns>
    internal Color ItemGlyphColor(ToolStripItem item) => item.Enabled ? GlyphColor : TextDisabledColor;

    /// <summary>Draws a Segoe icon-font glyph centred in <paramref name="rect"/>.</summary>
    /// <param name="graphics">The target surface.</param>
    /// <param name="glyph">The glyph string.</param>
    /// <param name="rect">The cell to centre the glyph in.</param>
    /// <param name="sizePx">The font size, in device pixels.</param>
    /// <param name="color">The glyph colour.</param>
    /// <returns><see langword="false"/> when no icon font is installed (the caller falls back).</returns>
    internal bool DrawGlyph(Graphics graphics, string glyph, Rectangle rect, int sizePx, Color color)
    {
        var family = IconFontFamilyName.Value;
        if (family is null || sizePx <= 0) return false;

        if (_disposed)
        {
            using var transient = new Font(family, sizePx, FontStyle.Regular, GraphicsUnit.Pixel);
            TextRenderer.DrawText(graphics, glyph, transient, rect, color, GlyphFormat);
            return true;
        }

        if (!_glyphFonts.TryGetValue(sizePx, out var font))
        {
            font = new Font(family, sizePx, FontStyle.Regular, GraphicsUnit.Pixel);
            _glyphFonts.Add(sizePx, font);
        }

        TextRenderer.DrawText(graphics, glyph, font, rect, color, GlyphFormat);
        return true;
    }

    /// <inheritdoc />
    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(BackgroundColor);
        e.Graphics.FillRectangle(brush, new Rectangle(Point.Empty, e.ToolStrip.Size));
    }

    /// <inheritdoc />
    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        var size = e.ToolStrip.Size;
        if (size.Width <= 0 || size.Height <= 0) return;

        using var pen = new Pen(BorderColor, 1f);
        e.Graphics.DrawRectangle(pen, 0, 0, size.Width - 1, size.Height - 1);
    }

    /// <inheritdoc />
    /// <remarks>Intentionally empty — the Office-style gradient column under the check/image margin is gone (§2.9).</remarks>
    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
        // No separate image-margin surface: the background already covers it.
    }

    /// <inheritdoc />
    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!ShouldHighlight(e.Item)) return;

        var dpi = DpiOf(e.Item);
        var bounds = HighlightBounds(e.Item.Size, dpi);
        if (bounds.IsEmpty) return;

        var radius = ScaleToPixels(Palette.CornerRadius, dpi);
        using var brush = new SolidBrush(HoverFillColor);
        if (radius <= 0)
        {
            e.Graphics.FillRectangle(brush, bounds);
            return;
        }

        var previous = e.Graphics.SmoothingMode;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        try
        {
            using var path = RoundedRectangle(bounds, radius);
            e.Graphics.FillPath(brush, path);
        }
        finally
        {
            e.Graphics.SmoothingMode = previous;
        }
    }

    /// <inheritdoc />
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text)) return;

        var font = e.TextFont ?? e.Item.Font;
        TextRenderer.DrawText(e.Graphics, e.Text, font, e.TextRectangle, ItemTextColor(e.Item), e.TextFormat);
    }

    /// <inheritdoc />
    /// <remarks>A bare checkmark glyph — no box, no background (Windows 11 menus, §2.9).</remarks>
    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var rect = e.ImageRectangle;
        if (rect.Width <= 0 || rect.Height <= 0) return;

        var dpi = DpiOf(e.Item);
        var color = ItemGlyphColor(e.Item);
        if (!DrawGlyph(e.Graphics, CheckmarkGlyph, rect, ScaleToPixels(CheckGlyphSizeDip, dpi), color))
        {
            DrawVectorCheckmark(e.Graphics, rect, color, dpi);
        }
    }

    /// <inheritdoc />
    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        var color = e.Item is null ? GlyphColor : ItemGlyphColor(e.Item);
        var dpi = e.Item is null ? BaseDpi : DpiOf(e.Item);
        if (e.Direction == ArrowDirection.Right &&
            DrawGlyph(e.Graphics, ChevronGlyph, e.ArrowRectangle, ScaleToPixels(ArrowGlyphSizeDip, dpi), color))
        {
            return;
        }

        // No icon font (or a direction the tray never uses): the stock triangle, in the palette colour.
        e.ArrowColor = color;
        base.OnRenderArrow(e);
    }

    /// <inheritdoc />
    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        if (e.Vertical) return;

        var dpi = DpiOf(e.Item);
        var inset = ScaleToPixels(SeparatorInsetDip, dpi);
        var size = e.Item.Size;
        var width = size.Width - (2 * inset);
        if (width <= 0 || size.Height <= 0) return;

        using var brush = new SolidBrush(SeparatorColor);
        e.Graphics.FillRectangle(brush, inset, size.Height / 2, width, 1);
    }

    private static int DpiOf(ToolStripItem item) => item.Owner?.DeviceDpi ?? BaseDpi;

    private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        var diameter = Math.Min(2 * radius, Math.Min(bounds.Width, bounds.Height));
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void DrawVectorCheckmark(Graphics graphics, Rectangle rect, Color color, int dpi)
    {
        var size = Math.Min(Math.Min(rect.Width, rect.Height), ScaleToPixels(CheckGlyphSizeDip, dpi));
        var left = rect.Left + ((rect.Width - size) / 2f);
        var top = rect.Top + ((rect.Height - size) / 2f);
        PointF[] points =
        [
            new(left + (size * 0.15f), top + (size * 0.55f)),
            new(left + (size * 0.4f), top + (size * 0.8f)),
            new(left + (size * 0.85f), top + (size * 0.25f)),
        ];

        var previous = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        try
        {
            using var pen = new Pen(color, Math.Max(1f, ScaleToPixels(1.5, dpi)));
            graphics.DrawLines(pen, points);
        }
        finally
        {
            graphics.SmoothingMode = previous;
        }
    }

    private static string? ResolveIconFontFamily()
    {
        foreach (var name in (string[])["Segoe Fluent Icons", "Segoe MDL2 Assets"])
        {
            try
            {
                using var family = new FontFamily(name);
                return family.Name;
            }
            catch (ArgumentException)
            {
                // Not installed on this Windows build — try the next one.
            }
        }

        return null;
    }
}
