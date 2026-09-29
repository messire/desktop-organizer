using System.Threading;
using System.Windows.Interop;
using Brushes = System.Windows.Media.Brushes;
using Pen = System.Windows.Media.Pen;

namespace DesktopOrganizer.Services.Tools;

/// <inheritdoc />
internal class IconFactory: IIconFactory
{
    private const int Thickness = 1;

    // 32x32 matches the real icon size returned by SHGFI_SHELLICONSIZE (see IconProvider.IconFlags):
    // a placeholder this size is shown at native resolution in the Grid item template (Image
    // Width/MaxHeight bound to IconSize, default ~32) instead of being upscaled/blurred from a
    // smaller source (P00 baseline finding H9 — quality-only, unrelated to the transparency defect
    // below, but free to fix alongside it). Since release-polish 1.0 real icons are requested at the
    // panel's own pixel size (IconPixelSize), so the placeholder — the very last resort — is the only
    // image still fixed at 32 px; the test suite pins that size (IconFactoryTests).
    private const int Height = 32;
    private const int Width = 32;
    private const int Dpi = 96;

    private static ImageSource? _placeholderIcon;

    /// <inheritdoc />
    /// <remarks>
    /// ADR-046 C-2: <c>_placeholderIcon</c> is published through <see cref="Interlocked.CompareExchange{T}(ref T, T, T)"/>
    /// rather than the previous plain <c>??=</c>, which was safe only as long as every caller ran on the
    /// UI thread — the background icon-load queue's worker threads can now race the UI thread (and each
    /// other) into this method. If two threads race, both may render a throwaway duplicate bitmap; only
    /// the FIRST one actually published wins and is what every caller (including the "loser") ends up
    /// returning, so there is still ever only one shared instance in circulation.
    /// </remarks>
    public ImageSource CreateBitmap()
    {
        if (_placeholderIcon is { } cached) return cached;

        var created = CreatePlaceholderBitmap();
        return Interlocked.CompareExchange(ref _placeholderIcon, created, null) ?? created;
    }

