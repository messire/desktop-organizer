using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DesktopOrganizer.Services.SingleInstance;

namespace DesktopOrganizer.Tests.Unit.Installer;

/// <summary>
/// T-INS-01 (docs/PLANS/phase-3-single-instance-ipc.md §5.4): pins <c>installer/DesktopOrganizer.iss</c>'s
/// <c>#define AppRunningMutexName</c>/<c>#define AppShutdownEventName</c> values against
/// <see cref="SingleInstanceNaming.InstallerDetectionMutexName"/> and
/// <see cref="SingleInstanceNaming.InstallerShutdownRequestEventName"/>. Neither name must change without
/// updating both sides in the same commit (ADR-023 D-1) — Inno Setup's own running-instance detection and
/// graceful-shutdown request depend on staying exactly in sync with what the app actually creates.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class InstallerMutexContractTests
{
    // O-1: "\r?$" rather than a bare "$" — RegexOptions.Multiline makes "$" match just before "\n", not
    // before "\r\n", so on a CRLF-checked-out-on-Windows file (the norm for this repo) a bare "$" would
    // leave the trailing "\r" inside the captured group, and Assert.Equal against the clean
    // SingleInstanceNaming constant would fail on every CRLF checkout despite the values actually
    // matching.
    private static readonly Regex AppRunningMutexPattern =
        new(@"^#define AppRunningMutexName ""(?<name>.+)""\r?$", RegexOptions.Multiline);

    private static readonly Regex AppShutdownEventPattern =
        new(@"^#define AppShutdownEventName ""(?<name>.+)""\r?$", RegexOptions.Multiline);

    // O-1: matches "AppMutex=", "  AppMutex=", "AppMutex =", and any casing — Inno Setup itself is
    // case-insensitive about [Setup] directive names and tolerates leading whitespace/space-before-'=',
    // so a regex that only matched the single exact spelling this feature happened to remove could miss
    // a regression written with slightly different formatting.
    private static readonly Regex LegacyAppMutexDirectivePattern =
        new(@"^\s*AppMutex\s*=", RegexOptions.Multiline | RegexOptions.IgnoreCase);

    // C-1 regression: Inno Setup's compiler detects a new INI-style section purely by "first non-blank
    // character of a physical line is '['" — a line-based scan that runs regardless of whether that line
    // is, to a human reader, plainly inside a Pascal `{ ... }` block comment. A comment line that happens
    // to start with '[' (e.g. "[Code] has no handle...") is misparsed as "Invalid section tag" and aborts
    // the whole compile. See Iss_CodeSectionHasNoLineStartingWithOpenBracket below.
    private static readonly Regex CodeSectionHeaderPattern = new(@"^\[Code\]\r?$", RegexOptions.Multiline);

    /// <summary>
    /// Walks up from the test assembly's own output directory to find the repo root (identified by
    /// <c>DesktopOrganizer.sln</c>) and reads <c>installer/DesktopOrganizer.iss</c> from there. This
    /// test is deliberately structured so that failing to find the repo root is a test FAILURE, not a
    /// skip: a silently-skipped version of this test would be worse than not having it at all, since it
    /// would look green while pinning nothing (ADR-023 phase-3 plan §5.4).
    /// </summary>
    [Fact]
    public void IssAppRunningMutex_MatchesSingleInstanceNamingConstant()
    {
        var issContent = ReadIssContent();

        var match = AppRunningMutexPattern.Match(issContent);
        Assert.True(match.Success, "installer/DesktopOrganizer.iss has no '#define AppRunningMutexName \"...\"' line.");

        var issValue = match.Groups["name"].Value.Trim();
        Assert.Equal(SingleInstanceNaming.InstallerDetectionMutexName, issValue);
    }

    /// <summary>
    /// Same contract as <see cref="IssAppRunningMutex_MatchesSingleInstanceNamingConstant"/>, for the
    /// graceful-shutdown-request event <c>InstallerShutdownRequestListener</c> waits on
    /// (<c>EnsureAppClosed</c>/<c>RequestGracefulShutdown</c> in the <c>.iss</c>'s <c>[Code]</c> section).
    /// </summary>
    [Fact]
    public void IssAppShutdownEvent_MatchesSingleInstanceNamingConstant()
    {
        var issContent = ReadIssContent();

        var match = AppShutdownEventPattern.Match(issContent);
        Assert.True(match.Success, "installer/DesktopOrganizer.iss has no '#define AppShutdownEventName \"...\"' line.");

        var issValue = match.Groups["name"].Value.Trim();
        Assert.Equal(SingleInstanceNaming.InstallerShutdownRequestEventName, issValue);
    }

    /// <summary>
    /// Guards against regressing to the hard-blocking behaviour this feature replaces: <c>[Setup]</c>'s
    /// own <c>AppMutex=</c> directive makes Inno Setup show its own non-closable "close the application
    /// and click OK to continue" prompt with no <c>[Code]</c> hook to intervene, which is exactly what
    /// <c>EnsureAppClosed</c>/<c>PrepareToInstall</c>/<c>InitializeUninstall</c> replace with an
    /// ask-then-close-gracefully-then-retry flow. If <c>AppMutex=</c> ever comes back, that flow is
    /// silently bypassed by Inno's own blocking prompt running first.
    /// </summary>
    [Fact]
    public void Iss_DoesNotUseLegacyAppMutexDirective()
    {
        var issContent = ReadIssContent();
        const string message =
            "installer/DesktopOrganizer.iss must not use the [Setup] 'AppMutex=' directive — it reinstates " +
            "the old non-closable blocking prompt instead of EnsureAppClosed's graceful close-then-continue flow.";

        Assert.False(LegacyAppMutexDirectivePattern.IsMatch(issContent), message);
    }

    /// <summary>
    /// C-1 regression: a real compile failure ("Error on line 150: Invalid section tag") caused by a
    /// <c>[Code]</c> comment line starting with the literal text <c>[Code] has no handle...</c> — see
    /// <see cref="CodeSectionHeaderPattern"/>'s remarks for the mechanism. Checks every physical line
    /// inside the <c>[Code]</c> section (i.e. from the <c>[Code]</c> header to end of file — it is the
    /// last section in this script) except the header line itself: none may start with <c>[</c> after
    /// leading whitespace is trimmed.
    /// </summary>
    [Fact]
    public void Iss_CodeSectionHasNoLineStartingWithOpenBracket()
    {
        var issContent = ReadIssContent();

        var headerMatch = CodeSectionHeaderPattern.Match(issContent);
        Assert.True(headerMatch.Success, "installer/DesktopOrganizer.iss has no '[Code]' section header.");

        // Everything strictly after the "[Code]" header line itself — that line legitimately starts with
        // '[' as the real section tag, and is deliberately excluded from the scan below.
        var codeSectionBody = issContent[(headerMatch.Index + headerMatch.Length)..];

        var offendingLines = codeSectionBody
            .Split('\n')
            .Select(line => line.TrimEnd('\r').TrimStart())
            .Where(trimmed => trimmed.StartsWith('['))
            .ToList();

        var message =
            "installer/DesktopOrganizer.iss's [Code] section has a line starting with '[' after trimming " +
            "leading whitespace — Inno Setup's compiler misparses this as a new section header ('Invalid " +
            "section tag') even inside a Pascal comment. Offending line(s): " + string.Join(" | ", offendingLines);

        Assert.True(offendingLines.Count == 0, message);
    }

    private static string ReadIssContent()
    {
        var repoRoot = FindRepoRoot(AppContext.BaseDirectory);
        Assert.True(
            repoRoot is not null,
            $"Could not locate the repository root (a directory containing DesktopOrganizer.sln) walking up from '{AppContext.BaseDirectory}'.");

        var issPath = Path.Combine(repoRoot!, "installer", "DesktopOrganizer.iss");
        Assert.True(File.Exists(issPath), $"Installer script not found at '{issPath}'.");

        return File.ReadAllText(issPath);
    }

    private static string? FindRepoRoot(string startDirectory)
    {
        var current = new DirectoryInfo(startDirectory);

        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "DesktopOrganizer.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }
}
