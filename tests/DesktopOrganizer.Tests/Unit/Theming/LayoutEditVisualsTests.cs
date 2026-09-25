using DesktopOrganizer.UI.Theming;

namespace DesktopOrganizer.Tests.Unit.Theming;

/// <summary>
/// Unit tests for <see cref="LayoutEditVisuals"/> (docs/PLANS/phase-6-edit-layout-ux.md §1 D-6, §7.1 item 4)
/// — the machine-checkable form of two DoD requirements: (1) Edit Layout mode is visually distinguishable
/// from Normal at every <see cref="PanelVisualState"/> (monotonicity), and (2) Phase 5's <see cref="InteractionVisualState"/>
/// does not move a single unit outside Edit Layout mode (exact regression).
/// </summary>
[Trait(UnitTests, "UI")]
public class LayoutEditVisualsTests
{
    private static readonly PanelVisualState[] AllStates =
    [
        PanelVisualState.Idle,
        PanelVisualState.Hover,
        PanelVisualState.Editing,
        PanelVisualState.Dragging,
        PanelVisualState.Resizing,
    ];

    /// <summary>
    /// Provides every <see cref="PanelVisualState"/> as theory data for the tests below.
    /// </summary>
    public static IEnumerable<object[]> States() => AllStates.Select(s => new object[] { s });

    // ------------------------------------------------------------------
    // Regression: isLayoutEditing:false must be EXACTLY InteractionVisualState's own value — Phase 5 does
    // not move (§0).
    // ------------------------------------------------------------------

    /// <summary>
    /// Verifies that <see cref="LayoutEditVisuals.BorderOpacityFor"/> with <c>isLayoutEditing:false</c>
    /// exactly matches <see cref="InteractionVisualState.BorderOpacityFor"/>.
    /// </summary>
    /// <param name="state">The visual state under test.</param>
    [Theory]
    [MemberData(nameof(States))]
    public void BorderOpacityFor_EditingFalse_MatchesInteractionVisualStateExactly(PanelVisualState state)
    {
        Assert.Equal(InteractionVisualState.BorderOpacityFor(state), LayoutEditVisuals.BorderOpacityFor(state, isLayoutEditing: false));
    }

    /// <summary>
    /// Verifies that <see cref="LayoutEditVisuals.ChromeOpacityFor"/> with <c>isLayoutEditing:false</c>
    /// exactly matches <see cref="InteractionVisualState.ChromeOpacityFor"/>.
    /// </summary>
    /// <param name="state">The visual state under test.</param>
    [Theory]
    [MemberData(nameof(States))]
    public void ChromeOpacityFor_EditingFalse_MatchesInteractionVisualStateExactly(PanelVisualState state)
    {
        Assert.Equal(InteractionVisualState.ChromeOpacityFor(state), LayoutEditVisuals.ChromeOpacityFor(state, isLayoutEditing: false));
    }

    /// <summary>
    /// Verifies that <see cref="LayoutEditVisuals.AccentEmphasisFor"/> with <c>isLayoutEditing:false</c>
    /// exactly matches <see cref="InteractionVisualState.AccentEmphasisFor"/>.
    /// </summary>
    /// <param name="state">The visual state under test.</param>
    [Theory]
    [MemberData(nameof(States))]
    public void AccentEmphasisFor_EditingFalse_MatchesInteractionVisualStateExactly(PanelVisualState state)
    {
        Assert.Equal(InteractionVisualState.AccentEmphasisFor(state), LayoutEditVisuals.AccentEmphasisFor(state, isLayoutEditing: false));
    }

    // ------------------------------------------------------------------
    // Monotonicity (DoD "edit-mode visually distinguishable"): for every state, isLayoutEditing:true is
    // >= isLayoutEditing:false on EVERY channel, and STRICTLY greater on at least one.
    // ------------------------------------------------------------------

    /// <summary>
    /// Verifies that <c>isLayoutEditing:true</c> is never less than <c>isLayoutEditing:false</c> on the
    /// border, chrome, and accent channels, for every visual state.
    /// </summary>
    /// <param name="state">The visual state under test.</param>
    [Theory]
    [MemberData(nameof(States))]
    public void EditingTrue_NeverLessThanEditingFalse_OnAnyChannel(PanelVisualState state)
    {
        Assert.True(LayoutEditVisuals.BorderOpacityFor(state, true) >= LayoutEditVisuals.BorderOpacityFor(state, false));
        Assert.True(LayoutEditVisuals.ChromeOpacityFor(state, true) >= LayoutEditVisuals.ChromeOpacityFor(state, false));
        Assert.True(LayoutEditVisuals.AccentEmphasisFor(state, true) >= LayoutEditVisuals.AccentEmphasisFor(state, false));
    }

