using System.Windows;
using DesktopOrganizer.UI.Interaction;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.Tests.Unit.Models;

/// <summary>
/// Unit tests for <see cref="WorkspaceLayoutPolicy"/> — the pure workspace panel grid geometry (ADR-028,
/// System B: WHERE a <see cref="PanelGeometryMode.GridAligned"/> panel is and HOW BIG it is). Every test
/// here works purely in DIP; there is no WPF window, no monitor, no DPI conversion anywhere in the type
/// under test (see its class remarks) — "DPI 100/125/150/200%" coverage below means "raw DIP values that
/// would result from converting a physical-pixel position at that scale", not that this type performs
/// any DPI-aware rounding itself.
/// </summary>
[Trait(UnitTests, "UI")]
public class WorkspaceLayoutPolicyTests
{
    private const double U = WorkspaceGridMetrics.UnitSize; // 8 DIP
    private const double G = WorkspaceGridMetrics.DefaultGap; // 8 DIP

    public static IEnumerable<object[]> RepeatedSnapRawValues()
    {
        // Includes plain values, values already on the grid, negative values, and fractional DIP values
        // that arise from converting a physical-pixel position at 100/125/150/200% DPI scale back to DIP
        // (px / scale) — e.g. 101px / 1.25 = 80.8 DIP.
        double[] rawValues = [0d, 8d, -8d, 3.5d, -3.5d, 100.3d, -1366.25d, 80.8d, 64.4d, 42.666666667d, 200.1d, -0.0001d];
        foreach (var raw in rawValues)
            yield return [raw];
    }

    public static IEnumerable<object[]> RoundTripSpans()
    {
        GridSpan[] spans = [new(1, 1), new(2, 3), new(12, 12), new(20, 20), new(60, 50), new(100, 1)];
        foreach (var span in spans)
            yield return [span];
    }

    public static IEnumerable<object[]> AllResizeEdges()
    {
        foreach (ResizeEdge edge in Enum.GetValues<ResizeEdge>())
            yield return [edge];
    }

    // ===================== SnapCoordinate / SnapPosition =====================
    [Theory]
    [InlineData(0d, 0d, 8d, 0d)]
    [InlineData(8d, 0d, 8d, 8d)]
    [InlineData(-8d, 0d, 8d, -8d)]
    public void SnapCoordinate_ExactMultiples_ReturnUnchanged(double raw, double origin, double unitSize, double expected)
        => Assert.Equal(expected, WorkspaceLayoutPolicy.SnapCoordinate(raw, origin, unitSize));

    /// <summary>
    /// Exactly halfway between two grid lines rounds to the EVEN neighbour (<see cref="Math.Round(double)"/>'s
    /// default <see cref="MidpointRounding.ToEven"/>), matching <see cref="GridSnap.SnapAxis"/>'s existing
    /// choice for the same "position snap" family (ADR-028 "px↔DIP").
    /// </summary>
    [Theory]
    [InlineData(4d, 0d, 8d, 0d)] // origin + 0.5*U: tie between 0*U (even multiple) and 1*U -> 0
    [InlineData(12d, 0d, 8d, 16d)] // origin + 1.5*U: tie between 1*U and 2*U (even) -> 16
    [InlineData(20d, 0d, 8d, 16d)] // origin + 2.5*U: tie between 2*U (even) and 3*U -> 16
    [InlineData(100d, 0d, 8d, 96d)] // origin + 12.5*U: tie between 12*U (even) and 13*U -> 96
    public void SnapCoordinate_HalfwayBetweenGridLines_RoundsToEven(double raw, double origin, double unitSize, double expected)
        => Assert.Equal(expected, WorkspaceLayoutPolicy.SnapCoordinate(raw, origin, unitSize));

    [Theory]
    [InlineData(3d, 0d, 8d, 0d)] // rounds down to nearest multiple (3/8 = 0.375)
    [InlineData(5d, 0d, 8d, 8d)] // rounds up (5/8 = 0.625)
    [InlineData(19d, 0d, 8d, 16d)]
    [InlineData(21d, 0d, 8d, 24d)]
    public void SnapCoordinate_RoundsToNearestUnit(double raw, double origin, double unitSize, double expected)
        => Assert.Equal(expected, WorkspaceLayoutPolicy.SnapCoordinate(raw, origin, unitSize));

    /// <summary>
    /// Negative-coordinate monitor case (ADR-028 "Отрицательные координаты"): a monitor left of/above the
    /// primary monitor has a work-area origin with negative components (e.g. <c>WorkArea.Left = -1366</c>).
    /// The formula requires no special-casing for sign — snapping near such an origin behaves exactly
    /// like snapping near a positive one, shifted by the origin.
    /// </summary>
    [Theory]
    [InlineData(-1366d, -1366d, 8d, -1366d)] // exactly at origin
    [InlineData(-1362d, -1366d, 8d, -1366d)] // origin + 0.5*U: tie -> even multiple (0*U) -> origin
    [InlineData(-1360d, -1366d, 8d, -1358d)] // origin + 0.75*U -> rounds up to origin + U
    [InlineData(-1400d, -1366d, 8d, -1398d)] // origin - 4.25*U -> rounds to origin - 4*U
    public void SnapCoordinate_NegativeOrigin_NoSpecialCasingForSign(double raw, double origin, double unitSize, double expected)
        => Assert.Equal(expected, WorkspaceLayoutPolicy.SnapCoordinate(raw, origin, unitSize));

    [Theory]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void SnapCoordinate_DegenerateUnitSize_ReturnsRawUnchanged(double unitSize)
        => Assert.Equal(123.456d, WorkspaceLayoutPolicy.SnapCoordinate(123.456d, 0d, unitSize));

    [Fact]
    public void SnapCoordinate_NonFiniteRawOrOrigin_ReturnsRawUnchanged()
    {
        Assert.Equal(double.NaN, WorkspaceLayoutPolicy.SnapCoordinate(double.NaN, 0d, 8d));
        Assert.True(double.IsPositiveInfinity(WorkspaceLayoutPolicy.SnapCoordinate(double.PositiveInfinity, 0d, 8d)));
    }

    [Fact]
    public void SnapPosition_SnapsBothAxesIndependently()
    {
        var raw = new Point(19d, -1360d);
        var origin = new Point(0d, -1366d);

        var snapped = WorkspaceLayoutPolicy.SnapPosition(raw, origin, U);

        Assert.Equal(16d, snapped.X);
        Assert.Equal(-1358d, snapped.Y);
    }

    // ===================== Idempotency: Snap(Snap(x)) == Snap(x) =====================
    [Theory]
    [MemberData(nameof(RepeatedSnapRawValues))]
    public void SnapCoordinate_RepeatedApplication_IsIdempotent(double raw)
    {
        var once = WorkspaceLayoutPolicy.SnapCoordinate(raw, 0d, U);
        var twice = WorkspaceLayoutPolicy.SnapCoordinate(once, 0d, U);

        Assert.Equal(once, twice);
    }

    [Theory]
    [MemberData(nameof(RepeatedSnapRawValues))]
    public void SnapCoordinate_RepeatedApplication_IsIdempotent_WithNegativeOrigin(double raw)
    {
        const double origin = -1366d;
        var once = WorkspaceLayoutPolicy.SnapCoordinate(raw, origin, U);
        var twice = WorkspaceLayoutPolicy.SnapCoordinate(once, origin, U);

        Assert.Equal(once, twice);
    }

    /// <summary>DPI 100/125/150/200%: idempotency must hold regardless of which scale the raw DIP value came from.</summary>
    [Theory]
    [InlineData(1.0d)]
    [InlineData(1.25d)]
    [InlineData(1.5d)]
    [InlineData(2.0d)]
    public void SnapPosition_RepeatedApplication_IsIdempotent_AcrossDpiScales(double dpiScale)
    {
        for (var px = 0; px < 500; px += 7)
        {
            var raw = new Point(px / dpiScale, (px + 3) / dpiScale);
            var origin = new Point(0d, 0d);

            var once = WorkspaceLayoutPolicy.SnapPosition(raw, origin, U);
            var twice = WorkspaceLayoutPolicy.SnapPosition(once, origin, U);

            Assert.Equal(once, twice);
        }
    }

