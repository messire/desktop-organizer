using System.IO;
using System.Text.RegularExpressions;

namespace DesktopOrganizer.Tests.Unit.Installer;

/// <summary>
/// Pins the release-engineering metadata contracts from ADR-038 (G1): <c>installer/DesktopOrganizer.iss</c>'s
/// publisher/support links must no longer be the placeholder <c>https://github.com/</c>, and the optional
/// code-signing hook (<c>SignTool=</c>) must stay gated behind the <c>#ifdef SignEnabled</c> preprocessor
/// guard so a plain, no-env-vars compile never requires a sign tool to exist.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.UI))]
public class InstallerMetadataContractTests
{
    private const string ExpectedAppUrl = "https://github.com/messire";

    private static readonly Regex MyAppUrlDefinePattern =
        new(@"^#define MyAppURL ""(?<value>[^""]+)""\s*$", RegexOptions.Multiline);

    private static readonly Regex AppPublisherUrlPattern =
        new(@"^AppPublisherURL=(?<value>.+?)\r?$", RegexOptions.Multiline);

    private static readonly Regex AppSupportUrlPattern =
        new(@"^AppSupportURL=(?<value>.+?)\r?$", RegexOptions.Multiline);

    private static readonly Regex SignToolDirectivePattern =
        new(@"^\s*SignTool\s*=", RegexOptions.Multiline | RegexOptions.IgnoreCase);

    private static readonly Regex SignEnabledIfdefPattern =
        new(@"#ifdef\s+SignEnabled\s*.*?SignTool\s*=.*?#endif", RegexOptions.Singleline | RegexOptions.IgnoreCase);

    /// <summary>
    /// The <c>#define MyAppURL</c> the <c>[Setup]</c> section's <c>AppPublisherURL</c>/<c>AppSupportURL</c>
    /// resolve through must be the project's real GitHub home, not the placeholder
    /// <c>https://github.com/</c> (a bare domain with no owner) that predates ADR-038.
    /// </summary>
    [Fact]
    public void IssMyAppUrl_IsNotThePlaceholderAndMatchesTheProjectHome()
    {
        var issContent = ReadIssContent();

        var match = MyAppUrlDefinePattern.Match(issContent);
        Assert.True(match.Success, "installer/DesktopOrganizer.iss has no '#define MyAppURL \"...\"' line.");

        var value = match.Groups["value"].Value;
        Assert.NotEqual("https://github.com/", value);
        Assert.Equal(ExpectedAppUrl, value);
    }

    /// <summary>
    /// <c>AppPublisherURL</c> must reference the <c>MyAppURL</c> define (not repeat the literal or fall
    /// back to the old placeholder) — <see cref="IssMyAppUrl_IsNotThePlaceholderAndMatchesTheProjectHome"/>
    /// already pins the define's own value.
    /// </summary>
    [Fact]
    public void IssAppPublisherUrl_ReferencesMyAppUrlDefine()
    {
        var issContent = ReadIssContent();

        var match = AppPublisherUrlPattern.Match(issContent);
        Assert.True(match.Success, "installer/DesktopOrganizer.iss has no 'AppPublisherURL=...' line.");
        Assert.Equal("{#MyAppURL}", match.Groups["value"].Value.Trim());
    }

    /// <summary>
    /// <c>AppSupportURL</c> must reference the <c>MyAppURL</c> define — this is the specific line ADR-038
    /// replaces (it used to be hardcoded to the bare, owner-less <c>https://github.com/</c> placeholder).
    /// </summary>
    [Fact]
    public void IssAppSupportUrl_ReferencesMyAppUrlDefine()
    {
        var issContent = ReadIssContent();

        var match = AppSupportUrlPattern.Match(issContent);
        Assert.True(match.Success, "installer/DesktopOrganizer.iss has no 'AppSupportURL=...' line.");
        Assert.Equal("{#MyAppURL}", match.Groups["value"].Value.Trim());
    }

    /// <summary>
    /// The optional code-signing hook must never be unconditional: a plain compile (no
    /// <c>/DSignEnabled</c>, the default for both a manual ISCC invocation and
    /// <c>installer/build.ps1</c> with no signing environment variables set) must not require a sign
    /// tool to exist. Guards against someone "simplifying" the <c>.iss</c> by dropping the
    /// <c>#ifdef</c>/<c>#endif</c> guard around <c>SignTool=</c>.
    /// </summary>
    [Fact]
    public void IssSignToolDirective_IsGatedBehindSignEnabledIfdef()
    {
        var issContent = ReadIssContent();

        const string missingSignToolMessage =
            "installer/DesktopOrganizer.iss has no 'SignTool=' directive — the optional code-signing hook is missing.";
        Assert.True(SignToolDirectivePattern.IsMatch(issContent), missingSignToolMessage);

        const string ungatedSignToolMessage =
            "installer/DesktopOrganizer.iss's 'SignTool=' directive must be wrapped in '#ifdef SignEnabled ... #endif' " +
            "so an unsigned build (the default) never requires a sign tool to exist.";
        Assert.True(SignEnabledIfdefPattern.IsMatch(issContent), ungatedSignToolMessage);
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
