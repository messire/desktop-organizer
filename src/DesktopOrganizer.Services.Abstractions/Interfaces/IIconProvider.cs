using System.Windows.Media;

namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// Provides functionality to cache file icons for improved performance.
/// </summary>
public interface IIconProvider
{
    /// <summary>
    /// Get file icon or placeholder.
    /// </summary>
    /// <param name="filePath">File path.</param>
    /// <returns>A <see cref="ImageSource"/> representing the icon.</returns>
    ImageSource GetIcon(string filePath);

    /// <summary>
    /// True if <paramref name="icon"/> is this provider's own generic placeholder (the uniform gray
    /// square <see cref="GetIcon"/> falls back to when neither the OS nor this provider could produce
    /// a real icon) rather than any icon actually resolved for a file/folder. Added for M3 (P08 fix
    /// cycle 1, 2026-09-22): lets a caller distinguish "this item's icon is the last-resort
    /// placeholder and might be worth retrying" from "this is a real, resolved icon that merely looks
    /// generic" — see <c>BaseViewModel.RefreshItemsLiveness</c>'s bounded placeholder-retry.
    /// </summary>
    /// <param name="icon">The icon previously returned by <see cref="GetIcon"/>.</param>
    bool IsPlaceholder(ImageSource? icon);
}
