using System.IO;
using DesktopOrganizer.Services.Abstractions;
using static DesktopOrganizer.Services.Enums.FileAttribute;
using static DesktopOrganizer.Services.Enums.FileFlag;
using Directory = System.IO.Directory;

namespace DesktopOrganizer.Services.Providers;

/// <inheritdoc />
internal class IconProvider(IIconHandler iconHandler, IIconFactory iconFactory)
    : IIconProvider
{
    private const FileFlag IconFlags = Icon | ShellIconSize | UseFileAttributes;
    private const FileAttribute FileAttributes = Normal;

    private readonly ConcurrentDictionary<string, ImageSource> _iconCache = new();

    private ImageSource PlaceholderIcon => iconFactory.CreateBitmap();

    /// <inheritdoc />
    public ImageSource GetIcon(string filePath)
    {
        if (!File.Exists(filePath) && !Directory.Exists(filePath))
        {
            return PlaceholderIcon;
        }

        if (_iconCache.TryGetValue(filePath, out var cachedIcon))
        {
            return cachedIcon;
        }

        return TryRetrieveIcon(filePath) ?? PlaceholderIcon;
    }

    /// <summary>
    /// Attempts to retrieve the icon for the specified file path.
    /// </summary>
    /// <param name="filePath">File path.</param>
    /// <returns>A <see cref="BitmapSource"/> representing the icon, or <see langword="null"/> if retrieval fails.</returns>
    private ImageSource? TryRetrieveIcon(string filePath)
    {
        try
        {
            var fileInfo = default(ShellFileInfo);
            var iconHandle = iconHandler.GetIconHandle(filePath, ref fileInfo, FileAttributes, IconFlags);
            return iconHandle == IntPtr.Zero ? null : TryCreateBitmapSource(filePath, ref fileInfo);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error retrieving icon for '{filePath}': {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Attempts to create bitmap source from the specified file icon.
    /// </summary>
    /// <param name="filePath">File path.</param>
    /// <param name="fileInfo">The shell file information structure containing icon details.</param>
    /// <returns>A <see cref="BitmapSource"/> representing the icon, or <see langword="null"/> if creation fails.</returns>
    private ImageSource TryCreateBitmapSource(string filePath, ref ShellFileInfo fileInfo)
    {
        try
        {
            var imageSource = iconFactory.CreateBitmap(fileInfo.IconHandle);
            _iconCache[filePath] = imageSource;
            return imageSource;
        }
        finally
        {
            iconHandler.DestroyIconHandle(fileInfo.IconHandle);
        }
    }
}
