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
}