    /// <inheritdoc />
    public ImageSource CreateBitmap(IntPtr iconHandle)
    {
        var bitmap = Imaging.CreateBitmapSourceFromHIcon(iconHandle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

        // The icon cache is a long-lived, shared cache handing the same ImageSource out to every
        // panel. An unfrozen Freezable carries dispatcher-thread affinity, which would blow up if
        // ever touched off the creating thread; freezing makes it safely shareable/immutable.
        bitmap.Freeze();
        return bitmap;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>Memory (M2, release-polish 1.0 review).</b> <c>IShellItemImageFactory</c> with
    /// <c>SIIGBF_BIGGERSIZEOK</c> hands back the next icon frame at or above the request — for most requests
    /// between 72 and 192 px that is the 256 px jumbo frame (256 KB of pixels). The provider's LRU cache is
    /// bounded by COUNT (1024 entries), so keeping those frames as-is would cost tens of MB for a few
    /// large panels. A larger bitmap is therefore resampled down to <paramref name="targetPixelSize"/> here
    /// and MATERIALIZED into a fresh buffer of exactly that size (<see cref="Downscale"/>) before freezing —
    /// neither the source bitmap nor the <see cref="TransformedBitmap"/> chain is retained by the result.
    /// A bitmap at or below the target size is never upscaled (WPF scales it at render time).
    /// </para>
    /// </remarks>
    public ImageSource CreateBitmap(NativeBitmapPixels pixels, int targetPixelSize)
    {
        if (pixels.Width <= 0 || pixels.Height <= 0 || pixels.Bgra.Length != pixels.Stride * pixels.Height)
        {
            throw new ArgumentException("Pixel buffer size does not match the bitmap dimensions.", nameof(pixels));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetPixelSize);

        var format = DetectPixelFormat(pixels.Bgra.Span);

        // BitmapSource.Create copies the buffer, so a whole backing array can be handed over as-is.
        var buffer = MemoryMarshal.TryGetArray(pixels.Bgra, out var segment)
                     && segment.Array is { } array
                     && segment.Offset == 0
                     && segment.Count == array.Length
            ? array
            : pixels.Bgra.ToArray();

        var bitmap = BitmapSource.Create(pixels.Width, pixels.Height, Dpi, Dpi, format, null, buffer, pixels.Stride);

        var largestSide = Math.Max(pixels.Width, pixels.Height);
        if (largestSide > targetPixelSize)
        {
            bitmap = Downscale(bitmap, (double)targetPixelSize / largestSide);
        }

        // Same reason as CreateBitmap(IntPtr): shared through the provider's cache across panels.
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// Resamples <paramref name="source"/> by <paramref name="scale"/> (&lt; 1) and copies the result into a new,
    /// self-contained <see cref="PixelFormats.Pbgra32"/> bitmap, so nothing of the (larger) source stays reachable.
    /// </summary>
    /// <remarks>
    /// Resampling runs in PREMULTIPLIED space: filtering straight-alpha (<see cref="PixelFormats.Bgra32"/>) data
    /// channel by channel mixes the colour of fully transparent pixels — usually black — into the anti-aliased
    /// edge pixels and draws a dark halo around the icon. <see cref="PixelFormats.Bgr32"/> (no alpha channel)
    /// converts to fully opaque. <see cref="TransformedBitmap"/> uses WIC's scaler (Fant interpolation for a pure
    /// scale), a proper box-filter-like downsampler.
    /// </remarks>
    /// <param name="source">The bitmap to shrink.</param>
    /// <param name="scale">Uniform scale factor in (0, 1).</param>
    /// <returns>An unfrozen bitmap owning only the resampled pixels.</returns>
    internal static BitmapSource Downscale(BitmapSource source, double scale)
    {
        BitmapSource premultiplied = source.Format == PixelFormats.Pbgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);

        BitmapSource scaled = new TransformedBitmap(premultiplied, new ScaleTransform(scale, scale));
        if (scaled.Format != PixelFormats.Pbgra32)
        {
            scaled = new FormatConvertedBitmap(scaled, PixelFormats.Pbgra32, null, 0);
        }

        var width = scaled.PixelWidth;
        var height = scaled.PixelHeight;
        var stride = width * 4;
        var buffer = new byte[stride * height];
        scaled.CopyPixels(buffer, stride, 0);

        return BitmapSource.Create(width, height, Dpi, Dpi, PixelFormats.Pbgra32, null, buffer, stride);
    }

    /// <summary>
    /// Decides how the alpha byte of a 32-bpp BGRA buffer must be interpreted.
    /// </summary>
    /// <remarks>
    /// <c>IShellItemImageFactory</c> does not document whether its bitmap carries premultiplied alpha, and
    /// in practice icon handlers differ. The data answers it: in premultiplied data no color channel can
    /// exceed its alpha, so any pixel with <c>B/G/R &gt; A</c> proves straight alpha. A buffer whose alpha
    /// is 0 everywhere has no alpha channel at all (an opaque DDB-style image) and is shown opaque instead
    /// of fully transparent. Mis-classifying straight data that happens to satisfy <c>B/G/R ≤ A</c>
    /// everywhere as premultiplied only brightens semi-transparent edge pixels marginally.
    /// </remarks>
    /// <param name="bgra">Top-down BGRA pixels.</param>
    /// <returns><see cref="PixelFormats.Pbgra32"/>, <see cref="PixelFormats.Bgra32"/> or <see cref="PixelFormats.Bgr32"/>.</returns>
    internal static PixelFormat DetectPixelFormat(ReadOnlySpan<byte> bgra)
    {
        var anyAlpha = false;
        var straightAlpha = false;
        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            var alpha = bgra[i + 3];
            if (alpha != 0) anyAlpha = true;
            if (bgra[i] > alpha || bgra[i + 1] > alpha || bgra[i + 2] > alpha) straightAlpha = true;
            if (anyAlpha && straightAlpha) return PixelFormats.Bgra32;
        }

        if (!anyAlpha) return PixelFormats.Bgr32;
        return straightAlpha ? PixelFormats.Bgra32 : PixelFormats.Pbgra32;
    }

    private static RenderTargetBitmap CreatePlaceholderBitmap()
    {
        var drawingVisual = new DrawingVisual();

        // CRITICAL (P00 baseline finding, part C): the DrawingContext MUST be closed (Dispose) before
        // RenderTargetBitmap.Render is called — a DrawingVisual only flushes its recorded drawing
        // instructions to the visual tree when its DrawingContext is closed. Rendering while the
        // context is still open (the previous `using var` — scoped to the whole method — closed it
        // only *after* Render had already run) captures a visual with nothing recorded yet, producing
        // a fully transparent bitmap (measured: 0/256 opaque pixels) instead of the drawn gray/black
        // rectangle. The explicit block scope below is what actually fixes this, not `using` itself.
        using (var drawingContext = drawingVisual.RenderOpen())
        {
            // NIT (P08 fix cycle 1, 2026-09-22): WPF centers a Pen's stroke ON the geometry edge — a
            // 1px-thick border drawn on the exact Rect(0,0,32,32) boundary has half its stroke fall
            // outside the 32x32 render surface (clipped) and half inside, instead of a crisp, fully
            // visible border. Inset by half the stroke thickness and shrink the size by one full
            // thickness so the entire border renders inside the canvas.
            drawingContext.DrawRectangle(
                Brushes.Gray,
                new Pen(Brushes.Black, Thickness),
                new Rect(Thickness / 2.0, Thickness / 2.0, Width - Thickness, Height - Thickness));
        }

        var bitmap = new RenderTargetBitmap(Width, Height, Dpi, Dpi, PixelFormats.Pbgra32);
        bitmap.Render(drawingVisual);
        bitmap.Freeze();
        return bitmap;
    }
}
