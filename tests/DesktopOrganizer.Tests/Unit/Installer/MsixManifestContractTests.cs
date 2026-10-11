using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DesktopOrganizer.Tests.Helpers;

namespace DesktopOrganizer.Tests.Unit.Installer;

/// <summary>
/// Pins the Microsoft Store packaging contract of <c>installer/msix</c> (docs/PLANS/store-publication-implementation.md,
/// Package 4): the manifest template that <c>installer/build-msix.ps1</c> fills in, the checked-in tile/logo PNG
/// set, the product version metadata and the "no .wapproj" decision. Pure file reads - nothing here runs
/// makeappx or touches the native layer.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.UI))]
public class MsixManifestContractTests
{
    /// <summary>
    /// The startup task id the application code looks for — the manifest <c>TaskId</c> and the code constant
    /// must stay identical, so the test reads the constant instead of repeating the literal.
    /// </summary>
    private const string ExpectedStartupTaskId = DesktopOrganizer.UI.Services.AutostartService.StartupTaskId;

    /// <summary>Store assets must stay below this size.</summary>
    private const long MaxAssetBytes = 200 * 1024;

    /// <summary>Longest <c>Description</c> the manifest schema accepts.</summary>
    private const int MaxDescriptionLength = 2048;

    /// <summary>The executable the package launches: the UI project's assembly name plus ".exe".</summary>
    private static readonly string ExpectedExecutable = typeof(DesktopOrganizer.UI.Program).Assembly.GetName().Name + ".exe";

    /// <summary>Every <c>$TOKEN$</c> build-msix.ps1 substitutes; the template must contain exactly these.</summary>
    private static readonly string[] ExpectedTokens =
    [
        "$DISPLAY_NAME$",
        "$IDENTITY_NAME$",
        "$PUBLISHER$",
        "$PUBLISHER_DISPLAY_NAME$",
        "$VERSION$",
    ];

    /// <summary>Default manifest namespace.</summary>
    private static readonly XNamespace Foundation = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";

    /// <summary>The <c>uap</c> manifest namespace (VisualElements).</summary>
    private static readonly XNamespace Uap = "http://schemas.microsoft.com/appx/manifest/uap/windows10";

    /// <summary>The <c>desktop</c> manifest namespace (startupTask).</summary>
    private static readonly XNamespace Desktop = "http://schemas.microsoft.com/appx/manifest/desktop/windows10";

    /// <summary>The <c>rescap</c> manifest namespace (restricted capabilities).</summary>
    private static readonly XNamespace Rescap = "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities";

    /// <summary>The eight-byte PNG file signature.</summary>
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Display scale plateaus the logos are generated for.</summary>
    private static readonly int[] Scales = [100, 125, 150, 200, 400];

    /// <summary>Taskbar/Start target sizes of the 44x44 logo.</summary>
    private static readonly int[] TargetSizes = [16, 24, 32, 48, 256];

    /// <summary>
    /// Every PNG <c>Generate-Assets.ps1</c> produces, with its expected pixel size.
    /// </summary>
    /// <returns>Rows of file name, width, height.</returns>
    public static TheoryData<string, int, int> ExpectedAssets()
    {
        var data = new TheoryData<string, int, int>();

        foreach (var scale in Scales)
        {
            data.Add($"StoreLogo.scale-{scale}.png", Scaled(50, scale), Scaled(50, scale));
            data.Add($"Square150x150Logo.scale-{scale}.png", Scaled(150, scale), Scaled(150, scale));
            data.Add($"Square44x44Logo.scale-{scale}.png", Scaled(44, scale), Scaled(44, scale));
        }

        foreach (var size in TargetSizes)
        {
            data.Add($"Square44x44Logo.targetsize-{size}.png", size, size);
            data.Add($"Square44x44Logo.targetsize-{size}_altform-unplated.png", size, size);
            data.Add($"Square44x44Logo.targetsize-{size}_altform-lightunplated.png", size, size);
        }

        data.Add("Wide310x150Logo.scale-100.png", 310, 150);
        data.Add("Wide310x150Logo.scale-200.png", 620, 300);

        return data;
    }

    /// <summary>The template parses and its root is the foundation/windows10 <c>Package</c>.</summary>
    [Fact]
    public void Manifest_IsWellFormedXmlInTheWindows10FoundationNamespace()
    {
        var manifest = LoadManifest();

        Assert.Equal(Foundation + "Package", manifest.Root!.Name);
    }

