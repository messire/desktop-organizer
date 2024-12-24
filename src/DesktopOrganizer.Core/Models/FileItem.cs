using System.Windows.Media;

namespace DesktopOrganizer.Core.Models;

/// <summary>
/// File data content.
/// </summary>
/// <param name="Path">The file path.</param>
/// <param name="Icon">The file icon.</param>
public record FileItem(string Path, ImageSource? Icon)
{
    /// <summary>
    /// The file name extracted from the file path.
    /// </summary>
    public string Name => System.IO.Path.GetFileName(Path);

    /// <summary>
    /// The file extension extracted from the file path.
    /// </summary>
    public string Type => System.IO.Path.GetExtension(Path);

    /// <summary>
    /// The file name as a string representation of the object.
    /// </summary>
    public override string ToString() => Name;
}
