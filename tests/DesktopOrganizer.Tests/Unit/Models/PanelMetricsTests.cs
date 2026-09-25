using System.Windows;
using DesktopOrganizer.Tests.Helpers;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.Tests.Unit.Models;

/// <summary>
/// Unit tests for the <see cref="PanelMetrics"/> members introduced by ADR-029's F-3 fix
/// (docs/PLANS/workspace-grid-00-baseline.md finding F-3, docs/fix-prompts/05-icon-layout.md). Existing
/// <see cref="PanelMetrics"/> members (<see cref="PanelMetrics.HeaderHeight"/>,
/// <see cref="PanelMetrics.AccentIndicatorThickness"/>, etc.) already have coverage elsewhere
/// (<c>PanelChromeMetadataTests</c>, <c>AmbientConverterTests</c>, <c>ResourceDictionaryMergeTests</c>) —
/// this class is scoped only to the two new members.
/// </summary>
[Trait(UnitTests, "UI")]
public class PanelMetricsTests
{
    /// <summary>
    /// ContentLeftInset must stay exactly 8 — the fixed <c>PART_AccentIndicator</c> column width
    /// (<c>Resources/Styles/Panels.xaml</c>, ADR-025 MAJOR-1) this constant mirrors. A change here needs
    /// a synchronized manual update to that XAML's column (now itself bound to
    /// <see cref="PanelMetrics.ContentLeftInsetColumnWidth"/>, so in practice only ONE edit is needed —
    /// see that property's doc comment).
    /// </summary>
    [Fact]
    public void ContentLeftInset_Is8()
    {
        Assert.Equal(8d, PanelMetrics.ContentLeftInset);
    }

    /// <summary>
    /// ContentLeftInsetColumnWidth must wrap ContentLeftInset exactly (same "double -&gt; GridLength"
    /// pattern as HeaderRowHeight/HeaderHeight) — a divergence here would mean Panels.xaml's accent
    /// column and GridMetrics.FileListHPadding stop agreeing on the same physical 8 DIP.
    /// </summary>
    [Fact]
    public void ContentLeftInsetColumnWidth_WrapsContentLeftInset()
    {
        Assert.Equal(new GridLength(PanelMetrics.ContentLeftInset), PanelMetrics.ContentLeftInsetColumnWidth);
        Assert.Equal(GridUnitType.Pixel, PanelMetrics.ContentLeftInsetColumnWidth.GridUnitType);
        Assert.Equal(PanelMetrics.ContentLeftInset, PanelMetrics.ContentLeftInsetColumnWidth.Value);
    }

    /// <summary>
    /// GridMetrics.FileListHPadding (ADR-029's single point of consumption for every snap-time formula)
    /// must read this exact constant, not an independent copy — the whole point of the F-3 fix (ADR-029
    /// Решение 2) is a single source of truth for the PanelChrome accent-column width. See
    /// <c>GridMetricsTests.FileListHPadding_EqualsPanelMetricsContentLeftInset</c> for the symmetric
    /// assertion from the <see cref="GridMetrics"/> side.
    /// </summary>
    [Fact]
    public void ContentLeftInset_EqualsGridMetricsFileListHPadding()
    {
        Assert.Equal(PanelMetrics.ContentLeftInset, GridMetrics.FileListHPadding);
    }

    // ------------------------------------------------------------------
    // ResizeCornerThickness/ResizeEdgeThickness (docs/PLANS/workspace-grid-07-visual.md fix-cycle 1
    // MAJOR-1, split into two independent constants in fix-cycle 2 MAJOR-A)
    // ------------------------------------------------------------------

    /// <summary>
    /// The corner square stays widened 6 -&gt; 8, matching <c>Radius.Panel</c>
    /// (<c>Resources/Styles/Spacing.xaml</c>) — a change here needs a synchronized check that it still
    /// covers the majority of the corner's own always-transparent lens (see
    /// <see cref="ResizeCornerThickness_CoversMajorityOfCornerDeadZone"/> below).
    /// </summary>
    [Fact]
    public void ResizeCornerThickness_Is8()
    {
        Assert.Equal(8d, PanelMetrics.ResizeCornerThickness);
    }

