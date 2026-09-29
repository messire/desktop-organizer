using System.Windows.Forms;
using DesktopOrganizer.Services.Abstractions;
using Bitmap = System.Drawing.Bitmap;
using DrawingColor = System.Drawing.Color;
using Font = System.Drawing.Font;
using Graphics = System.Drawing.Graphics;
using Rectangle = System.Drawing.Rectangle;
using Size = System.Drawing.Size;

namespace DesktopOrganizer.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="TrayMenuRenderer"/> (design system 1.x, docs/PLANS/design-system-1x.md
/// §2.6/§2.9 variant A, phase 6.4, T-11): palette → drawing colours, the §2.6 metrics at 96 and 144 DPI,
/// and pixel smoke tests of each owner-drawn part rendered into an off-screen <see cref="Bitmap"/> through
/// the public <c>ToolStripRenderer.Draw*</c> entry points on an isolated, never-shown strip.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class TrayMenuRendererTests
{
    // Distinct, easily told-apart colours so a pixel can only match the part that was supposed to draw it.
    private static readonly TrayMenuPalette Palette = new(
        Background: 0xFFF9F9F9,
        Border: 0xFF808080,
        Text: 0xFF1B1B1B,
        TextDisabled: 0xFF8A8A8A,
        HoverFill: 0xFFD0E0F0,
        Separator: 0xFF00A000,
        Glyph: 0xFF0000C0,
        CornerRadius: 4);

    /// <summary>Every palette slot maps to the matching GDI colour, alpha included.</summary>
    [Fact]
    public void Constructor_MapsEveryPaletteSlot()
    {
        using var sut = new TrayMenuRenderer(Palette);

        Assert.Same(Palette, sut.Palette);
        Assert.Equal(DrawingColor.FromArgb(0xFF, 0xF9, 0xF9, 0xF9), sut.BackgroundColor);
        Assert.Equal(DrawingColor.FromArgb(0xFF, 0x80, 0x80, 0x80), sut.BorderColor);
        Assert.Equal(DrawingColor.FromArgb(0xFF, 0x1B, 0x1B, 0x1B), sut.TextColor);
        Assert.Equal(DrawingColor.FromArgb(0xFF, 0x8A, 0x8A, 0x8A), sut.TextDisabledColor);
        Assert.Equal(DrawingColor.FromArgb(0xFF, 0xD0, 0xE0, 0xF0), sut.HoverFillColor);
        Assert.Equal(DrawingColor.FromArgb(0xFF, 0x00, 0xA0, 0x00), sut.SeparatorColor);
        Assert.Equal(DrawingColor.FromArgb(0xFF, 0x00, 0x00, 0xC0), sut.GlyphColor);
        Assert.Equal(DrawingColor.FromArgb(0x80, 0x10, 0x20, 0x30), TrayMenuRenderer.ToColor(0x80102030));
    }

    /// <summary>A null palette is rejected.</summary>
    [Fact]
    public void Constructor_NullPalette_Throws()
        => Assert.Throws<ArgumentNullException>(() => new TrayMenuRenderer(null!));

    /// <summary>DIP → pixel scaling at 100 % and 150 % (and the 96-DPI fallback for an unknown DPI).</summary>
    /// <param name="dip">Length in DIPs.</param>
    /// <param name="dpi">Device DPI.</param>
    /// <param name="expected">Expected pixels.</param>
    [Theory]
    [InlineData(30, 96, 30)]
    [InlineData(30, 144, 45)]
    [InlineData(4, 96, 4)]
    [InlineData(4, 144, 6)]
    [InlineData(12, 120, 15)]
    [InlineData(30, 0, 30)]
    public void ScaleToPixels_ScalesByDpi(double dip, int dpi, int expected)
        => Assert.Equal(expected, TrayMenuRenderer.ScaleToPixels(dip, dpi));

    /// <summary>
    /// §2.6: the vertical padding makes a row 30 DIP tall — content + padding = 30 px at 96 DPI and 45 px at
    /// 144 DPI — while the horizontal padding is preserved.
    /// </summary>
    /// <param name="dpi">Device DPI.</param>
    /// <param name="textHeight">Text line height in pixels.</param>
    /// <param name="expectedTop">Expected top padding.</param>
    /// <param name="expectedBottom">Expected bottom padding.</param>
    [Theory]
    [InlineData(96, 15, 7, 7)]
    [InlineData(96, 16, 7, 7)]
    [InlineData(96, 17, 6, 7)]
    [InlineData(144, 23, 10, 11)]
    [InlineData(144, 25, 10, 10)]
    [InlineData(96, 40, 0, 0)]
    public void ItemPadding_MakesRowsItemHeightTall(int dpi, int textHeight, int expectedTop, int expectedBottom)
    {
        var padding = TrayMenuRenderer.ItemPadding(dpi, textHeight, new Padding(3, 1, 5, 1));

        Assert.Equal(3, padding.Left);
        Assert.Equal(5, padding.Right);
        Assert.Equal(expectedTop, padding.Top);
        Assert.Equal(expectedBottom, padding.Bottom);
        var content = Math.Max(textHeight, TrayMenuRenderer.ScaleToPixels(16, dpi));
        var rowHeight = TrayMenuRenderer.ScaleToPixels(TrayMenuRenderer.ItemHeightDip, dpi);
        if (content < rowHeight)
        {
            Assert.Equal(rowHeight, content + padding.Vertical);
        }
    }

    /// <summary>The 16-DIP check column is the minimum content height (a tiny font does not shrink the row below it).</summary>
    [Fact]
    public void ItemPadding_TinyFont_UsesCheckColumnAsContentHeight()
    {
        var padding = TrayMenuRenderer.ItemPadding(144, 10, Padding.Empty);

        Assert.Equal(45 - 24, padding.Vertical);
    }

    /// <summary>The highlight is inset 4 DIP horizontally and spans the full row height, at 96 and 144 DPI.</summary>
    [Fact]
    public void HighlightBounds_InsetFourDip_ScaledByDpi()
    {
        Assert.Equal(new Rectangle(4, 0, 192, 30), TrayMenuRenderer.HighlightBounds(new Size(200, 30), 96));
        Assert.Equal(new Rectangle(6, 0, 288, 45), TrayMenuRenderer.HighlightBounds(new Size(300, 45), 144));
        Assert.True(TrayMenuRenderer.HighlightBounds(new Size(8, 30), 96).IsEmpty);
    }

    /// <summary>Enabled items use the text/glyph colours, disabled items the disabled colour for both.</summary>
    [Fact]
    public void ItemColors_FollowEnabledState()
    {
        using var sut = new TrayMenuRenderer(Palette);
        using var enabled = new ToolStripMenuItem("a");
        using var disabled = new ToolStripMenuItem("b") { Enabled = false };

        Assert.Equal(sut.TextColor, sut.ItemTextColor(enabled));
        Assert.Equal(sut.GlyphColor, sut.ItemGlyphColor(enabled));
        Assert.Equal(sut.TextDisabledColor, sut.ItemTextColor(disabled));
        Assert.Equal(sut.TextDisabledColor, sut.ItemGlyphColor(disabled));
    }

    /// <summary>Only an enabled, selected (or submenu-open) item is highlighted.</summary>
    [Fact]
    public void ShouldHighlight_OnlyEnabledSelectedItems()
    {
        using var idle = new ToolStripMenuItem("a");
        using var selected = new SelectedMenuItem();
        using var selectedDisabled = new SelectedMenuItem { Enabled = false };

        Assert.False(TrayMenuRenderer.ShouldHighlight(idle));
        Assert.True(TrayMenuRenderer.ShouldHighlight(selected));
        Assert.False(TrayMenuRenderer.ShouldHighlight(selectedDisabled));
    }

    /// <summary>Background fills the whole strip with the palette surface; the border is a 1 px palette-coloured frame.</summary>
    [Fact]
    public void Background_And_Border_UsePaletteColours()
    {
        using var sut = new TrayMenuRenderer(Palette);
        using var strip = new ContextMenuStrip { AutoSize = false, Size = new Size(120, 60) };
        using var bitmap = NewBitmap(120, 60, DrawingColor.Magenta);

        using (var graphics = Graphics.FromImage(bitmap))
        {
            sut.DrawToolStripBackground(new ToolStripRenderEventArgs(graphics, strip));
            sut.DrawToolStripBorder(new ToolStripRenderEventArgs(graphics, strip));
        }

        AssertPixel(sut.BackgroundColor, bitmap, 60, 30);
        AssertPixel(sut.BorderColor, bitmap, 0, 0);
        AssertPixel(sut.BorderColor, bitmap, 119, 59);
        AssertPixel(sut.BorderColor, bitmap, 60, 0);
        AssertPixel(sut.BackgroundColor, bitmap, 1, 1);
    }

    /// <summary>
    /// The image margin is not painted at all (no Office gradient column) — the background underneath stays.
    /// </summary>
    [Fact]
    public void ImageMargin_IsNotPainted()
    {
        using var sut = new TrayMenuRenderer(Palette);
        using var strip = new ContextMenuStrip { AutoSize = false, Size = new Size(120, 60) };
        using var bitmap = NewBitmap(120, 60, DrawingColor.Magenta);

        using (var graphics = Graphics.FromImage(bitmap))
        {
            sut.DrawImageMargin(new ToolStripRenderEventArgs(graphics, strip, new Rectangle(0, 0, 30, 60), DrawingColor.Red));
        }

        AssertPixel(DrawingColor.Magenta, bitmap, 10, 30);
    }

    /// <summary>
    /// A selected item gets the rounded, inset highlight: hover fill in the middle, background left in the
    /// inset gutter and at the rounded corner; an idle item draws nothing.
    /// </summary>
    [Fact]
    public void MenuItemBackground_Selected_DrawsRoundedInsetHighlight()
    {
        using var sut = new TrayMenuRenderer(Palette);
        using var item = new SelectedMenuItem { AutoSize = false, Size = new Size(200, 30) };
        using var idle = new ToolStripMenuItem("idle") { AutoSize = false, Size = new Size(200, 30) };
        using var bitmap = NewBitmap(200, 30, sut.BackgroundColor);
        using var idleBitmap = NewBitmap(200, 30, sut.BackgroundColor);

        using (var graphics = Graphics.FromImage(bitmap))
        {
            sut.DrawMenuItemBackground(new ToolStripItemRenderEventArgs(graphics, item));
        }

        using (var graphics = Graphics.FromImage(idleBitmap))
        {
            sut.DrawMenuItemBackground(new ToolStripItemRenderEventArgs(graphics, idle));
        }

        // Inside the highlight (away from the anti-aliased edges), in the 4-DIP gutters, and at the rounded
        // top-left corner (outside the r = 4 arc — anti-aliasing may tint it slightly, but it must stay far
        // closer to the background than to the fill).
        AssertPixel(sut.HoverFillColor, bitmap, 100, 15);
        AssertPixel(sut.HoverFillColor, bitmap, 6, 15);
        AssertPixel(sut.BackgroundColor, bitmap, 1, 15);
        AssertPixel(sut.BackgroundColor, bitmap, 198, 15);
        AssertCloserTo(sut.BackgroundColor, sut.HoverFillColor, bitmap.GetPixel(4, 0));
        AssertPixel(sut.BackgroundColor, idleBitmap, 100, 15);
    }

    /// <summary>Item text is drawn in the palette text colour, disabled text in the disabled colour.</summary>
    /// <param name="enabled">Whether the item is enabled.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ItemText_UsesTextColourByEnabledState(bool enabled)
    {
        using var sut = new TrayMenuRenderer(Palette);
        using var item = new ToolStripMenuItem("MMMM") { Enabled = enabled };
        using var font = new Font("Segoe UI", 16, System.Drawing.GraphicsUnit.Pixel);
        using var bitmap = NewBitmap(120, 30, sut.BackgroundColor);

        using (var graphics = Graphics.FromImage(bitmap))
        {
            sut.DrawItemText(new ToolStripItemTextRenderEventArgs(
                graphics, item, "MMMM", new Rectangle(0, 0, 120, 30), DrawingColor.Red, font, TextFormatFlags.Left | TextFormatFlags.VerticalCenter));
        }

        var expected = enabled ? sut.TextColor : sut.TextDisabledColor;
        AssertClose(expected, InkColor(bitmap, sut.BackgroundColor), tolerance: 12);
        Assert.False(ContainsReddish(bitmap), "the stock e.TextColor (red) must not be used");
    }

    /// <summary>
    /// The checkmark is a bare glyph in the glyph colour: the corners of the check rectangle keep the
    /// background (no box), and the glyph itself is drawn.
    /// </summary>
    [Fact]
    public void ItemCheck_DrawsGlyphWithoutBox()
    {
        using var sut = new TrayMenuRenderer(Palette);
        using var item = new ToolStripMenuItem("a") { Checked = true };
        using var image = new Bitmap(16, 16);
        using var bitmap = NewBitmap(24, 24, sut.BackgroundColor);

        using (var graphics = Graphics.FromImage(bitmap))
        {
            sut.DrawItemCheck(new ToolStripItemImageRenderEventArgs(graphics, item, image, new Rectangle(0, 0, 24, 24)));
        }

        AssertPixel(sut.BackgroundColor, bitmap, 0, 0);
        AssertPixel(sut.BackgroundColor, bitmap, 23, 0);
        AssertPixel(sut.BackgroundColor, bitmap, 0, 23);
        AssertPixel(sut.BackgroundColor, bitmap, 23, 23);
        AssertGlyphInk(InkColor(bitmap, sut.BackgroundColor));
    }

    /// <summary>The submenu arrow uses the glyph colour, never the stock arrow colour passed in.</summary>
    [Fact]
    public void Arrow_UsesGlyphColour()
    {
        using var sut = new TrayMenuRenderer(Palette);
        using var item = new ToolStripMenuItem("Pages");
        using var bitmap = NewBitmap(20, 20, sut.BackgroundColor);

        using (var graphics = Graphics.FromImage(bitmap))
        {
            sut.DrawArrow(new ToolStripArrowRenderEventArgs(graphics, item, new Rectangle(0, 0, 20, 20), DrawingColor.Red, ArrowDirection.Right));
        }

        AssertGlyphInk(InkColor(bitmap, sut.BackgroundColor));
        Assert.False(ContainsReddish(bitmap), "the stock arrow colour (red) must not be used");
    }

    /// <summary>A horizontal separator is a 1 px palette-coloured line, inset 4 DIP from both edges, vertically centred.</summary>
    [Fact]
    public void Separator_IsInsetOnePixelLine()
    {
        using var sut = new TrayMenuRenderer(Palette);
        using var separator = new ToolStripSeparator { AutoSize = false, Size = new Size(100, 9) };
        using var bitmap = NewBitmap(100, 9, sut.BackgroundColor);

        using (var graphics = Graphics.FromImage(bitmap))
        {
            sut.DrawSeparator(new ToolStripSeparatorRenderEventArgs(graphics, separator, vertical: false));
        }

        AssertPixel(sut.SeparatorColor, bitmap, 50, 4);
        AssertPixel(sut.SeparatorColor, bitmap, 4, 4);
        AssertPixel(sut.BackgroundColor, bitmap, 3, 4);
        AssertPixel(sut.BackgroundColor, bitmap, 96, 4);
        AssertPixel(sut.BackgroundColor, bitmap, 50, 3);
        AssertPixel(sut.BackgroundColor, bitmap, 50, 5);
    }

    /// <summary>§2.6 row metrics are applied to every menu item of a strip (not to separators).</summary>
    [Fact]
    public void ApplyLayoutMetrics_SetsRowPaddingOnMenuItemsOnly()
    {
        using var strip = new ContextMenuStrip();
        var item = new ToolStripMenuItem("Item");
        var separator = new ToolStripSeparator();
        strip.Items.Add(item);
        strip.Items.Add(separator);
        var separatorPadding = separator.Padding;

        TrayMenuRenderer.ApplyLayoutMetrics(strip);

        var textHeight = TextRenderer.MeasureText("Ag", strip.Font).Height;
        Assert.Equal(TrayMenuRenderer.ItemPadding(strip.DeviceDpi, textHeight, item.Padding), item.Padding);
        Assert.True(item.Padding.Vertical > 2, "row padding was not applied");
        Assert.Equal(separatorPadding, separator.Padding);
    }

    /// <summary>
    /// Review L-7: glyph fonts are cached per pixel size — repeated paints at the same size reuse one
    /// <see cref="Font"/> (the checkmark and the chevron sizes give two entries at 96 DPI).
    /// </summary>
    [Fact]
    public void DrawGlyph_CachesOneFontPerPixelSize()
    {
        using var sut = new TrayMenuRenderer(Palette);
        using var bitmap = NewBitmap(24, 24, sut.BackgroundColor);
        using var graphics = Graphics.FromImage(bitmap);
        var cell = new Rectangle(0, 0, 24, 24);

        var drawn = sut.DrawGlyph(graphics, TrayMenuRenderer.CheckmarkGlyph, cell, 12, sut.GlyphColor);
        sut.DrawGlyph(graphics, TrayMenuRenderer.CheckmarkGlyph, cell, 12, sut.GlyphColor);
        sut.DrawGlyph(graphics, TrayMenuRenderer.ChevronGlyph, cell, 10, sut.GlyphColor);
        sut.DrawGlyph(graphics, TrayMenuRenderer.ChevronGlyph, cell, 10, sut.GlyphColor);

        // No icon font installed → nothing is drawn or cached (the callers fall back to vector shapes).
        Assert.Equal(drawn ? 2 : 0, sut.CachedGlyphFontCount);
        Assert.False(sut.DrawGlyph(graphics, TrayMenuRenderer.CheckmarkGlyph, cell, 0, sut.GlyphColor));
        Assert.Equal(drawn ? 2 : 0, sut.CachedGlyphFontCount);
    }

    /// <summary>
    /// Review L-7: <see cref="TrayMenuRenderer.Dispose"/> releases the cached fonts, is idempotent, and a
    /// late paint through a disposed renderer still draws (with a per-paint font) without re-filling the cache.
    /// </summary>
    [Fact]
    public void Dispose_ReleasesCachedFonts_IsIdempotent_LatePaintStillDraws()
    {
        var sut = new TrayMenuRenderer(Palette);
        using var bitmap = NewBitmap(24, 24, sut.BackgroundColor);
        using var graphics = Graphics.FromImage(bitmap);
        var cell = new Rectangle(0, 0, 24, 24);
        var drawn = sut.DrawGlyph(graphics, TrayMenuRenderer.CheckmarkGlyph, cell, 12, sut.GlyphColor);

        sut.Dispose();
        var secondDispose = Record.Exception(sut.Dispose);

        Assert.Null(secondDispose);
        Assert.True(sut.IsDisposed);
        Assert.Equal(0, sut.CachedGlyphFontCount);
        Assert.Equal(drawn, sut.DrawGlyph(graphics, TrayMenuRenderer.CheckmarkGlyph, cell, 12, sut.GlyphColor));
        Assert.Equal(0, sut.CachedGlyphFontCount);
    }

    private static Bitmap NewBitmap(int width, int height, DrawingColor fill)
    {
        var bitmap = new Bitmap(width, height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(fill);
        return bitmap;
    }

    private static void AssertPixel(DrawingColor expected, Bitmap bitmap, int x, int y)
    {
        var actual = bitmap.GetPixel(x, y);
        Assert.True(expected.ToArgb() == actual.ToArgb(), $"pixel ({x},{y}): expected {expected}, actual {actual}");
    }

    /// <summary>
    /// Thin icon-font strokes never fully cover a ClearType sub-pixel, so the ink never reaches the exact
    /// glyph colour; the test palette's glyph colour is pure blue, so "clearly blue" proves the glyph colour
    /// (not the grey text colour, not the red stock colour) was used.
    /// </summary>
    private static void AssertGlyphInk(DrawingColor ink)
        => Assert.True(ink.B >= 96 && ink.R <= 40 && ink.G <= 40, $"glyph ink {ink} is not the (blue) glyph colour");

    private static void AssertClose(DrawingColor expected, DrawingColor actual, int tolerance)
    {
        Assert.True(
            Math.Abs(expected.R - actual.R) <= tolerance &&
            Math.Abs(expected.G - actual.G) <= tolerance &&
            Math.Abs(expected.B - actual.B) <= tolerance,
            $"expected ≈{expected}, actual {actual}");
    }

    private static void AssertCloserTo(DrawingColor expected, DrawingColor other, DrawingColor actual)
    {
        static int Distance(DrawingColor a, DrawingColor b) => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);

        Assert.True(Distance(actual, expected) < Distance(actual, other), $"{actual} is not closer to {expected} than to {other}");
    }

    /// <summary>
    /// The colour the "ink" (every pixel that is not the untouched background) was drawn with, as the
    /// per-channel maximum over those pixels. GDI ClearType text drawn through <see cref="TextRenderer"/> into a
    /// GDI+ <see cref="Bitmap"/> is composited against black per sub-pixel, so no single pixel carries the
    /// exact colour — but each channel of the fully covered sub-pixels reaches it, and never exceeds it.
    /// </summary>
    private static DrawingColor InkColor(Bitmap bitmap, DrawingColor background)
    {
        int r = 0, g = 0, b = 0;
        var any = false;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.ToArgb() == background.ToArgb()) continue;

                any = true;
                r = Math.Max(r, pixel.R);
                g = Math.Max(g, pixel.G);
                b = Math.Max(b, pixel.B);
            }
        }

        Assert.True(any, "nothing was drawn");
        return DrawingColor.FromArgb(r, g, b);
    }

    private static bool ContainsReddish(Bitmap bitmap)
    {
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.R > 180 && pixel.G < 80 && pixel.B < 80)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>A menu item that always reports itself as selected (hovered/keyboard-focused) — <see cref="ToolStripItem.Selected"/> is virtual.</summary>
    private sealed class SelectedMenuItem : ToolStripMenuItem
    {
        public SelectedMenuItem()
            : base("Selected")
        {
        }

        public override bool Selected => true;
    }
}