    /// <summary>The values build-msix.ps1 substitutes are still tokens (nothing hard-coded).</summary>
    [Fact]
    public void Manifest_IdentityAndDisplayNames_AreSubstitutionTokens()
    {
        var package = LoadManifest().Root!;
        var identity = package.Element(Foundation + "Identity");
        var properties = package.Element(Foundation + "Properties")!;

        Assert.NotNull(identity);
        Assert.Equal("$IDENTITY_NAME$", (string?)identity!.Attribute("Name"));
        Assert.Equal("$PUBLISHER$", (string?)identity.Attribute("Publisher"));
        Assert.Equal("$VERSION$", (string?)identity.Attribute("Version"));
        Assert.Equal("x64", (string?)identity.Attribute("ProcessorArchitecture"));
        Assert.Equal("$PUBLISHER_DISPLAY_NAME$", properties.Element(Foundation + "PublisherDisplayName")!.Value);
        Assert.Equal("$DISPLAY_NAME$", properties.Element(Foundation + "DisplayName")!.Value);
        Assert.Equal("$DISPLAY_NAME$", (string?)package.Descendants(Uap + "VisualElements").Single().Attribute("DisplayName"));
    }

    /// <summary>The template carries exactly the tokens the script knows how to fill - no more, no fewer.</summary>
    [Fact]
    public void Manifest_ContainsExactlyTheExpectedSubstitutionTokens()
    {
        var tokenPattern = new Regex(@"\$[A-Z_]+\$");
        var manifest = LoadManifest();

        var values = manifest.Descendants().Attributes().Select(attribute => attribute.Value)
            .Concat(manifest.Descendants().Where(element => !element.HasElements).Select(element => element.Value));
        var tokens = values.SelectMany(value => tokenPattern.Matches(value).Select(match => match.Value))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ExpectedTokens, tokens);
    }

    /// <summary>The manifest namespaces the optional elements live in are declared ignorable.</summary>
    [Fact]
    public void Manifest_IgnorableNamespaces_ListUapDesktopAndRescap()
    {
        var ignorable = ((string?)LoadManifest().Root!.Attribute("IgnorableNamespaces"))
            ?.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        Assert.NotNull(ignorable);
        Assert.Contains("uap", ignorable!);
        Assert.Contains("desktop", ignorable!);
        Assert.Contains("rescap", ignorable!);
    }

    /// <summary>The startupTask matches the code-side id and is off until the user enables it (D-5).</summary>
    [Fact]
    public void Manifest_StartupTask_UsesTheCodeTaskIdAndIsDisabledByDefault()
    {
        var startupTask = LoadManifest().Descendants(Desktop + "StartupTask").Single();

        Assert.Equal(ExpectedStartupTaskId, (string?)startupTask.Attribute("TaskId"));
        Assert.Equal("false", (string?)startupTask.Attribute("Enabled"));
        Assert.Equal("$DISPLAY_NAME$", (string?)startupTask.Attribute("DisplayName"));

        var extension = startupTask.Parent!;
        Assert.Equal(Desktop + "Extension", extension.Name);
        Assert.Equal("windows.startupTask", (string?)extension.Attribute("Category"));
        Assert.Equal(ExpectedExecutable, (string?)extension.Attribute("Executable"));
        Assert.Equal("Windows.FullTrustApplication", (string?)extension.Attribute("EntryPoint"));
    }

    /// <summary>The single application is a classic full-trust launch of the UI executable.</summary>
    [Fact]
    public void Manifest_Application_IsAFullTrustLaunchOfTheUiExecutable()
    {
        var application = LoadManifest().Descendants(Foundation + "Application").Single();

        Assert.Equal(ExpectedExecutable, (string?)application.Attribute("Executable"));
        Assert.Equal("Windows.FullTrustApplication", (string?)application.Attribute("EntryPoint"));
        Assert.False(string.IsNullOrWhiteSpace((string?)application.Attribute("Id")), "Application/@Id must not be empty.");
    }

    /// <summary>The tile description fits the schema limit.</summary>
    [Fact]
    public void Manifest_Description_IsNotEmptyAndWithinTheSchemaLimit()
    {
        var description = (string?)LoadManifest().Descendants(Uap + "VisualElements").Single().Attribute("Description");

        Assert.False(string.IsNullOrWhiteSpace(description));
        Assert.True(
            description!.Length <= MaxDescriptionLength,
            $"Description is {description.Length} characters; the manifest schema allows {MaxDescriptionLength}.");
    }

    /// <summary>The package asks for <c>runFullTrust</c> and nothing else.</summary>
    [Fact]
    public void Manifest_Capabilities_AreExactlyRunFullTrust()
    {
        var capabilities = LoadManifest().Root!.Element(Foundation + "Capabilities");

        Assert.NotNull(capabilities);
        var capability = Assert.Single(capabilities!.Elements());
        Assert.Equal(Rescap + "Capability", capability.Name);
        Assert.Equal("runFullTrust", (string?)capability.Attribute("Name"));
    }

    /// <summary>Windows 10 2004 (19041) and newer on the desktop family.</summary>
    [Fact]
    public void Manifest_TargetDeviceFamily_IsWindowsDesktopFrom19041()
    {
        var family = LoadManifest().Descendants(Foundation + "TargetDeviceFamily").Single();

        Assert.Equal("Windows.Desktop", (string?)family.Attribute("Name"));
        Assert.Equal("10.0.19041.0", (string?)family.Attribute("MinVersion"));
        Assert.Equal("10.0.26100.0", (string?)family.Attribute("MaxVersionTested"));
    }

    /// <summary>The package languages mirror the satellite languages the build ships (en, ru).</summary>
    [Fact]
    public void Manifest_Resources_AreEnglishAndRussian()
    {
        var languages = LoadManifest()
            .Descendants(Foundation + "Resource")
            .Select(resource => (string)resource.Attribute("Language")!)
            .ToArray();

        Assert.Equal(new[] { "en-US", "ru-RU" }, languages);
    }

    /// <summary>Tiles are transparent and every logo points at the Assets folder by its base name.</summary>
    [Fact]
    public void Manifest_VisualElements_AreTransparentAndPointAtTheAssetsFolder()
    {
        var package = LoadManifest().Root!;
        var visualElements = package.Descendants(Uap + "VisualElements").Single();

        Assert.Equal("transparent", (string?)visualElements.Attribute("BackgroundColor"));
        Assert.Equal(@"Assets\Square150x150Logo.png", (string?)visualElements.Attribute("Square150x150Logo"));
        Assert.Equal(@"Assets\Square44x44Logo.png", (string?)visualElements.Attribute("Square44x44Logo"));
        Assert.Equal(@"Assets\Wide310x150Logo.png", (string?)visualElements.Descendants(Uap + "DefaultTile").Single().Attribute("Wide310x150Logo"));
        Assert.Equal(@"Assets\StoreLogo.png", package.Element(Foundation + "Properties")!.Element(Foundation + "Logo")!.Value);
    }

    /// <summary>
    /// The manifest references base names (resources.pri resolves the qualified variants), so the checked-in set
    /// must at least contain the scale-100 variant of each.
    /// </summary>
    [Fact]
    public void Manifest_EveryReferencedLogo_HasAScale100VariantOnDisk()
    {
        var package = LoadManifest().Root!;
        var visualElements = package.Descendants(Uap + "VisualElements").Single();
        var references = new[]
        {
            package.Element(Foundation + "Properties")!.Element(Foundation + "Logo")!.Value,
            (string)visualElements.Attribute("Square150x150Logo")!,
            (string)visualElements.Attribute("Square44x44Logo")!,
            (string)visualElements.Descendants(Uap + "DefaultTile").Single().Attribute("Wide310x150Logo")!,
        };

        foreach (var reference in references)
        {
            var baseName = Path.GetFileNameWithoutExtension(reference.Replace('\\', Path.DirectorySeparatorChar));
            var variant = Path.Combine(AssetsDirectory(), $"{baseName}.scale-100.png");

            Assert.True(File.Exists(variant), $"Manifest references '{reference}' but '{variant}' is missing; run installer/msix/Generate-Assets.ps1.");
        }
    }

    /// <summary>Each generated PNG exists, is a real PNG of the expected pixel size and is under the byte limit.</summary>
    /// <param name="fileName">The asset file name.</param>
    /// <param name="expectedWidth">Expected width in pixels.</param>
    /// <param name="expectedHeight">Expected height in pixels.</param>
    [Theory]
    [MemberData(nameof(ExpectedAssets))]
    public void Asset_IsAPngOfTheExpectedSizeUnderTheByteLimit(string fileName, int expectedWidth, int expectedHeight)
    {
        var path = Path.Combine(AssetsDirectory(), fileName);
        Assert.True(File.Exists(path), $"Missing asset '{path}'; run installer/msix/Generate-Assets.ps1.");

        var info = new FileInfo(path);
        Assert.True(info.Length < MaxAssetBytes, $"{fileName} is {info.Length} bytes; the limit is {MaxAssetBytes}.");

        var header = new byte[24];
        using (var stream = File.OpenRead(path))
        {
            stream.ReadExactly(header);
        }

        Assert.True(header.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature), $"{fileName} does not start with the PNG signature.");

        // IHDR: 4-byte length, "IHDR", then width and height as big-endian 32-bit integers.
        var width = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
        var height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];

        Assert.Equal((expectedWidth, expectedHeight), (width, height));
    }

    /// <summary>No stray file (an old variant, a source image) sits in the Assets folder.</summary>
    [Fact]
    public void Assets_ContainOnlyTheExpectedFiles()
    {
        var expected = ExpectedAssets().Select(row => (string)row[0]!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actual = Directory.GetFiles(AssetsDirectory()).Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Empty(actual.Except(expected));
    }

    /// <summary>The Store version is major.minor.build with a non-zero major (D-2).</summary>
    [Fact]
    public void DirectoryBuildProps_Version_IsThreePartAndNotZeroMajor()
    {
        var props = XDocument.Load(Path.Combine(RepoRoot(), "Directory.Build.props"));
        var version = props.Descendants("Version").Select(element => element.Value).Single();

        Assert.Matches(@"^[1-9]\d*\.\d+\.\d+$", version);
    }

    /// <summary>The source is closed: the product description must not claim "open-source".</summary>
    [Fact]
    public void DirectoryBuildProps_Description_DoesNotClaimOpenSource()
    {
        var props = XDocument.Load(Path.Combine(RepoRoot(), "Directory.Build.props"));
        var description = props.Descendants("Description").Select(element => element.Value).Single();

        Assert.DoesNotContain("open-source", description, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The packaging script cannot be pointed at a Debug build and never ships symbols.</summary>
    [Fact]
    public void BuildScript_IsReleaseOnlyAndDropsDebugSymbols()
    {
        var script = ReadBuildScript();

        Assert.Contains("[ValidateSet(\"Release\")]", script, StringComparison.Ordinal);

        // The layout filter excludes ".pdb" through an Extension comparison (-ne / -notin / ...), in any quoting or spacing.
        Assert.Matches(
            new Regex(@"\.Extension\s+-(?:ne|notin|notlike|notmatch)\s+@?\(?\s*[""']\*?\.pdb[""']", RegexOptions.IgnoreCase),
            script);
    }

    /// <summary>
    /// The generated priconfig.xml has its <c>packaging</c> node removed before <c>makepri new</c>, and a leftover
    /// split (<c>resources.*.pri</c>) fails the build: the layout only ever receives <c>resources.pri</c>.
    /// </summary>
    [Fact]
    public void BuildScript_RemovesThePriPackagingSectionAndGuardsAgainstResourcePackSplit()
    {
        var script = ReadBuildScript();

        Assert.Matches(new Regex(@"SelectSingleNode\(\s*""/resources/packaging""\s*\)"), script);
        Assert.Matches(new Regex(@"RemoveChild\("), script);
        Assert.Matches(new Regex(@"-Filter\s+""resources\.\*\.pri"""), script);
        Assert.Contains("makepri split resources.pri into resource packs", script, StringComparison.Ordinal);
    }

    /// <summary>Both packaging scripts run under strict mode, so typos in variable or property names fail loudly.</summary>
    /// <param name="relativePath">Script path relative to the repository root.</param>
    [Theory]
    [InlineData("installer/build-msix.ps1")]
    [InlineData("installer/msix/Generate-Assets.ps1")]
    public void PackagingScripts_RunInStrictMode(string relativePath)
    {
        var script = File.ReadAllText(Path.Combine(RepoRootLocator.Find(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

        Assert.Matches(new Regex(@"Set-StrictMode\s+-Version\s+Latest", RegexOptions.IgnoreCase), script);
    }

    /// <summary>Packaging stays script-only: no <c>.wapproj</c> is added to the solution.</summary>
    [Fact]
    public void Solution_DoesNotReferenceAWindowsApplicationPackagingProject()
    {
        var solution = File.ReadAllText(Path.Combine(RepoRoot(), "DesktopOrganizer.sln"));

        Assert.DoesNotContain(".wapproj", solution, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Scales a base pixel size, rounding half away from zero like Generate-Assets.ps1.</summary>
    /// <param name="baseSize">Size at scale 100.</param>
    /// <param name="scale">Scale percentage.</param>
    /// <returns>The scaled size in pixels.</returns>
    private static int Scaled(int baseSize, int scale)
        => (int)Math.Round(baseSize * scale / 100.0, MidpointRounding.AwayFromZero);

    /// <summary>Folder holding the checked-in PNGs.</summary>
    /// <returns>Absolute path of <c>installer/msix/Assets</c>.</returns>
    private static string AssetsDirectory()
        => Path.Combine(RepoRoot(), "installer", "msix", "Assets");

    /// <summary>Loads the manifest template.</summary>
    /// <returns>The parsed document.</returns>
    private static XDocument LoadManifest()
    {
        var path = Path.Combine(RepoRoot(), "installer", "msix", "AppxManifest.xml");
        Assert.True(File.Exists(path), $"MSIX manifest template not found at '{path}'.");

        return XDocument.Load(path);
    }

    /// <summary>Reads <c>installer/build-msix.ps1</c>.</summary>
    /// <returns>The script text.</returns>
    private static string ReadBuildScript()
        => File.ReadAllText(Path.Combine(RepoRoot(), "installer", "build-msix.ps1"));

    /// <summary>Locates the repository root.</summary>
    /// <returns>The directory containing <c>DesktopOrganizer.sln</c>.</returns>
    private static string RepoRoot()
        => RepoRootLocator.Find();
}
