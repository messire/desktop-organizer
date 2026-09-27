using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Properties;
using DesktopOrganizer.UI.State.Dto;

namespace DesktopOrganizer.Tests.Unit.Models;

/// <summary>
/// Unit tests for <see cref="SizeStepPolicy"/> (docs/PLANS/color-presets-and-size-step.md §2.1/§2.2/§2.3):
/// pins the step and ranges, and proves every existing default/legacy value that could have been persisted
/// still lands inside the new ranges — the reason §2.3 gives for not needing a persistence-freeze
/// <c>CLARIFICATION_NEEDED</c>.
/// </summary>
[Trait(UnitTests, "UI")]
public class SizeStepPolicyTests
{
    /// <summary>Verifies <see cref="SizeStepPolicy.Step"/> is 8 (§2.1 D-S1).</summary>
    [Fact]
    public void Step_Is8()
    {
        Assert.Equal(8, SizeStepPolicy.Step);
    }

    /// <summary>Verifies the icon-size range is 16..128 (§2.2).</summary>
    [Fact]
    public void IconSizeRange_Is16To128()
    {
        Assert.Equal(16, SizeStepPolicy.IconSizeMinimum);
        Assert.Equal(128, SizeStepPolicy.IconSizeMaximum);
    }

    /// <summary>Verifies the grid-step range is 2..128 (§2.2).</summary>
    [Fact]
    public void GridStepRange_Is2To128()
    {
        Assert.Equal(2, SizeStepPolicy.GridStepMinimum);
        Assert.Equal(128, SizeStepPolicy.GridStepMaximum);
    }

    /// <summary>Verifies the two ranges share the same upper bound.</summary>
    [Fact]
    public void IconSizeMaximum_EqualsGridStepMaximum()
    {
        Assert.Equal(SizeStepPolicy.GridStepMaximum, SizeStepPolicy.IconSizeMaximum);
    }

    /// <summary>Verifies <see cref="SizeStepPolicy.IconSizeMinimum"/> is a multiple of <see cref="SizeStepPolicy.Step"/>.</summary>
    [Fact]
    public void IconSizeMinimum_IsMultipleOfStep()
    {
        Assert.Equal(0, SizeStepPolicy.IconSizeMinimum % SizeStepPolicy.Step);
    }

    /// <summary>Verifies <see cref="SizeStepPolicy.IconSizeMaximum"/> is a multiple of <see cref="SizeStepPolicy.Step"/>.</summary>
    [Fact]
    public void Maximum_IsMultipleOfStep()
    {
        Assert.Equal(0, SizeStepPolicy.IconSizeMaximum % SizeStepPolicy.Step);
    }

    /// <summary>
    /// Verifies <see cref="SizeStepPolicy.GridStepMinimum"/> is deliberately NOT a multiple of
    /// <see cref="SizeStepPolicy.Step"/> — M-6 (legacy <c>GridSize</c> == 2/3) keeps the floor at 2 so
    /// those legacy values remain valid without a migration (§2.2).
    /// </summary>
    [Fact]
    public void GridStepMinimum_IsNotMultipleOfStep_ByDesign()
    {
        Assert.NotEqual(0, SizeStepPolicy.GridStepMinimum % SizeStepPolicy.Step);
    }

    /// <summary>Verifies the old icon-size slider's endpoints (16..96) are a subset of the new range (§2.3).</summary>
    /// <param name="legacyValue">An endpoint of the old 16..96 icon-size range.</param>
    [Theory]
    [InlineData(16)]
    [InlineData(96)]
    public void LegacyIconSizeRange_IsSubsetOfNewRange(int legacyValue)
    {
        Assert.InRange(legacyValue, SizeStepPolicy.IconSizeMinimum, SizeStepPolicy.IconSizeMaximum);
    }

    /// <summary>Verifies the old grid-step slider's endpoints (2..128) are a subset of the new range (§2.3).</summary>
    /// <param name="legacyValue">An endpoint of the old 2..128 grid-step range.</param>
    [Theory]
    [InlineData(2)]
    [InlineData(128)]
    public void LegacyGridStepRange_IsSubsetOfNewRange(int legacyValue)
    {
        Assert.InRange(legacyValue, SizeStepPolicy.GridStepMinimum, SizeStepPolicy.GridStepMaximum);
    }

    /// <summary>
    /// <c>Settings.settings</c>' shipped <c>DefaultIconSize</c> default (metadata only — this reads the
    /// setting's declared default value, never the user's actual <c>user.config</c>) is unchanged, in
    /// range, and a multiple of <see cref="SizeStepPolicy.Step"/> (§2.3).
    /// </summary>
    [Fact]
    public void DefaultIconSizeSettingMetadata_Is48_InRangeAndOnStep()
    {
        var defaultValue = Settings.Default.Properties["DefaultIconSize"]!.DefaultValue;

        Assert.Equal("48", defaultValue);
        Assert.InRange(48, SizeStepPolicy.IconSizeMinimum, SizeStepPolicy.IconSizeMaximum);
        Assert.Equal(0, 48 % SizeStepPolicy.Step);
    }

    /// <summary>
    /// <see cref="PanelAppearanceDto"/>'s own field initializer default (§2.3) is unchanged, in range, and
    /// a multiple of <see cref="SizeStepPolicy.Step"/>.
    /// </summary>
    [Fact]
    public void PanelAppearanceDtoDefaultIconSize_Is48_InRangeAndOnStep()
    {
        var dto = new PanelAppearanceDto();

        Assert.Equal(48, dto.IconSize);
        Assert.InRange(dto.IconSize, SizeStepPolicy.IconSizeMinimum, SizeStepPolicy.IconSizeMaximum);
        Assert.Equal(0, (int)dto.IconSize % SizeStepPolicy.Step);
    }
}
