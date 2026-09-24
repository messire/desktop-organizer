using System.Windows;
using DesktopOrganizer.UI.Interaction.Native;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.Tests.Unit.Interaction.Native;

/// <summary>
/// P04 §3.8 (docs/PLANS/workspace-grid-03-04-integration.md): pins the ACCEPTED limit of
/// <see cref="NativeSizingCoordinateMapper.ToPx"/>'s independent per-edge rounding for
/// <see cref="PanelGeometryMode.GridAligned"/> panels, and proves the error neither exceeds one physical
/// pixel nor accumulates between frames.
/// </summary>
/// <remarks>
/// <para>
/// <b>Decision: accept, do not change <see cref="NativeSizingCoordinateMapper.ToPx"/>.</b> Rounding
/// <c>Left</c> and <c>Right</c> independently can make the resulting PIXEL width differ from
/// <c>round(Width * scale)</c> by at most 1 px when a grid edge (a whole multiple of 8 DIP from a
/// possibly fractional work-area origin) does not land on a whole pixel — systematic at 110%/175%. It is
/// still the right trade-off, for three reasons the ADR-028 amendment records: (1) <c>ToPx</c> serves
/// BOTH geometry modes and is covered by golden equivalence tests, so changing its rounding would move
/// the <see cref="PanelGeometryMode.LegacyFreeform"/> baseline it exists to protect; (2) the error is
/// bounded by one physical pixel and cannot accumulate — every <c>WM_SIZING</c> frame is recomputed from
/// the absolute DIP rectangle, never from the previous frame's pixels (proven below); (3) the persisted
/// value is a SPAN derived from DIP, so nothing about the error survives a restart.
/// </para>
/// <para>MANUAL VERIFY MV-11 (100/125/150/175%) still applies to how it LOOKS; this file pins the math.</para>
/// </remarks>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class NativeSizingCoordinateMapperGridAlignedTests
{
    private const double U = WorkspaceGridMetrics.UnitSize;
    private const double G = WorkspaceGridMetrics.DefaultGap;

    // A work-area origin that is NOT a whole number of pixels at any of the scales below (a top-docked
    // taskbar, say) — the case where grid edges land off the pixel grid.
    private static readonly Point Origin = new(0d, 37d);

    public static TheoryData<double> DpiScales => [1.0, 1.1, 1.25, 1.5, 1.75];

    [Theory]
    [MemberData(nameof(DpiScales))]
    public void ToPx_GridAlignedEdges_WidthAndHeightDeviateByAtMostOnePixel(double scale)
    {
        foreach (var rect in GridAlignedRects())
        {
            var px = NativeSizingCoordinateMapper.ToPx(rect, scale);

            Assert.True(
                Math.Abs(px.Width - Math.Round(rect.Width * scale)) <= 1,
                $"width deviated by more than 1px at scale {scale} for {rect}: {px.Width} vs {Math.Round(rect.Width * scale)}");
            Assert.True(
                Math.Abs(px.Height - Math.Round(rect.Height * scale)) <= 1,
                $"height deviated by more than 1px at scale {scale} for {rect}: {px.Height} vs {Math.Round(rect.Height * scale)}");
        }
    }

    /// <summary>
    /// Not vacuous: at a fractional scale the deviation really does reach 1 px for some grid rectangles
    /// — this is the behaviour being ACCEPTED, so if a future change silently removed it (or made it
    /// worse), one of these two assertions fails.
    /// </summary>
    [Fact]
    public void ToPx_GridAlignedEdges_ActuallyDeviateByExactlyOnePixel_AtSomeFractionalScale()
    {
        var deviations = new List<int>();
        foreach (var scale in new[] { 1.1, 1.25, 1.5, 1.75 })
        {
            foreach (var rect in GridAlignedRects())
            {
                var px = NativeSizingCoordinateMapper.ToPx(rect, scale);
                deviations.Add((int)Math.Abs(px.Width - Math.Round(rect.Width * scale)));
            }
        }

        Assert.Contains(1, deviations);
        Assert.DoesNotContain(deviations, d => d > 1);
    }

    /// <summary>
    /// The error does not accumulate: the native path recomputes every frame from the absolute DIP
    /// rectangle (<c>NativePanelResizeController.ProposeBounds</c>, D-4), so converting the same
    /// rectangle N times — or walking it one grid unit at a time — never drifts.
    /// </summary>
    [Theory]
    [MemberData(nameof(DpiScales))]
    public void ToPx_IsDeterministicAndNonCumulative_AcrossManyFrames(double scale)
    {
        var rect = new Rect(Origin.X + (12d * U), Origin.Y + (25d * U), (32d * U) - G, (30d * U) - G);

        var first = NativeSizingCoordinateMapper.ToPx(rect, scale);
        for (var frame = 0; frame < 30; frame++)
            Assert.Equal(first, NativeSizingCoordinateMapper.ToPx(rect, scale));

        // Walking the panel one unit at a time keeps the same bounded error at every step — it is a
        // function of the CURRENT rectangle only, never of the path taken to it.
        for (var step = 1; step <= 30; step++)
        {
            var moved = new Rect(rect.X + (step * U), rect.Y, rect.Width, rect.Height);
            var movedPx = NativeSizingCoordinateMapper.ToPx(moved, scale);

            Assert.True(Math.Abs(movedPx.Width - Math.Round(rect.Width * scale)) <= 1);
        }
    }

    /// <summary>
    /// What actually gets persisted is unaffected: the DIP rectangle recovered from the pixels still
    /// quantises to the SAME <see cref="GridSpan"/>, so a restart replays the exact same geometry
    /// (DoD D4-5).
    /// </summary>
    [Theory]
    [MemberData(nameof(DpiScales))]
    public void RoundTripThroughPixels_PreservesTheGridSpan(double scale)
    {
        foreach (var rect in GridAlignedRects())
        {
            var roundTripped = NativeSizingCoordinateMapper.ToDip(NativeSizingCoordinateMapper.ToPx(rect, scale), scale);

            Assert.Equal(
                WorkspaceLayoutPolicy.BoundsToSpans(rect.Size, U, G),
                WorkspaceLayoutPolicy.BoundsToSpans(roundTripped.Size, U, G));
        }
    }

    /// <summary>Panel rectangles whose edges are whole grid units from <see cref="Origin"/> and whose sizes are whole span extents.</summary>
    private static IEnumerable<Rect> GridAlignedRects()
    {
        foreach (var col in new[] { 0, 1, 5, 12, 37 })
        {
            foreach (var row in new[] { 0, 3, 25 })
            {
                foreach (var span in new[] { WorkspaceGridMetrics.MinSpanExpanded, new GridSpan(20, 20), new GridSpan(32, 30), new GridSpan(44, 40) })
                {
                    var size = WorkspaceLayoutPolicy.SpansToBounds(span, U, G);
                    yield return new Rect(Origin.X + (col * U), Origin.Y + (row * U), size.Width, size.Height);
                }
            }
        }
    }
}
