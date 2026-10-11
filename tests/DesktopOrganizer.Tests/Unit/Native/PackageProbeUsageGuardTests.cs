using System.IO;
using System.Text.RegularExpressions;

namespace DesktopOrganizer.Tests.Unit.Native;

/// <summary>
/// Source-scan guard for the one documented exception to "P/Invoke only behind <c>INativeApi</c>"
/// (Microsoft Store channel): the identity probe is declared only in <c>NativeApi.cs</c>, and its cached
/// static result (<c>NativeApi.HasPackageIdentity</c>) is read from exactly one place outside that file,
/// <c>AppStorageRoot</c>, which is needed before DI exists (it also re-exposes the raw probe code as a
/// pre-DI facade for the single diagnostic log line). Every other consumer must go through
/// <c>INativeApi.IsPackagedProcess</c> / <c>IAppPackageContext</c>; the "root really lives under the user
/// profile" flag (<c>AppStorageRoot.IsPackagedLocation</c>) is read only by <c>Program.cs</c> and the
/// code-behind that wires the Move hint. Line and doc comments are stripped before matching, so a comment that merely mentions a symbol
/// never trips the guard.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class PackageProbeUsageGuardTests
{
    private static readonly string NativeApiFile = Path.Combine("src", "DesktopOrganizer.Services", "Tools", "NativeApi.cs");
    private static readonly string AppStorageRootFile = Path.Combine("src", "DesktopOrganizer.Services", "Storage", "AppStorageRoot.cs");
    private static readonly string ProgramFile = Path.Combine("src", "DesktopOrganizer.UI", "Program.cs");
    private static readonly string ChildWindowCodeBehindFile = Path.Combine("src", "DesktopOrganizer.UI", "Views", "ChildWindow.xaml.cs");

    /// <summary>
    /// Verifies that <c>GetCurrentPackageFullName</c> (the P/Invoke declaration and call) appears only in <c>NativeApi.cs</c>.
    /// </summary>
    [Fact]
    public void GetCurrentPackageFullName_AppearsOnlyInNativeApi()
        => AssertOnlyIn(new Regex(@"\bGetCurrentPackageFullName\b"), [NativeApiFile]);

    /// <summary>
    /// Verifies that <c>HasPackageIdentity</c> appears only in <c>NativeApi.cs</c> and <c>AppStorageRoot.cs</c>.
    /// </summary>
    [Fact]
    public void HasPackageIdentity_AppearsOnlyInNativeApiAndAppStorageRoot()
        => AssertOnlyIn(new Regex(@"\bHasPackageIdentity\b"), [NativeApiFile, AppStorageRootFile]);

    /// <summary>
    /// Verifies that the raw probe code is declared in <c>NativeApi.cs</c> and re-exposed only by the pre-DI
    /// facade in <c>AppStorageRoot.cs</c>. Callers (<c>Program.cs</c>) must reach it through the qualified
    /// facade <c>AppStorageRoot.PackageProbeResultCode</c>, which the pattern deliberately does not match;
    /// any other reference (notably <c>NativeApi.PackageProbeResultCode</c> from the UI layer) is a violation.
    /// </summary>
    [Fact]
    public void PackageProbeResultCode_AppearsOnlyInNativeApiAndAppStorageRoot()
        => AssertOnlyIn(new Regex(@"(?<!AppStorageRoot\.)\bPackageProbeResultCode\b"), [NativeApiFile, AppStorageRootFile]);

    /// <summary>
    /// Verifies that <c>AppStorageRoot.IsPackagedLocation</c> (the "root really lives under the user profile"
    /// flag) is consumed only where the answer is about the <em>location</em> (does a moved file survive an
    /// uninstall), not about the process identity: defined in <c>AppStorageRoot.cs</c>, read by <c>Program.cs</c>
    /// (first-run README) and handed to the Move hint by <c>ChildWindow.xaml.cs</c> (the hint controller itself
    /// takes it as a required constructor argument and never reads the static). Everything else asks
    /// <c>IAppPackageContext</c>.
    /// </summary>
    [Fact]
    public void IsPackagedLocation_AppearsOnlyWhereTheLocationMatters()
        => AssertOnlyIn(
            new Regex(@"\bIsPackagedLocation\b"),
            [AppStorageRootFile, ProgramFile, ChildWindowCodeBehindFile]);

    private static void AssertOnlyIn(Regex pattern, string[] allowlist)
    {
        var repoRoot = RepoRootLocator.Find();
        var allowed = allowlist.Select(relative => Path.GetFullPath(Path.Combine(repoRoot, relative))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(Path.Combine(repoRoot, "src"), "*.cs", SearchOption.AllDirectories))
        {
            var segments = file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Any(s => s.Equals("obj", StringComparison.OrdinalIgnoreCase) || s.Equals("bin", StringComparison.OrdinalIgnoreCase)))
                continue;

            if (!pattern.IsMatch(StripLineComments(File.ReadAllText(file)))) continue;

            var full = Path.GetFullPath(file);
            if (allowed.Contains(full)) found.Add(full);
            else violations.Add(Path.GetRelativePath(repoRoot, file));
        }

        Assert.True(
            violations.Count == 0,
            $"'{pattern}' must appear only in {string.Join(", ", allowlist)}, but also found in:{Environment.NewLine}{string.Join(Environment.NewLine, violations)}");

        // The guard must not rot into a vacuous pass: the symbol has to exist in every allowed file.
        Assert.Equal(allowed.Count, found.Count);
    }

    // Known limitations of this deliberately simple pre-filter (a source scan, not a C# parser):
    //  - It cuts at the first "//" on a line, including one inside a string literal (e.g. a URL), so a
    //    symbol written after such a literal on the same line is not seen. That can only hide a violation
    //    (false negative), never invent one.
    //  - Block comments (/* ... */) are NOT removed, so a symbol mentioned inside one is matched and
    //    reported as a violation (false positive); the code base uses only line and XML-doc comments.
    private static string StripLineComments(string source)
    {
        var lines = source.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var index = lines[i].IndexOf("//", StringComparison.Ordinal);
            if (index >= 0) lines[i] = lines[i][..index];
        }

        return string.Join('\n', lines);
    }
}