    /// <summary>
    /// MAJOR-A fix-cycle 2: the straight-edge strip is restored to its pre-fix-cycle-1 value of 6 — a
    /// straight edge never sits near <c>Radius.Panel</c>'s rounded arc, so MAJOR-1's corner-only
    /// click-through-lens argument never applied to it, and widening it to match the corner (fix-cycle 1)
    /// was itself the direct cause of a real header-button/scrollbar hot-zone overlap regression.
    /// </summary>
    [Fact]
    public void ResizeEdgeThickness_Is6()
    {
        Assert.Equal(6d, PanelMetrics.ResizeEdgeThickness);
    }

    /// <summary>
    /// MAJOR-A: the corner square must stay wider than (or, at minimum, as wide as) the straight-edge
    /// strip — a corner narrower than the edge it is meant to widen past would be a nonsensical shape (see
    /// <see cref="Interaction.Native.NativeHitTestMapper.Map"/>'s own <c>cornerDip</c> remarks).
    /// </summary>
    [Fact]
    public void ResizeCornerThickness_IsAtLeastResizeEdgeThickness()
    {
        Assert.True(
            PanelMetrics.ResizeCornerThickness >= PanelMetrics.ResizeEdgeThickness,
            $"ResizeCornerThickness ({PanelMetrics.ResizeCornerThickness}) must be >= ResizeEdgeThickness ({PanelMetrics.ResizeEdgeThickness}).");
    }

    /// <summary>
    /// Same "double -&gt; GridLength" wrapper pattern as <see cref="PanelMetrics.HeaderRowHeight"/>/
    /// <see cref="PanelMetrics.ContentLeftInsetColumnWidth"/> — <c>ChildWindow.xaml</c>'s
    /// <c>ResizeOverlay</c> Grid columns/rows read this, not a raw double. There is no equivalent wrapper
    /// for <see cref="PanelMetrics.ResizeCornerThickness"/>: the corner <c>Thumb</c>s consume it directly
    /// as a plain <see langword="double"/> <c>Width</c>/<c>Height</c>, never through a
    /// <see cref="ColumnDefinition"/>/<see cref="RowDefinition"/>.
    /// </summary>
    [Fact]
    public void ResizeEdgeThicknessLength_WrapsResizeEdgeThickness()
    {
        Assert.Equal(new GridLength(PanelMetrics.ResizeEdgeThickness), PanelMetrics.ResizeEdgeThicknessLength);
        Assert.Equal(GridUnitType.Pixel, PanelMetrics.ResizeEdgeThicknessLength.GridUnitType);
        Assert.Equal(PanelMetrics.ResizeEdgeThickness, PanelMetrics.ResizeEdgeThicknessLength.Value);
    }

