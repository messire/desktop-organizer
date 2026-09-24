using System.IO;

namespace DesktopOrganizer.Services.Diagnostics;

/// <summary>
/// Pure, allocation-light helpers for naming and retaining <see cref="JsonLinesDiagnosticsLog"/>
/// files. Deliberately does no I/O of its own so it's fully unit-testable without touching disk.
/// </summary>
public static class DiagnosticsLogFileNaming
{
    private const string Prefix = "gestures-";
    private const string Extension = ".jsonl";

    /// <summary>
    /// Builds the file name for a new diagnostics session started at <paramref name="now"/> by
    /// process <paramref name="processId"/>, e.g. <c>gestures-20260917-101500-12345.jsonl</c>.
    /// </summary>
    /// <param name="now">The session start time (local time).</param>
    /// <param name="processId">The current process id, disambiguating concurrent app instances.</param>
    public static string BuildFileName(DateTimeOffset now, int processId) =>
        $"{Prefix}{now:yyyyMMdd-HHmmss}-{processId}{Extension}";

    /// <summary>
    /// Given the diagnostics log files currently on disk, returns the subset that should be deleted
    /// so that at most <paramref name="keep"/> of the newest ones remain. "Newest" is determined by
    /// an ordinal comparison of file names: the fixed-width <c>yyyyMMdd-HHmmss</c> prefix produced
    /// by <see cref="BuildFileName"/> sorts chronologically as a plain string, so no parsing is
    /// needed here.
    /// </summary>
    /// <param name="existing">
    /// The existing diagnostics file paths (or bare names — only the file name portion of each
    /// entry is used for ordering).
    /// </param>
    /// <param name="keep">The number of newest files to retain. Negative values are treated as 0.</param>
    /// <returns>
    /// The entries of <paramref name="existing"/> (in their original form) that should be deleted,
    /// oldest first. Empty if <paramref name="existing"/> already has <paramref name="keep"/> or
    /// fewer entries.
    /// </returns>
    public static IReadOnlyList<string> SelectForDeletion(IReadOnlyList<string> existing, int keep)
    {
        ArgumentNullException.ThrowIfNull(existing);

        var effectiveKeep = Math.Max(0, keep);
        if (existing.Count <= effectiveKeep) return Array.Empty<string>();

        // Oldest-first by file name; everything before the last `effectiveKeep` entries is deleted.
        var ordered = existing
            .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
            .ToList();

        var deleteCount = ordered.Count - effectiveKeep;
        return ordered.Take(deleteCount).ToList();
    }
}
