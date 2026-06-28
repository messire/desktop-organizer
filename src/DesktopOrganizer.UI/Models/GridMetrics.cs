namespace DesktopOrganizer.UI.Models;

/// <summary>
/// Pure static helpers for icon-grid geometry. No WPF or P/Invoke dependencies — fully unit-testable.
/// </summary>
public static class GridMetrics
{
    /// <summary>Margin on each side of the item StackPanel in WrapViewTemplate (matches <c>Margin="6"</c>).</summary>
    public const double ItemMargin = 6d;

    /// <summary>
    /// Total horizontal chrome of FileListHost (<c>Margin="4,0,4,4"</c> → left 4 + right 4).
    /// Must be added back to the quantised content width to get the window width.
    /// </summary>
    public const double FileListHPadding = 8d;

    /// <summary>
    /// Width of the item StackPanel (icon box) in WrapViewTemplate.
    /// Formula: icon + 25 % padding on each side = <c>iconSize * 1.5</c>.
    /// Example: <c>BoxSize(48) == 72</c>.
    /// </summary>
    public static double BoxSize(double iconSize) => iconSize + (iconSize / 4d) * 2d;

    /// <summary>
    /// Full column footprint: <see cref="BoxSize"/> + two item margins.
    /// This is the step used for wrap-mode width quantisation.
    /// Example: <c>CellSize(48) == 84</c> (72 box + 6 + 6 margins).
    /// </summary>
    public static double CellSize(double iconSize) => BoxSize(iconSize) + ItemMargin * 2d;

    /// <summary>
    /// Rounds <paramref name="content"/> to the nearest multiple of <paramref name="cell"/>,
    /// with a minimum of one full cell.
    /// </summary>
    /// <param name="content">The raw content dimension to quantise (must be &gt;= 0).</param>
    /// <param name="cell">The cell size. Must be &gt; 0.</param>
    /// <returns>The quantised value, at least <paramref name="cell"/>.</returns>
    public static double Quantize(double content, double cell)
    {
        if (cell <= 0) return content;
        var rounded = Math.Round(content / cell) * cell;
        return Math.Max(cell, rounded);
    }
}
