namespace DesktopOrganizer.Services.Interfaces;

/// <summary>
/// Factory for creating icons.
/// </summary>
internal interface IIconFactory
{
    /// <summary>
    /// Retrieves the placeholder icon.
    /// </summary>
    /// <returns>The placeholder icon as a BitmapSource.</returns>
    ImageSource CreateBitmap();

    /// <summary>
    /// Creates <see cref="BitmapSource"/> from the specified icon handle.
    /// </summary>
    /// <param name="iconHandle">The handle to the icon.</param>
    /// <returns>A <see cref="ImageSource"/> representing the icon.</returns>
    ImageSource CreateBitmap(IntPtr iconHandle);

    /// <summary>
    /// Creates a frozen <see cref="BitmapSource"/> from pixels copied out of a native bitmap (the
    /// <c>IShellItemImageFactory</c> stage), choosing the pixel format from the data itself: premultiplied
    /// alpha, straight alpha, or no alpha channel at all. A source larger than <paramref name="targetPixelSize"/>
    /// (the shell may return a bigger frame — <c>SIIGBF_BIGGERSIZEOK</c>) is downscaled so its larger side is
    /// exactly <paramref name="targetPixelSize"/>; the result never retains the larger source pixels.
    /// </summary>
    /// <param name="pixels">Top-down 32-bpp BGRA pixels.</param>
    /// <param name="targetPixelSize">The snapped pixel size the icon was requested at (the cache bucket).</param>
    /// <returns>A frozen <see cref="ImageSource"/>.</returns>
    /// <exception cref="ArgumentException">The buffer size does not match the dimensions.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="targetPixelSize"/> is not positive.</exception>
    ImageSource CreateBitmap(NativeBitmapPixels pixels, int targetPixelSize);
}