    /// <summary>
    /// Geometric regression pin for MAJOR-1's actual claim: a panel window with
    /// <c>AllowsTransparency="True"</c> never paints (and the OS therefore never routes clicks to)
    /// the small square lens outside a rounded <c>Border</c>'s own quarter-circle arc — area
    /// <c>r² · (1 - π/4)</c>, independent of the resize grip's own size. Widening the CORNER grip to (at
    /// least) match the corner radius does not shrink that lens, but DOES shrink the FRACTION of the
    /// grip's own footprint it occupies — this pins that fraction stays comfortably majority-non-
    /// transparent at the actual shipped radius/grip pair, rather than merely asserting the two constants
    /// are numerically equal. MAJOR-A (fix-cycle 2): <c>Radius.Panel</c> is read from the REAL compiled
    /// XAML resource (<c>Resources/Styles/Spacing.xaml</c>) via <see cref="StaTestRunner"/>, instead of a
    /// hand-copied <see langword="const"/> literal that could silently drift out of sync with the XAML
    /// value it was meant to mirror (the exact class of bug this whole fix cycle is about).
    /// </summary>
    [Fact]
    public void ResizeCornerThickness_CoversMajorityOfCornerDeadZone()
    {
        var radiusPanel = 0d;
        StaTestRunner.Run(() =>
        {
            // Registering the "pack" URI scheme requires System.Windows.Application's own static
            // constructor to have run at least once in this process — normally already true by the time
            // this test runs (many other fixtures construct one first), but if THIS happens to be the
            // very first WPF-touching test, this test must populate the REAL merge chain if it ends up
            // being the one to construct Application, or it would leave Application.Current permanently
            // non-null with EMPTY Resources for the rest of the `dotnet test` process (an Application,
            // once constructed, cannot be un-constructed) — breaking every OTHER fixture that assumes
            // Application.Current.Resources already holds the full chain App.xaml.cs's own
            // LoadResourceDictionaries() builds (e.g. ChildWindowViewModel.GetTemplate's
            // FindResource("ListViewTemplate") calls).
            //
            // M-2 fix (P08 fix cycle 2): single owner via WpfApplicationHost.EnsureApplication() — the
            // one place that merge chain is defined — instead of a second, hand-rolled copy of it
            // (no-ops if some other fixture already constructed Application.Current).
            WpfApplicationHost.EnsureApplication();

            var spacing = new ResourceDictionary
            {
                Source = new Uri("/DesktopOrganizer.UI;component/Resources/Styles/Spacing.xaml", UriKind.Relative),
            };
            radiusPanel = ((CornerRadius)spacing["Radius.Panel"]).TopLeft;
        });

        var corner = PanelMetrics.ResizeCornerThickness;
        Assert.True(
            corner >= radiusPanel,
            $"ResizeCornerThickness ({corner}) should be at least as wide as Radius.Panel ({radiusPanel}) so the corner grip's own footprint is not smaller than the panel's own rounded corner.");

        var deadZoneArea = radiusPanel * radiusPanel * (1 - (Math.PI / 4));
        var cornerArea = corner * corner;
        var coveredFraction = 1 - (deadZoneArea / cornerArea);

        Assert.True(
            coveredFraction >= 0.75,
            $"Expected at least 75% of the resize corner grip's own footprint to stay outside the permanently-transparent corner lens; got {coveredFraction:P1}.");
    }

    /// <summary>
    /// MAJOR-A geometry pin: neither straight-edge <c>Thumb</c> may overlap a header action button's own
    /// bounding box (28x28, <see cref="PanelMetrics.HeaderButtonSize"/>, vertically centred in the
    /// 32-DIP <see cref="PanelMetrics.HeaderHeight"/> header — i.e. a 2 DIP top margin) BY MORE than the
    /// straight-edge strip's own unavoidable thickness allows. This does not (and, within this fix
    /// cycle's frozen <see cref="PanelMetrics.HeaderButtonSize"/>/<see cref="PanelMetrics.HeaderHeight"/>,
    /// cannot) assert ZERO overlap for the CORNER Thumbs specifically against the right-most header
    /// button (Overflow) — see this test's own remarks below for why that residual is a DOCUMENTED,
    /// out-of-scope-for-this-fix-cycle gap, not an oversight.
    /// </summary>
    [Fact]
    public void ResizeEdgeThickness_TopStrip_OverlapWithHeaderButtonTopMargin_IsReducedFromFixCycle1()
    {
        // The straight-edge Top Thumb spans Y in [0, ResizeEdgeThickness). A header button's own top
        // margin (vertically centred 28-DIP button in a 32-DIP header) is (HeaderHeight - HeaderButtonSize) / 2.
        var buttonTopMargin = (PanelMetrics.HeaderHeight - PanelMetrics.HeaderButtonSize) / 2d;
        var topStripOverlapWithButton = Math.Max(0d, PanelMetrics.ResizeEdgeThickness - buttonTopMargin);

        // Fix-cycle 1's uniform 8 DIP grip produced a 6 DIP overlap (8 - 2) — 21% of the button's own
        // 28-DIP height. This fix cycle's restored 6 DIP edge strip reduces it to 4 DIP (6 - 2) — ~14%.
        // A LITERAL zero is not achievable without widening HeaderHeight, shrinking HeaderButtonSize, or
        // increasing the header actions' own top margin — all three are explicitly frozen invariants for
        // this fix cycle (docs/PLANS/workspace-grid-07-visual.md "Зона"/"НЕ тронуто"). Documented residual,
        // not a silently-accepted regression: see docs/PLANS/workspace-grid-07-visual.md §11 (fix-cycle 2)
        // and adr/ADR-025-ambient-accent-visual-language.md's matching Amendment for the manual-verify
        // writeup and the numeric before/after this assertion pins.
        Assert.True(
            topStripOverlapWithButton < 6d,
            $"Top-edge overlap with a header button's own top margin ({topStripOverlapWithButton} DIP) should be strictly less than fix-cycle 1's pre-fix value (6 DIP).");
        Assert.Equal(4d, topStripOverlapWithButton);
    }

