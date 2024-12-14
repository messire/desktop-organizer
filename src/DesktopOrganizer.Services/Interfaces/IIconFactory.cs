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
}
