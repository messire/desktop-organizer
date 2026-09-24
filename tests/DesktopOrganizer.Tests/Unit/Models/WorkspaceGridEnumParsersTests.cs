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

    [Theory]
    [InlineData("LegacyFreeform", PanelGeometryMode.LegacyFreeform)]
    [InlineData("legacyfreeform", PanelGeometryMode.LegacyFreeform)]
    [InlineData("GridAligned", PanelGeometryMode.GridAligned)]
    [InlineData("gridaligned", PanelGeometryMode.GridAligned)]
    public void PanelGeometryMode_ValidInput_ParsesCaseInsensitively(string value, PanelGeometryMode expected) =>
        Assert.Equal(expected, PanelGeometryModeParser.ParseOrLegacyFreeform(value));

    // ── PanelSizingModeParser ────────────────────────────────────────────────────────────────────
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

    [Theory]
    [InlineData("Manual", PanelSizingMode.Manual)]
    [InlineData("AutoContent", PanelSizingMode.AutoContent)]
    [InlineData("autocontent", PanelSizingMode.AutoContent)]
    public void PanelSizingMode_ValidInput_ParsesCaseInsensitively(string value, PanelSizingMode expected) =>
        Assert.Equal(expected, PanelSizingModeParser.ParseOrManual(value));

    // ── IconLayoutPolicyParser ───────────────────────────────────────────────────────────────────
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

    [Theory]
    [InlineData("LegacyStretch", IconLayoutPolicy.LegacyStretch)]
    [InlineData("FixedGap", IconLayoutPolicy.FixedGap)]
    [InlineData("fixedgap", IconLayoutPolicy.FixedGap)]
    public void IconLayoutPolicy_ValidInput_ParsesCaseInsensitively(string value, IconLayoutPolicy expected) =>
        Assert.Equal(expected, IconLayoutPolicyParser.ParseOrLegacyStretch(value));
}
