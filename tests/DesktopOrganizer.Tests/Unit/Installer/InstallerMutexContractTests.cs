using System.IO;
using System.Text.RegularExpressions;
using DesktopOrganizer.Services.SingleInstance;

namespace DesktopOrganizer.Tests.Unit.Installer;

/// <summary>
/// T-INS-01 (docs/PLANS/phase-3-single-instance-ipc.md §5.4): pins <c>installer/DesktopOrganizer.iss</c>'s
/// <c>AppMutex=</c> value against <see cref="SingleInstanceNaming.InstallerDetectionMutexName"/>. That
/// name must never change (ADR-023 D-1) — Inno Setup's own running-instance detection depends on it
/// staying exactly in sync with what the app actually creates.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class InstallerMutexContractTests
{
    private static readonly Regex AppMutexPattern = new(@"^AppMutex=(?<name>.+)$", RegexOptions.Multiline);

    /// <summary>
    /// Walks up from the test assembly's own output directory to find the repo root (identified by
    /// <c>DesktopOrganizer.sln</c>) and reads <c>installer/DesktopOrganizer.iss</c> from there. This
    /// test is deliberately structured so that failing to find the repo root is a test FAILURE, not a
    /// skip: a silently-skipped version of this test would be worse than not having it at all, since it
    /// would look green while pinning nothing (ADR-023 phase-3 plan §5.4).
    /// </summary>
    [Fact]
    public void IssAppMutex_MatchesSingleInstanceNamingConstant()
    {
        var repoRoot = FindRepoRoot(AppContext.BaseDirectory);
        Assert.True(
            repoRoot is not null,
            $"Could not locate the repository root (a directory containing DesktopOrganizer.sln) walking up from '{AppContext.BaseDirectory}'.");

        var issPath = Path.Combine(repoRoot!, "installer", "DesktopOrganizer.iss");
        Assert.True(File.Exists(issPath), $"Installer script not found at '{issPath}'.");

        var issContent = File.ReadAllText(issPath);
        var match = AppMutexPattern.Match(issContent);
        Assert.True(match.Success, $"'{issPath}' has no 'AppMutex=' line.");

        var issValue = match.Groups["name"].Value.Trim();
        Assert.Equal(SingleInstanceNaming.InstallerDetectionMutexName, issValue);
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
