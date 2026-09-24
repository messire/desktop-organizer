using System.IO;
using System.Text.RegularExpressions;

namespace DesktopOrganizer.Services.Diagnostics;

/// <summary>
/// Shared constants/helpers for the icon-pipeline's <c>icon</c> diagnostics category (P08,
/// docs/DIAGNOSTICS/icon-loading-findings.md), used by <c>IconHandler</c> and <c>IconProvider</c> in
/// place of the <c>Console.Error.WriteLine</c> calls the P00 baseline audit found (silent in a
/// <c>WinExe</c> process — nothing ever reads process stderr).
/// </summary>
internal static partial class IconDiagnostics
{
    /// <summary>The <c>category</c> value for every icon-pipeline diagnostics event.</summary>
    public const string Category = "icon";

    /// <summary>
    /// The closed-set fallback value for a path whose trailing dotted segment does not look like a
    /// real file extension (see <see cref="SafeExtension"/>'s allow-list) — never the raw segment
    /// itself, which could carry an arbitrary personal-data fragment (M2 fix, P08 fix cycle 1,
    /// 2026-09-22, ADR-027 Amendment).
    /// </summary>
    public const string OtherExtension = "other";

    /// <summary>
    /// Extracts a privacy-safe file extension for diagnostics payloads: the privacy invariant
    /// (docs/Refactoring/Testing/diagnostics-log-events.md) forbids file names/paths in payload, but
    /// a short, lower-cased extension (<c>".exe"</c>, <c>".lnk"</c>, or <c>string.Empty</c> when
    /// there isn't one) carries no personal data and is exactly the granularity needed to tell "a
    /// shortcut's icon failed" from "a folder's icon failed" in an attached log.
    /// </summary>
    /// <remarks>
    /// <b>M2 fix (P08 fix cycle 1, 2026-09-22):</b> <see cref="Path.GetExtension(string)"/> has no
    /// notion of a "real" registered file extension — for a path like <c>C:\Users\ivan.petrov</c> (a
    /// DIRECTORY whose own trailing name segment contains a dot — e.g. a Windows user-profile folder),
    /// it happily returns <c>".petrov"</c>, which can carry a surname or other personal-data fragment
    /// straight into the JSONL diagnostics log testers attach to bug reports — exactly what the privacy
    /// invariant exists to prevent. Two guards apply, closing two DIFFERENT (not fully overlapping)
    /// slices of the risk:
    /// <list type="bullet">
    /// <item>A directory never yields anything but <see cref="string.Empty"/> — its "extension" (if
    /// any) is a directory-name artifact, never a real file-type signal. This is what closes the exact
    /// <c>ivan.petrov</c> example above.</item>
    /// <item>Whatever <see cref="Path.GetExtension(string)"/> returns for anything else (a FILE, not a
    /// directory) is only accepted if it matches <c>^\.[a-z0-9]{1,8}$</c> (case-insensitive, checked
    /// against the already-lower-cased value) — closing the unicode/punctuation/implausibly-long class
    /// of fragment (e.g. <c>.日本語</c>, <c>.a-b</c>) by degrading to the closed
    /// <see cref="OtherExtension"/> bucket. <b>Known, accepted residual limitation:</b> a short,
    /// alphanumeric FILE-name fragment that happens to look exactly like a plausible extension (e.g. a
    /// file literally named <c>report.smith</c>) is structurally indistinguishable from a real one and
    /// is NOT filtered by this regex — there is no way to tell "an obscure but real extension" from "a
    /// surname that happens to be short and alphabetic" without maintaining an explicit allow-list of
    /// known real extensions (rejected — see ADR-027 Amendment, "Alternatives considered").</item>
    /// </list>
    /// The previous <c>catch (ArgumentException)</c> around <see cref="Path.GetExtension(string)"/> is
    /// removed: that overload performs no path validation in modern .NET and does not throw for any
    /// input string, so the catch was unreachable dead code.
    /// </remarks>
    /// <param name="path">The file-system path an icon-pipeline operation was attempted against.</param>
    public static string SafeExtension(string path)
    {
        // NIT fix (P08 fix cycle 2, 2026-09-22): compute the extension FIRST — a path with no dotted
        // segment at all knowably yields string.Empty either way, so there is no need to pay for the
        // Directory.Exists disk I/O call below on every single icon-pipeline diagnostics event (the
        // common case, e.g. "C:\Users\me\Desktop\README" or any extension-less path). The
        // Directory.Exists check below is only reached — and only matters — when there's actually a
        // dotted segment to potentially misattribute as a file extension.
        var extension = Path.GetExtension(path); // never throws in modern .NET — no path validation performed
        if (string.IsNullOrEmpty(extension)) return string.Empty;

        // A directory's own name can legitimately contain a dot (e.g. a username like
        // "ivan.petrov") that has nothing to do with a file type — never derive an "extension" from
        // one at all.
        if (Directory.Exists(path)) return string.Empty;

        extension = extension.ToLowerInvariant();
        return SafeExtensionPattern().IsMatch(extension) ? extension : OtherExtension;
    }

    [GeneratedRegex(@"^\.[a-z0-9]{1,8}$")]
    private static partial Regex SafeExtensionPattern();
}
