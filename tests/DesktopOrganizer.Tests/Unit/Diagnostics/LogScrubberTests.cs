using DesktopOrganizer.Services.Diagnostics;

namespace DesktopOrganizer.Tests.Unit.Diagnostics;

/// <summary>
/// Unit tests for <see cref="LogScrubber"/> — the path/username redaction <c>IAppLog</c> applies to
/// every string value before it reaches disk (ADR-033 part 1).
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class LogScrubberTests
{
    // ── Drive-letter paths ───────────────────────────────────────────────────

    /// <summary>A drive-letter path with a recognizable extension is reduced to <c>&lt;path:.ext&gt;</c>.</summary>
    [Fact]
    public void Scrub_DriveLetterPathWithExtension_ReplacedWithPathAndExtension()
    {
        var result = LogScrubber.Scrub(@"failed to read C:\Users\alice\Documents\report.docx");

        Assert.Equal("failed to read <path:.docx>", result);
    }

    /// <summary>A drive-letter path with no extension (a bare folder) is reduced to <c>&lt;path&gt;</c>.</summary>
    [Fact]
    public void Scrub_DriveLetterPathWithoutExtension_ReplacedWithBarePath()
    {
        var result = LogScrubber.Scrub(@"could not access C:\Users\alice\Desktop\MyFolder");

        Assert.Equal("could not access <path>", result);
    }

    /// <summary>
    /// L4 (release-hardening review) regression: an <c>https://</c>/<c>http://</c> URL must not be
    /// mangled — the old, unanchored pattern matched the single letter right before "://" (e.g. the "s"
    /// in "https") as if it were a one-letter drive, turning "https://example.com/a.txt" into
    /// "http&lt;path:.txt&gt;".
    /// </summary>
    /// <param name="message">The message containing a URL, under test.</param>
    /// <param name="expected">The expected (unchanged) result.</param>
    [Theory]
    [InlineData("see https://example.com/a.txt for details", "see https://example.com/a.txt for details")]
    [InlineData("see http://example.com/releases for details", "see http://example.com/releases for details")]
    [InlineData("see ftp://example.com/file.zip for details", "see ftp://example.com/file.zip for details")]
    public void Scrub_UrlWithScheme_LeftUnchanged(string message, string expected)
    {
        var result = LogScrubber.Scrub(message);

        Assert.Equal(expected, result);
    }

    /// <summary>A genuine drive-letter path immediately after a URL in the same message is still scrubbed — the URL fix must not disable path scrubbing generally.</summary>
    [Fact]
    public void Scrub_UrlFollowedByDriveLetterPath_OnlyThePathIsScrubbed()
    {
        var result = LogScrubber.Scrub(@"see https://example.com/docs, local copy at C:\Users\alice\a.txt");

        Assert.Equal(@"see https://example.com/docs, local copy at <path:.txt>", result);
    }

    // ── UNC paths ─────────────────────────────────────────────────────────────

    /// <summary>A UNC path is reduced to <c>&lt;path:.ext&gt;</c>, same as a drive-letter path.</summary>
    [Fact]
    public void Scrub_UncPath_ReplacedWithPathAndExtension()
    {
        var result = LogScrubber.Scrub(@"copy failed: \\server\share\folder\file.pdf");

        Assert.Equal("copy failed: <path:.pdf>", result);
    }

    // ── Multiple paths in one message ────────────────────────────────────────

    /// <summary>Every path occurrence in a message is scrubbed independently, not just the first.</summary>
    [Fact]
    public void Scrub_MultiplePaths_AllReplaced()
    {
        var result = LogScrubber.Scrub(@"move failed: C:\Users\alice\a.txt -> D:\Backup\b.log");

        Assert.Equal("move failed: <path:.txt> -> <path:.log>", result);
    }

    // ── No paths present ──────────────────────────────────────────────────────

    /// <summary>A message with no path-shaped substring is returned unchanged.</summary>
    [Fact]
    public void Scrub_NoPaths_ReturnsUnchanged()
    {
        const string message = "the operation timed out after 3 attempts";

        var result = LogScrubber.Scrub(message);

        Assert.Equal(message, result);
    }

    /// <summary><see langword="null"/>/empty input round-trips to an empty string without throwing.</summary>
    /// <param name="input">The <see langword="null"/> or empty input to scrub.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Scrub_NullOrEmpty_ReturnsEmptyString(string? input)
    {
        var result = LogScrubber.Scrub(input);

        Assert.Equal(string.Empty, result);
    }

    // ── Username ──────────────────────────────────────────────────────────────

    /// <summary>An occurrence of the current user's account name outside any path is replaced with
    /// the literal <c>%USERPROFILE%</c>.</summary>
    [Fact]
    public void Scrub_CurrentUserName_ReplacedWithUserProfileToken()
    {
        var userName = Environment.UserName;
        var message = $"profile for {userName} could not be resolved";

        var result = LogScrubber.Scrub(message);

        Assert.Equal("profile for %USERPROFILE% could not be resolved", result);
        Assert.DoesNotContain(userName, result, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A message containing neither a path nor the user name is untouched by either pass.</summary>
    [Fact]
    public void Scrub_UnrelatedText_LeavesUnrelatedWordsAlone()
    {
        const string message = "manifest write failed, aborting rename";

        var result = LogScrubber.Scrub(message);

        Assert.Equal(message, result);
    }

    /// <summary>
    /// L4 (release-hardening review): an account name shorter than 3 characters is never substituted —
    /// it is far more likely to collide with an ordinary word/substring than to actually identify the
    /// user. Uses the internal <c>userName</c>-override seam since the real <see cref="Environment.UserName"/>
    /// can't be faked to a short value from a test.
    /// </summary>
    /// <param name="shortUserName">The account name (shorter than 3 characters) under test.</param>
    [Theory]
    [InlineData("a")]
    [InlineData("al")]
    public void Scrub_ShortUserNameOverride_NotSubstituted(string shortUserName)
    {
        const string message = "value calculation failed";

        var result = LogScrubber.Scrub(message, shortUserName);

        Assert.Equal(message, result);
    }

    /// <summary>A 3-character (the shortest still-substituted length) account name is still redacted.</summary>
    [Fact]
    public void Scrub_ThreeCharacterUserNameOverride_IsSubstituted()
    {
        var result = LogScrubber.Scrub("profile for bob could not be resolved", "bob");

        Assert.Equal("profile for %USERPROFILE% could not be resolved", result);
    }
}
