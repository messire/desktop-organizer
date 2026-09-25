using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.Tests.Unit.Models;

/// <summary>
/// P00 audit (docs/fix-prompts/00-audit.md) — characterization tests pinning the CURRENT ("legacy")
/// geometry behavior of <see cref="AdaptiveGridLayout"/>/<see cref="GridMetrics"/> against real panel
/// geometry captured from an actual user's <c>application-state.json</c> and <c>user.config</c> on this
/// dev machine (read-only inspection; no personal paths reproduced here, only the numeric
/// IconSize/Width/Height/item-count tuples). These tests intentionally do NOT assert "good" behavior —
/// they pin what the code does TODAY so a future policy change (docs/fix-prompts/01+) has a documented,
/// runnable baseline to diff against and, if desired, keep reachable as an explicit "legacy" mode. Do NOT
/// "fix" these by changing <see cref="AdaptiveGridLayout"/>/<see cref="GridMetrics"/> — that is out of
/// scope for P00 (audit only, no implementation changes).
/// </summary>
[Trait(UnitTests, "UI")]
public class AdaptiveGridLayoutBaselineTests
{
    /// <summary>
    /// Baseline_OrphanRow: real "tools" panel from a live user's application-state.json —
    /// IconSize≈31.512447285700045 (a NON-tick value despite MainWindow.xaml's IconSize sliders
    /// declaring <c>TickFrequency="4" IsSnapToTickEnabled="True"</c> — see the P00 write-up's
    /// "Двойной счёт spacing / наблюдения" section for the open question this raises), Width=625,
    /// 10 items. Pins that the CURRENT greedy-wrap formula splits 10 items into a 9-item first row
    /// (RowCapacity, itemsInRow both 9 — a FULL row, so its gap is the stretched formula, not MinGap)
    /// plus a single orphaned icon alone on row 2 — not a balanced 5+5 a Fences-style "fit contents"
    /// policy might produce. This is one of the ≥3 required "disproportionate panel" reproductions
    /// (docs/fix-prompts/00-audit.md DoD).
    /// </summary>
    [Fact]
    public void Baseline_OrphanRow_TenItemsAtRealIconSize_SplitsNineAndOne()
    {
        const double iconSize = 31.512447285700045d;
        const double width = 625d;
        const int itemCount = 10;

        var cell = GridMetrics.CellSize(iconSize);
        var m = AdaptiveGridLayout.EdgePadding(cell, AdaptiveGridLayout.DefaultEdgePaddingRatio);

        var rowCapacity = AdaptiveGridLayout.RowCapacity(width, cell, AdaptiveGridLayout.DefaultMinGap, m);
        var rowCount = AdaptiveGridLayout.RowCount(itemCount, rowCapacity);
        var itemsInRow0 = AdaptiveGridLayout.ItemsInRow(itemCount, rowCapacity, 0);
        var itemsInRow1 = AdaptiveGridLayout.ItemsInRow(itemCount, rowCapacity, 1);

        Assert.Equal(9, rowCapacity);
        Assert.Equal(2, rowCount);
        Assert.Equal(9, itemsInRow0);
        Assert.Equal(1, itemsInRow1);

        // Row 0 is FULL (itemsInRow0 == rowCapacity) with >1 item, so RowGap stretches instead of
        // resting at MinGap — the visible seam is NOT the documented "~16px" (ItemMargin*2 + MinGap).
        var gapRow0 = AdaptiveGridLayout.RowGap(itemsInRow0, rowCapacity, width, cell, AdaptiveGridLayout.DefaultMinGap, m);
        Assert.True(gapRow0 >= AdaptiveGridLayout.DefaultMinGap);
    }

    /// <summary>
    /// Baseline_SameIntentDifferentColumnCount: two real panels from the same user session, both
    /// Grid-mode single-row "small utility" panels with an IconSize the user almost certainly intended
    /// as "the default ~32px" (one is exactly 32, the other 31.630540508481275 — see the P00 write-up
    /// for why this fractional value is itself suspicious given the sliders' declared tick-snap), yet
    /// their real persisted widths (504 vs 437) resolve to different RowCapacity — i.e. two panels the
    /// user would describe identically ("a handful of icons, default size") do not share a column
    /// count or step width. Pins the CURRENT behavior; not an assertion that this is correct.
    /// </summary>
    [Fact]
    public void Baseline_SameIntentDifferentIconSize_ProducesDifferentRowCapacity()
    {
        const double iconSizeWork = 32d;
        const double widthWork = 504d;
        const double iconSizeSoft2 = 31.630540508481275d;
        const double widthSoft2 = 437d;

        var cellWork = GridMetrics.CellSize(iconSizeWork);
        var mWork = AdaptiveGridLayout.EdgePadding(cellWork, AdaptiveGridLayout.DefaultEdgePaddingRatio);
        var capacityWork = AdaptiveGridLayout.RowCapacity(widthWork, cellWork, AdaptiveGridLayout.DefaultMinGap, mWork);

        var cellSoft2 = GridMetrics.CellSize(iconSizeSoft2);
        var mSoft2 = AdaptiveGridLayout.EdgePadding(cellSoft2, AdaptiveGridLayout.DefaultEdgePaddingRatio);
        var capacitySoft2 = AdaptiveGridLayout.RowCapacity(widthSoft2, cellSoft2, AdaptiveGridLayout.DefaultMinGap, mSoft2);

        Assert.Equal(7, capacityWork);
        Assert.Equal(6, capacitySoft2);
        Assert.NotEqual(capacityWork, capacitySoft2);
    }

