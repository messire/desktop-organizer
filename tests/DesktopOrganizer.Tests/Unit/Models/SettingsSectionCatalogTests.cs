using System.Globalization;
using DesktopOrganizer.Localization;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.Tests.Unit.Models;

/// <summary>
/// Pins <see cref="SettingsSectionCatalog"/>'s sidebar entries to real RESX keys
/// (docs/PLANS/localization.md §4.5 DoD: "карточка языка есть" + G-L2-style coverage for the one place
/// this catalog is consumed, <c>Resources/Styles/Settings.xaml</c>'s <c>Settings.SidebarItemTemplate</c>).
/// A typo in a <see cref="SettingsSectionInfo.TitleKey"/> would otherwise silently render as the key
/// itself in the sidebar (<see cref="LocalizationResources.GetString"/> never throws) — this test makes
/// that failure loud instead.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class SettingsSectionCatalogTests
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru");

    /// <summary>Every <see cref="SettingsSectionCatalog.Primary"/> entry plus <see cref="SettingsSectionCatalog.About"/>.</summary>
    public static TheoryData<SettingsSectionInfo> AllEntries()
    {
        var data = new TheoryData<SettingsSectionInfo>();
        foreach (var entry in SettingsSectionCatalog.Primary)
            data.Add(entry);

        data.Add(SettingsSectionCatalog.About);
        return data;
    }

    /// <summary>
    /// Verifies every catalog entry's <see cref="SettingsSectionInfo.TitleKey"/> resolves to real text —
    /// not a fallback of the key itself — in both English and Russian.
    /// </summary>
    /// <param name="entry">The catalog entry under test.</param>
    [Theory]
    [MemberData(nameof(AllEntries))]
    public void TitleKey_ResolvesToRealTextInBothLanguages(SettingsSectionInfo entry)
    {
        var english = LocalizationResources.GetString(entry.TitleKey, English);
        var russian = LocalizationResources.GetString(entry.TitleKey, Russian);

        Assert.NotEqual(entry.TitleKey, english);
        Assert.NotEqual(entry.TitleKey, russian);
        Assert.False(string.IsNullOrWhiteSpace(english));
        Assert.False(string.IsNullOrWhiteSpace(russian));
    }

    /// <summary>Every entry's <see cref="SettingsSectionInfo.TitleKey"/> follows the <c>Settings_Nav_*</c> convention (§4.5).</summary>
    /// <param name="entry">The catalog entry under test.</param>
    [Theory]
    [MemberData(nameof(AllEntries))]
    public void TitleKey_UsesSettingsNavPrefix(SettingsSectionInfo entry) =>
        Assert.StartsWith("Settings_Nav_", entry.TitleKey, StringComparison.Ordinal);

    /// <summary>The eight <see cref="SettingsSection"/> members are all represented exactly once.</summary>
    [Fact]
    public void Catalog_CoversEverySectionExactlyOnce()
    {
        var sections = SettingsSectionCatalog.Primary.Select(e => e.Section)
            .Append(SettingsSectionCatalog.About.Section)
            .ToList();

        Assert.Equal(Enum.GetValues<SettingsSection>().Length, sections.Distinct().Count());
    }
}
