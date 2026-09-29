using System.IO;
using System.Text.RegularExpressions;

namespace DesktopOrganizer.Tests.Unit.Localization;

/// <summary>
/// Phase 6.0 of docs/PLANS/design-system-1x.md (T-6, "храповик"): an ALLOWLIST gate over every
/// <c>MessageBox.Show(</c> call site under <c>src/DesktopOrganizer.UI</c> — docs/PLANS/design-system-1x.md
/// §2.8/§0.2's own matrix says every remaining call site is a permanent, deliberate exception (crash/
/// startup/CLI restore, ADR-033: these must keep minimal dependencies and run before/after WPF's own UI is
/// even up); phase 6.3 removed the one time-limited entry (<c>Services/ConfirmationService.cs</c>, which now
/// delegates to <c>IDialogPresenter</c>/<c>AppDialogWindow</c> instead). Unlike
/// <see cref="ChromeXamlLiteralGuardTests"/>'s pure count ratchet, this is a closed ALLOWLIST (by file path):
/// a call site OUTSIDE the two files below is always a regression (a new, un-reviewed blocking dialog),
/// never "known debt" to merely track — so this gate does not merely cap a count, it names the exact two
/// files allowed to contain one.
/// </summary>
/// <remarks>
/// Reads source files directly off disk (<see cref="RepoRootLocator"/>), the same convention every other
/// guard test in this file group already uses.
/// </remarks>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class MessageBoxUsageGuardTests
{
    // A real invocation, not merely a textual mention (e.g. a comment referencing "MessageBox.Show" in
    // prose, which ChildWindow.xaml.cs's own remarks do, harmlessly, and must NOT be flagged here).
    private static readonly Regex MessageBoxShowInvocationPattern = new(@"MessageBox\.Show\s*\(", RegexOptions.Compiled);

    /// <summary>
    /// The only two files allowed to call <c>MessageBox.Show</c> — both permanent, minimal-dependency
    /// exceptions (ADR-033): crash/startup/CLI restore dialogs that must work before/after WPF's own UI is
    /// up.
    /// </summary>
    private static readonly Dictionary<string, string> AllowedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["App.xaml.cs"] = "crash dialog (ADR-033) — permanent, minimal-dependency exception",
        ["Program.cs"] = "startup/CLI restore dialogs (ADR-033) — permanent, minimal-dependency exception",
    };

    /// <summary>
    /// Scans every <c>*.cs</c> file under <c>src/DesktopOrganizer.UI</c> and fails, listing every offending
    /// <c>file:line</c>, if any <c>MessageBox.Show(</c> call site turns up outside <see cref="AllowedFiles"/>.
    /// </summary>
    [Fact]
    public void UiLayer_HasNoMessageBoxShowCallSiteOutsideAllowlist()
    {
        var repoRoot = RepoRootLocator.Find();
        var uiRoot = Path.Combine(repoRoot, "src", "DesktopOrganizer.UI");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(uiRoot, "*.cs", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(uiRoot, file).Replace('\\', '/');
            if (AllowedFiles.ContainsKey(relativePath)) continue;

            var text = File.ReadAllText(file);

            foreach (Match match in MessageBoxShowInvocationPattern.Matches(text))
            {
                var line = 1 + text[..match.Index].Count(c => c == '\n');
                offenders.Add($"{relativePath}:{line}");
            }
        }

        var message = "MessageBox.Show() call site(s) found outside the T-6 allowlist (docs/PLANS/design-system-1x.md §0.2/§2.8):\n" +
            string.Join('\n', offenders);
        Assert.True(offenders.Count == 0, message);
    }

    /// <summary>
    /// Sanity check on <see cref="AllowedFiles"/> itself: every allowlisted path must actually exist AND
    /// actually contain at least one <c>MessageBox.Show(</c> call — an allowlist entry for a file that no
    /// longer calls it (e.g. once 6.3 lands) is stale and should be removed in that same commit, not left
    /// to silently widen the allowlist for nothing.
    /// </summary>
    [Fact]
    public void AllowlistedFiles_ExistAndActuallyCallMessageBoxShow()
    {
        var repoRoot = RepoRootLocator.Find();
        var uiRoot = Path.Combine(repoRoot, "src", "DesktopOrganizer.UI");

        foreach (var relativePath in AllowedFiles.Keys)
        {
            var fullPath = Path.Combine(uiRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(fullPath), $"Allowlisted file '{relativePath}' does not exist — stale T-6 allowlist entry.");

            var text = File.ReadAllText(fullPath);
            Assert.True(MessageBoxShowInvocationPattern.IsMatch(text), $"Allowlisted file '{relativePath}' no longer calls MessageBox.Show() — stale T-6 allowlist entry, remove it.");
        }
    }
}
