using System.Globalization;
using DesktopOrganizer.Localization;

namespace DesktopOrganizer.Tests.Unit.Localization;

/// <summary>
/// Unit tests for <see cref="SupportedLanguages"/> (docs/PLANS/localization.md D-4, WP-0S DoD 5).
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Localization))]
public class SupportedLanguagesTests
{
    /// <summary>
    /// The full matrix the plan's own DoD calls out: every preference in
    /// <c>{"en", "ru", "", null, "de", "RU"}</c> crossed with every system UI culture in
    /// <c>{ru-RU, en-US, uk-UA, de-DE}</c>.
    /// </summary>
    public static IEnumerable<object?[]> ResolveCases()
    {
        string?[] preferences = { "en", "ru", string.Empty, null, "de", "RU" };
        string[] systemCultureTags = { "ru-RU", "en-US", "uk-UA", "de-DE" };

        foreach (var preference in preferences)
        {
            foreach (var systemCultureTag in systemCultureTags)
            {
                var expected = preference switch
                {
                    "en" => "en",
                    "ru" or "RU" => "ru",
                    _ => systemCultureTag == "ru-RU" ? "ru" : "en",
                };

                yield return new object?[] { preference, systemCultureTag, expected };
            }
        }
    }

    /// <summary>
    /// Verifies <see cref="SupportedLanguages.Resolve"/> across every preference/system-culture
    /// combination in <see cref="ResolveCases"/>.
    /// </summary>
    /// <param name="preference">The raw preference passed to <see cref="SupportedLanguages.Resolve"/>.</param>
    /// <param name="systemCultureTag">The system UI culture tag (e.g. <c>"ru-RU"</c>) to resolve against.</param>
    /// <param name="expectedTag">The expected resolved neutral culture tag (<c>"en"</c> or <c>"ru"</c>).</param>
    [Theory]
    [MemberData(nameof(ResolveCases))]
    public void Resolve_ReturnsExpectedCulture(string? preference, string systemCultureTag, string expectedTag)
    {
        var systemCulture = CultureInfo.GetCultureInfo(systemCultureTag);

        var resolved = SupportedLanguages.Resolve(preference, systemCulture);

        Assert.Equal(expectedTag, resolved.Name);
    }

    /// <summary>
    /// Verifies that a <see langword="null"/> <c>systemUiCulture</c> falls back to English rather than
    /// throwing (defensive, even though the parameter itself is non-nullable).
    /// </summary>
    [Fact]
    public void Resolve_NullSystemCulture_FallsBackToEnglish() =>
        Assert.Equal("en", SupportedLanguages.Resolve(null, null!).Name);

    /// <summary>
    /// Verifies that <see cref="SupportedLanguages.All"/> lists English first, then Russian (picker
    /// display order).
    /// </summary>
    [Fact]
    public void All_ContainsEnglishThenRussian() =>
        Assert.Equal(new[] { "en", "ru" }, SupportedLanguages.All.Select(c => c.Name));

    /// <summary>
    /// Verifies <see cref="SupportedLanguages.IsSupported"/> for supported tags (case-insensitive),
    /// <see langword="null"/>, empty, and an unsupported tag.
    /// </summary>
    /// <param name="tag">The candidate language tag.</param>
    /// <param name="expected">The expected result.</param>
    [Theory]
    [InlineData("en", true)]
    [InlineData("EN", true)]
    [InlineData("ru", true)]
    [InlineData("RU", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("de", false)]
    public void IsSupported_ReturnsExpected(string? tag, bool expected) =>
        Assert.Equal(expected, SupportedLanguages.IsSupported(tag));

    /// <summary>
    /// Verifies that <see cref="SupportedLanguages.FollowSystem"/> is the empty string (D-1's default).
    /// </summary>
    [Fact]
    public void FollowSystem_IsEmptyString() =>
        Assert.Equal(SupportedLanguages.FollowSystem, string.Empty);
}
