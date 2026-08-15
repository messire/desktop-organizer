using System.IO;

namespace DesktopOrganizer.Core.Models;

/// <summary>
/// Pure, file-system-free validation of a candidate bare file or folder name (no directory
/// component). Mirrors the constraints Windows itself enforces on a single path segment, checked
/// up front so a rename can be rejected as <c>RenameOutcome.InvalidName</c> before any file-system
/// I/O is attempted.
/// </summary>
public static class FileNameValidation
{
    // Windows reserved device names — reserved with or without an extension ("CON.txt" is just as
    // reserved as bare "CON"), case-insensitive.
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// True if <paramref name="name"/> is a valid bare Windows file/folder name:
    /// <list type="bullet">
    /// <item>not empty or all-whitespace;</item>
    /// <item>contains none of <see cref="Path.GetInvalidFileNameChars"/> — which already includes
    /// both path separators, so this alone rejects any attempt to smuggle a directory component or
    /// escape the target directory (e.g. <c>..\..\evil</c>);</item>
    /// <item>isn't exactly <c>"."</c> or <c>".."</c>;</item>
    /// <item>doesn't end with a trailing space or a trailing dot (Windows silently strips these,
    /// which would make the on-disk name diverge from what the user typed);</item>
    /// <item>isn't a reserved Windows device name (<c>CON</c>, <c>PRN</c>, <c>AUX</c>, <c>NUL</c>,
    /// <c>CONIN$</c>, <c>CONOUT$</c>, <c>COM1</c>-<c>COM9</c>, <c>LPT1</c>-<c>LPT9</c>), with or
    /// without an extension, regardless of case.</item>
    /// </list>
    /// </summary>
    /// <param name="name">A candidate bare file or folder name — no directory component.</param>
    public static bool IsValid(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        if (name is "." or "..") return false;
        if (name[^1] is ' ' or '.') return false;

        // Windows reserves a device name by the portion of the segment BEFORE its FIRST dot, not
        // Path.GetFileNameWithoutExtension's "before the LAST dot" — so "COM1.txt.bak" is just as
        // reserved as bare "COM1", even though its stem-before-the-last-dot is "COM1.txt".
        var firstDot = name.IndexOf('.');
        var stem = firstDot < 0 ? name : name[..firstDot];
        if (ReservedNames.Contains(stem)) return false;

        return true;
    }
}
