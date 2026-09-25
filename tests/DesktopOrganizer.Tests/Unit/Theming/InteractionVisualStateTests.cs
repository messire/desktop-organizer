using DesktopOrganizer.UI.Interaction;
using DesktopOrganizer.UI.Theming;

namespace DesktopOrganizer.Tests.Unit.Theming;

/// <summary>
/// Unit tests for <see cref="InteractionVisualState"/> — the machine-checkable form of D-6
/// (docs/PLANS/phase-5-ambient-accent-ui.md §3.3) and the DoD "Idle/Hover/Editing/Dragging/Resizing
/// are visually distinguishable". <see cref="PanelInteractionTracker"/> itself is not touched or
/// exercised here — only the pure <see cref="InteractionVisualState.Resolve"/> function, which must
/// stay well-defined for every (gesture, isPointerOver, isTitleEditing) combination even though the
/// real tracker (Phase 1, ADR-021) only ever produces Idle/Dragging/Resizing (T-TRK-08).
/// </summary>
[Trait(UnitTests, "UI")]
public class InteractionVisualStateTests
{
    // Priority (D-6): Dragging|Resizing > Editing > Hover > Idle. A Dragging/Resizing gesture always
    // wins regardless of pointer/editing facts; otherwise isTitleEditing beats isPointerOver.

    /// <summary>
    /// Provides every (gesture, isPointerOver, isTitleEditing) combination along with the expected
    /// <see cref="PanelVisualState"/> per the D-6 priority table, as theory data for
    /// <see cref="Resolve_AllCombinations_MatchPriorityTable"/>.
    /// </summary>
    public static IEnumerable<object[]> ResolveCases()
    {
        foreach (var gesture in Enum.GetValues<PanelInteractionState>())
        {
            foreach (var isPointerOver in new[] { false, true })
            {
                foreach (var isTitleEditing in new[] { false, true })
                {
                    var expected = gesture switch
                    {
                        PanelInteractionState.Dragging => PanelVisualState.Dragging,
                        PanelInteractionState.Resizing => PanelVisualState.Resizing,
                        _ => isTitleEditing
                            ? PanelVisualState.Editing
                            : isPointerOver
                                ? PanelVisualState.Hover
                                : PanelVisualState.Idle,
                    };

                    yield return new object[] { gesture, isPointerOver, isTitleEditing, expected };
                }
            }
        }
    }

    /// <summary>
    /// Verifies that <see cref="InteractionVisualState.Resolve"/> matches the D-6 priority table for
    /// every (gesture, isPointerOver, isTitleEditing) combination.
    /// </summary>
    /// <param name="gesture">The active interaction gesture.</param>
    /// <param name="isPointerOver">Whether the pointer is hovering the panel.</param>
    /// <param name="isTitleEditing">Whether the panel title is being edited.</param>
    /// <param name="expected">The expected resolved visual state.</param>
    [Theory]
    [MemberData(nameof(ResolveCases))]
    public void Resolve_AllCombinations_MatchPriorityTable(
        PanelInteractionState gesture, bool isPointerOver, bool isTitleEditing, PanelVisualState expected)
    {
        var result = InteractionVisualState.Resolve(gesture, isPointerOver, isTitleEditing);

        Assert.Equal(expected, result);
    }

    /// <summary>
    /// Verifies that a dragging gesture always resolves to <see cref="PanelVisualState.Dragging"/>, even
    /// when the pointer is over the panel and the title is being edited.
    /// </summary>
    [Fact]
    public void Resolve_DraggingGesture_AlwaysWinsOverEditingAndHover()
    {
        var result = InteractionVisualState.Resolve(PanelInteractionState.Dragging, isPointerOver: true, isTitleEditing: true);

        Assert.Equal(PanelVisualState.Dragging, result);
    }

    /// <summary>
    /// Verifies that a resizing gesture always resolves to <see cref="PanelVisualState.Resizing"/>, even
    /// when the pointer is over the panel and the title is being edited.
    /// </summary>
    [Fact]
    public void Resolve_ResizingGesture_AlwaysWinsOverEditingAndHover()
    {
        var result = InteractionVisualState.Resolve(PanelInteractionState.Resizing, isPointerOver: true, isTitleEditing: true);

        Assert.Equal(PanelVisualState.Resizing, result);
    }

    private static readonly PanelVisualState[] PriorityOrder =
    {
        PanelVisualState.Idle,
        PanelVisualState.Hover,
        PanelVisualState.Editing,
        PanelVisualState.Dragging,
        PanelVisualState.Resizing,
    };

    /// <summary>
    /// Verifies that <see cref="InteractionVisualState.BorderOpacityFor"/> strictly increases across all
    /// five visual states, including between Dragging and Resizing (unlike the chrome/accent channels).
    /// </summary>
    [Fact]
    public void BorderOpacityFor_StrictlyIncreasesAcrossAllFiveStates()
    {
        // MAJOR-5 fix: unlike ChromeOpacityFor/AccentEmphasisFor below, BorderOpacityFor no longer
        // allows Dragging/Resizing to tie — it alone is what keeps the two states' combined visual
        // signature pairwise-distinct (see VisualStateSignatures_AreAllPairwiseDistinct_AcrossAllFiveStates).
        var values = Array.ConvertAll(PriorityOrder, InteractionVisualState.BorderOpacityFor);

        for (var i = 1; i < values.Length; i++)
            Assert.True(values[i] > values[i - 1], $"Expected strictly increasing at index {i} ({PriorityOrder[i]})");
    }

