using System.IO;
using System.Text.RegularExpressions;

namespace DesktopOrganizer.Services.Diagnostics;

/// <summary>
/// Strips personally-identifying path/user fragments out of free-form text before it is ever allowed
/// onto disk via <c>IAppLog</c> (ADR-033 part 1). Diagnostics files are meant to be safe to hand to a
/// tester without exposing anything personal about the machine they were captured on — the same
/// invariant <see cref="DesktopOrganizer.Services.Abstractions.DiagnosticEvent"/> already documents for
/// the verbose gesture sink, extended here to the free-form exception messages/stack traces the always-on
/// app log carries.
/// </summary>
/// <remarks>
/// Pure and allocation-light; does no I/O. Two independent substitutions run in sequence:
/// <list type="number">
/// <item>Every rooted Windows path (drive-letter <c>C:\...</c> or UNC <c>\\server\share\...</c>) is
/// replaced with <c>&lt;path:.ext&gt;</c> (or bare <c>&lt;path&gt;</c> when the matched segment has no
/// file extension) — keeping only the extension, which is useful for triage without identifying a real
/// file or folder name.</item>
/// <item>Any remaining occurrence of the current user's account name (<see cref="Environment.UserName"/>)
/// — e.g. embedded in a message that isn't itself a path — is replaced with the literal
/// <c>%USERPROFILE%</c>.</item>
/// </list>
/// </remarks>
public static class LogScrubber
{
    /// <summary>Shortest account name this method will ever substitute — see <see cref="Scrub(string?)"/>'s remarks.</summary>
    private const int MinUserNameLength = 3;

    // Drive-letter path: "C:\" (or "c:/", tolerated) followed by anything that isn't whitespace or a
    // character that would never appear in a Windows path/typically closes a quoted/bracketed message.
    // L4 (release-hardening review): anchored with a negative lookbehind so the single drive letter is
    // never actually the tail of a longer word/scheme — without it, "https://example.com" itself matched
    // (the "s:" right before "//"), mangling the URL into "http<path>" instead of leaving it alone.
    private static readonly Regex DriveLetterPathPattern =
        new(@"(?<![A-Za-z])[A-Za-z]:[\\/][^\s""'<>|]*", RegexOptions.Compiled);

    // UNC path: "\\server\share\..." — at least two path segments after the leading "\\".
    private static readonly Regex UncPathPattern =
        new(@"\\\\[^\s""'<>|\\]+\\[^\s""'<>|]*", RegexOptions.Compiled);

    /// <summary>
    /// Scrubs <paramref name="text"/> of rooted/UNC file paths and the current user's account name.
    /// </summary>
    /// <param name="text">The text to scrub, or <see langword="null"/>/empty (returned unchanged).</param>
    /// <returns>
    /// <paramref name="text"/> with every matched path replaced by <c>&lt;path:.ext&gt;</c>/<c>&lt;path&gt;</c>
    /// and every occurrence of <see cref="Environment.UserName"/> replaced by <c>%USERPROFILE%</c>; the
    /// original string, unmodified, when it contains neither.
    /// </returns>
    public static string Scrub(string? text) => Scrub(text, Environment.UserName);

    /// <summary>
    /// Same as <see cref="Scrub(string?)"/>, but with the account name to redact passed explicitly.
    /// Internal — a test seam only (<c>Environment.UserName</c> itself can't be overridden from a test),
    /// so <c>LogScrubberTests</c> can pin the "skip names shorter than <see cref="MinUserNameLength"/>
    /// characters" rule (L4, release-hardening review) deterministically instead of depending on whatever
    /// account name happens to be running the test suite.
    /// </summary>
    /// <param name="text">See <see cref="Scrub(string?)"/>.</param>
    /// <param name="userName">The account name to redact, or <see langword="null"/>/empty to skip that pass entirely.</param>
    internal static string Scrub(string? text, string? userName)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;

        var scrubbed = UncPathPattern.Replace(text, match => ReplacePath(match.Value));
        scrubbed = DriveLetterPathPattern.Replace(scrubbed, match => ReplacePath(match.Value));

        // L4 (release-hardening review): a very short account name (1-2 chars) is far more likely to
        // collide with an ordinary word/substring inside an unrelated message than to actually identify
        // the user — skip the substitution entirely rather than mangling unrelated text on a coincidental
        // match.
        if (!string.IsNullOrEmpty(userName) && userName.Length >= MinUserNameLength && scrubbed.Contains(userName, StringComparison.OrdinalIgnoreCase))
            scrubbed = Regex.Replace(scrubbed, Regex.Escape(userName), "%USERPROFILE%", RegexOptions.IgnoreCase);

        return scrubbed;
    }

    private static string ReplacePath(string path)
    {
        var ext = Path.GetExtension(path);
        return string.IsNullOrEmpty(ext) ? "<path>" : $"<path:{ext}>";
    }
}
