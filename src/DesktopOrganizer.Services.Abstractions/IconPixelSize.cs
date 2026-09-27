namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// Pixel-size policy for panel item icons requested through <see cref="IIconProvider.GetIcon(string, int)"/>:
/// converts a panel's DIP icon size and its DPI scale into a physical pixel size, and snaps it to a small,
/// closed set of shell-friendly sizes.
/// </summary>
/// <remarks>
/// <para>
/// Snapping exists for two reasons. (1) The icon cache is keyed by path AND size, and the per-panel
/// <c>IconSize</c> slider is continuous (fractional values are legal) — without buckets every slider tick
/// would be a new cache key and a new shell round-trip for every item of the panel. (2) Every bucket is a
/// size icon authors actually ship (16/20/24/32/40/48/64/72/96/128/144/192/256 — the 100/125/150/200 %
/// multiples of the classic 16/32/48/96/128 sizes), so the shell usually returns a hand-drawn frame
/// instead of resampling one.
/// </para>
/// <para>
/// A request is always rounded UP to the next bucket: WPF then scales the bitmap slightly DOWN (sharp,
/// <c>BitmapScalingMode.HighQuality</c> in the grid template) rather than up (blurry) — the defect this
/// policy replaces was a 32 px bitmap upscaled to 48+ px.
/// </para>
/// </remarks>
public static class IconPixelSize
{
    /// <summary>
    /// Pixel size used by <see cref="IIconProvider.GetIcon(string)"/>: the default per-panel
    /// <c>IconSize</c> (48 DIP) at 100 % scale.
    /// </summary>
    public const int Default = 48;

    /// <summary>Smallest pixel size ever requested from the shell.</summary>
    public const int Min = 16;

    /// <summary>Largest pixel size ever requested from the shell (the Windows "jumbo" icon size).</summary>
    public const int Max = 256;

    private static readonly int[] Buckets = [16, 20, 24, 32, 40, 48, 64, 72, 96, 128, 144, 192, 256];

    /// <summary>
    /// Snaps <paramref name="requestedPixels"/> up to the next supported bucket, clamped to
    /// [<see cref="Min"/>, <see cref="Max"/>].
    /// </summary>
    /// <param name="requestedPixels">Desired physical pixel size of the icon's square.</param>
    /// <returns>The bucketed pixel size; never smaller than <see cref="Min"/> nor larger than <see cref="Max"/>.</returns>
    public static int Snap(int requestedPixels)
    {
        foreach (var bucket in Buckets)
        {
            if (requestedPixels <= bucket) return bucket;
        }

        return Max;
    }

    /// <summary>
    /// Converts an icon size in DIP rendered at <paramref name="dpiScale"/> into a bucketed physical pixel
    /// size (see <see cref="Snap"/>).
    /// </summary>
    /// <param name="sizeDip">Icon size in device-independent pixels (e.g. a panel's <c>IconSize</c>).</param>
    /// <param name="dpiScale">DPI scale of the surface the icon is rendered on (1.0 at 96 DPI).</param>
    /// <returns>
    /// The bucketed pixel size. A non-finite or non-positive input degrades to 1.0 scale / <see cref="Default"/>
    /// rather than throwing — a panel with a corrupt persisted size must still get icons.
    /// </returns>
    public static int FromDip(double sizeDip, double dpiScale)
    {
        var dip = double.IsFinite(sizeDip) && sizeDip > 0 ? sizeDip : Default;
        var scale = double.IsFinite(dpiScale) && dpiScale > 0 ? dpiScale : 1.0;

        // Clamped before the int conversion so an absurd value cannot overflow.
        var pixels = Math.Min(Math.Ceiling(dip * scale), Max);
        return Snap((int)pixels);
    }
}
