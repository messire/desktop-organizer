using System.IO;
using System.Text.RegularExpressions;
using DesktopOrganizer.Tests.Helpers;

namespace DesktopOrganizer.Tests.Unit.Diagnostics;

/// <summary>
/// Package B gate (release-hardening-1.0 plan §1): scans every <c>*.cs</c> file under <c>src/</c> for a
/// real <c>Console.Error.WriteLine(</c> call — never a bare substring match, so a doc-comment merely
/// mentioning the method name (e.g. explaining what a diagnostics event replaced) never trips this gate.
/// A regression here means a future edit reintroduced a raw console write instead of going through
/// <c>IAppLog</c>/the ambient <c>AppLog.Current</c>.
/// </summary>
/// <remarks>
/// Reads source files directly off disk (<see cref="RepoRootLocator"/>), mirroring
/// <c>XamlLiteralGuardTests</c>' own approach for the same reason: it catches a regression the moment
/// it's typed, not only after a rebuild. The only allowed survivors are the three <c>Program.cs</c> lines
/// marked with the <c>// cli-output</c> comment (plan §1: CLI-only <c>--restore-state-backup</c> output
/// that intentionally stays on the console, in English, for scripts/developers reading the terminal —
/// each is also duplicated into <c>AppLog</c> right next to it).
/// </remarks>
[Trait(UnitTests, "Gates")]
public class ConsoleErrorGuardTests
{
    private const string AllowlistMarker = "// cli-output";

    private static readonly Regex ConsoleErrorWriteLineCall = new(@"Console\.Error\.WriteLine\s*\(", RegexOptions.Compiled);

    /// <summary>
    /// Scans every <c>*.cs</c> file under <c>src/</c> and fails, listing every offending <c>file:line</c>,
    /// if a real <c>Console.Error.WriteLine(</c> invocation turns up outside the <c>// cli-output</c>
    /// allowlist.
    /// </summary>
    [Fact]
    public void SourceFiles_HaveNoConsoleErrorWriteLineCallsOutsideAllowlist()
    {
        var repoRoot = RepoRootLocator.Find();
        var srcRoot = Path.Combine(repoRoot, "src");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);
            var relativePath = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (!ConsoleErrorWriteLineCall.IsMatch(line)) continue;
                if (line.Contains(AllowlistMarker, StringComparison.Ordinal)) continue;

                offenders.Add($"{relativePath}:{i + 1}: {line.Trim()}");
            }
        }

        var message = "Console.Error.WriteLine( call(s) found outside the // cli-output allowlist " +
            "(docs/PLANS/release-hardening-1.0.md §1) — replace with IAppLog/AppLog.Current:\n" +
            string.Join('\n', offenders);
        Assert.True(offenders.Count == 0, message);
    }

    /// <summary>
    /// Pins the allowlist itself at exactly three lines, all in <c>Program.cs</c> — a regression that
    /// either loses the marker off a real CLI line, or (more likely) copies the marker onto a new call
    /// site to dodge the gate above, would otherwise pass silently.
    /// </summary>
    [Fact]
    public void Allowlist_IsExactlyThreeLinesInProgramCs()
    {
        var repoRoot = RepoRootLocator.Find();
        var srcRoot = Path.Combine(repoRoot, "src");
        var allowlisted = new List<string>();

        foreach (var file in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);
            var relativePath = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');

            for (var i = 0; i < lines.Length; i++)
            {
                if (ConsoleErrorWriteLineCall.IsMatch(lines[i]) && lines[i].Contains(AllowlistMarker, StringComparison.Ordinal))
                    allowlisted.Add($"{relativePath}:{i + 1}");
            }
        }

        Assert.Equal(3, allowlisted.Count);
        Assert.All(allowlisted, entry => Assert.StartsWith("src/DesktopOrganizer.UI/Program.cs:", entry, StringComparison.Ordinal));
    }
}
