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
    // below, but free to fix alongside it).
    private const int Height = 32;
    private const int Width = 32;
    private const int Dpi = 96;

    private static ImageSource? _placeholderIcon;

    /// <inheritdoc />
    public ImageSource CreateBitmap()
        => _placeholderIcon ?? CreatePlaceHolder();

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

    private static RenderTargetBitmap CreatePlaceHolder()
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
        _placeholderIcon = bitmap;
        return bitmap;
    }
}
