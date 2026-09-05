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
}
