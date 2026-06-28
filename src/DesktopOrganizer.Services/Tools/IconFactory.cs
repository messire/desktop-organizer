using System.Windows.Interop;
using Brushes = System.Windows.Media.Brushes;
using Pen = System.Windows.Media.Pen;

namespace DesktopOrganizer.Services.Tools;

/// <inheritdoc />
internal class IconFactory: IIconFactory
{
    private const int Thickness = 1;
    private const int Height = 16;
    private const int Width = 16;
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
        using var drawingContext = drawingVisual.RenderOpen();
        drawingContext.DrawRectangle(Brushes.Gray, new Pen(Brushes.Black, Thickness), new Rect(0, 0, Width, Height));
        var bitmap = new RenderTargetBitmap(Width, Height, Dpi, Dpi, PixelFormats.Pbgra32);
        bitmap.Render(drawingVisual);
        bitmap.Freeze();
        _placeholderIcon = bitmap;
        return bitmap;
    }
}
