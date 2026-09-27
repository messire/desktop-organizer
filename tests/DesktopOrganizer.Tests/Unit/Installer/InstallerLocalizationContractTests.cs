using System.IO;
using System.Text.RegularExpressions;

namespace DesktopOrganizer.Tests.Unit.Installer;

/// <summary>
/// Pins the two localization-related installer contracts from docs/PLANS/localization.md §4.2/§6 (WP-1):
/// <list type="bullet">
/// <item>
/// <c>installer/DesktopOrganizer.iss</c>'s Russian <c>AppRunningCloseFailedRetry</c> message must mention
/// both the current, localized tray label ("Выход") and the legacy English one ("Exit") — this message is
/// shown while an <em>already-installed, older</em> version is still running, and that older version's
/// tray only ever had the English label (docs/PLANS/localization.md §4.2: "Сообщение адресовано старой
/// установленной версии, а у неё трей только английский"). The English string is deliberately left
/// unchanged (an English-language installer only ever talks to an English-only-tray predecessor).
/// </item>
/// <item>
/// <c>installer/build.ps1</c> must pass <c>SatelliteResourceLanguages=en;ru</c> to <c>dotnet publish</c>
/// (G-8, §6.8) so publish doesn't drag in satellite resource assemblies for the ~13 other cultures WPF/.NET
/// ship by default, and must verify the actual publish output afterwards rather than merely trusting the
/// MSBuild property.
/// </item>
/// </list>
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class InstallerLocalizationContractTests
{
    private static readonly Regex RussianCloseFailedRetryPattern =
        new(@"^russian\.AppRunningCloseFailedRetry=(?<value>.+?)\r?$", RegexOptions.Multiline);

    private static readonly Regex EnglishCloseFailedRetryPattern =
        new(@"^english\.AppRunningCloseFailedRetry=(?<value>.+?)\r?$", RegexOptions.Multiline);

    private static readonly Regex SatelliteResourceLanguagesPattern =
        new(@"-p:SatelliteResourceLanguages=en(?:;|%3B)ru", RegexOptions.IgnoreCase);

    /// <summary>
    /// Verifies that the Russian "close failed, retry manually" message names both the current
    /// localized tray label and the legacy English one, so a user upgrading from an older,
    /// English-only-tray build can actually find the menu item being described.
    /// </summary>
    [Fact]
    public void Iss_RussianCloseFailedRetry_MentionsBothLocalizedAndLegacyTrayExitLabel()
    {
        var issContent = ReadIssContent();

        var match = RussianCloseFailedRetryPattern.Match(issContent);
        Assert.True(match.Success, "installer/DesktopOrganizer.iss has no 'russian.AppRunningCloseFailedRetry=...' line.");

        var value = match.Groups["value"].Value;
        Assert.Contains("Выход", value, StringComparison.Ordinal);
        Assert.Contains("Exit", value, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that the English "close failed, retry manually" message is left as-is: it only ever
    /// talks to an English-language installer's own English-only-tray predecessor, so it never needs
    /// the "legacy vs. current label" disambiguation the Russian string above needs.
    /// </summary>
    [Fact]
    public void Iss_EnglishCloseFailedRetry_StillOnlyMentionsExit()
    {
        var issContent = ReadIssContent();

        var match = EnglishCloseFailedRetryPattern.Match(issContent);
        Assert.True(match.Success, "installer/DesktopOrganizer.iss has no 'english.AppRunningCloseFailedRetry=...' line.");

        var value = match.Groups["value"].Value;
        Assert.Contains("Exit", value, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that <c>installer/build.ps1</c> passes an explicit <c>SatelliteResourceLanguages=en;ru</c>
    /// MSBuild property to <c>dotnet publish</c>, so the self-contained publish output never carries
    /// satellite resource assemblies for cultures beyond the two this app supports.
    /// </summary>
    [Fact]
    public void BuildScript_PassesSatelliteResourceLanguagesToPublish()
    {
        var scriptContent = ReadBuildScriptContent();

        Assert.Matches(SatelliteResourceLanguagesPattern, scriptContent);
    }

    /// <summary>
    /// Verifies that <c>installer/build.ps1</c> actually inspects the publish output for stray
    /// culture folders and for the presence of the Russian localization satellite assembly, rather than
    /// only trusting the MSBuild property above (docs/PLANS/localization.md §4.2: "проверка после
    /// publish").
    /// </summary>
    [Fact]
    public void BuildScript_VerifiesPublishOutputCultureFoldersAndLocalizationSatellite()
    {
        var scriptContent = ReadBuildScriptContent();

        Assert.Contains("*.resources.dll", scriptContent, StringComparison.Ordinal);
        Assert.Contains("DesktopOrganizer.Localization.resources.dll", scriptContent, StringComparison.Ordinal);
        Assert.Contains("throw", scriptContent, StringComparison.Ordinal);
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

    private static string ReadBuildScriptContent()
    {
        var repoRoot = FindRepoRoot(AppContext.BaseDirectory);
        Assert.True(
            repoRoot is not null,
            $"Could not locate the repository root (a directory containing DesktopOrganizer.sln) walking up from '{AppContext.BaseDirectory}'.");

        var scriptPath = Path.Combine(repoRoot!, "installer", "build.ps1");
        Assert.True(File.Exists(scriptPath), $"Build script not found at '{scriptPath}'.");

        return File.ReadAllText(scriptPath);
    }

    private static string? FindRepoRoot(string startDirectory)
    {
        var current = new DirectoryInfo(startDirectory);

        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "DesktopOrganizer.sln")))
                return current.FullName;

            current = current.Parent;
        }

        return null;
    }
}
