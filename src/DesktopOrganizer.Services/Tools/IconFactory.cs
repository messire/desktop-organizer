using System.Windows.Interop;
using DesktopOrganizer.Services.Interfaces;

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
        => Imaging.CreateBitmapSourceFromHIcon(iconHandle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

    private static RenderTargetBitmap CreatePlaceHolder()
    {
        var drawingVisual = new DrawingVisual();
        using var drawingContext = drawingVisual.RenderOpen();
        drawingContext.DrawRectangle(Brushes.Gray, new Pen(Brushes.Black, Thickness), new Rect(0, 0, Width, Height));
        var bitmap = new RenderTargetBitmap(Width, Height, Dpi, Dpi, PixelFormats.Pbgra32);
        bitmap.Render(drawingVisual);
        _placeholderIcon = bitmap;
        return bitmap;
    }
}
