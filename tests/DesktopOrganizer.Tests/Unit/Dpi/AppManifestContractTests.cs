using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DesktopOrganizer.Tests.Unit.Dpi;

/// <summary>
/// Pins the DPI-awareness contract of release-hardening F1 (ADR-037, plan §3.4/§7): the UI executable
/// declares PerMonitorV2 (fallback PerMonitor, then <c>true/pm</c>) in <c>src/DesktopOrganizer.UI/app.manifest</c>,
/// only the UI project references a manifest, and no code overrides the awareness at run time.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.UI))]
public class AppManifestContractTests
{
    private static readonly XNamespace AsmV1 = "urn:schemas-microsoft-com:asm.v1";
    private static readonly XNamespace AsmV3 = "urn:schemas-microsoft-com:asm.v3";
    private static readonly XNamespace WindowsSettings2005 = "http://schemas.microsoft.com/SMI/2005/WindowsSettings";
    private static readonly XNamespace WindowsSettings2016 = "http://schemas.microsoft.com/SMI/2016/WindowsSettings";

    private static readonly Regex ApplicationManifestPattern =
        new(@"<ApplicationManifest>\s*(?<value>[^<]+?)\s*</ApplicationManifest>", RegexOptions.IgnoreCase);

    private static readonly Regex RuntimeAwarenessOverridePattern =
        new(@"\b(SetProcessDpiAwareness(Context)?|SetProcessDPIAware|SetThreadDpiAwarenessContext|SetHighDpiMode)\b");

    /// <summary>The manifest exists and declares <c>PerMonitorV2, PerMonitor</c> in the 2016 namespace.</summary>
    [Fact]
    public void Manifest_DeclaresPerMonitorV2WithPerMonitorFallback()
    {
        var settings = LoadWindowsSettings();

        var awareness = settings.Element(WindowsSettings2016 + "dpiAwareness");
        Assert.NotNull(awareness);
        Assert.Equal("PerMonitorV2, PerMonitor", awareness.Value.Trim());
    }

    /// <summary>The legacy 2005 <c>dpiAware</c> fallback is <c>true/pm</c>.</summary>
    [Fact]
    public void Manifest_DeclaresLegacyTruePmFallback()
    {
        var settings = LoadWindowsSettings();

        var dpiAware = settings.Element(WindowsSettings2005 + "dpiAware");
        Assert.NotNull(dpiAware);
        Assert.Equal("true/pm", dpiAware.Value.Trim());
    }

    /// <summary>The manifest keeps the default <c>asInvoker</c> execution level (no elevation prompt).</summary>
    [Fact]
    public void Manifest_RequestsAsInvoker()
    {
        var doc = XDocument.Load(ManifestPath);

        var level = doc.Descendants().Single(e => e.Name.LocalName == "requestedExecutionLevel");
        Assert.Equal("asInvoker", (string?)level.Attribute("level"));
    }

    /// <summary>The UI csproj wires the manifest through <c>ApplicationManifest</c>.</summary>
    [Fact]
    public void UiCsproj_ReferencesTheManifest()
    {
        var csproj = File.ReadAllText(Path.Combine(RepoRootLocator.Find(), "src", "DesktopOrganizer.UI", "DesktopOrganizer.UI.csproj"));

        var match = ApplicationManifestPattern.Match(csproj);
        Assert.True(match.Success, "DesktopOrganizer.UI.csproj has no <ApplicationManifest> element.");
        Assert.Equal("app.manifest", match.Groups["value"].Value);
    }

    /// <summary>No other project (nor the shared build props) declares an application manifest.</summary>
    [Fact]
    public void OnlyUiCsproj_DeclaresApplicationManifest()
    {
        var root = RepoRootLocator.Find();
        var offenders = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Append(Path.Combine(root, "Directory.Build.props"))
            .Where(File.Exists)
            .Where(p => !p.EndsWith("DesktopOrganizer.UI.csproj", StringComparison.OrdinalIgnoreCase))
            .Where(p => ApplicationManifestPattern.IsMatch(File.ReadAllText(p)))
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// DPI awareness is declared ONLY by the manifest: no source file calls the run-time awareness APIs
    /// (which would silently override it or split it per thread).
    /// </summary>
    [Fact]
    public void NoSource_OverridesDpiAwarenessAtRuntime()
    {
        var src = Path.Combine(RepoRootLocator.Find(), "src");
        var offenders = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(p => !IsBuildOutput(p))
            .Where(p => RuntimeAwarenessOverridePattern.IsMatch(File.ReadAllText(p)))
            .Select(p => Path.GetRelativePath(src, p))
            .ToList();

        Assert.Empty(offenders);
    }

    private static string ManifestPath
    {
        get
        {
            var path = Path.Combine(RepoRootLocator.Find(), "src", "DesktopOrganizer.UI", "app.manifest");
            Assert.True(File.Exists(path), $"Missing {path}.");
            return path;
        }
    }

    private static XElement LoadWindowsSettings()
    {
        var doc = XDocument.Load(ManifestPath);
        Assert.NotNull(doc.Root);
        Assert.Equal(AsmV1 + "assembly", doc.Root.Name);

        var settings = doc.Root.Element(AsmV3 + "application")?.Element(AsmV3 + "windowsSettings");
        Assert.NotNull(settings);
        return settings;
    }

    private static bool IsBuildOutput(string path)
    {
        var sep = Path.DirectorySeparatorChar;
        return path.Contains($"{sep}obj{sep}", StringComparison.OrdinalIgnoreCase)
               || path.Contains($"{sep}bin{sep}", StringComparison.OrdinalIgnoreCase);
    }
}
