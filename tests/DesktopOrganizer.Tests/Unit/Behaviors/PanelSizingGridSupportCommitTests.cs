using System.Windows;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Behaviors;
using DesktopOrganizer.UI.Interaction;
using DesktopOrganizer.UI.Interaction.Native;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.Tests.Unit.Behaviors;

/// <summary>
/// P04 fix-cycle 1, MAJOR-2: <c>PanelSizingGridSupport.PersistSpans</c> — the commit step that has to
/// leave the window, <c>ChildWindowModel.Size</c> and the persisted <see cref="GridSpan"/> describing the
/// SAME rectangle, across the px round trip <c>NativeSizingCoordinateMapper</c> performs at every
/// non-100% DPI scale. Plus the <see cref="IPanelSizingGridHost.ParticipatesInGrid"/> fast path (MINOR).
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class PanelSizingGridSupportCommitTests
{
    private const double U = WorkspaceGridMetrics.UnitSize;
    private const double G = WorkspaceGridMetrics.DefaultGap;

    // 312 x 240 DIP = spans (40, 31) — every edge on the 8 DIP grid.
    private static readonly GridSpan StartSpan = new(40, 31);
    private static readonly Rect StartBounds = new(96d, 200d, 312d, 240d);

    // ── The review's exact repro. A whole-span panel, round-tripped DIP -> px -> DIP with the ±1 px
    // error a real WM_SIZING/WPF layout pass can introduce, must still persist the span it started with.
    // Pre-fix BoundsToSpans (Ceiling, 0.5 DIP tolerance) said 41 for +1 px at 150% (0.667 DIP > 0.5), so
    // the stored span claimed 320 DIP while PanelGeometryWriter wrote model.Size = (int)312.667 = 312. ──
    [Theory]
    [InlineData(1.25, +1)]
    [InlineData(1.25, -1)]
    [InlineData(1.5, +1)]
    [InlineData(1.5, -1)]
    [InlineData(1.75, +1)]
    [InlineData(1.75, -1)]
    [InlineData(1.0, 0)]
    public void APixelOfRoundTripNoise_DoesNotChangeThePersistedSpan(double scale, int pxError)
    {
        var host = GridAlignedHost();

        PanelSizingGridSupport.PersistSpans(host, PerturbByPixels(StartBounds, scale, pxError), ResizeEdge.Right);

        var commit = Assert.Single(host.Commits);
        Assert.Equal(StartSpan, commit.Span);

        // …and the bounds committed are the span's EXACT extent, so window/model/span cannot disagree.
        Assert.Equal(WorkspaceLayoutPolicy.SpansToBounds(StartSpan, U, G), commit.Bounds.Size);
        Assert.Equal(312d, commit.Bounds.Width);
        Assert.Equal(240d, commit.Bounds.Height);
    }

    // ── The ratchet itself: feed each commit's own output back through the px round trip, five gestures
    // in a row. Pre-fix the span grew by one unit per gesture (40 -> 41 -> 42 …), i.e. a panel that never
    // moved on screen inflating by 8 DIP every time the user touched an edge. ───────────────────────────
    [Fact]
    public void FiveConsecutiveCommits_DoNotRatchetTheSpan()
    {
        var host = GridAlignedHost();
        var bounds = StartBounds;

        for (var gesture = 0; gesture < 5; gesture++)
        {
            PanelSizingGridSupport.PersistSpans(host, PerturbByPixels(bounds, 1.5, +1), ResizeEdge.Right);
            bounds = host.Commits[^1].Bounds;
        }

        Assert.Equal(5, host.Commits.Count);
        Assert.All(host.Commits, c => Assert.Equal(StartSpan, c.Span));
        Assert.Equal(StartBounds, bounds);
    }

    // ── The span and the int-truncated model size PanelGeometryWriter derives from the same rectangle
    // must agree — the "три источника не расходятся" half of MAJOR-2. ───────────────────────────────────
    [Theory]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(1.75)]
    public void TheCommittedBounds_SurviveTheIntTruncationPanelGeometryWriterApplies(double scale)
    {
        var host = GridAlignedHost();

        PanelSizingGridSupport.PersistSpans(host, PerturbByPixels(StartBounds, scale, +1), ResizeEdge.Right);

        var commit = Assert.Single(host.Commits);
        var modelSize = new Size((int)commit.Bounds.Width, (int)commit.Bounds.Height);

        Assert.Equal(WorkspaceLayoutPolicy.SpansToBounds(commit.Span, U, G), modelSize);
    }

    // ── A caller with no edge (RollUpBehavior pinning the expanded height) normalises the SIZE only,
    // anchored at the top-left, and never touches position/work area/anti-overlap. ──────────────────────
    [Fact]
    public void WithoutAnEdge_OnlyTheSizeIsNormalised_TopLeftAnchored()
    {
        var host = GridAlignedHost();
        var noisy = PerturbByPixels(StartBounds, 1.5, +1);

        PanelSizingGridSupport.PersistSpans(host, noisy);

        var commit = Assert.Single(host.Commits);
        Assert.Equal(StartSpan, commit.Span);
        Assert.Equal(noisy.X, commit.Bounds.X);
        Assert.Equal(noisy.Y, commit.Bounds.Y);
        Assert.Equal(WorkspaceLayoutPolicy.SpansToBounds(StartSpan, U, G), commit.Bounds.Size);
    }

    // ── MAJOR-1 at the commit step: an oversized final rectangle is SHRUNK from the moving edge, not
    // translated — even if the live frames never saw the wall (Alt was held the whole way). ────────────
    [Fact]
    public void AnOversizedFinalRectangle_IsShrunkFromTheMovingEdge()
    {
        var host = GridAlignedHost();

        PanelSizingGridSupport.PersistSpans(host, new Rect(1600d, 200d, 480d, 240d), ResizeEdge.Right);

        var commit = Assert.Single(host.Commits);
        Assert.Equal(1600d, commit.Bounds.Left);
        Assert.Equal(1920d, commit.Bounds.Right);
        Assert.Equal(new GridSpan(41, 31), commit.Span);
    }

    // ── Anti-overlap is re-applied at commit (symmetric with P03's drop-time hard wall). ──────────────
    [Fact]
    public void AnObstacleIsStillHonouredAtCommit()
    {
        var host = GridAlignedHost();
        host.Obstacles.Add(new Rect(480d, 200d, 200d, 240d));

        PanelSizingGridSupport.PersistSpans(host, new Rect(96d, 200d, 520d, 240d), ResizeEdge.Right);

        var commit = Assert.Single(host.Commits);
        Assert.Equal(96d, commit.Bounds.Left);
        Assert.Equal(480d - G, commit.Bounds.Right);
    }

    // ── MINOR: a LegacyFreeform panel must not pay for a monitor enumeration on every IconSize tick. ──
    [Fact]
    public void IsGridAligned_ShortCircuits_WithoutResolvingAnything_WhenThePanelDoesNotParticipate()
    {
        var host = GridAlignedHost();
        host.ParticipatesInGrid = false;

        Assert.False(PanelSizingGridSupport.IsGridAligned(host, StartBounds));
        Assert.Equal(0, host.ResolveCallCount);
    }

    [Fact]
    public void IsGridAligned_StillResolves_WhenThePanelDoesParticipate()
    {
        var host = GridAlignedHost();

        Assert.True(PanelSizingGridSupport.IsGridAligned(host, StartBounds));
        Assert.Equal(1, host.ResolveCallCount);
    }

    [Fact]
    public void PersistSpans_ShortCircuits_WhenThePanelDoesNotParticipate()
    {
        var host = GridAlignedHost();
        host.ParticipatesInGrid = false;

        PanelSizingGridSupport.PersistSpans(host, StartBounds, ResizeEdge.Right);

        Assert.Empty(host.Commits);
        Assert.Equal(0, host.ResolveCallCount);
    }

    private static FakePanelSizingGridHost GridAlignedHost()
        => new() { State = FakePanelSizingGridHost.GridAligned() };

    /// <summary>
    /// <paramref name="bounds"/> after a DIP -&gt; px -&gt; DIP round trip in which the far edges land
    /// <paramref name="pxError"/> physical pixels off — the error <c>NativeSizingCoordinateMapper.ToPx</c>'s
    /// per-edge rounding is documented to accept (ADR-028 §3.8 "решение: принять").
    /// </summary>
    private static Rect PerturbByPixels(Rect bounds, double scale, int pxError)
    {
        var px = NativeSizingCoordinateMapper.ToPx(bounds, scale);
        var perturbed = new NativeRectPx(px.Left, px.Top, px.Right + pxError, px.Bottom + pxError);

        return NativeSizingCoordinateMapper.ToDip(perturbed, scale);
    }
}