    /// <summary>
    /// Verifies that <c>isLayoutEditing:true</c> is strictly greater than <c>isLayoutEditing:false</c> on
    /// at least one of the border, chrome, or accent channels, for every visual state.
    /// </summary>
    /// <param name="state">The visual state under test.</param>
    [Theory]
    [MemberData(nameof(States))]
    public void EditingTrue_StrictlyGreaterThanEditingFalse_OnAtLeastOneChannel(PanelVisualState state)
    {
        var border = LayoutEditVisuals.BorderOpacityFor(state, true) > LayoutEditVisuals.BorderOpacityFor(state, false);
        var chrome = LayoutEditVisuals.ChromeOpacityFor(state, true) > LayoutEditVisuals.ChromeOpacityFor(state, false);
        var accent = LayoutEditVisuals.AccentEmphasisFor(state, true) > LayoutEditVisuals.AccentEmphasisFor(state, false);

        Assert.True(border || chrome || accent, $"No channel strictly increased for {state} while editing.");
    }

    // ------------------------------------------------------------------
    // Specific value pins
    // ------------------------------------------------------------------

    /// <summary>
    /// Verifies that <see cref="LayoutEditVisuals.BorderOpacityFor"/> for idle, editing is floored at
    /// <see cref="LayoutEditVisuals.EditBorderFloor"/> rather than the idle baseline.
    /// </summary>
    [Fact]
    public void BorderOpacityFor_Idle_Editing_IsFlooredAtEditingLevel()
    {
        Assert.Equal(0.15, InteractionVisualState.BorderOpacityFor(PanelVisualState.Idle));
        Assert.Equal(LayoutEditVisuals.EditBorderFloor, LayoutEditVisuals.BorderOpacityFor(PanelVisualState.Idle, isLayoutEditing: true));
    }

    /// <summary>
    /// Verifies that a state whose baseline border opacity already exceeds
    /// <see cref="LayoutEditVisuals.EditBorderFloor"/> is unchanged by entering edit mode.
    /// </summary>
    [Fact]
    public void BorderOpacityFor_AlreadyAboveFloor_IsUnchangedByEditing()
    {
        // Resizing's baseline (1.0) already exceeds EditBorderFloor (0.60) — the floor must never lower it.
        Assert.Equal(
            InteractionVisualState.BorderOpacityFor(PanelVisualState.Resizing),
            LayoutEditVisuals.BorderOpacityFor(PanelVisualState.Resizing, isLayoutEditing: true));
    }

    /// <summary>
    /// Verifies that <see cref="LayoutEditVisuals.ChromeOpacityFor"/> for idle, editing is forced fully opaque.
    /// </summary>
    [Fact]
    public void ChromeOpacityFor_Idle_Editing_IsForcedFullyOpaque()
    {
        Assert.Equal(1.0, LayoutEditVisuals.ChromeOpacityFor(PanelVisualState.Idle, isLayoutEditing: true));
    }

    /// <summary>
    /// Verifies that <see cref="LayoutEditVisuals.ChromeOpacityFor"/> for non-idle states is unaffected by
    /// entering edit mode, matching <see cref="InteractionVisualState.ChromeOpacityFor"/> exactly.
    /// </summary>
    /// <param name="state">The non-idle visual state under test.</param>
    [Theory]
    [InlineData(PanelVisualState.Hover)]
    [InlineData(PanelVisualState.Editing)]
    [InlineData(PanelVisualState.Dragging)]
    [InlineData(PanelVisualState.Resizing)]
    public void ChromeOpacityFor_NonIdleStates_UnaffectedByEditing(PanelVisualState state)
    {
        Assert.Equal(InteractionVisualState.ChromeOpacityFor(state), LayoutEditVisuals.ChromeOpacityFor(state, isLayoutEditing: true));
    }

    /// <summary>
    /// Verifies that <see cref="LayoutEditVisuals.AccentEmphasisFor"/> for editing scales the baseline
    /// <see cref="InteractionVisualState.AccentEmphasisFor"/> value by <see cref="LayoutEditVisuals.AccentEmphasis"/>.
    /// </summary>
    [Fact]
    public void AccentEmphasisFor_Editing_ScalesBaselineByAccentEmphasisConstant()
    {
        var expected = InteractionVisualState.AccentEmphasisFor(PanelVisualState.Hover) * LayoutEditVisuals.AccentEmphasis;

        Assert.Equal(expected, LayoutEditVisuals.AccentEmphasisFor(PanelVisualState.Hover, isLayoutEditing: true));
    }

    /// <summary>
    /// Verifies that <see cref="LayoutEditVisuals.AccentEmphasis"/> stays pinned at 1.35.
    /// </summary>
    [Fact]
    public void AccentEmphasis_IsPinnedAt135()
    {
        Assert.Equal(1.35, LayoutEditVisuals.AccentEmphasis);
    }

    /// <summary>
    /// Verifies that <see cref="LayoutEditVisuals.EditBorderFloor"/> stays pinned at 0.60, matching
    /// <see cref="InteractionVisualState.BorderOpacityFor"/> for <see cref="PanelVisualState.Editing"/>.
    /// </summary>
    [Fact]
    public void EditBorderFloor_IsPinnedAtEditingStateLevel()
    {
        Assert.Equal(0.60, LayoutEditVisuals.EditBorderFloor);
        Assert.Equal(LayoutEditVisuals.EditBorderFloor, InteractionVisualState.BorderOpacityFor(PanelVisualState.Editing));
    }
}