    /// <summary>
    /// MAJOR-A geometry pin, RIGHT edge vs. the vertical scrollbar: <c>FileListHost</c>
    /// (<c>ChildWindow.xaml</c>) has no right margin (documented, unrelated, "НЕ тронуто" for this fix
    /// cycle's <c>ResizeOverlay</c>-only zone) — its scrollbar's own right edge sits flush with the
    /// panel's right edge, the same edge the RIGHT straight-edge <c>Thumb</c> occupies. Restoring
    /// <see cref="PanelMetrics.ResizeEdgeThickness"/> 8 -&gt; 6 reduces (but, within this fix cycle's
    /// frozen <c>FileListHost</c> margin, cannot eliminate) that overlap: fix-cycle 1's uniform 8 DIP
    /// grip covered ~47% of <see cref="SystemParameters.VerticalScrollBarWidth"/>'s default (17 DIP)
    /// width; this fix cycle's restored 6 DIP edge strip covers ~35%. Reads the REAL live
    /// <see cref="SystemParameters.VerticalScrollBarWidth"/> system metric rather than hardcoding 17, so
    /// this stays correct on a machine/theme with a different scrollbar width.
    /// </summary>
    [Fact]
    public void ResizeEdgeThickness_RightStrip_OverlapWithVerticalScrollBar_IsReducedFromFixCycle1()
    {
        const double fixCycle1EdgeThickness = 8d; // PanelMetrics.ResizeGripThickness, pre-fix-cycle-2

        var scrollBarWidth = SystemParameters.VerticalScrollBarWidth;
        var overlapFraction = PanelMetrics.ResizeEdgeThickness / scrollBarWidth;
        var fixCycle1OverlapFraction = fixCycle1EdgeThickness / scrollBarWidth;

        Assert.True(
            overlapFraction < fixCycle1OverlapFraction,
            $"Right-edge overlap fraction of the scrollbar ({overlapFraction:P1}) should be smaller than fix-cycle 1's ({fixCycle1OverlapFraction:P1}).");

        // A LITERAL zero would require a FileListHost right margin — out of scope for this fix cycle's
        // ResizeOverlay-only zone (docs/PLANS/workspace-grid-07-visual.md "Зона"). Documented residual,
        // not silently accepted: see that plan's §11 (fix-cycle 2) and the matching ADR-025 Amendment.
        Assert.True(
            overlapFraction > 0d,
            "This residual overlap is expected to be nonzero within this fix cycle's scope — if it is ever exactly zero, update this test's own documentation (it would mean FileListHost gained a margin elsewhere).");
    }

    // ------------------------------------------------------------------
    // ADR-030 "Reference-Style Layered Panel Chrome" (docs/PLANS/reference-panel-chrome.md §5.1,
    // T-M1..T-M6) — the 2-DIP chrome inset budget and the glow/stroke/marker tokens it bounds.
    // ------------------------------------------------------------------

