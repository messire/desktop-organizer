namespace DesktopOrganizer.Services.Interfaces;

/// <summary>
/// Provides functionality to convert native icon handles to bitmap sources.
/// </summary>
internal interface IIconConverter
{
    /// <summary>
    /// Creates <see cref="BitmapSource"/> from the specified icon handle.
    /// </summary>
    /// <param name="iconHandle">The handle to the icon.</param>
    /// <returns>A <see cref="BitmapSource"/> representing the icon.</returns>
    BitmapSource CreateBitmap(IntPtr iconHandle);
}
