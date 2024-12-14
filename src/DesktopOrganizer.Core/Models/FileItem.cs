using System.IO;

namespace DesktopOrganizer.Core.Models;

/// <summary>
/// File data content.
/// </summary>
public class FileItem
{
    /// <summary>
    /// The file path.
    /// </summary>
    public required string FilePath { get; init; }

    /// <summary>
    /// The file name extracted from the file path.
    /// </summary>
    public string FileName => Path.GetFileName(FilePath) ?? string.Empty;

    /// <summary>
    /// The file extension extracted from the file path.
    /// </summary>
    public string FileType => Path.GetExtension(FilePath) ?? string.Empty;

    /// <summary>
    /// The file name as a string representation of the object.
    /// </summary>
    public override string ToString() => FileName;
}