    /// <summary>T-M1: the rolled-up header row still fits a header button after two insets, and the inner row split reconstructs HeaderHeight exactly.</summary>
    [Fact]
    public void ChromeOuterInset_HeaderRowSplit_FitsHeaderButtonAndReconstructsHeaderHeight()
    {
        Assert.True(
            PanelMetrics.HeaderHeight - (2 * PanelMetrics.ChromeOuterInset) >= PanelMetrics.HeaderButtonSize,
            $"HeaderHeight ({PanelMetrics.HeaderHeight}) minus 2x ChromeOuterInset ({PanelMetrics.ChromeOuterInset}) must be >= HeaderButtonSize ({PanelMetrics.HeaderButtonSize}).");

        Assert.Equal(
            PanelMetrics.HeaderHeight,
            PanelMetrics.ChromeOuterInset + PanelMetrics.HeaderInnerHeight + PanelMetrics.HeaderContentGap);
    }

    /// <summary>T-M2: the new symmetric side-padding decomposition sums to exactly the same ContentLeftInset/FileListHPadding total as before ADR-030 (F-3 invariant preserved).</summary>
    [Fact]
    public void ChromeOuterInset_ContentSidePaddingSplit_EqualsContentLeftInsetAndFileListHPadding()
    {
        var reconstructed = (2 * PanelMetrics.ChromeOuterInset) + (2 * PanelMetrics.ContentSidePadding);

        Assert.Equal(PanelMetrics.ContentLeftInset, reconstructed);
        Assert.Equal(GridMetrics.FileListHPadding, reconstructed);
    }

    /// <summary>T-M3: every inset pixel lies inside the resize hit-test strip on both edges and corners.</summary>
    [Fact]
    public void ChromeOuterInset_IsWithinResizeHitTestStrips()
    {
        Assert.True(
            PanelMetrics.ChromeOuterInset < PanelMetrics.ResizeEdgeThickness,
            $"ChromeOuterInset ({PanelMetrics.ChromeOuterInset}) must be strictly less than ResizeEdgeThickness ({PanelMetrics.ResizeEdgeThickness}).");
        Assert.True(
            PanelMetrics.ChromeOuterInset <= PanelMetrics.ResizeCornerThickness,
            $"ChromeOuterInset ({PanelMetrics.ChromeOuterInset}) must be at most ResizeCornerThickness ({PanelMetrics.ResizeCornerThickness}).");
    }

    /// <summary>T-M4: the bottom inset fits inside GridMetrics.FileListVPadding's already-reserved vertical chrome (F-1's unused reserve).</summary>
    [Fact]
    public void ChromeOuterInset_FitsWithinFileListVPadding()
    {
        Assert.True(
            PanelMetrics.ChromeOuterInset <= GridMetrics.FileListVPadding,
            $"ChromeOuterInset ({PanelMetrics.ChromeOuterInset}) must be at most GridMetrics.FileListVPadding ({GridMetrics.FileListVPadding}).");
    }

    /// <summary>T-M5: the glow's modeled residual edge alpha (D-10) stays at/under GlowEdgeResidualMax, and the fallback ring never sits farther out than the inset itself.</summary>
    [Fact]
    public void Glow_ResidualEdgeAlpha_StaysUnderGlowEdgeResidualMax()
    {
        var sigma = PanelMetrics.GlowBlurRadius / 3d;
        var residual = PanelMetrics.GlowEffectOpacity * PanelMetrics.GlowOpacityMax
            * Math.Exp(-(PanelMetrics.ChromeOuterInset * PanelMetrics.ChromeOuterInset) / (2d * sigma * sigma));

        Assert.True(
            residual <= PanelMetrics.GlowEdgeResidualMax,
            $"Modeled glow residual edge alpha ({residual:F4}) must be <= GlowEdgeResidualMax ({PanelMetrics.GlowEdgeResidualMax}).");

        Assert.True(
            PanelMetrics.GlowFallbackRingOffset <= PanelMetrics.ChromeOuterInset,
            $"GlowFallbackRingOffset ({PanelMetrics.GlowFallbackRingOffset}) must be <= ChromeOuterInset ({PanelMetrics.ChromeOuterInset}).");
    }

