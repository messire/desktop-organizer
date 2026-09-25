using System.Windows;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.Tests.Unit.Models;

/// <summary>
/// P04 fix-cycle 1, MAJOR-3: <see cref="PanelGridStateResolver.Resolve"/>'s lazy span migration must be
/// IDEMPOTENT — it clamps the span it derives from the live bounds to the same floor
/// <c>HasValidPersistedSpan</c> validates against, so the second resolve finds a valid span instead of
/// re-deriving the same too-small one forever.
/// </summary>
/// <remarks>
/// The asymmetry that made this reachable: <c>ChildWindow.xaml</c> coerces <c>Window.MinWidth</c> to 120
/// DIP (= <see cref="WorkspaceGridMetrics.MinSpanExpanded"/>'s width), so a too-narrow panel self-heals —
/// but its <c>MinHeight</c> is the COLLAPSED 32 DIP, far below
/// <see cref="WorkspaceGridMetrics.MinHeightSpanExpanded"/> (12 units = 88 DIP), so a too-short panel
/// never did.
/// </remarks>
[Trait(UnitTests, "UI")]
public class PanelGridStateResolverMigrationTests
{
    private const double U = WorkspaceGridMetrics.UnitSize;
    private const double G = WorkspaceGridMetrics.DefaultGap;

    private static readonly (string DeviceName, Rect Bounds, Rect WorkArea)[] Monitors =
        [(@"\\.\DISPLAY1", new Rect(0, 0, 1920, 1080), new Rect(0, 0, 1920, 1040))];

    // 56 DIP tall: derives span 8, which is BELOW MinHeightSpanExpanded (12) — the exact repro.
    private static readonly Rect TooShort = new(0d, 0d, 120d, 56d);

    /// <summary>
    /// Verifies that a panel shorter than the minimum height migrates its persisted span to the
    /// minimum floor (<see cref="WorkspaceGridMetrics.MinHeightSpanExpanded"/>), not to the smaller
    /// span its own too-short bounds would otherwise derive.
    /// </summary>
    [Fact]
    public void AShorterThanMinimumPanel_MigratesToTheMinimumSpan_NotToTheDerivedOne()
    {
        var model = new ChildWindowModel { GeometryMode = PanelGeometryMode.GridAligned };

        var state = PanelGridStateResolver.Resolve(model, TooShort, Monitors, isRolledUp: false);

        Assert.True(state.IsGridAligned);
        Assert.Equal(WorkspaceGridMetrics.MinWidthSpanExpanded, model.WidthSpanUnits);
        Assert.Equal(WorkspaceGridMetrics.MinHeightSpanExpanded, model.HeightSpanUnits);
    }

    /// <summary>
    /// Verifies the fix for the MAJOR-3 idempotency bug: after the first resolve migrates the span to
    /// the floor, a second resolve of the same model finds that migrated span already valid and
    /// raises no further property-changed notifications.
    /// </summary>
    [Fact]
    public void TheSecondResolve_FindsThePersistedSpanValid_AndWritesNothing()
    {
        var model = new ChildWindowModel { GeometryMode = PanelGeometryMode.GridAligned };
        var changed = new List<string?>();
        model.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        PanelGridStateResolver.Resolve(model, TooShort, Monitors, isRolledUp: false);
        var afterFirst = (model.WidthSpanUnits, model.HeightSpanUnits);
        changed.Clear();

        PanelGridStateResolver.Resolve(model, TooShort, Monitors, isRolledUp: false);

        // Pre-fix the migration re-ran forever (span 8 was written back, then rejected again on entry) —
        // so the span stayed below the floor and every preset/F-2/resize/roll-up resolve re-derived it.
        Assert.Equal(afterFirst, (model.WidthSpanUnits, model.HeightSpanUnits));
        Assert.Empty(changed);
    }

    // ── The migrated span really does restore a legal panel: bounds -> span -> bounds is at/above the
    // floor, so roll-down and restore no longer replay the too-small height. ────────────────────────────

    /// <summary>
    /// Verifies that converting the migrated span back to DIP bounds yields a rectangle at or above
    /// the minimum-span floor in both dimensions, so a subsequent roll-down/restore cycle never
    /// replays the original too-small height.
    /// </summary>
    [Fact]
    public void TheMigratedSpan_RestoresAPanelAtOrAboveTheFloor()
    {
        var model = new ChildWindowModel { GeometryMode = PanelGeometryMode.GridAligned };

        PanelGridStateResolver.Resolve(model, TooShort, Monitors, isRolledUp: false);

        var restored = WorkspaceLayoutPolicy.SpansToBounds(
            new GridSpan(model.WidthSpanUnits!.Value, model.HeightSpanUnits!.Value), U, G);
        var floor = WorkspaceLayoutPolicy.SpansToBounds(WorkspaceGridMetrics.MinSpanExpanded, U, G);

        Assert.True(restored.Width >= floor.Width);
        Assert.True(restored.Height >= floor.Height);
    }

    // ── Not vacuous: a panel ABOVE the floor still migrates to the span its own size implies. ──────────

    /// <summary>
    /// Verifies that the migration is not vacuous: a panel already above the minimum floor still
    /// migrates to the span its own size derives, not just to the floor.
    /// </summary>
    [Fact]
    public void AnOrdinarySizedPanel_StillMigratesToItsOwnDerivedSpan()
    {
        var model = new ChildWindowModel { GeometryMode = PanelGeometryMode.GridAligned };

        PanelGridStateResolver.Resolve(model, new Rect(0d, 0d, 264d, 304d), Monitors, isRolledUp: false);

        Assert.Equal(34, model.WidthSpanUnits);
        Assert.Equal(39, model.HeightSpanUnits);
    }

    // ── A ROLLED-UP panel migrates against the collapsed floor, so its (legitimately short) collapsed
    // height is not inflated to the expanded minimum. ───────────────────────────────────────────────────

    /// <summary>
    /// Verifies that a rolled-up panel migrates its height span against the collapsed floor
    /// (<see cref="WorkspaceGridMetrics.CollapsedHeaderSpan"/>), so its legitimately short collapsed
    /// height is not inflated to the expanded minimum.
    /// </summary>
    [Fact]
    public void ARolledUpPanel_MigratesAgainstTheCollapsedFloor()
    {
        var model = new ChildWindowModel { GeometryMode = PanelGeometryMode.GridAligned, IsRolledUp = true };

        PanelGridStateResolver.Resolve(model, new Rect(0d, 0d, 264d, 32d), Monitors, isRolledUp: true);

        Assert.Equal(WorkspaceGridMetrics.CollapsedHeaderSpan, model.HeightSpanUnits);
    }
}