    /// <summary>
    /// Baseline_HeightDecoupledFromContent: four real single-row Grid panels from the same user
    /// session ("work", "soft2", "dev", "personal") all need essentially IDENTICAL analytical window
    /// height for their content — one row of ~32px icons, <c>TitleBarHeight(32) + FileListVPadding(4)
    /// + AdaptiveGridLayout.Extent(1, RowHeight(iconSize), ..., 0)</c> — because
    /// <c>ChildWindow.Initialize</c> only calls <c>SnapWidthToGrid</c> on load (ChildWindow.xaml.cs),
    /// never an equivalent height re-snap: a panel's height only ever tracks
    /// <see cref="AdaptiveGridLayout"/>'s row math while a LIVE resize drag is in progress, so a height
    /// set once (by a lasso-drag or an old resize) is never reconciled with the actual content again.
    /// Pins that the four panels' PERSISTED heights nonetheless range from 120 to 351 — nearly 3x —
    /// for functionally identical one-row content, purely as an artifact of each panel's own resize
    /// history. The third (in fact fourth) of the ≥3 required "disproportionate panel" reproductions
    /// (docs/fix-prompts/00-audit.md DoD).
    /// </summary>
    [Fact]
    public void Baseline_HeightDecoupledFromContent_AcrossFourRealSingleRowPanels()
    {
        const double titleBarHeight = 32d; // PanelMetrics.HeaderHeight.
        const double verticalChrome = 4d; // GridMetrics.FileListVPadding.

        double RequiredOneRowHeight(double iconSize) => titleBarHeight + verticalChrome +
            AdaptiveGridLayout.Extent(1, GridMetrics.RowHeight(iconSize), AdaptiveGridLayout.DefaultMinGap, 0d);

        var requiredWork = RequiredOneRowHeight(32d);
        var requiredSoft2 = RequiredOneRowHeight(31.630540508481275d);
        var requiredDev = RequiredOneRowHeight(31.630540508481275d);
        var requiredPersonal = RequiredOneRowHeight(31.630540508481275d);

        // Content requirement is near-identical across all four (within 1px of each other) — same
        // conceptual "one row of ~32px icons".
        Assert.Equal(112d, requiredWork, 1e-9);
        Assert.Equal(111.630540508481275d, requiredSoft2, 1e-9);
        Assert.Equal(requiredSoft2, requiredDev, 1e-9);
        Assert.Equal(requiredSoft2, requiredPersonal, 1e-9);
        Assert.True(Math.Abs(requiredWork - requiredPersonal) < 1d);

        // Real persisted window heights for these same four panels, read from a live user's
        // application-state.json — wildly divergent despite near-identical content requirement.
        const double persistedWork = 120d;
        const double persistedSoft2 = 120d;
        const double persistedDev = 120d;
        const double persistedPersonal = 351d;

        Assert.True(requiredWork <= persistedWork);
        Assert.True(requiredSoft2 <= persistedSoft2);
        Assert.True(requiredDev <= persistedDev);
        Assert.True(requiredPersonal <= persistedPersonal);

        var spread = new[] { persistedWork, persistedSoft2, persistedDev, persistedPersonal };
        Assert.Equal(351d, spread.Max());
        Assert.Equal(120d, spread.Min());
        Assert.True(spread.Max() / spread.Min() > 2.9d, "Persisted heights should span nearly 3x for equivalent one-row content.");
    }

    /// <summary>
    /// Baseline_PersistedDefaultIconSize_IsNotOnATick: real <c>user.config</c> on this dev machine has
    /// <c>DefaultIconSize = 32.010182570303485</c> even though both IconSize sliders in MainWindow.xaml
    /// declare <c>Minimum="16" TickFrequency="4" IsSnapToTickEnabled="True"</c> (which — per
    /// adr/ADR-018-adaptive-icon-grid-layout.md's MAJOR-bug M-1 fix — was supposed to stop fractional
    /// IconSize values reaching persistence at all, to protect <see cref="AdaptiveGridLayout.RowCapacity"/>'s
    /// <see cref="AdaptiveGridLayout.LayoutTolerance"/> sub-pixel margin from a source that keeps
    /// producing them anyway). This test does not exercise the Slider (no STA/WPF infrastructure in
    /// this project) — it documents, as a literal value pinned from real persisted state, that the tick
    /// grid {16, 20, 24, ..., 96} does not contain this value, i.e. the M-1 fix's tick-snap does not
    /// fully prevent fractional IconSize from reaching disk in current production builds. See the P00
    /// write-up's "Двойной счёт spacing / наблюдения" section for the open root-cause question (not
    /// resolved here — audit only).
    /// </summary>
    [Fact]
    public void Baseline_PersistedDefaultIconSize_IsNotOnATick()
    {
        const double persisted = 32.010182570303485d;
        const double tickFrequency = 4d;
        const double minimum = 16d;

        var stepsFromMinimum = (persisted - minimum) / tickFrequency;
        var nearestTickValue = minimum + (Math.Round(stepsFromMinimum) * tickFrequency);

        Assert.NotEqual(persisted, nearestTickValue);
    }
}
