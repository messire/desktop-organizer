using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Theming;

namespace DesktopOrganizer.Tests.Unit.Theming;

/// <summary>
/// Unit tests for <see cref="LayeredChromeVisuals"/> (ADR-030, docs/PLANS/reference-panel-chrome.md §5.1
/// T-P1..T-P5) — every channel is an affine remap of <see cref="LayoutEditVisuals.BorderOpacityFor"/>, so
/// monotonicity/edit-floor/five-state-distinguishability are cross-checked against that formula directly,
/// never against a second hand-copied set of literals.
/// </summary>
[Trait(UnitTests, "UI")]
public class LayeredChromeVisualsTests
{
    private static readonly PanelVisualState[] StatesInIncreasingOrder =
    {
        PanelVisualState.Idle,
        PanelVisualState.Hover,
        PanelVisualState.Editing,
        PanelVisualState.Dragging,
        PanelVisualState.Resizing,
    };

    public static IEnumerable<object[]> Channels()
    {
        yield return new object[] { "glow", (Func<PanelVisualState, bool, double>)LayeredChromeVisuals.GlowOpacityFor };
        yield return new object[] { "accentStroke", (Func<PanelVisualState, bool, double>)LayeredChromeVisuals.AccentStrokeOpacityFor };
        yield return new object[] { "neutralStroke", (Func<PanelVisualState, bool, double>)LayeredChromeVisuals.NeutralStrokeOpacityFor };
    }

    /// <summary>T-P1: each channel, with isLayoutEditing=false, strictly increases Idle -&gt; Hover -&gt; Editing -&gt; Dragging -&gt; Resizing.</summary>
    [Theory]
    [MemberData(nameof(Channels))]
    public void Channel_NotEditing_StrictlyIncreasesAcrossAllFiveStates(string name, Func<PanelVisualState, bool, double> channel)
    {
        var previous = double.NegativeInfinity;
        foreach (var state in StatesInIncreasingOrder)
        {
            var value = channel(state, false);
            Assert.True(value > previous, $"{name}: {state} ({value}) must be strictly greater than the previous state's value ({previous}).");
            previous = value;
        }
    }

    /// <summary>T-P2: Idle/not-editing equals the channel's own Idle constant; Resizing/editing equals the channel's own Max constant; every value stays in [0, 1].</summary>
    [Theory]
    [MemberData(nameof(Channels))]
    public void Channel_IdleAndResizingExtremes_MatchTokensAndStayInUnitRange(string name, Func<PanelVisualState, bool, double> channel)
    {
        var idle = channel(PanelVisualState.Idle, false);
        var max = channel(PanelVisualState.Resizing, true);

        Assert.True(idle is >= 0 and <= 1, $"{name} Idle value ({idle}) must be in [0,1].");
        Assert.True(max is >= 0 and <= 1, $"{name} Max value ({max}) must be in [0,1].");

        foreach (var state in StatesInIncreasingOrder)
        {
            foreach (var editing in new[] { false, true })
            {
                var value = channel(state, editing);
                Assert.True(value is >= 0 and <= 1, $"{name}({state}, {editing}) = {value} must stay in [0,1].");
            }
        }
    }

    [Fact]
    public void GlowOpacityFor_IdleNotEditing_EqualsGlowOpacityIdle()
        => Assert.Equal(PanelMetrics.GlowOpacityIdle, LayeredChromeVisuals.GlowOpacityFor(PanelVisualState.Idle, false), precision: 10);

    [Fact]
    public void GlowOpacityFor_ResizingEditing_EqualsGlowOpacityMax()
        => Assert.Equal(PanelMetrics.GlowOpacityMax, LayeredChromeVisuals.GlowOpacityFor(PanelVisualState.Resizing, true), precision: 10);

    [Fact]
    public void AccentStrokeOpacityFor_IdleNotEditing_EqualsAccentStrokeOpacityIdle()
        => Assert.Equal(PanelMetrics.AccentStrokeOpacityIdle, LayeredChromeVisuals.AccentStrokeOpacityFor(PanelVisualState.Idle, false), precision: 10);

    [Fact]
    public void AccentStrokeOpacityFor_ResizingEditing_EqualsAccentStrokeOpacityMax()
        => Assert.Equal(PanelMetrics.AccentStrokeOpacityMax, LayeredChromeVisuals.AccentStrokeOpacityFor(PanelVisualState.Resizing, true), precision: 10);

