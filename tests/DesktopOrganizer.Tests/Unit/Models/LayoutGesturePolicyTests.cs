using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.Tests.Unit.Models;

/// <summary>
/// Unit tests for <see cref="LayoutGesturePolicy"/> — the pure 2×2×3 decision table behind Edit Layout
/// mode's move/resize gating (docs/PLANS/phase-6-edit-layout-ux.md §1 D-2). TDD red phase
/// (tdd-test-engineer, P6-1): written before <see cref="LayoutGesturePolicy.Resolve"/> is implemented
/// — every <c>Resolve_*</c> test below is expected to fail (currently via
/// <see cref="NotImplementedException"/>) until dotnet-services-engineer implements it.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class LayoutGesturePolicyTests
{
    // Full truth table, plan §1 D-2:
    //
    // | Locked | Mode       | NormalPolicy   | CanMove | CanResize |
    // |--------|------------|----------------|---------|-----------|
    // | true   | any        | any            | false   | false     |
    // | false  | EditLayout | any            | true    | true      |
    // | false  | Normal     | MoveAndResize  | true    | true      |
    // | false  | Normal     | MoveOnly       | true    | false     |
    // | false  | Normal     | None           | false   | false     |

    // ----- Locked always wins (6 cases: 2 modes x 3 normal policies) -----

    [Theory]
    [InlineData(LayoutInteractionMode.Normal, NormalModeLayoutGestures.MoveAndResize)]
    [InlineData(LayoutInteractionMode.Normal, NormalModeLayoutGestures.MoveOnly)]
    [InlineData(LayoutInteractionMode.Normal, NormalModeLayoutGestures.None)]
    [InlineData(LayoutInteractionMode.EditLayout, NormalModeLayoutGestures.MoveAndResize)]
    [InlineData(LayoutInteractionMode.EditLayout, NormalModeLayoutGestures.MoveOnly)]
    [InlineData(LayoutInteractionMode.EditLayout, NormalModeLayoutGestures.None)]
    public void Resolve_WhenLocked_AlwaysForbidsMoveAndResize(LayoutInteractionMode mode, NormalModeLayoutGestures normalPolicy)
    {
        var result = LayoutGesturePolicy.Resolve(mode, isLayoutLocked: true, normalPolicy);

        Assert.False(result.CanMove);
        Assert.False(result.CanResize);
    }

    // ----- Unlocked + EditLayout always allows both, regardless of NormalPolicy (3 cases) -----

    [Theory]
    [InlineData(NormalModeLayoutGestures.MoveAndResize)]
    [InlineData(NormalModeLayoutGestures.MoveOnly)]
    [InlineData(NormalModeLayoutGestures.None)]
    public void Resolve_UnlockedEditLayout_AlwaysAllowsMoveAndResize(NormalModeLayoutGestures normalPolicy)
    {
        var result = LayoutGesturePolicy.Resolve(LayoutInteractionMode.EditLayout, isLayoutLocked: false, normalPolicy);

        Assert.True(result.CanMove);
        Assert.True(result.CanResize);
    }

    // ----- Unlocked + Normal is decided per-gesture by NormalPolicy (3 cases) -----

    [Fact]
    public void Resolve_UnlockedNormal_MoveAndResize_AllowsBoth()
    {
        var result = LayoutGesturePolicy.Resolve(LayoutInteractionMode.Normal, isLayoutLocked: false, NormalModeLayoutGestures.MoveAndResize);

        Assert.True(result.CanMove);
        Assert.True(result.CanResize);
    }

    [Fact]
    public void Resolve_UnlockedNormal_MoveOnly_AllowsMoveForbidsResize()
    {
        var result = LayoutGesturePolicy.Resolve(LayoutInteractionMode.Normal, isLayoutLocked: false, NormalModeLayoutGestures.MoveOnly);

        Assert.True(result.CanMove);
        Assert.False(result.CanResize);
    }

    [Fact]
    public void Resolve_UnlockedNormal_None_ForbidsBoth()
    {
        var result = LayoutGesturePolicy.Resolve(LayoutInteractionMode.Normal, isLayoutLocked: false, NormalModeLayoutGestures.None);

        Assert.False(result.CanMove);
        Assert.False(result.CanResize);
    }

    // ----- NormalModeLayoutGesturesParser — same contract as PanelSurfaceStyleParser (ADR-025 D-10) -----

    [Theory]
    [InlineData("MoveAndResize", NormalModeLayoutGestures.MoveAndResize)]
    [InlineData("moveandresize", NormalModeLayoutGestures.MoveAndResize)]
    [InlineData("MoveOnly", NormalModeLayoutGestures.MoveOnly)]
    [InlineData("None", NormalModeLayoutGestures.None)]
    [InlineData("nOnE", NormalModeLayoutGestures.None)]
    public void ParseOrMoveOnly_RecognisedValue_ParsesCaseInsensitively(string value, NormalModeLayoutGestures expected)
    {
        var result = NormalModeLayoutGesturesParser.ParseOrMoveOnly(value);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("NotARealValue")]
    public void ParseOrMoveOnly_UnrecognisedOrEmptyValue_FallsBackToMoveOnly_NeverThrows(string? value)
    {
        var result = NormalModeLayoutGesturesParser.ParseOrMoveOnly(value);

        Assert.Equal(NormalModeLayoutGestures.MoveOnly, result);
    }
}
