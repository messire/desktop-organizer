using System.Globalization;
using DesktopOrganizer.UI.Localization;

namespace DesktopOrganizer.Tests.Unit.Localization;

/// <summary>
/// Unit tests for <see cref="PageDisplayNames.Resolve"/> (ADR-043, docs/PLANS/page-display-names.md
/// §5.3) — the culture-aware counterpart of <c>PageNameRules.EffectiveName</c> (which takes an
/// already-resolved localized string instead of a <see cref="CultureInfo"/>); both must agree on every
/// case.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class PageDisplayNamesTests
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru");

    /// <summary>An unnamed <c>"default"</c> page resolves to the localized <c>Page_Default_DisplayName</c>, per culture.</summary>
    [Fact]
    public void Resolve_DefaultUnnamed_ReturnsLocalizedDisplayName()
    {
        Assert.Equal("Default", PageDisplayNames.Resolve("default", null, English));
        Assert.Equal("Основная", PageDisplayNames.Resolve("default", null, Russian));
    }

    /// <summary>A "default" page that has been given a stored name shows that name as-is, regardless of culture.</summary>
    [Fact]
    public void Resolve_DefaultWithStoredName_ReturnsStoredName()
    {
        Assert.Equal("Дом", PageDisplayNames.Resolve("default", "Дом", English));
        Assert.Equal("Дом", PageDisplayNames.Resolve("default", "Дом", Russian));
    }

    /// <summary>A non-default page with a stored name shows that name as-is.</summary>
    [Fact]
    public void Resolve_NonDefaultWithStoredName_ReturnsStoredName()
    {
        Assert.Equal("Work", PageDisplayNames.Resolve("x", "Work", English));
    }

    /// <summary>A non-default page with no stored name falls back to its own id (corrupted/hand-edited data — not expected through normal load).</summary>
    [Fact]
    public void Resolve_NonDefaultWithNoStoredName_FallsBackToId()
    {
        Assert.Equal("x", PageDisplayNames.Resolve("x", null, English));
    }

    /// <summary>A whitespace-only stored name is treated the same as an absent name.</summary>
    [Fact]
    public void Resolve_WhitespaceStoredName_TreatedAsAbsent()
    {
        Assert.Equal("Default", PageDisplayNames.Resolve("default", "   ", English));
        Assert.Equal("x", PageDisplayNames.Resolve("x", "   ", English));
    }
}
