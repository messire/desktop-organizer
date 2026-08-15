using System.IO;
using System.Text.RegularExpressions;

namespace DesktopOrganizer.Core.Models;

/// <summary>
/// Pure, file-system-free string helpers for panel item names. No I/O — every method here operates
/// only on the string form of a name.
/// </summary>
public static class ManagedItemName
{
    // Matches a trailing `_` followed by exactly 32 hex characters (case-insensitive) — the legacy
    // flat-layout suffix PanelItemStorageService used to append before moving the GUID into a
    // directory name instead (see ADR-013 Decision 2/3: `{name}_{guid:N}{ext}` -> `{guid}\{name}{ext}`).
    // Anchored to the end of the (extension-stripped) name so it only ever strips a suffix that sits
    // immediately before the extension for a file, or at the very end of the string for an
    // extensionless folder name.
    private static readonly Regex LegacyGuidSuffixPattern = new(
        "_[0-9a-fA-F]{32}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Strips a legacy flat-layout managed-storage GUID suffix from a bare file or folder name, e.g.
    /// <c>Report_9f3c1a2b3c4d5e6f7a8b9c0d1e2f3a4b.docx</c> becomes <c>Report.docx</c>, and
    /// <c>MyFolder_9f3c1a2b3c4d5e6f7a8b9c0d1e2f3a4b</c> becomes <c>MyFolder</c>. If
    /// <paramref name="fileName"/> doesn't end with a suffix in exactly this form (a literal
    /// underscore followed by 32 hex digits), it is returned unchanged — including the degenerate
    /// case where stripping it would leave nothing (e.g. a name that <i>was</i> just the guid).
    /// </summary>
    /// <param name="fileName">A bare file or folder name — no directory component.</param>
    public static string StripLegacyGuidSuffix(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return fileName;

        var ext = Path.GetExtension(fileName);
        var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);

        var match = LegacyGuidSuffixPattern.Match(nameWithoutExt);
        if (!match.Success) return fileName;

        var stripped = nameWithoutExt[..match.Index];

        // A name that was ONLY the guid suffix (e.g. "_9f3c1a2b3c4d5e6f7a8b9c0d1e2f3a4b") isn't a
        // plausible original name — treat it as a non-match and return the input unchanged.
        if (stripped.Length == 0) return fileName;

        return stripped + ext;
    }
}