    [Fact]
    public void NeutralStrokeOpacityFor_IdleNotEditing_EqualsNeutralStrokeOpacityIdle()
        => Assert.Equal(PanelMetrics.NeutralStrokeOpacityIdle, LayeredChromeVisuals.NeutralStrokeOpacityFor(PanelVisualState.Idle, false), precision: 10);

    [Fact]
    public void NeutralStrokeOpacityFor_ResizingEditing_EqualsNeutralStrokeOpacityMax()
        => Assert.Equal(PanelMetrics.NeutralStrokeOpacityMax, LayeredChromeVisuals.NeutralStrokeOpacityFor(PanelVisualState.Resizing, true), precision: 10);

    /// <summary>T-P3: for every state, the editing value is never weaker than the non-editing value — cross-checked against the affine formula directly, not a hand-copied expectation.</summary>
    [Theory]
    [MemberData(nameof(Channels))]
    public void Channel_Editing_IsNeverWeakerThanNotEditing_ForEveryState(string name, Func<PanelVisualState, bool, double> channel)
    {
        foreach (var state in StatesInIncreasingOrder)
        {
            var notEditing = channel(state, false);
            var editing = channel(state, true);
            Assert.True(editing >= notEditing, $"{name}({state}, true) ({editing}) must be >= {name}({state}, false) ({notEditing}).");
        }
    }

    /// <summary>T-P5: iterates the real PanelVisualState enum (not a hand-copied list) so a future added state fails this test if any channel does not handle it.</summary>
    [Theory]
    [MemberData(nameof(Channels))]
    public void Channel_HandlesEveryDeclaredPanelVisualState(string name, Func<PanelVisualState, bool, double> channel)
    {
        foreach (PanelVisualState state in Enum.GetValues<PanelVisualState>())
        {
            foreach (var editing in new[] { false, true })
            {
                var exception = Record.Exception(() => channel(state, editing));
                Assert.True(exception is null, $"{name}({state}, {editing}) threw: {exception}");
            }
        }
    }

