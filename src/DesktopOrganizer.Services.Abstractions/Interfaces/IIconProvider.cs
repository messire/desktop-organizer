using System.Windows.Media;

namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// Provides functionality to cache file icons for improved performance.
/// </summary>
public interface IIconProvider
{
    /// <summary>
    /// Get file icon or placeholder at the default size (<see cref="IconPixelSize.Default"/> pixels).
    /// Equivalent to <see cref="GetIcon(string, int)"/> with <see cref="IconPixelSize.Default"/>.
    /// </summary>
    /// <param name="filePath">File path.</param>
    /// <returns>A <see cref="ImageSource"/> representing the icon.</returns>
    ImageSource GetIcon(string filePath);

    /// <summary>
    /// Get the file's own icon (Explorer-equivalent: <c>.lnk</c> target icon, embedded <c>.exe</c>/<c>.ico</c>
    /// icon, custom folder icon) rendered for a square of <paramref name="pixelSize"/> physical pixels, or
    /// a placeholder.
    /// </summary>
    /// <remarks>
    /// <paramref name="pixelSize"/> is snapped up to a shell-friendly size (<see cref="IconPixelSize.Snap"/>),
    /// and the snapped value is part of the cache key — two panels showing the same file at different sizes
    /// each get their own bitmap. The returned bitmap may be larger than requested (the shell may hand back a
    /// bigger frame); callers size the <c>Image</c> element themselves. A path that does not exist gets a
    /// type icon by extension, never cached (see the implementation's remarks). UI-thread (STA) call.
    /// </remarks>
    /// <param name="filePath">File path.</param>
    /// <param name="pixelSize">
    /// Physical pixel size the icon will be displayed at (DIP size × DPI scale — see
    /// <see cref="IconPixelSize.FromDip"/>).
    /// </param>
    /// <returns>A frozen <see cref="ImageSource"/> representing the icon.</returns>
    ImageSource GetIcon(string filePath, int pixelSize);

    /// <summary>
    /// True if <paramref name="icon"/> is this provider's own generic placeholder (the uniform gray
    /// square <see cref="GetIcon(string, int)"/> falls back to when neither the OS nor this provider could produce
    /// a real icon) rather than any icon actually resolved for a file/folder. Added for M3 (P08 fix
    /// cycle 1, 2026-09-22): lets a caller distinguish "this item's icon is the last-resort
    /// placeholder and might be worth retrying" from "this is a real, resolved icon that merely looks
    /// generic" — see <c>BaseViewModel.RefreshItemsLiveness</c>'s bounded placeholder-retry.
    /// </summary>
    /// <param name="icon">The icon previously returned by <see cref="GetIcon(string, int)"/>.</param>
    bool IsPlaceholder(ImageSource? icon);
}