    /// <summary>
    /// Fix-cycle 1, MAJOR-7: the DPI-scales idempotency test above only ever used <c>origin = (0, 0)</c> —
    /// this extends the same idempotency property to negative AND fractional origins (a secondary monitor
    /// left of/above the primary, whose <c>WorkArea</c> top-left need not itself be a whole DIP after a
    /// fractional System-DPI conversion, ADR-028 "Origin для системы B").
    /// </summary>
    [Theory]
    [InlineData(-1366.25d, -200.5d)]
    [InlineData(1366.25d, 0d)]
    [InlineData(-0.001d, 999.999d)]
    public void SnapPosition_RepeatedApplication_IsIdempotent_NegativeOrFractionalOrigin(double originX, double originY)
    {
        var origin = new Point(originX, originY);

        for (var px = -500; px < 500; px += 7)
        {
            var raw = new Point(px + 0.3d, px - 0.7d);

            var once = WorkspaceLayoutPolicy.SnapPosition(raw, origin, U);
            var twice = WorkspaceLayoutPolicy.SnapPosition(once, origin, U);

            Assert.Equal(once, twice);
        }
    }

    /// <summary>
    /// Fix-cycle 1, MAJOR-7: a REAL drag trajectory (not just <see cref="WorkspaceLayoutPolicy.SnapCoordinate"/>
    /// in isolation) — a sequence of per-frame mouse positions -> <see cref="WorkspaceLayoutPolicy.SnapPosition"/>
    /// -> the gap-aware <see cref="PanelCollision.ClampAgainstObstacles(Rect,double,double,IReadOnlyList{Rect},double)"/>
    /// (MAJOR-5(b)) — at a NEGATIVE, FRACTIONAL monitor origin, the combination this file's earlier tests
    /// never exercised together. <see cref="PanelCollision.ClampAgainstObstacles(Rect,double,double,IReadOnlyList{Rect},double)"/>
    /// itself is origin-agnostic (it only ever sees absolute DIP rectangles), so the origin only feeds the
    /// <see cref="WorkspaceLayoutPolicy.SnapPosition"/> step here — this test's point is the realistic
    /// PIPELINE, not any one function in isolation.
    /// </summary>
    [Fact]
    public void DragTrajectory_SnapThenGapAwareClamp_NegativeFractionalOrigin_NeverClosesWithinGapOfObstacle()
    {
        var origin = new Point(-1366.5d, -200.25d);
        var panelSize = new Size(100d, 100d);
        var obstacle = new Rect(-1000d, -150d, 100d, 100d); // a neighbouring panel on the same monitor
        IReadOnlyList<Rect> obstacles = [obstacle];

        var current = new Rect(new Point(-1300d, -180d), panelSize);

        for (var dx = 0d; dx <= 250d; dx += 5d)
        {
            var raw = new Point(-1300d + dx, -180d);
            var snapped = WorkspaceLayoutPolicy.SnapPosition(raw, origin, U);
            var clamped = PanelCollision.ClampAgainstObstacles(current, snapped.X, snapped.Y, obstacles, G);

            current = clamped; // already a full Rect (position + the same panelSize)

            var clearanceX = obstacle.X - current.Right;
            Assert.True(clearanceX >= G - 1d, $"dx={dx}: clearance {clearanceX} DIP is less than gap {G} DIP (current={current}).");
        }

        // The drag pushed well past the obstacle's near edge (raw.X went from -1300 to -1050, i.e. past
        // -1000-100-G=-1108) -- confirm the hard wall actually engaged, not merely "never got close".
        Assert.True(current.Right <= obstacle.X - G + 1e-6, $"current={current} never actually stopped against the obstacle.");
    }

    // ===================== PositionToSlot / SlotToPosition =====================

    /// <summary>Fix-cycle 1, MAJOR-5(a): the position-side counterpart of <see cref="WorkspaceLayoutPolicy.BoundsToSpans"/>/<see cref="WorkspaceLayoutPolicy.SpansToBounds"/>.</summary>
    [Theory]
    [InlineData(0d, 0d, 0d, 0d, 0, 0)]
    [InlineData(16d, 24d, 0d, 0d, 2, 3)]
    [InlineData(-1366d, -200d, -1366d, -200d, 0, 0)] // exactly at a negative origin
    [InlineData(-1350d, -184d, -1366d, -200d, 2, 2)] // negative origin, offset by 2 units on each axis
    public void PositionToSlot_ExactGridPositions_ReturnsWholeSlotIndices(double rawX, double rawY, double originX, double originY, int expectedCol, int expectedRow)
    {
        var slot = WorkspaceLayoutPolicy.PositionToSlot(new Point(rawX, rawY), new Point(originX, originY), U);

        Assert.Equal((expectedCol, expectedRow), slot);
    }

    [Fact]
    public void SlotToPosition_IsInverseOfPositionToSlot_ForWholeSlots()
    {
        var origin = new Point(-1366.5d, 40d);

        foreach (var (col, row) in new[] { (0, 0), (5, -3), (-12, 40), (100, 1) })
        {
            var position = WorkspaceLayoutPolicy.SlotToPosition((col, row), origin, U);
            var roundTripped = WorkspaceLayoutPolicy.PositionToSlot(position, origin, U);

            Assert.Equal((col, row), roundTripped);
        }
    }

    [Theory]
    [MemberData(nameof(RepeatedSnapRawValues))]
    public void PositionToSlot_SlotToPosition_RoundTrip_IsIdempotentForAlreadySnappedPositions(double rawOffset)
    {
        var origin = new Point(-1366.25d, 99.75d);
        var raw = new Point(rawOffset, rawOffset);

        var snapped = WorkspaceLayoutPolicy.SnapPosition(raw, origin, U);
        var slot = WorkspaceLayoutPolicy.PositionToSlot(snapped, origin, U);
        var restored = WorkspaceLayoutPolicy.SlotToPosition(slot, origin, U);

        Assert.Equal(snapped, restored);
    }

    // ===================== Continuous drag trajectory: monotonicity, no jitter =====================

    /// <summary>
    /// A continuous drag trajectory (raw coordinate increasing by a small step every mouse-move) must
    /// snap to a monotonically non-decreasing sequence of grid lines — no jitter (no temporary jump
    /// backwards) as the raw coordinate crosses a half-unit (±0.5U) boundary.
    /// </summary>
    [Fact]
    public void SnapCoordinate_ContinuousIncreasingTrajectory_IsMonotonicNonDecreasing()
    {
        var previous = WorkspaceLayoutPolicy.SnapCoordinate(-50d, 0d, U);

        for (var raw = -50d; raw <= 50d; raw += 0.25d)
        {
            var snapped = WorkspaceLayoutPolicy.SnapCoordinate(raw, 0d, U);

            Assert.True(snapped >= previous, $"raw={raw}: snapped ({snapped}) regressed below previous ({previous}).");
            previous = snapped;
        }
    }

    /// <summary>Symmetric check for a continuous DEcreasing trajectory.</summary>
    [Fact]
    public void SnapCoordinate_ContinuousDecreasingTrajectory_IsMonotonicNonIncreasing()
    {
        var previous = WorkspaceLayoutPolicy.SnapCoordinate(50d, 0d, U);

        for (var raw = 50d; raw >= -50d; raw -= 0.25d)
        {
            var snapped = WorkspaceLayoutPolicy.SnapCoordinate(raw, 0d, U);

            Assert.True(snapped <= previous, $"raw={raw}: snapped ({snapped}) regressed above previous ({previous}).");
            previous = snapped;
        }
    }

    /// <summary>
    /// Right at a half-unit boundary (raw = origin + n*U + 0.5*U), a ±0.5 DIP perturbation (well within
    /// normal cursor jitter) must not cause the snapped value to jump by more than one grid step —
    /// i.e. no "dribbling" between two far-apart grid lines from sub-pixel mouse noise.
    /// </summary>
    [Theory]
    [InlineData(4d)] // origin + 0.5*U (U=8) — tie between grid line 0 and grid line 1
    [InlineData(12d)] // origin + 1.5*U — tie between grid line 1 and grid line 2
    public void SnapCoordinate_NearHalfUnitBoundary_SmallPerturbationStaysWithinOneStep(double tieRaw)
    {
        var below = WorkspaceLayoutPolicy.SnapCoordinate(tieRaw - 0.5d, 0d, U);
        var at = WorkspaceLayoutPolicy.SnapCoordinate(tieRaw, 0d, U);
        var above = WorkspaceLayoutPolicy.SnapCoordinate(tieRaw + 0.5d, 0d, U);

        Assert.True(above - below <= U, $"below={below}, at={at}, above={above} span more than one grid step ({U}).");
        Assert.InRange(at, below, above);
    }