    /// <summary>T-M6: every new Thickness/GridLength wrapper token wraps its own underlying double exactly — same pattern as ContentLeftInsetColumnWidth_WrapsContentLeftInset above.</summary>
    [Fact]
    public void ChromeOuterInsetThickness_WrapsChromeOuterInset()
    {
        Assert.Equal(new Thickness(PanelMetrics.ChromeOuterInset), PanelMetrics.ChromeOuterInsetThickness);
    }

    [Fact]
    public void ContentSidePaddingLength_WrapsContentSidePadding()
    {
        Assert.Equal(new GridLength(PanelMetrics.ContentSidePadding), PanelMetrics.ContentSidePaddingLength);
        Assert.Equal(GridUnitType.Pixel, PanelMetrics.ContentSidePaddingLength.GridUnitType);
    }

    [Fact]
    public void HeaderInnerRowHeight_WrapsHeaderInnerHeight()
    {
        Assert.Equal(new GridLength(PanelMetrics.HeaderInnerHeight), PanelMetrics.HeaderInnerRowHeight);
        Assert.Equal(GridUnitType.Pixel, PanelMetrics.HeaderInnerRowHeight.GridUnitType);
    }

    [Fact]
    public void HeaderContentGapLength_WrapsHeaderContentGap()
    {
        Assert.Equal(new GridLength(PanelMetrics.HeaderContentGap), PanelMetrics.HeaderContentGapLength);
        Assert.Equal(GridUnitType.Pixel, PanelMetrics.HeaderContentGapLength.GridUnitType);
    }

    [Fact]
    public void GlowFallbackMarginThickness_WrapsChromeOuterInsetMinusGlowFallbackRingOffset()
    {
        Assert.Equal(
            new Thickness(PanelMetrics.ChromeOuterInset - PanelMetrics.GlowFallbackRingOffset),
            PanelMetrics.GlowFallbackMarginThickness);
    }

    /// <summary>D-4 collision requirement (б): the marker host's own computed Height must not push its bottom edge past HeaderInnerHeight (else it would visually overflow into the content row — Grid does not clip children to a row's own bounds by default).</summary>
    [Fact]
    public void AccentMarkerHostHeight_DoesNotExceedHeaderInnerHeight()
    {
        Assert.True(
            PanelMetrics.AccentMarkerHostHeight <= PanelMetrics.HeaderInnerHeight,
            $"AccentMarkerHostHeight ({PanelMetrics.AccentMarkerHostHeight}) must be <= HeaderInnerHeight ({PanelMetrics.HeaderInnerHeight}).");
    }

    /// <summary>Sanity: the marker host is a finite, positive, non-NaN size — a fixed host size (not Auto) is what keeps a per-state StrokeThickness change from ever perturbing PanelChrome's own DesiredSize (plan D-7).</summary>
    [Fact]
    public void AccentMarkerHost_WidthAndHeight_AreFinitePositiveNumbers()
    {
        Assert.True(PanelMetrics.AccentMarkerHostWidth is > 0 and not double.NaN);
        Assert.True(PanelMetrics.AccentMarkerHostHeight is > 0 and not double.NaN);
        Assert.False(double.IsInfinity(PanelMetrics.AccentMarkerHostWidth));
        Assert.False(double.IsInfinity(PanelMetrics.AccentMarkerHostHeight));
    }