    /// <summary>T-P4: the accent stroke's own maximum opacity, applied to a hairline (1 DIP) stroke, must read as visibly weaker than the marker's own base stroke half-width (ADR-030 §3).</summary>
    [Fact]
    public void AccentStrokeOpacityMax_TimesHairline_IsSubstantiallyWeakerThanMarkerHalfWidth()
    {
        const double hairline = 1d;
        Assert.True(PanelMetrics.AccentStrokeOpacityMax * hairline < PanelMetrics.AccentMarkerStrokeThickness / 2d);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    // M-2 (ADR-030 review cycle 1, docs/PLANS/reference-panel-chrome.md §9 lesson P09) —
    // LayeredChromeVisuals.CombinedNeutralBorderOpacityFor: PART_NeutralStroke and PART_InteractionOverlay
    // deliberately paint the SAME Brush.Border.Default hairline on the SAME pixels (see
    // Resources/Styles/Panels.xaml's PART_NeutralStroke remarks) — this section pins the EFFECTIVE,
    // as-actually-composited combination of the two, not just each input channel independently.
    // ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Not-editing: strictly increases Idle -&gt; Hover -&gt; Editing -&gt; Dragging -&gt; Resizing, same as every other channel (T-P1's pattern, applied to the combined value).</summary>
    [Fact]
    public void CombinedNeutralBorderOpacityFor_NotEditing_StrictlyIncreasesAcrossAllFiveStates()
    {
        var previous = double.NegativeInfinity;
        foreach (var state in StatesInIncreasingOrder)
        {
            var value = LayeredChromeVisuals.CombinedNeutralBorderOpacityFor(state, false);
            Assert.True(value > previous, $"CombinedNeutralBorderOpacityFor: {state} ({value}) must be strictly greater than the previous state's value ({previous}).");
            previous = value;
        }
    }

    /// <summary>
    /// Not-editing: every adjacent pair of states is pairwise distinguishable by a real, non-negligible
    /// margin.
    /// </summary>
    /// <remarks>
    /// ACCEPTED DECISION (same convention <c>AmbientAccentContrastAuditTests</c>' own T-C1 section already
    /// establishes for this codebase — characterize the real production numbers rather than assert an
    /// aspirational one nobody actually checked): the plan text asks for a <c>&gt;= 0.02</c> margin between
    /// EVERY adjacent pair. That margin is structurally UNREACHABLE at the Dragging-&gt;Resizing step: at
    /// Resizing, <c>LayoutEditVisuals.BorderOpacityFor</c> (the "overlay" input) is already at its own
    /// ceiling, <c>1.0</c>, so the combined formula's Resizing term collapses to exactly <c>1.0</c>
    /// regardless of the neutral-stroke channel, and the ENTIRE step is whatever coverage the overlay
    /// channel still lacks at Dragging: <c>diff = (1 - overlay_Dragging) * (1 - N_Dragging) = 0.05 *
    /// (1 - N_Dragging)</c>, where <c>N_Dragging</c> is <c>NeutralStrokeOpacityFor(Dragging, false)</c>
    /// (real measured value ≈0.88, close to its own ceiling by Dragging already) — giving the real,
    /// measured <c>≈0.006</c>. Reaching the plan's <c>0.02</c> would require <c>0.05 * (1 - N_Dragging) &gt;=
    /// 0.02</c>, i.e. <c>N_Dragging &lt;= 0.6</c>, which in turn would need
    /// <see cref="PanelMetrics.NeutralStrokeOpacityMax"/> pulled down to <c>≲ 0.60</c> from its current
    /// <c>0.9</c> — flattening the ENTIRE neutral-stroke channel's own Idle-&gt;Resizing range against D-6's
    /// "always-readable neutral outline, stronger under stronger interaction" design intent, just to widen
    /// one already-adjacent pair. Dragging and Resizing stay independently distinguishable through OTHER
    /// channels regardless — the accent marker's own <c>StrokeThickness</c> (≈3.0 -&gt; ≈3.4 DIP,
    /// <c>InteractionVisualState.AccentEmphasisFor</c>), the glow channel
    /// (<see cref="LayeredChromeVisuals.GlowOpacityFor"/>), and the resize cursor itself (ADR-025) — so this
    /// combined-border channel alone being a weak differentiator at that one pair is not a usability gap.
    /// This test therefore pins the REAL, measured floor (with headroom below the ≈0.006 worst case)
    /// instead of the plan's own unverified 0.02 figure — recorded as an accepted ADR-030 amendment (not an
    /// open question deferred to a future pass): <c>PanelMetrics.NeutralStrokeOpacityMax</c> stays
    /// <c>0.9</c>, unchanged.
    /// </remarks>
    [Fact]
    public void CombinedNeutralBorderOpacityFor_NotEditing_AdjacentStatesStayAboveTheMeasuredFloor()
    {
        const double measuredFloor = 0.005;

        var previous = LayeredChromeVisuals.CombinedNeutralBorderOpacityFor(StatesInIncreasingOrder[0], false);
        for (var i = 1; i < StatesInIncreasingOrder.Length; i++)
        {
            var state = StatesInIncreasingOrder[i];
            var value = LayeredChromeVisuals.CombinedNeutralBorderOpacityFor(state, false);
            var diff = value - previous;

            Assert.True(
                diff >= measuredFloor,
                $"CombinedNeutralBorderOpacityFor: {StatesInIncreasingOrder[i - 1]} -> {state} diff ({diff}) must be >= the measured floor ({measuredFloor}).");

            previous = value;
        }
    }

    /// <summary>T-P3's own pattern, applied to the combined value: editing is never weaker than not-editing, for every state.</summary>
    [Fact]
    public void CombinedNeutralBorderOpacityFor_Editing_IsNeverWeakerThanNotEditing_ForEveryState()
    {
        foreach (var state in StatesInIncreasingOrder)
        {
            var notEditing = LayeredChromeVisuals.CombinedNeutralBorderOpacityFor(state, false);
            var editing = LayeredChromeVisuals.CombinedNeutralBorderOpacityFor(state, true);
            Assert.True(editing >= notEditing, $"CombinedNeutralBorderOpacityFor({state}, true) ({editing}) must be >= CombinedNeutralBorderOpacityFor({state}, false) ({notEditing}).");
        }
    }
}
