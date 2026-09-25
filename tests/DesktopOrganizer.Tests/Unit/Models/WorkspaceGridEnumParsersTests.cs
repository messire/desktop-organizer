using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.Tests.Unit.Models;

/// <summary>
/// Unit tests for <see cref="PanelGeometryModeParser"/>/<see cref="PanelSizingModeParser"/>/
/// <see cref="IconLayoutPolicyParser"/>'s <c>Enum.IsDefined</c> hardening (fix-cycle 1 MINOR finding,
/// docs/PLANS/workspace-grid-migration.md §10.5): a bare <c>Enum.TryParse</c> on a non-<c>[Flags]</c>
/// enum still accepts a numeric string outside the defined range and a comma-separated combination of
/// member names — both are reachable from a hand-edited or corrupted v5 document and must degrade to
/// the safe default exactly like any other unrecognised string, not silently produce an undefined value.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class WorkspaceGridEnumParsersTests
{
    // ── PanelGeometryModeParser ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that out-of-range numeric strings, a comma-separated combination of member names,
    /// unrecognised text, empty, and null all degrade to <see cref="PanelGeometryMode.LegacyFreeform"/>
    /// rather than producing an undefined enum value.
    /// </summary>
    /// <param name="value">The raw, possibly invalid or corrupted persisted string to parse.</param>
    [Theory]
    [InlineData("7")]
    [InlineData("-1")]
    [InlineData("2")]
    [InlineData("LegacyFreeform,GridAligned")]
    [InlineData("not-a-real-value")]
    [InlineData("")]
    [InlineData(null)]
    public void PanelGeometryMode_InvalidInput_FallsBackToLegacyFreeform(string? value) =>
        Assert.Equal(PanelGeometryMode.LegacyFreeform, PanelGeometryModeParser.ParseOrLegacyFreeform(value));

    /// <summary>
    /// Verifies that valid member names parse to their corresponding <see cref="PanelGeometryMode"/>
    /// value regardless of letter case.
    /// </summary>
    /// <param name="value">The member name to parse, in any letter case.</param>
    /// <param name="expected">The <see cref="PanelGeometryMode"/> value that name should parse to.</param>
    [Theory]
    [InlineData("LegacyFreeform", PanelGeometryMode.LegacyFreeform)]
    [InlineData("legacyfreeform", PanelGeometryMode.LegacyFreeform)]
    [InlineData("GridAligned", PanelGeometryMode.GridAligned)]
    [InlineData("gridaligned", PanelGeometryMode.GridAligned)]
    public void PanelGeometryMode_ValidInput_ParsesCaseInsensitively(string value, PanelGeometryMode expected) =>
        Assert.Equal(expected, PanelGeometryModeParser.ParseOrLegacyFreeform(value));

    // ── PanelSizingModeParser ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that out-of-range numeric strings, a comma-separated combination of member names,
    /// unrecognised text, empty, and null all degrade to <see cref="PanelSizingMode.Manual"/> rather
    /// than producing an undefined enum value.
    /// </summary>
    /// <param name="value">The raw, possibly invalid or corrupted persisted string to parse.</param>
    [Theory]
    [InlineData("7")]
    [InlineData("-1")]
    [InlineData("2")]
    [InlineData("Manual,AutoContent")]
    [InlineData("not-a-real-value")]
    [InlineData("")]
    [InlineData(null)]
    public void PanelSizingMode_InvalidInput_FallsBackToManual(string? value) =>
        Assert.Equal(PanelSizingMode.Manual, PanelSizingModeParser.ParseOrManual(value));

    /// <summary>
    /// Verifies that valid member names parse to their corresponding <see cref="PanelSizingMode"/>
    /// value regardless of letter case.
    /// </summary>
    /// <param name="value">The member name to parse, in any letter case.</param>
    /// <param name="expected">The <see cref="PanelSizingMode"/> value that name should parse to.</param>
    [Theory]
    [InlineData("Manual", PanelSizingMode.Manual)]
    [InlineData("AutoContent", PanelSizingMode.AutoContent)]
    [InlineData("autocontent", PanelSizingMode.AutoContent)]
    public void PanelSizingMode_ValidInput_ParsesCaseInsensitively(string value, PanelSizingMode expected) =>
        Assert.Equal(expected, PanelSizingModeParser.ParseOrManual(value));

    // ── IconLayoutPolicyParser ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that out-of-range numeric strings, a comma-separated combination of member names,
    /// unrecognised text, empty, and null all degrade to <see cref="IconLayoutPolicy.LegacyStretch"/>
    /// rather than producing an undefined enum value.
    /// </summary>
    /// <param name="value">The raw, possibly invalid or corrupted persisted string to parse.</param>
    [Theory]
    [InlineData("7")]
    [InlineData("-1")]
    [InlineData("2")]
    [InlineData("LegacyStretch,FixedGap")]
    [InlineData("not-a-real-value")]
    [InlineData("")]
    [InlineData(null)]
    public void IconLayoutPolicy_InvalidInput_FallsBackToLegacyStretch(string? value) =>
        Assert.Equal(IconLayoutPolicy.LegacyStretch, IconLayoutPolicyParser.ParseOrLegacyStretch(value));

    /// <summary>
    /// Verifies that valid member names parse to their corresponding <see cref="IconLayoutPolicy"/>
    /// value regardless of letter case.
    /// </summary>
    /// <param name="value">The member name to parse, in any letter case.</param>
    /// <param name="expected">The <see cref="IconLayoutPolicy"/> value that name should parse to.</param>
    [Theory]
    [InlineData("LegacyStretch", IconLayoutPolicy.LegacyStretch)]
    [InlineData("FixedGap", IconLayoutPolicy.FixedGap)]
    [InlineData("fixedgap", IconLayoutPolicy.FixedGap)]
    public void IconLayoutPolicy_ValidInput_ParsesCaseInsensitively(string value, IconLayoutPolicy expected) =>
        Assert.Equal(expected, IconLayoutPolicyParser.ParseOrLegacyStretch(value));
}
