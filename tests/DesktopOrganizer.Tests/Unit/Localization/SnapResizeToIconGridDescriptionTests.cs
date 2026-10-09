using System.IO;
using System.Xml.Linq;

namespace DesktopOrganizer.Tests.Unit.Localization;

/// <summary>
/// ADR-audit review cycle 1 (H1): the "Snap panel size to icon grid" card's description must tell the
/// user the toggle has no effect while "Snap panel position to grid" is on (the toggle is disabled then),
/// in both locales, while staying within the <see cref="ResxTextLengthGuardTests"/> 100-character mark.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Localization))]
public class SnapResizeToIconGridDescriptionTests
{
    private const string DescriptionKey = "Settings_Layout_SnapResizeToIconGrid_Description";

    /// <summary>Verifies the English description mentions the position snapping it depends on and stays short.</summary>
    [Fact]
    public void Description_Neutral_ExplainsPositionSnapDependencyAndStaysShort()
    {
        AssertDescription("Strings.resx", "position");
    }

    /// <summary>Verifies the Russian description mentions the position snapping it depends on and stays short.</summary>
    [Fact]
    public void Description_Russian_ExplainsPositionSnapDependencyAndStaysShort()
    {
        AssertDescription("Strings.ru.resx", "позиции");
    }

    private static void AssertDescription(string fileName, string positionWord)
    {
        var resx = LoadResx(fileName);
        var description = resx[DescriptionKey];

        Assert.True(description.Length <= 100, $"{fileName}: {DescriptionKey} is {description.Length} chars (limit 100).");
        Assert.Contains(positionWord, description, StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> LoadResx(string fileName)
    {
        var path = Path.Combine(RepoRootLocator.Find(), "src", "DesktopOrganizer.Localization", fileName);
        return XDocument.Load(path).Root!.Elements("data")
            .Where(d => d.Attribute("name") is not null)
            .ToDictionary(d => d.Attribute("name")!.Value, d => d.Element("value")?.Value ?? string.Empty, StringComparer.Ordinal);
    }
}