    /// <summary>
    /// Verifies that <see cref="InteractionVisualState.ChromeOpacityFor"/> increases monotonically with
    /// priority across the five visual states, allowing Dragging and Resizing to tie.
    /// </summary>
    [Fact]
    public void ChromeOpacityFor_MonotonicallyIncreasesWithPriority_DraggingAndResizingMayTie()
    {
        AssertMonotonicWithAllowedTie(InteractionVisualState.ChromeOpacityFor);
    }

    /// <summary>
    /// Verifies that <see cref="InteractionVisualState.BorderOpacityFor"/> is lowest for
    /// <see cref="PanelVisualState.Idle"/> and highest for <see cref="PanelVisualState.Resizing"/>.
    /// </summary>
    [Fact]
    public void BorderOpacityFor_IdleIsLowest_ResizingIsHighest()
    {
        Assert.True(InteractionVisualState.BorderOpacityFor(PanelVisualState.Idle) < InteractionVisualState.BorderOpacityFor(PanelVisualState.Resizing));
    }

    /// <summary>
    /// Verifies that <see cref="InteractionVisualState.ChromeOpacityFor"/> is muted (not fully opaque) for
    /// <see cref="PanelVisualState.Idle"/> and fully revealed for <see cref="PanelVisualState.Resizing"/>.
    /// </summary>
    [Fact]
    public void ChromeOpacityFor_IdleIsMuted_ResizingIsFullyRevealed()
    {
        // "actions muted in idle" DoD: Idle must not be fully opaque, Resizing must be.
        Assert.True(InteractionVisualState.ChromeOpacityFor(PanelVisualState.Idle) < 1.0);
        Assert.Equal(1.0, InteractionVisualState.ChromeOpacityFor(PanelVisualState.Resizing));
    }

    /// <summary>
    /// Verifies that <see cref="InteractionVisualState.AccentEmphasisFor"/> increases monotonically with
    /// priority across the five visual states, allowing Dragging and Resizing to tie.
    /// </summary>
    [Fact]
    public void AccentEmphasisFor_MonotonicallyIncreasesWithPriority_DraggingAndResizingMayTie()
    {
        AssertMonotonicWithAllowedTie(InteractionVisualState.AccentEmphasisFor);
    }

    /// <summary>
    /// Verifies that <see cref="InteractionVisualState.AccentEmphasisFor"/> pins Resizing (1.7) strictly
    /// greater than Dragging (1.5), so the two no longer tie on the accent channel.
    /// </summary>
    [Fact]
    public void AccentEmphasisFor_ResizingIsStrictlyGreaterThanDragging()
    {
        // Cheap fix (review round 3): this class's own doc comment (and InteractionVisualState.cs's,
        // ~lines 13-18/70-77) claims Dragging/Resizing "no longer tie on the accent channel" now that
        // AccentEmphasisFor is wired to PART_AccentIndicator's width (MAJOR-B) — but until this Fact,
        // that specific claim was only ever covered by the *_MayTie test above, which permits (but does
        // not require) Resizing > Dragging. Pin the actual current values (1.7 vs. 1.5) so a future
        // change that silently re-ties them (e.g. reverting Resizing back to 1.5) fails a test instead of
        // only regressing an unverified doc claim.
        Assert.True(
            InteractionVisualState.AccentEmphasisFor(PanelVisualState.Resizing) > InteractionVisualState.AccentEmphasisFor(PanelVisualState.Dragging));
        Assert.Equal(1.7, InteractionVisualState.AccentEmphasisFor(PanelVisualState.Resizing));
        Assert.Equal(1.5, InteractionVisualState.AccentEmphasisFor(PanelVisualState.Dragging));
    }

    /// <summary>
    /// Verifies that the combined (border, chrome, accent) signature for each of the five visual states
    /// is pairwise distinct, even though individual channels may tie between Dragging and Resizing.
    /// </summary>
    [Fact]
    public void VisualStateSignatures_AreAllPairwiseDistinct_AcrossAllFiveStates()
    {
        // The literal DoD, machine-checked (MAJOR-5 fix): Idle/Hover/Editing/Dragging/Resizing must each
        // render as a visually distinguishable combination of (border, chrome, accent) — not necessarily
        // distinct on EVERY individual channel (ChromeOpacityFor/AccentEmphasisFor may still tie between
        // Dragging and Resizing), but distinct as a whole for every pair of states.
        var signatures = Array.ConvertAll(PriorityOrder, state => (
            Border: InteractionVisualState.BorderOpacityFor(state),
            Chrome: InteractionVisualState.ChromeOpacityFor(state),
            Accent: InteractionVisualState.AccentEmphasisFor(state)));

        for (var i = 0; i < signatures.Length; i++)
        {
            for (var j = i + 1; j < signatures.Length; j++)
            {
                Assert.True(
                    signatures[i] != signatures[j],
                    $"{PriorityOrder[i]} and {PriorityOrder[j]} render an identical (border, chrome, accent) signature: {signatures[i]}");
            }
        }
    }

    private static void AssertMonotonicWithAllowedTie(Func<PanelVisualState, double> selector)
    {
        var values = Array.ConvertAll(PriorityOrder, state => selector(state));

        // Strictly increasing Idle -> Hover -> Editing -> Dragging.
        for (var i = 1; i < values.Length - 1; i++)
            Assert.True(values[i] > values[i - 1], $"Expected strictly increasing at index {i} ({PriorityOrder[i]})");

        // Dragging and Resizing (last two) may tie, but neither may fall below Editing.
        Assert.True(values[^1] >= values[^2]);
        Assert.True(values[^1] > values[^3]);
    }
}
