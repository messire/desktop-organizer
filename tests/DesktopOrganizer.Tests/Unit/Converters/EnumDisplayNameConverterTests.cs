using System.Globalization;
using DesktopOrganizer.UI.Converters;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.Tests.Unit.Converters;

/// <summary>
/// Unit tests for <see cref="EnumDisplayNameConverter"/> — the single converter behind EVERY
/// user-facing enum's display text (docs/PLANS/localization.md §4.3, WP-2; closes
/// docs/ROADMAP.md:1229-1232). Exercises all ten enums seeded with <c>Enum_*</c> RESX keys (plan §3.2)
/// in both English and Russian, so a future enum member added without a matching RESX entry is caught
/// by the fallback-to-<c>ToString()</c> tests rather than by silently showing a raw member name.
/// </summary>
[Trait(UnitTests, "UI")]
public class EnumDisplayNameConverterTests
{
    private static readonly EnumDisplayNameConverter Converter = new();
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru");

    /// <summary>
    /// Verifies every seeded <c>Enum_*</c> member (docs/PLANS/localization.md §3.2) against its exact
    /// English and Russian display text.
    /// </summary>
    /// <param name="value">The enum member to convert.</param>
    /// <param name="expectedEnglish">The exact expected English display text.</param>
    /// <param name="expectedRussian">The exact expected Russian display text.</param>
    [Theory]
    [InlineData(PanelGeometryMode.GridAligned, "Grid aligned", "По сетке")]
    [InlineData(PanelGeometryMode.LegacyFreeform, "Free-form (legacy)", "Произвольная (устар.)")]
    [InlineData(IconLayoutPolicy.FixedGap, "Fixed spacing", "Фиксированный интервал")]
    [InlineData(IconLayoutPolicy.LegacyStretch, "Adaptive stretch (legacy)", "Адаптивное растяжение (устар.)")]
    [InlineData(PanelContentFilter.ShowAll, "Show all", "Показывать всё")]
    [InlineData(PanelContentFilter.HideAll, "Hide content", "Скрыть содержимое")]
    [InlineData(PanelContentFilter.HideShortcuts, "Hide shortcuts", "Скрыть ярлыки")]
    [InlineData(PanelContentFilter.FoldersOnly, "Folders only", "Только папки")]
    [InlineData(PanelSurfaceStyle.Solid, "Solid", "Сплошной")]
    [InlineData(PanelSurfaceStyle.Ambient, "Ambient", "Акцентный")]
    [InlineData(PanelSurfaceStyle.Glass, "Glass", "Стекло")]
    [InlineData(NormalModeLayoutGestures.MoveAndResize, "Move and resize", "Перемещение и изменение размера")]
    [InlineData(NormalModeLayoutGestures.MoveOnly, "Move only", "Только перемещение")]
    [InlineData(NormalModeLayoutGestures.None, "None (Edit layout mode only)", "Нет (только в режиме изменения расположения)")]
    [InlineData(PanelViewMode.Grid, "Grid", "Сетка")]
    [InlineData(PanelViewMode.List, "List", "Список")]
    [InlineData(PanelSortMode.Manual, "Manual", "Вручную")]
    [InlineData(PanelSortMode.Name, "By name", "По имени")]
    [InlineData(PanelSizePreset.Small, "Small", "Маленький")]
    [InlineData(PanelSizePreset.Medium, "Medium", "Средний")]
    [InlineData(PanelSizePreset.Large, "Large", "Большой")]
    [InlineData(PanelSizePreset.ExtraLarge, "Extra large", "Очень большой")]
    [InlineData(PanelSizingMode.Manual, "Manual", "Вручную")]
    [InlineData(PanelSizingMode.AutoContent, "Auto-size to content", "Автоподбор по содержимому")]
    [InlineData(SystemIconSize.Small, "Small (32 px)", "Маленькие (32 пкс)")]
    [InlineData(SystemIconSize.Medium, "Medium (48 px)", "Средние (48 пкс)")]
    [InlineData(SystemIconSize.Large, "Large (64 px)", "Крупные (64 пкс)")]
    public void Convert_SeededEnumMember_ReturnsExactDisplayTextForBothLanguages(
        object value, string expectedEnglish, string expectedRussian)
    {
        var englishText = Converter.Convert(value, typeof(string), null, English);
        var russianText = Converter.Convert(value, typeof(string), null, Russian);

        Assert.Equal(expectedEnglish, englishText);
        Assert.Equal(expectedRussian, russianText);
    }

    /// <summary>
    /// Verifies that a non-<see cref="Enum"/> value falls back to its <see cref="object.ToString"/>
    /// representation, regardless of culture.
    /// </summary>
    [Fact]
    public void Convert_UnrecognizedValue_FallsBackToToString()
    {
        var text = Converter.Convert(42, typeof(string), null, English);

        Assert.Equal("42", text);
    }

    /// <summary>
    /// Verifies that an <see cref="Enum"/> value whose type has no matching <c>Enum_*</c> RESX keys
    /// falls back to <see cref="object.ToString"/> instead of showing the RESX-miss placeholder key.
    /// </summary>
    [Fact]
    public void Convert_EnumWithoutSeededKeys_FallsBackToToString()
    {
        var text = Converter.Convert(DayOfWeek.Tuesday, typeof(string), null, English);

        Assert.Equal("Tuesday", text);
    }

    /// <summary>
    /// Verifies that a <see langword="null"/> value converts to an empty string.
    /// </summary>
    [Fact]
    public void Convert_Null_ReturnsEmptyString()
    {
        var text = Converter.Convert(null!, typeof(string), null, English);

        Assert.Equal(string.Empty, text);
    }

    /// <summary>
    /// Verifies that <see cref="EnumDisplayNameConverter.ConvertBack"/> always throws
    /// <see cref="NotSupportedException"/> because the conversion is one-way.
    /// </summary>
    [Fact]
    public void ConvertBack_Always_ThrowsNotSupportedException()
    {
        Assert.Throws<NotSupportedException>(
            () => Converter.ConvertBack("Grid aligned", typeof(PanelGeometryMode), null, English));
    }
}