    /// <summary>T-P4 (accent-stroke-vs-marker weight, ADR-030 §3): the accent stroke's own maximum opacity times a hairline must be visibly weaker than the marker's own base stroke half-width.</summary>
    [Fact]
    public void AccentStrokeOpacityMax_TimesHairline_IsLessThanAccentMarkerStrokeThicknessHalf()
    {
        Assert.True(
            PanelMetrics.AccentStrokeOpacityMax * 1d < PanelMetrics.AccentMarkerStrokeThickness / 2d,
            "AccentStrokeOpacityMax * hairline must be strictly less than AccentMarkerStrokeThickness / 2.");
    }

    /// <summary>Radius.Panel.Inner (Spacing.xaml) must equal the real Radius.Panel minus a real hairline Thickness — read from the compiled XAML resources, not hand-copied literals.</summary>
    [Fact]
    public void RadiusPanelInner_EqualsRadiusPanelMinusHairline()
    {
        var (radiusPanel, radiusPanelInner, hairline) = StaTestRunner.Run(() =>
        {
            WpfApplicationHost.EnsureApplication();

            var spacing = new ResourceDictionary
            {
                Source = new Uri("/DesktopOrganizer.UI;component/Resources/Styles/Spacing.xaml", UriKind.Relative),
            };

            var panel = ((CornerRadius)spacing["Radius.Panel"]).TopLeft;
            var panelInner = ((CornerRadius)spacing["Radius.Panel.Inner"]).TopLeft;
            var hairlineThickness = ((Thickness)spacing["Thickness.Hairline"]).Left;
            return (panel, panelInner, hairlineThickness);
        });

        Assert.Equal(radiusPanel - hairline, radiusPanelInner);
    }

    /// <summary>
    /// ADR-030 fix cycle 1 (MINOR 6): <see cref="PanelMetrics.AccentMarkerCornerRadiusBudget"/> is a
    /// hand-written mirror of <c>Spacing.xaml</c>'s <c>Radius.Panel</c> (XAML has no C# counterpart this
    /// class could read at compile time) — this pins the equality against the REAL compiled resource,
    /// same "read from the real resource, not a hand-copied literal" pattern as
    /// <see cref="RadiusPanelInner_EqualsRadiusPanelMinusHairline"/> above.
    /// </summary>
    [Fact]
    public void AccentMarkerCornerRadiusBudget_EqualsRadiusPanel()
    {
        var radiusPanel = StaTestRunner.Run(() =>
        {
            WpfApplicationHost.EnsureApplication();

            var spacing = new ResourceDictionary
            {
                Source = new Uri("/DesktopOrganizer.UI;component/Resources/Styles/Spacing.xaml", UriKind.Relative),
            };

            return ((CornerRadius)spacing["Radius.Panel"]).TopLeft;
        });

        Assert.Equal(PanelMetrics.AccentMarkerCornerRadiusBudget, radiusPanel);
    }

    /// <summary>
    /// ADR-030 fix cycle 1 (MINOR 6): <see cref="PanelMetrics.AccentMarkerMaxStrokeThickness"/> must equal
    /// the REAL <see cref="DesktopOrganizer.UI.Theming.InteractionVisualState.AccentEmphasisFor"/>/
    /// <see cref="DesktopOrganizer.UI.Theming.LayoutEditVisuals.AccentEmphasis"/> formula it is now
    /// computed from (no more hand-copied <c>1.7</c>/<c>1.35</c> mirror constants) — same formula
    /// <c>ResourceDictionaryMergeTests</c>' own T-R4 test independently cross-checks against the marker
    /// host's real size.
    /// </summary>
    [Fact]
    public void AccentMarkerMaxStrokeThickness_EqualsRealAccentEmphasisFormula()
    {
        var expected = PanelMetrics.AccentMarkerStrokeThickness
            * DesktopOrganizer.UI.Theming.InteractionVisualState.AccentEmphasisFor(DesktopOrganizer.UI.Theming.PanelVisualState.Resizing)
            * DesktopOrganizer.UI.Theming.LayoutEditVisuals.AccentEmphasis;

        Assert.Equal(expected, PanelMetrics.AccentMarkerMaxStrokeThickness, precision: 10);
    }
}
