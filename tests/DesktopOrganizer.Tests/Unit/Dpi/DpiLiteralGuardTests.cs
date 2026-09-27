using System.IO;
using System.Text.RegularExpressions;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Tests.Helpers;

namespace DesktopOrganizer.Tests.Unit.Dpi;

/// <summary>
/// Guard test for release-hardening F4 (docs/PLANS/release-hardening-1.0-F-dpi.md §7 "DpiLiteralGuardTests",
/// ADR-037): scans every <c>src/**/*.cs</c> file (never <c>obj/</c>/<c>bin/</c>) for the three ways the
/// pre-F codebase used to do px↔DIP arithmetic OUTSIDE the single place the plan allows it — a stray
/// <c>/ 96</c> or <c>* 96</c>, a raw <see cref="System.Windows.Media.VisualTreeHelper.GetDpi(System.Windows.Media.Visual)"/>/
/// <c>TransformToDevice</c>/<c>Graphics.FromHwnd</c> call outside the one adapter file allowed to make it,
/// or the <c>GetSystemDpiScale</c> symbol appearing anywhere other than the native-interop boundary and
/// the provider that owns it. Line/doc comments (<c>//</c>, <c>///</c>) are stripped before matching, so a
/// comment merely MENTIONING one of these (e.g. explaining why a nearby line does NOT do this) never
/// trips the guard — only actual source code does.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class DpiLiteralGuardTests
{
    /// <summary>
    /// The ONLY file allowed to divide/multiply by the literal <c>96</c> (<see cref="DesktopScaleModel.BaseDpi"/>
    /// is defined there, and every px↔DIP formula in the codebase is meant to route through it).
    /// </summary>
    private static readonly string[] Literal96Allowlist =
    [
        Path.Combine("src", "DesktopOrganizer.Services.Abstractions", "DesktopScaleModel.cs"),
    ];

    /// <summary>
    /// The ONLY files allowed to call <c>VisualTreeHelper.GetDpi</c>/<c>TransformToDevice</c> — the px↔DIP
    /// boundary between a live WPF window and desktop-DIP (plan §5 "Адаптер").
    /// </summary>
    private static readonly string[] WindowDpiQueryAllowlist =
    [
        Path.Combine("src", "DesktopOrganizer.UI", "Interaction", "PanelWindowCoordinates.cs"),
        Path.Combine("src", "DesktopOrganizer.UI", "Interaction", "TopLevelPlacement.cs"),
    ];

    /// <summary>
    /// The ONLY files allowed to reference the <c>GetSystemDpiScale</c> symbol: the native-interop boundary
    /// that defines/implements it, and the provider that is its only legitimate caller.
    /// </summary>
    private static readonly string[] GetSystemDpiScaleAllowlist =
    [
        Path.Combine("src", "DesktopOrganizer.Services", "Interfaces", "INativeApi.cs"),
        Path.Combine("src", "DesktopOrganizer.Services", "Tools", "NativeApi.cs"),
        Path.Combine("src", "DesktopOrganizer.Services", "Tools", "DesktopScaleProvider.cs"),
    ];

    private static readonly Regex Literal96Pattern =
        new(@"[*/]\s*96(\.0)?[dD]?\b|\b96(\.0)?[dD]?\s*[*/]", RegexOptions.Compiled);

    private static readonly Regex WindowDpiQueryPattern =
        new(@"VisualTreeHelper\.GetDpi|TransformToDevice|Graphics\.FromHwnd", RegexOptions.Compiled);

    private static readonly Regex GetSystemDpiScalePattern = new(@"\bGetSystemDpiScale\b", RegexOptions.Compiled);

    /// <summary>
    /// Verifies that no <c>src/**/*.cs</c> file, other than <see cref="DesktopScaleModel"/> itself,
    /// divides or multiplies by the literal <c>96</c> — every px↔DIP scale conversion must route through
    /// <see cref="DesktopScaleModel.ScaleFromDpi"/>/<see cref="DesktopScaleModel.BaseDpi"/> instead.
    /// </summary>
    [Fact]
    public void NoSourceFileOutsideTheAllowlist_DividesOrMultipliesByTheLiteral96()
        => AssertNoViolationsOutsideAllowlist(Literal96Pattern, Literal96Allowlist, "divides/multiplies by the literal 96");

    /// <summary>
    /// Verifies that no <c>src/**/*.cs</c> file, other than the two adapter files, calls
    /// <c>VisualTreeHelper.GetDpi</c>/<c>TransformToDevice</c>/<c>Graphics.FromHwnd</c> — a live window's
    /// DPI may only be queried at the single px↔DIP boundary (plan §5 "Адаптер").
    /// </summary>
    [Fact]
    public void NoSourceFileOutsideTheAllowlist_CallsAWindowDpiQueryApi()
        => AssertNoViolationsOutsideAllowlist(WindowDpiQueryPattern, WindowDpiQueryAllowlist, "calls a window DPI query API (VisualTreeHelper.GetDpi/TransformToDevice/Graphics.FromHwnd)");

    /// <summary>
    /// Verifies that the <c>GetSystemDpiScale</c> symbol appears only at the native-interop boundary and
    /// its provider — <em>not</em> on <c>IMonitorService</c> or anywhere else in the UI/Services layer.
    /// </summary>
    [Fact]
    public void GetSystemDpiScaleSymbol_AppearsOnlyAtTheNativeInteropBoundary()
        => AssertNoViolationsOutsideAllowlist(GetSystemDpiScalePattern, GetSystemDpiScaleAllowlist, "references the GetSystemDpiScale symbol");

    /// <summary>
    /// Verifies that <see cref="IMonitorService"/> — checked directly via reflection, independent of the
    /// source-scan guards above — no longer declares a <c>GetSystemDpiScale</c> member (release-hardening
    /// F4: zero UI callers, confirmed by grep; <c>INativeApi.GetSystemDpiScale</c> is a DIFFERENT member
    /// and is unaffected).
    /// </summary>
    [Fact]
    public void IMonitorService_NoLongerDeclaresGetSystemDpiScale()
    {
        var member = typeof(IMonitorService).GetMethod("GetSystemDpiScale");

        Assert.Null(member);
    }

    private static void AssertNoViolationsOutsideAllowlist(Regex pattern, string[] allowlist, string violationDescription)
    {
        var repoRoot = RepoRootLocator.Find();
        var srcRoot = Path.Combine(repoRoot, "src");
        var allowedFullPaths = allowlist.Select(relative => Path.GetFullPath(Path.Combine(repoRoot, relative))).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (IsExcludedDirectory(file)) continue;
            if (allowedFullPaths.Contains(Path.GetFullPath(file))) continue;

            var codeOnly = StripLineComments(File.ReadAllText(file));
            if (pattern.IsMatch(codeOnly))
                violations.Add(Path.GetRelativePath(repoRoot, file));
        }

        var message = $"The following src/**/*.cs file(s) {violationDescription}, outside the allowlist "
            + $"({string.Join(", ", allowlist)}):{Environment.NewLine}{string.Join(Environment.NewLine, violations)}";
        Assert.True(violations.Count == 0, message);
    }

    /// <summary>
    /// A directory segment that means this file is generated/intermediate output, not source — never
    /// scanned by this guard.
    /// </summary>
    private static bool IsExcludedDirectory(string filePath)
    {
        var segments = filePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(s => string.Equals(s, "obj", StringComparison.OrdinalIgnoreCase) || string.Equals(s, "bin", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Strips every <c>//</c>-to-end-of-line comment (which also covers <c>///</c> doc comments) from each
    /// line, so a comment merely mentioning a guarded pattern is never mistaken for actual source code.
    /// Deliberately simple (no string-literal-aware tokenizing): this codebase has no <c>//</c> occurring
    /// inside a string literal near any of the guarded patterns, and a false negative here would only ever
    /// make the guard MORE permissive, never hide a real violation as a false positive.
    /// </summary>
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
