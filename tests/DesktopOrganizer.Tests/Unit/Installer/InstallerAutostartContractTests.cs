using System.IO;
using System.Text.RegularExpressions;
using DesktopOrganizer.UI.Services;

namespace DesktopOrganizer.Tests.Unit.Installer;

/// <summary>
/// Pins the autostart registry contract shared between <c>installer/DesktopOrganizer.iss</c> (the
/// "Start with Windows" checkbox on the Finished page) and <see cref="AutostartService"/> (the
/// checkbox in the Settings window). Both toggle the very same HKCU Run value — there is no JSON
/// mirror — so the key path and value name literals in the <c>.iss</c> must stay byte-for-byte equal
/// to the service's constants, otherwise the two checkboxes silently stop being "the same setting".
/// The installer only ever ships a Release build, so the contract is pinned against
/// <see cref="AutostartService.InstallerValueName"/>. The Task Manager flag key
/// (<see cref="AutostartService.StartupApprovedKeyPath"/>) is part of the same contract: both sides treat a
/// value Windows disabled there as "off" and clear the flag when enabling.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.UI))]
public class InstallerAutostartContractTests
{
    private static readonly Regex RunKeyPattern =
        new(@"^#define AutostartRunKey ""(?<value>[^""]+)""\s*$", RegexOptions.Multiline);

    private static readonly Regex ApprovedKeyPattern =
        new(@"^#define AutostartApprovedKey ""(?<value>[^""]+)""\s*$", RegexOptions.Multiline);

    private static readonly Regex ValueNamePattern =
        new(@"^#define AutostartValueName ""(?<value>[^""]+)""\s*$", RegexOptions.Multiline);

    /// <summary>
    /// Verifies that the installer script's <c>AutostartRunKey</c> define matches
    /// <see cref="AutostartService.RunKeyPath"/> byte-for-byte.
    /// </summary>
    [Fact]
    public void IssAutostartRunKey_MatchesAutostartServiceConstant()
    {
        var issContent = ReadInstallerScript();
        var match = RunKeyPattern.Match(issContent);
        Assert.True(match.Success, "Installer script has no '#define AutostartRunKey \"...\"' line.");
        Assert.Equal(AutostartService.RunKeyPath, match.Groups["value"].Value);
    }

    /// <summary>
    /// Verifies that the installer script's <c>AutostartApprovedKey</c> define matches
    /// <see cref="AutostartService.StartupApprovedKeyPath"/> byte-for-byte.
    /// </summary>
    [Fact]
    public void IssAutostartApprovedKey_MatchesAutostartServiceConstant()
    {
        var issContent = ReadInstallerScript();
        var match = ApprovedKeyPattern.Match(issContent);
        Assert.True(match.Success, "Installer script has no '#define AutostartApprovedKey \"...\"' line.");
        Assert.Equal(AutostartService.StartupApprovedKeyPath, match.Groups["value"].Value);
    }

    /// <summary>
    /// Verifies that the installer script's <c>AutostartValueName</c> define matches
    /// <see cref="AutostartService.InstallerValueName"/> byte-for-byte.
    /// </summary>
    [Fact]
    public void IssAutostartValueName_MatchesAutostartServiceConstant()
    {
        var issContent = ReadInstallerScript();
        var match = ValueNamePattern.Match(issContent);
        Assert.True(match.Success, "Installer script has no '#define AutostartValueName \"...\"' line.");
        Assert.Equal(AutostartService.InstallerValueName, match.Groups["value"].Value);
    }

    /// <summary>
    /// The [Code] section must consume the two defines rather than repeat the literals inline —
    /// otherwise the defines above could be kept in sync while the actual registry calls drift.
    /// </summary>
    [Fact]
    public void IssCodeSection_UsesDefinesInsteadOfInlineLiterals()
    {
        var issContent = ReadInstallerScript();
        var codeStart = issContent.IndexOf("[Code]", StringComparison.Ordinal);
        Assert.True(codeStart >= 0, "Installer script has no [Code] section.");

        var codeSection = issContent[codeStart..];
        Assert.Contains("'{#AutostartRunKey}'", codeSection, StringComparison.Ordinal);
        Assert.Contains("'{#AutostartApprovedKey}'", codeSection, StringComparison.Ordinal);
        Assert.Contains("'{#AutostartValueName}'", codeSection, StringComparison.Ordinal);
        Assert.DoesNotContain($"'{AutostartService.RunKeyPath}'", codeSection, StringComparison.Ordinal);
        Assert.DoesNotContain($"'{AutostartService.StartupApprovedKeyPath}'", codeSection, StringComparison.Ordinal);
        Assert.DoesNotContain($"'{AutostartService.InstallerValueName}'", codeSection, StringComparison.Ordinal);
    }

    private static string ReadInstallerScript()
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