    // ===================== BoundsToSpans / SpansToBounds =====================
    [Theory]
    [InlineData(1, 1, 8d, 8d, 0d, 0d)] // n*U - G = 1*8 - 8 = 0
    [InlineData(2, 1, 8d, 8d, 8d, 0d)] // 2*8 - 8 = 8
    [InlineData(12, 12, 8d, 8d, 88d, 88d)] // 12*8-8 = 88
    public void SpansToBounds_ComputesCellPlusGapExtent(int widthUnits, int heightUnits, double unitSize, double gap, double expectedWidth, double expectedHeight)
    {
        var size = WorkspaceLayoutPolicy.SpansToBounds(new GridSpan(widthUnits, heightUnits), unitSize, gap);

        Assert.Equal(expectedWidth, size.Width);
        Assert.Equal(expectedHeight, size.Height);
    }

    [Fact]
    public void SpansToBounds_NeverReturnsNegativeSize()
    {
        // Pathological: gap larger than the span's own extent.
        var size = WorkspaceLayoutPolicy.SpansToBounds(new GridSpan(1, 1), 8d, 100d);

        Assert.Equal(0d, size.Width);
        Assert.Equal(0d, size.Height);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-5, -5)]
    public void SpansToBounds_NonPositiveSpanComponents_ClampToOneUnit(int widthUnits, int heightUnits)
    {
        var size = WorkspaceLayoutPolicy.SpansToBounds(new GridSpan(widthUnits, heightUnits), U, G);

        Assert.Equal(U - G, size.Width);
        Assert.Equal(U - G, size.Height);
    }

    /// <summary>
    /// Fix-cycle 1, MAJOR-1: <see cref="WorkspaceLayoutPolicy.BoundsToSpans"/> now subtracts a 0.5 DIP
    /// tolerance before <see cref="Math.Ceiling(double)"/> (see <c>SpanQuantizationToleranceDip</c>'s own
    /// remarks) to avoid the DIP→px→DIP round-trip "ratchet" bug (MAJOR-1) — so a sub-tolerance overshoot
    /// of an exact span boundary (e.g. <c>0.001</c> DIP over) no longer bumps to the next whole span the
    /// way it did before this fix; only an overshoot PAST the 0.5 DIP tolerance does. The two inline rows
    /// that previously asserted "even a hair over needs the next unit"/"just over 88 needs 13" are updated
    /// here to the new, deliberate contract (was: <c>(0.001,...,2,2)</c>/<c>(88.001,...,13,13)</c>); the
    /// new <c>0.5xxx</c> rows below cover the actual (now much narrower) "just over the tolerance" case
    /// that those two rows used to stand in for.
    /// </summary>
    [Theory]
    [InlineData(0d, 0d, 8d, 8d, 1, 1)] // empty content still occupies at least 1x1 unit
    [InlineData(0.001d, 0.001d, 8d, 8d, 1, 1)] // within the 0.5 DIP tolerance of the 1-unit (0 DIP) boundary -- stays at 1
    [InlineData(0.5d, 0.5d, 8d, 8d, 1, 1)] // exactly AT the tolerance boundary -- still 1
    [InlineData(0.5001d, 0.5001d, 8d, 8d, 2, 2)] // a hair PAST the tolerance boundary -- now genuinely needs the next unit
    [InlineData(88d, 88d, 8d, 8d, 12, 12)] // exactly SpansToBounds(12,12)
    [InlineData(87.999d, 87.999d, 8d, 8d, 12, 12)] // just under an exact extent still ceils up, never down
    [InlineData(88.001d, 88.001d, 8d, 8d, 12, 12)] // within tolerance of 88 -- stays at 12 (was: bumped to 13 pre-fix)
    [InlineData(88.5001d, 88.5001d, 8d, 8d, 13, 13)] // a hair past 88 + the 0.5 DIP tolerance -- genuinely needs 13
    public void BoundsToSpans_CeilsToWholeSpan_NeverLosesSpace(double width, double height, double unitSize, double gap, int expectedWidthUnits, int expectedHeightUnits)
    {
        var span = WorkspaceLayoutPolicy.BoundsToSpans(new Size(width, height), unitSize, gap);

        Assert.Equal(expectedWidthUnits, span.WidthUnits);
        Assert.Equal(expectedHeightUnits, span.HeightUnits);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-1d)]
    public void BoundsToSpans_NonPositiveUnitSize_ReturnsOneByOneSpan(double unitSize)
    {
        var span = WorkspaceLayoutPolicy.BoundsToSpans(new Size(500d, 500d), unitSize, G);

        Assert.Equal(new GridSpan(1, 1), span);
    }

    /// <summary>
    /// <see cref="Size"/> itself rejects a directly-constructed negative component (throws in its own
    /// constructor), so the only realistic "negative-ish" input this method can ever see is the
    /// <see cref="Size.Empty"/> sentinel (<c>NegativeInfinity</c> on both axes — e.g. an unmeasured
    /// WPF element's <c>DesiredSize</c>). A <see cref="double.NaN"/> component IS constructible directly.
    /// Both must clamp to <c>0</c> DIP before quantising, never propagate as a span of <c>0</c> or less.
    /// </summary>
    [Fact]
    public void BoundsToSpans_NaNWidth_ClampsToZeroBeforeQuantising()
    {
        var span = WorkspaceLayoutPolicy.BoundsToSpans(new Size(double.NaN, 5d), U, G);

        Assert.Equal(1, span.WidthUnits); // 0 DIP content still needs at least one unit
    }

    [Fact]
    public void BoundsToSpans_EmptySize_ClampsToZeroBeforeQuantising()
    {
        var span = WorkspaceLayoutPolicy.BoundsToSpans(Size.Empty, U, G);

        Assert.Equal(new GridSpan(1, 1), span);
    }

    // ----- Round-trip idempotency: BoundsToSpans(SpansToBounds(span)) == span -----
    [Theory]
    [MemberData(nameof(RoundTripSpans))]
    public void SpansToBounds_BoundsToSpans_RoundTrip_IsIdempotent(GridSpan span)
    {
        var size = WorkspaceLayoutPolicy.SpansToBounds(span, U, G);
        var roundTripped = WorkspaceLayoutPolicy.BoundsToSpans(size, U, G);

        Assert.Equal(span, roundTripped);
    }

    /// <summary>
    /// Same round-trip, at DPI 100/110/125/150/175/200% (fix-cycle 1, MAJOR-1/MAJOR-7: extended from the
    /// original 100/125/150/200% list to also cover the two non-"nice" scales — 110%/175% — that actually
    /// trigger sub-pixel px-rounding noise against an 8 DIP unit; 125%/150%/200% all divide 8 DIP evenly at
    /// the tested spans and would never have caught the MAJOR-1 ratchet bug on their own). Two assertions,
    /// not one (MAJOR-7: the original version only checked <c>span1 == span2</c> — stability BETWEEN two
    /// round-tripped values — which is a tautology a consistently-WRONG implementation could also satisfy;
    /// <c>Assert.Equal(span, span1)</c> is the assertion that actually catches MAJOR-1's ratchet bug, by
    /// checking the FIRST round-trip against the ORIGINAL span, not just against a second round-trip).
    /// </summary>
    [Theory]
    [InlineData(1.0d)]
    [InlineData(1.1d)]
    [InlineData(1.25d)]
    [InlineData(1.5d)]
    [InlineData(1.75d)]
    [InlineData(2.0d)]
    public void SpansToBounds_BoundsToSpans_RoundTrip_IsIdempotent_AcrossDpiScales(double dpiScale)
    {
        foreach (var span in new[] { new GridSpan(1, 1), new GridSpan(2, 3), new GridSpan(12, 12), new GridSpan(20, 20), new GridSpan(32, 30), new GridSpan(60, 50), new GridSpan(100, 1) })
        {
            var size = WorkspaceLayoutPolicy.SpansToBounds(span, U, G);

            // Simulate a size that has round-tripped once through a px-scaled screen and back to DIP.
            var pxRoundTripped = new Size(Math.Round(size.Width * dpiScale) / dpiScale, Math.Round(size.Height * dpiScale) / dpiScale);
            var span1 = WorkspaceLayoutPolicy.BoundsToSpans(pxRoundTripped, U, G);

            Assert.Equal(span, span1); // MAJOR-1: must round-trip back to the ORIGINAL span, not ratchet up.

            var size1 = WorkspaceLayoutPolicy.SpansToBounds(span1, U, G);
            var span2 = WorkspaceLayoutPolicy.BoundsToSpans(size1, U, G);

            Assert.Equal(span1, span2);
        }
    }

    // ===================== SnapBounds (position variant) =====================
    [Fact]
    public void SnapBounds_QuantisesPositionAndSizeTogether()
    {
        var raw = new Rect(19d, 21d, 100d, 100d);
        var origin = new Point(0d, 0d);

        var result = WorkspaceLayoutPolicy.SnapBounds(raw, origin, U, G, new GridSpan(1, 1));

        Assert.Equal(16d, result.X); // 19/8 = 2.375 -> rounds to 2*8 = 16
        Assert.Equal(24d, result.Y); // 21/8 = 2.625 -> rounds to 3*8 = 24
    }

    [Fact]
    public void SnapBounds_EnforcesMinimumSpan()
    {
        var raw = new Rect(0d, 0d, 10d, 10d); // far below WorkspaceGridMetrics.MinSpanExpanded
        var origin = new Point(0d, 0d);

        var result = WorkspaceLayoutPolicy.SnapBounds(raw, origin, U, G, WorkspaceGridMetrics.MinSpanExpanded);

        var expectedSize = WorkspaceLayoutPolicy.SpansToBounds(WorkspaceGridMetrics.MinSpanExpanded, U, G);
        Assert.Equal(expectedSize.Width, result.Width);
        Assert.Equal(expectedSize.Height, result.Height);
    }

    /// <summary>Negative-coordinate monitor case for a whole rectangle, not just a single coordinate.</summary>
    [Fact]
    public void SnapBounds_NegativeOriginMonitor_SnapsCorrectly()
    {
        var raw = new Rect(-1360d, -195d, 100d, 100d);
        var origin = new Point(-1366d, -200d);

        var result = WorkspaceLayoutPolicy.SnapBounds(raw, origin, U, G, new GridSpan(1, 1));

        Assert.Equal(WorkspaceLayoutPolicy.SnapCoordinate(-1360d, -1366d, U), result.X);
        Assert.Equal(WorkspaceLayoutPolicy.SnapCoordinate(-195d, -200d, U), result.Y);
    }

    [Theory]
    [MemberData(nameof(RepeatedSnapRawValues))]
    public void SnapBounds_RepeatedApplication_IsIdempotent(double rawOffset)
    {
        // Fixed 100x100 size regardless of the offset under test — rawOffset alone drives the POSITION
        // axis of this test; folding it into the size too could otherwise produce a negative Width/Height
        // for the most extreme offsets (e.g. -1366.25), which the Rect constructor rejects.
        var raw = new Rect(new Point(rawOffset, rawOffset), new Size(100d, 100d));
        var origin = new Point(0d, 0d);
        var minSpan = new GridSpan(1, 1);

        var once = WorkspaceLayoutPolicy.SnapBounds(raw, origin, U, G, minSpan);
        var twice = WorkspaceLayoutPolicy.SnapBounds(once, origin, U, G, minSpan);

        Assert.Equal(once, twice);
    }

    /// <summary>
    /// Several independently-created panels given the same target <see cref="GridSpan"/> must resolve to
    /// exactly the same DIP size on the same workspace (ADR-028 DoD) — a direct consequence of
    /// <see cref="WorkspaceLayoutPolicy.SpansToBounds"/> being a pure function of the span alone, never of
    /// which panel/instance asked for it or what its previous raw size happened to be.
    /// <para>
    /// Fix-cycle 1, MINOR: the original version of this test used <c>expectedSize</c> — i.e. the EXACT
    /// SAME raw size — for both "panels", with only a sub-DIP (0.1-0.3 DIP) difference in POSITION, while
    /// its own comment claimed "very different raw (unsnapped) starting SIZES". Neither the data nor the
    /// (barely-different) positions ever exercised the stated invariant ("whatever its previous raw size
    /// happened to be"). Fixed to use genuinely different raw WIDTH/HEIGHT values (81/85 and 88.5/80.6 DIP
    /// — all four numbers distinct from each other AND from <c>expectedSize</c>'s 88 DIP) that all still
    /// quantise to the same span 12 (valid range, given the MAJOR-1 tolerance fix: <c>(80.5, 88.5]</c> DIP).
    /// </para>
    /// </summary>
    [Fact]
    public void SnapBounds_SeveralPanelsSameSpan_ProduceIdenticalGeometryOnSameWorkspace()
    {
        var origin = new Point(0d, 0d);
        var minSpan = new GridSpan(1, 1);
        var span = new GridSpan(12, 12);
        var expectedSize = WorkspaceLayoutPolicy.SpansToBounds(span, U, G);

        // Four different raw (unsnapped) sizes -- 81x85, 88.5x80.6 -- neither matching each other nor
        // expectedSize (88x88), all within span 12's valid quantisation range, all quantising to the SAME
        // span/final DIP size regardless of "which panel asked" or "what its raw size happened to be".
        var panelA = WorkspaceLayoutPolicy.SnapBounds(new Rect(new Point(80d, 80d), new Size(81d, 85d)), origin, U, G, minSpan);
        var panelB = WorkspaceLayoutPolicy.SnapBounds(new Rect(new Point(80.2d, 79.9d), new Size(88.5d, 80.6d)), origin, U, G, minSpan);

        Assert.Equal(panelA.Size, panelB.Size);
        Assert.Equal(expectedSize, panelA.Size);
    }

    /// <summary>
    /// Fix-cycle 2, MINOR-E: <c>gap</c> not a whole multiple of <c>unitSize</c> silently violated the "both
    /// edges land on the grid" guarantee before this fix-cycle added the missing
    /// <see cref="WorkspaceGridMetrics.ValidateGap"/> call.
    /// </summary>
    [Fact]
    public void SnapBounds_GapNotWholeMultipleOfUnitSize_Throws()
    {
        var raw = new Rect(0d, 0d, 100d, 100d);
        var origin = new Point(0d, 0d);

        Assert.Throws<ArgumentException>(() => WorkspaceLayoutPolicy.SnapBounds(raw, origin, U, 5d, new GridSpan(1, 1)));
    }

    // ===================== SnapBounds (resize edge variant): 8 edges/corners =====================
    // Fix-cycle 1, MAJOR-3: this overload now takes an `origin` (ADR-028 Amendment 2026-09-22) and uses
    // nearest-span ROUNDING, not Ceiling -- see WorkspaceLayoutPolicy.SnapBounds(Rect,Point,ResizeEdge,...)'s
    // own remarks. The single "always fixed regardless" anchoring test is now two tests: one for an
    // anchor that is ALREADY on-grid (stays fixed, as before), one for an anchor that is OFF-grid (gets
    // aligned as part of the call -- the new, deliberate ADR rule this fix-cycle introduces).

    /// <summary>
    /// Resizing from any of the 8 edges/corners, when the OPPOSITE edge is already exactly on the
    /// workspace grid, must leave it exactly fixed — the "hard wall" anchoring contract shared with the
    /// legacy <c>ResizeGeometryCalculator.Apply</c> (ADR-028 "Resize: ... Corner/edge anchoring").
    /// </summary>
    [Theory]
    [MemberData(nameof(AllResizeEdges))]
    public void SnapBounds_ResizeFromEachEdge_KeepsOppositeEdgeFixed_WhenAlreadyOnGrid(ResizeEdge edge)
    {
        // Every one of Left/Top/Right/Bottom is already an exact multiple of U=8 from origin (Right=144,
        // Bottom=160) -- both the anchor AND the moving edge start (and, since the size is already exactly
        // a whole span, stay) on-grid, so the result should equal `raw` exactly regardless of which edge
        // is nominally "dragged".
        var raw = new Rect(48d, 64d, 96d, 96d);
        var origin = new Point(0d, 0d);
        var minSpan = new GridSpan(1, 1);

        var result = WorkspaceLayoutPolicy.SnapBounds(raw, origin, edge, U, G, minSpan);

        if (edge.AnchorsRight())
            Assert.Equal(raw.Right, result.Right);
        else
            Assert.Equal(raw.Left, result.Left);

        if (edge.AnchorsBottom())
            Assert.Equal(raw.Bottom, result.Bottom);
        else
            Assert.Equal(raw.Top, result.Top);
    }

    /// <summary>
    /// Fix-cycle 1, MAJOR-3: when the OPPOSITE edge is NOT already on the workspace grid (e.g. this is the
    /// panel's first-ever <see cref="PanelGeometryMode.GridAligned"/> resize right after switching from
    /// <see cref="PanelGeometryMode.LegacyFreeform"/>, before an explicit "Align to workspace grid"), the
    /// ADR rule is "align first": the anchor is snapped to the nearest grid line as part of this same
    /// call, rather than being copied through off-grid forever (the pre-fix bug this replaces). Also
    /// verifies BOTH resulting edges on every resized axis end up exactly on the grid, regardless of which
    /// single edge was nominally dragged.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllResizeEdges))]
    public void SnapBounds_ResizeFromEachEdge_WhenOppositeEdgeOffGrid_AlignsItFirst(ResizeEdge edge)
    {
        var raw = new Rect(50d, 60d, 101d, 103d); // Right=151, Bottom=163 -- neither on the U=8 grid
        var origin = new Point(0d, 0d);
        var minSpan = new GridSpan(1, 1);

        var result = WorkspaceLayoutPolicy.SnapBounds(raw, origin, edge, U, G, minSpan);

        var expectedAnchorX = edge.AnchorsRight()
            ? WorkspaceLayoutPolicy.SnapCoordinate(raw.Right, origin.X, U)
            : WorkspaceLayoutPolicy.SnapCoordinate(raw.Left, origin.X, U);
        var expectedAnchorY = edge.AnchorsBottom()
            ? WorkspaceLayoutPolicy.SnapCoordinate(raw.Bottom, origin.Y, U)
            : WorkspaceLayoutPolicy.SnapCoordinate(raw.Top, origin.Y, U);

        if (edge.AnchorsRight())
            Assert.Equal(expectedAnchorX, result.Right);
        else
            Assert.Equal(expectedAnchorX, result.Left);

        if (edge.AnchorsBottom())
            Assert.Equal(expectedAnchorY, result.Bottom);
        else
            Assert.Equal(expectedAnchorY, result.Top);

        // Both edges of the result are always exactly on the grid, regardless of which one "moved".
        AssertOnGrid(result.Left, origin.X, U);
        AssertOnGrid(result.Right, origin.X, U);
        AssertOnGrid(result.Top, origin.Y, U);
        AssertOnGrid(result.Bottom, origin.Y, U);
    }

    [Theory]
    [MemberData(nameof(AllResizeEdges))]
    public void SnapBounds_ResizeFromEachEdge_EnforcesMinimumSpan(ResizeEdge edge)
    {
        var raw = new Rect(50d, 60d, 10d, 10d); // far below the minimum
        var origin = new Point(0d, 0d);

        var result = WorkspaceLayoutPolicy.SnapBounds(raw, origin, edge, U, G, WorkspaceGridMetrics.MinSpanExpanded);

        var expectedSize = WorkspaceLayoutPolicy.SpansToBounds(WorkspaceGridMetrics.MinSpanExpanded, U, G);
        Assert.Equal(expectedSize.Width, result.Width);
        Assert.Equal(expectedSize.Height, result.Height);
    }

    /// <summary>
    /// Fix-cycle 1, MAJOR-7: exercises the min-clamp branch together with anchoring — the
    /// <c>EnforcesMinimumSpan</c> test above only ever checked the resulting SIZE, never that the anchor
    /// edge stayed exactly where <see cref="WorkspaceLayoutPolicy.SnapBounds(Rect,Point,ResizeEdge,double,double,GridSpan)"/>'s
    /// own contract promises, and every other resize-edge test in this file used <c>minSpan = (1, 1)</c>,
    /// which the tiny 101x103 raw rect never actually clamps against — so the min-clamp branch's
    /// interaction with anchoring was never covered by any of them. Uses <see cref="WorkspaceGridMetrics.MinSpanExpanded"/>
    /// (12x12), the REAL production floor, not an arbitrary "> 1x1" placeholder.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllResizeEdges))]
    public void SnapBounds_ResizeFromEachEdge_MinClampPreservesAnchorPosition(ResizeEdge edge)
    {
        var raw = new Rect(48d, 64d, 16d, 16d); // Left/Top/Right(64)/Bottom(80) all already on the U=8 grid
        var origin = new Point(0d, 0d);
        var minSpan = WorkspaceGridMetrics.MinSpanExpanded; // 12x12 -- far above the tiny 16x16 raw size

        var result = WorkspaceLayoutPolicy.SnapBounds(raw, origin, edge, U, G, minSpan);
        var expectedSize = WorkspaceLayoutPolicy.SpansToBounds(minSpan, U, G);

        Assert.Equal(expectedSize.Width, result.Width);
        Assert.Equal(expectedSize.Height, result.Height);

        if (edge.AnchorsRight())
            Assert.Equal(raw.Right, result.Right);
        else
            Assert.Equal(raw.Left, result.Left);

        if (edge.AnchorsBottom())
            Assert.Equal(raw.Bottom, result.Bottom);
        else
            Assert.Equal(raw.Top, result.Top);
    }

    [Theory]
    [MemberData(nameof(AllResizeEdges))]
    public void SnapBounds_ResizeFromEachEdge_ResultIsAlreadyOnGrid_SoRepeatedResizeFromSameEdgeIsIdempotent(ResizeEdge edge)
    {
        var raw = new Rect(50d, 60d, 101d, 103d);
        var origin = new Point(0d, 0d);
        var minSpan = new GridSpan(1, 1);

        var once = WorkspaceLayoutPolicy.SnapBounds(raw, origin, edge, U, G, minSpan);
        var twice = WorkspaceLayoutPolicy.SnapBounds(once, origin, edge, U, G, minSpan);

        Assert.Equal(once, twice);
    }

    /// <summary>Asserts <paramref name="coordinate"/> is exactly <paramref name="origin"/> plus a whole multiple of <paramref name="unitSize"/> (within floating-point tolerance).</summary>
    private static void AssertOnGrid(double coordinate, double origin, double unitSize)
    {
        var units = (coordinate - origin) / unitSize;
        Assert.Equal(Math.Round(units), units, 9);
    }

    /// <summary>Fix-cycle 2, MINOR-E: same missing-validation gap as the position-variant overload.</summary>
    [Fact]
    public void SnapBounds_ResizeEdge_GapNotWholeMultipleOfUnitSize_Throws()
    {
        var raw = new Rect(0d, 0d, 100d, 100d);
        var origin = new Point(0d, 0d);

        Assert.Throws<ArgumentException>(() => WorkspaceLayoutPolicy.SnapBounds(raw, origin, ResizeEdge.Right, U, 5d, new GridSpan(1, 1)));
    }

    // ===================== ClampResizeToSlots =====================

    /// <summary>Fix-cycle 1, MAJOR-5(c): no obstacle within gap DIP -- candidate returned unchanged.</summary>
    [Fact]
    public void ClampResizeToSlots_NoNearbyObstacle_ReturnsCandidateUnchanged()
    {
        var candidate = new Rect(0d, 0d, 232d, 72d); // span (30,10)
        IReadOnlyList<Rect> obstacles = [new Rect(1000d, 1000d, 100d, 100d)];

        var result = WorkspaceLayoutPolicy.ClampResizeToSlots(candidate, ResizeEdge.Right, obstacles, U, G, new GridSpan(1, 1));

        Assert.Equal(candidate, result);
    }

    /// <summary>
    /// Fix-cycle 1, MAJOR-5(c): dragging Right into a neighbour shrinks the width one whole slot at a time
    /// until the candidate clears the neighbour by a full <c>gap</c> DIP — since both panels share the same
    /// grid, the result always lands exactly on a slot boundary, never "half inside" the neighbour.
    /// </summary>
    [Fact]
    public void ClampResizeToSlots_OverlapsObstacle_ShrinksToNearestNonOverlappingSlot()
    {
        var candidate = new Rect(0d, 0d, 232d, 72d); // span (30,10) -- Right=232, overlaps the obstacle below
        var obstacle = new Rect(200d, 0d, 100d, 100d);
        IReadOnlyList<Rect> obstacles = [obstacle];

        var result = WorkspaceLayoutPolicy.ClampResizeToSlots(candidate, ResizeEdge.Right, obstacles, U, G, new GridSpan(1, 1));

        Assert.Equal(192d, result.Right); // n=25: 25*8-8=192 -- obstacle.X(200) - gap(8) = 192
        Assert.Equal(0d, result.Left); // Left (the anchor for a Right-edge drag) never moves
        Assert.Equal(72d, result.Height); // untouched axis unaffected
    }

    /// <summary>Fix-cycle 1, MAJOR-5(c): never shrinks below minSpan, even if a residual overlap remains.</summary>
    [Fact]
    public void ClampResizeToSlots_NeverShrinksBelowMinSpan_EvenIfStillOverlapping()
    {
        var candidate = new Rect(0d, 0d, 232d, 72d); // span (30,10)
        var obstacle = new Rect(200d, 0d, 100d, 100d);
        IReadOnlyList<Rect> obstacles = [obstacle];
        var minSpan = new GridSpan(28, 1); // deliberately ABOVE the (25,_) slot that would clear the obstacle

        var result = WorkspaceLayoutPolicy.ClampResizeToSlots(candidate, ResizeEdge.Right, obstacles, U, G, minSpan);

        var expectedWidth = WorkspaceLayoutPolicy.SpansToBounds(new GridSpan(28, 10), U, G).Width;
        Assert.Equal(expectedWidth, result.Width); // stopped at the floor (28), not shrunk further to 25
        Assert.Equal(0d, result.Left);
    }

    [Fact]
    public void ClampResizeToSlots_EmptyObstacles_ReturnsCandidateUnchanged()
    {
        var candidate = new Rect(0d, 0d, 100d, 100d);

        var result = WorkspaceLayoutPolicy.ClampResizeToSlots(candidate, ResizeEdge.Right, [], U, G, new GridSpan(1, 1));

        Assert.Equal(candidate, result);
    }

    /// <summary>
    /// Fix-cycle 2, MAJOR-B repro (exact numbers from the review): an obstacle on the ANCHOR side of the
    /// resize can never be cleared by moving the opposite edge — the anchor itself never moves. The old
    /// implementation shrank blindly for 20 iterations down to <c>(100,100,88,248)</c>, still overlapping,
    /// width destroyed for nothing. The fix returns <paramref name="candidate"/> completely unchanged for
    /// every one of the 8 edges/corners, because the obstacle is verifiably unresolvable on every axis each
    /// one actually moves.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllResizeEdges))]
    public void ClampResizeToSlots_ObstacleOnAnchorSide_NeverClears_ReturnsCandidateUnchanged(ResizeEdge edge)
    {
        var candidate = new Rect(100d, 100d, 248d, 248d); // Left=100 Top=100 Right=348 Bottom=348
        var anchorX = edge.AnchorsRight() ? candidate.Right : candidate.Left;
        var anchorY = edge.AnchorsBottom() ? candidate.Bottom : candidate.Top;

        // Straddles the anchor on every axis this edge actually moves; on any axis it does NOT move, the
        // obstacle simply overlaps the candidate's own (unchanging) extent -- so an unfiltered shrink loop
        // would still see a gap violation and try (uselessly) to shrink toward the floor.
        var left = edge.AffectsWidth() ? anchorX - 20d : candidate.Left;
        var right = edge.AffectsWidth() ? anchorX + 20d : candidate.Right;
        var top = edge.AffectsHeight() ? anchorY - 20d : candidate.Top;
        var bottom = edge.AffectsHeight() ? anchorY + 20d : candidate.Bottom;
        var obstacle = new Rect(new Point(left, top), new Point(right, bottom));

        var result = WorkspaceLayoutPolicy.ClampResizeToSlots(candidate, edge, [obstacle], U, G, new GridSpan(1, 1));

        Assert.Equal(candidate, result);
    }

    /// <summary>
    /// Fix-cycle 2, MAJOR-B "обратная ситуация": an obstacle genuinely AHEAD of the moving edge (not
    /// anchor-side) is unaffected by the new filter and still shrinks exactly to <c>gap</c> DIP of
    /// clearance, same as fix-cycle 1's <c>ClampResizeToSlots_OverlapsObstacle_ShrinksToNearestNonOverlappingSlot</c>
    /// but reusing this fix-cycle's <c>candidate</c>/edge — obstacle to the RIGHT for a <c>Right</c>-edge drag.
    /// </summary>
    [Fact]
    public void ClampResizeToSlots_ObstacleAheadOfMovingEdge_ShrinksExactlyToGap()
    {
        var candidate = new Rect(100d, 100d, 248d, 248d); // Right = 348
        var obstacle = new Rect(300d, 100d, 100d, 248d); // ahead of the Right edge, not anchor-side

        var result = WorkspaceLayoutPolicy.ClampResizeToSlots(candidate, ResizeEdge.Right, [obstacle], U, G, new GridSpan(1, 1));

        Assert.Equal(292d, result.Right); // obstacle.X (300) - gap (8)
        Assert.Equal(100d, result.Left); // anchor never moves
    }

    /// <summary>
    /// Fix-cycle 2, MINOR-C repro: a collapsed (rolled-up) panel's height span (4, i.e.
    /// <see cref="WorkspaceGridMetrics.CollapsedHeaderSpan"/>) is legitimately below the EXPANDED minimum
    /// (<see cref="WorkspaceGridMetrics.MinHeightSpanExpanded"/>, 12). The old floor check tested BOTH
    /// span axes unconditionally, so a width-only resize (<see cref="ResizeEdge.Right"/>, which never
    /// touches height) silently no-opped on its very first iteration merely because the UNCHANGED height
    /// span already read as "below minSpan.HeightUnits" -- even though nothing was asking to shrink it.
    /// </summary>
    [Fact]
    public void ClampResizeToSlots_CollapsedPanelHeightBelowExpandedMinimum_DoesNotBlockWidthOnlyResize()
    {
        var candidate = new Rect(0d, 0d, 248d, 24d); // width span 32, height span 4 (collapsed header row)
        var obstacle = new Rect(252d, 0d, 100d, 24d); // 4 DIP within the required 8 DIP gap
        var minSpan = WorkspaceGridMetrics.MinSpanExpanded; // (12,12) -- deliberately the EXPANDED floor, not MinSpanCollapsed

        var result = WorkspaceLayoutPolicy.ClampResizeToSlots(candidate, ResizeEdge.Right, [obstacle], U, G, minSpan);

        Assert.Equal(240d, result.Right); // shrank by exactly the 1 slot needed to clear the gap
        Assert.Equal(24d, result.Height); // untouched axis, even though its span (4) is below minSpan.HeightUnits (12)
    }

    /// <summary>Fix-cycle 2, MINOR-E: missing <see cref="WorkspaceGridMetrics.ValidateGap"/> entry check.</summary>
    [Fact]
    public void ClampResizeToSlots_GapNotWholeMultipleOfUnitSize_Throws()
    {
        var candidate = new Rect(0d, 0d, 100d, 100d);

        Assert.Throws<ArgumentException>(() => WorkspaceLayoutPolicy.ClampResizeToSlots(candidate, ResizeEdge.Right, [], U, 5d, new GridSpan(1, 1)));
    }

    // ===================== PreferredSize =====================
    [Theory]
    [InlineData(PanelSizePreset.Small, 20, 20)]
    [InlineData(PanelSizePreset.Medium, 32, 30)]
    [InlineData(PanelSizePreset.Large, 44, 40)]
    [InlineData(PanelSizePreset.ExtraLarge, 60, 50)]
    public void PreferredSize_MatchesPresetSpanExtent(PanelSizePreset preset, int widthUnits, int heightUnits)
    {
        var expected = WorkspaceLayoutPolicy.SpansToBounds(new GridSpan(widthUnits, heightUnits), U, G);

        var actual = WorkspaceLayoutPolicy.PreferredSize(preset, U, G);

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// Fix-cycle 1, MAJOR-6: the 3-<see langword="double"/> overload above always resolves through
    /// <see cref="WorkspaceGridMetrics.SpanForPreset"/> — the fixed DEFAULT table — no matter what a
    /// caller's own <see cref="WorkspaceGridOptions.PresetSpans"/> says. This test proves the
    /// <see cref="WorkspaceGridOptions"/>-aware overload actually consults the CUSTOM table (previously it
    /// did not exist at all — <see cref="WorkspaceLayoutPolicy.PreferredSize(PanelSizePreset,double,double)"/>
    /// was the only overload, so a customized preset table had no way to reach this method).
    /// </summary>
    [Fact]
    public void PreferredSize_WithCustomOptions_UsesOptionsPresetSpans_NotDefaults()
    {
        var customSpans = new Dictionary<PanelSizePreset, GridSpan>(WorkspaceGridMetrics.DefaultPresetSpans)
        {
            [PanelSizePreset.Medium] = new GridSpan(12, 12), // deliberately NOT the default 32x30
        };
        var options = WorkspaceGridOptions.Default with { PresetSpans = customSpans };

        var actual = WorkspaceLayoutPolicy.PreferredSize(PanelSizePreset.Medium, options);
        var expected = WorkspaceLayoutPolicy.SpansToBounds(new GridSpan(12, 12), U, G);
        var defaultMedium = WorkspaceLayoutPolicy.PreferredSize(PanelSizePreset.Medium, U, G);

        Assert.Equal(expected, actual);
        Assert.NotEqual(defaultMedium, actual); // proves the custom table was actually consulted
    }

    /// <summary>
    /// Fix-cycle 1, MINOR: an <see cref="WorkspaceGridOptions"/> whose <see cref="WorkspaceGridOptions.PresetSpans"/>
    /// genuinely has no entry for the requested preset (a caller that skipped <see cref="WorkspaceGridOptions.Validate"/>)
    /// falls back to <see cref="WorkspaceGridMetrics.SpanForPreset"/>'s default rather than throwing.
    /// </summary>
    [Fact]
    public void PreferredSize_OptionsMissingPresetEntry_FallsBackToDefaultSpan()
    {
        var options = WorkspaceGridOptions.Default with
        {
            PresetSpans = new Dictionary<PanelSizePreset, GridSpan> { [PanelSizePreset.Small] = new GridSpan(20, 20) },
        };

        var actual = WorkspaceLayoutPolicy.PreferredSize(PanelSizePreset.Large, options);
        var expected = WorkspaceLayoutPolicy.SpansToBounds(WorkspaceGridMetrics.SpanForPreset(PanelSizePreset.Large), U, G);

        Assert.Equal(expected, actual);
    }

    // ===================== SafeClamp =====================
    [Fact]
    public void SafeClamp_FullyInsideWorkArea_ReturnsUnchanged()
    {
        var proposed = new Rect(100d, 100d, 200d, 200d);
        var workArea = new Rect(0d, 0d, 1920d, 1080d);

        var result = WorkspaceLayoutPolicy.SafeClampFreeform(proposed, workArea);

        Assert.Equal(proposed, result);
    }

    [Fact]
    public void SafeClamp_ProposedPastRightEdge_ClampsLeftSoPanelFitsInside()
    {
        var proposed = new Rect(1900d, 100d, 200d, 200d); // right edge at 2100, past workArea.Right (1920)
        var workArea = new Rect(0d, 0d, 1920d, 1080d);

        var result = WorkspaceLayoutPolicy.SafeClampFreeform(proposed, workArea);

        Assert.Equal(1720d, result.X); // workArea.Right - width = 1920 - 200
        Assert.Equal(200d, result.Width); // size untouched
    }

    /// <summary>Negative-origin work area (a monitor left of/above the primary monitor).</summary>
    [Fact]
    public void SafeClamp_NegativeOriginWorkArea_ClampsRelativeToNegativeOrigin()
    {
        var workArea = new Rect(-1366d, -200d, 1366d, 768d); // secondary monitor entirely left of primary
        var proposed = new Rect(-2000d, -50d, 300d, 200d); // left edge past workArea.Left (-1366)

        var result = WorkspaceLayoutPolicy.SafeClampFreeform(proposed, workArea);

        Assert.Equal(-1366d, result.X); // pinned to workArea.Left
        Assert.Equal(-50d, result.Y); // Y was already inside, untouched
        Assert.Equal(300d, result.Width);
        Assert.Equal(200d, result.Height);
    }

    /// <summary>
    /// A panel larger than the work area on an axis is pinned to that axis's start, never shrunk —
    /// ADR-028 "SafeClamp сокращает позицию, никогда размер".
    /// </summary>
    [Fact]
    public void SafeClamp_PanelLargerThanWorkArea_PinsToStart_NeverShrinksSize()
    {
        var workArea = new Rect(0d, 0d, 800d, 600d);
        var proposed = new Rect(-100d, -50d, 2000d, 2000d); // far larger than the work area on both axes

        var result = WorkspaceLayoutPolicy.SafeClampFreeform(proposed, workArea);

        Assert.Equal(0d, result.X);
        Assert.Equal(0d, result.Y);
        Assert.Equal(2000d, result.Width); // size never shrunk
        Assert.Equal(2000d, result.Height);
    }

    [Fact]
    public void SafeClamp_NonFiniteProposed_ReturnsProposedUnchanged()
    {
        var proposed = new Rect(double.NaN, 0d, 100d, 100d);
        var workArea = new Rect(0d, 0d, 800d, 600d);

        var result = WorkspaceLayoutPolicy.SafeClampFreeform(proposed, workArea);

        Assert.True(double.IsNaN(result.X));
    }

    [Theory]
    [InlineData(-100d, -50d, 200d, 200d)]
    [InlineData(500d, 500d, 200d, 200d)]
    [InlineData(1900d, 1000d, 500d, 500d)]
    public void SafeClamp_NeverChangesSize(double x, double y, double width, double height)
    {
        var proposed = new Rect(x, y, width, height);
        var workArea = new Rect(0d, 0d, 1920d, 1080d);

        var result = WorkspaceLayoutPolicy.SafeClampFreeform(proposed, workArea);

        Assert.Equal(width, result.Width);
        Assert.Equal(height, result.Height);
    }

    // ===================== SafeClamp (grid-aware overload): fix-cycle 1, MAJOR-2 =====================

    /// <summary>
    /// Reproduces the exact MAJOR-2 scenario: a 1366 DIP-wide work area and a 248 DIP-wide (Medium
    /// preset) panel. The plain continuous <see cref="WorkspaceLayoutPolicy.SafeClampFreeform"/> would
    /// clamp to <c>X=1118</c> (off-grid), and the very next <see cref="WorkspaceLayoutPolicy.SnapCoordinate"/>
    /// would then round that UP to <c>1120</c>, pushing <c>Right=1368</c> past the work area's own
    /// <c>Right=1366</c>. The grid-aware overload must instead land on <c>X=1112</c> (<c>Right=1360</c>) —
    /// on-grid AND inside the work area, so no follow-up snap can ever push it back out.
    /// </summary>
    [Fact]
    public void SafeClamp_GridAware_ReproducesMajor2Example_StaysOnGridAndInsideWorkArea()
    {
        var workArea = new Rect(0d, 0d, 1366d, 768d);
        var proposed = new Rect(1118d, 0d, 248d, 200d); // the exact off-grid continuous-clamp result from MAJOR-2

        var result = WorkspaceLayoutPolicy.SafeClamp(proposed, workArea, U, G);

        Assert.Equal(1112d, result.X);
        Assert.True(result.Right <= workArea.Right, $"result.Right ({result.Right}) exceeds workArea.Right ({workArea.Right}).");

        // On-grid: re-snapping the result must be a no-op (this is exactly the property MAJOR-2's bug violated).
        var reSnapped = WorkspaceLayoutPolicy.SnapCoordinate(result.X, workArea.X, U);
        Assert.Equal(result.X, reSnapped);
    }

    [Theory]
    [InlineData(0d, 0d, 1366d, 768d, 200d, 200d)]
    [InlineData(-1366.5d, -200.25d, 1366d, 768d, 248d, 232d)] // negative, fractional origin
    [InlineData(0d, 0d, 800d, 600d, 96d, 96d)]
    public void SafeClamp_GridAware_ResultAlwaysOnGrid(double originX, double originY, double areaWidth, double areaHeight, double panelWidth, double panelHeight)
    {
        var workArea = new Rect(originX, originY, areaWidth, areaHeight);
        var proposed = new Rect(originX + areaWidth - 5d, originY + areaHeight - 5d, panelWidth, panelHeight); // deliberately off-grid, hugging the far corner

        var result = WorkspaceLayoutPolicy.SafeClamp(proposed, workArea, U, G);

        AssertOnGrid(result.X, workArea.X, U);
        AssertOnGrid(result.Y, workArea.Y, U);
    }

    [Theory]
    [InlineData(0d, 0d, 1366d, 768d, 200d, 200d)]
    [InlineData(-1366.5d, -200.25d, 1366d, 768d, 248d, 232d)]
    public void SafeClamp_GridAware_ResultNeverExceedsWorkArea(double originX, double originY, double areaWidth, double areaHeight, double panelWidth, double panelHeight)
    {
        var workArea = new Rect(originX, originY, areaWidth, areaHeight);
        var proposed = new Rect(originX + areaWidth - 5d, originY + areaHeight - 5d, panelWidth, panelHeight);

        var result = WorkspaceLayoutPolicy.SafeClamp(proposed, workArea, U, G);

        Assert.True(result.Right <= workArea.Right + 1e-9);
        Assert.True(result.Bottom <= workArea.Bottom + 1e-9);
    }

    /// <summary>Same "pin to start, never shrink size" rule as the continuous overload — ADR-028 "SafeClamp сокращает позицию, никогда размер".</summary>
    [Fact]
    public void SafeClamp_GridAware_PanelLargerThanWorkArea_PinsToOrigin_NeverShrinksSize()
    {
        var workArea = new Rect(0d, 0d, 800d, 600d);
        var proposed = new Rect(-100d, -50d, 2000d, 2000d);

        var result = WorkspaceLayoutPolicy.SafeClamp(proposed, workArea, U, G);

        Assert.Equal(0d, result.X);
        Assert.Equal(0d, result.Y);
        Assert.Equal(2000d, result.Width);
        Assert.Equal(2000d, result.Height);
    }

    [Fact]
    public void SafeClamp_GridAware_NonFiniteProposed_ReturnsProposedUnchanged()
    {
        var proposed = new Rect(double.NaN, 0d, 100d, 100d);
        var workArea = new Rect(0d, 0d, 800d, 600d);

        var result = WorkspaceLayoutPolicy.SafeClamp(proposed, workArea, U, G);

        Assert.True(double.IsNaN(result.X));
    }

    [Theory]
    [InlineData(-100d, -50d, 200d, 200d)]
    [InlineData(500d, 500d, 200d, 200d)]
    public void SafeClamp_GridAware_NeverChangesSize(double x, double y, double width, double height)
    {
        var proposed = new Rect(x, y, width, height);
        var workArea = new Rect(0d, 0d, 1920d, 1080d);

        var result = WorkspaceLayoutPolicy.SafeClamp(proposed, workArea, U, G);

        Assert.Equal(width, result.Width);
        Assert.Equal(height, result.Height);
    }

    /// <summary>
    /// Fix-cycle 2, NIT-G. The review's literally-suggested repro width (250 DIP) always makes
    /// <see cref="WorkspaceLayoutPolicy.BoundsToSpans"/> round its span UP (raw 32.25, span 33, occupied
    /// extent 256 DIP -- MORE than the real 250), which can only make the OLD span-based clamp MORE
    /// conservative, never overshoot, regardless of work area size -- kept here as a plain regression guard
    /// for that literal value, not as the failing-before-the-fix repro (see the next test for that).
    /// </summary>
    [Fact]
    public void SafeClamp_GridAware_NeverOvershootsWorkArea_WithWidth250()
    {
        var workArea = new Rect(0d, 0d, 1366d, 400d);
        var proposed = new Rect(1112d, 0d, 250d, 200d);

        var result = WorkspaceLayoutPolicy.SafeClamp(proposed, workArea, U, G);

        Assert.True(result.Right <= workArea.Right + 1e-9, $"result.Right ({result.Right}) exceeds workArea.Right ({workArea.Right}).");
    }

    /// <summary>
    /// Fix-cycle 2, NIT-G — the GENUINE overshoot repro. A width just inside the MAJOR-1 tolerance window
    /// (248, 248.5] DIP quantises to span 32 (occupied extent 248 DIP) rather than bumping to 33, by design
    /// (absorbing DPI round-trip noise) -- but the OLD <c>ClampAxisToGrid</c> used that 248 DIP occupied
    /// extent, not the panel's REAL 248.5 DIP width, to decide the max slot. At a work area sized so slot
    /// 139 is an EXACT fit for a 248 DIP-wide panel (no floor-rounding slack to absorb the 0.5 DIP
    /// difference), the OLD formula placed the real, wider rectangle up to 0.5 DIP PAST the work area's
    /// edge. Fixed by computing the max slot from the panel's ACTUAL <c>Width</c> directly.
    /// </summary>
    [Fact]
    public void SafeClamp_GridAware_WidthInsideTolerance_NeverExceedsWorkArea()
    {
        const double width = 248.5d; // > exact span-32 extent (248 DIP) by the MAJOR-1 tolerance's own 0.5 DIP ceiling
        var workArea = new Rect(0d, 0d, 1360d, 400d); // sized so slot 139 is an EXACT fit for a 248 DIP-wide panel
        var proposed = new Rect(1112d, 0d, width, 200d); // already at the tight slot 139 (origin=0, 139*8=1112)

        var result = WorkspaceLayoutPolicy.SafeClamp(proposed, workArea, U, G);

        Assert.True(result.Right <= workArea.Right + 1e-9, $"result.Right ({result.Right}) exceeds workArea.Right ({workArea.Right}).");
    }

    /// <summary>Fix-cycle 2, MINOR-E extension: this overload validates too, even though it no longer reads <c>gap</c> in its own arithmetic (see its remarks).</summary>
    [Fact]
    public void SafeClamp_GridAware_GapNotWholeMultipleOfUnitSize_Throws()
    {
        var proposed = new Rect(0d, 0d, 100d, 100d);
        var workArea = new Rect(0d, 0d, 1920d, 1080d);

        Assert.Throws<ArgumentException>(() => WorkspaceLayoutPolicy.SafeClamp(proposed, workArea, U, 5d));
    }

    // ===================== Equal gap between adjacent GridAligned panels =====================

    /// <summary>
    /// Two panels placed in adjacent workspace-grid slots (ADR-028 "cell + gap" model) must leave EXACTLY
    /// <c>G</c> DIP of empty space between them — the structural guarantee that makes resize anti-overlap
    /// possible for <see cref="PanelGeometryMode.GridAligned"/> panels (ADR-028 closes the ADR-017 backlog
    /// item for this mode specifically because adjacent panel edges always land on the same grid node).
    /// <para>
    /// Fix-cycle 1, MINOR: the "adjacent slot" position was previously computed by hand
    /// (<c>origin.X + panelASpan.WidthUnits * U</c>) instead of through an API — this now goes through
    /// <see cref="WorkspaceLayoutPolicy.SlotToPosition"/> (fix-cycle 1, MAJOR-5(a)), dogfooding the new
    /// position-side slot API this same fix cycle introduces, and making the "next slot along X" intent
    /// explicit rather than an inline formula.
    /// </para>
    /// </summary>
    [Fact]
    public void AdjacentSlots_LeaveExactlyGapBetweenPanels()
    {
        var origin = new Point(100d, 100d);
        var panelASpan = new GridSpan(12, 12);
        var panelBSpan = new GridSpan(20, 20);

        var panelASize = WorkspaceLayoutPolicy.SpansToBounds(panelASpan, U, G);
        var panelA = new Rect(origin, panelASize);

        // Panel B starts at the slot immediately after panel A's span, along X (row 0, same as panel A).
        var panelBPosition = WorkspaceLayoutPolicy.SlotToPosition((panelASpan.WidthUnits, 0), origin, U);
        var panelBSize = WorkspaceLayoutPolicy.SpansToBounds(panelBSpan, U, G);
        var panelB = new Rect(panelBPosition, panelBSize);

        var gapBetween = panelB.Left - panelA.Right;

        Assert.Equal(G, gapBetween);
    }
}
