using System.Windows;
using DesktopOrganizer.UI.Behaviors;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.Tests.Helpers;

/// <summary>
/// Headless stand-in for <c>Views.ChildWindow</c>'s <see cref="IPanelSizingGridHost"/> implementation
/// (P04, docs/PLANS/workspace-grid-03-04-integration.md §3.3/§3.4) — lets the resize controllers and
/// <c>RollUpBehavior</c> be driven through their real workspace-grid paths without a live
/// <c>ChildWindow</c>, an <c>IMonitorService</c> or a <c>PanelGeometryWriter</c>.
/// </summary>
public sealed class FakePanelSizingGridHost : IPanelSizingGridHost
{
    /// <summary>
    /// The state every <see cref="ResolveGridState"/> call returns while <see cref="MirrorRealResolver"/>
    /// is <see langword="false"/>. Defaults to <see cref="PanelGridState.Legacy"/>.
    /// </summary>
    public PanelGridState State { get; set; } = PanelGridState.Legacy;

    /// <summary>
    /// <see cref="IPanelSizingGridHost.ParticipatesInGrid"/> — the cheap pre-check both
    /// <c>PanelSizingGridSupport.IsGridAligned</c> and <c>PersistSpans</c> short-circuit on (P04
    /// fix-cycle 1, MINOR). Defaults to <see langword="true"/>, i.e. "always ask
    /// <see cref="ResolveGridState"/>", which is the pre-fix behaviour every existing test expects.
    /// </summary>
    public bool ParticipatesInGrid { get; set; } = true;

    /// <summary>
    /// P04 fix-cycle 1 (MINOR): when <see langword="true"/> and <see cref="Model"/> is set,
    /// <see cref="ResolveGridState"/> runs the REAL <see cref="PanelGridStateResolver.Resolve"/> against
    /// <see cref="Monitors"/> instead of returning the constant <see cref="State"/> — including its lazy
    /// span-migration SIDE EFFECT on the model.
    /// </summary>
    /// <remarks>
    /// Without this, no controller-level test could ever observe that side effect (the constant-returning
    /// fake silently hid MAJOR-3: a derived span below the floor was written back and re-derived forever).
    /// Off by default so the existing "hand me exactly this state" tests are unaffected.
    /// </remarks>
    public bool MirrorRealResolver { get; set; }

    /// <summary>
    /// The monitor snapshot <see cref="MirrorRealResolver"/> resolves against — one 1080p primary with a
    /// bottom taskbar, the same shape <see cref="GridAligned"/> describes.
    /// </summary>
    public List<(string DeviceName, Rect Bounds, Rect WorkArea)> Monitors { get; } =
        [(@"\.\DISPLAY1", new Rect(0d, 0d, 1920d, 1080d), new Rect(0d, 0d, 1920d, 1040d))];

    /// <summary>The obstacle rectangles <see cref="CollectResizeObstacles"/> hands out.</summary>
    public List<Rect> Obstacles { get; } = [];

    /// <summary>Every <see cref="CommitGridGeometry"/> call, in order — the span-persistence spy (P04-G6).</summary>
    public List<(Rect Bounds, GridSpan Span)> Commits { get; } = [];

    /// <summary>How many times <see cref="ResolveGridState"/> was called (per-gesture snapshot contract, §1.3).</summary>
    public int ResolveCallCount { get; private set; }

    /// <summary>The <c>boundsWindowSpace</c> argument of the most recent <see cref="ResolveGridState"/> call.</summary>
    public Rect LastResolvedBounds { get; private set; }

    /// <summary>
    /// When set, <see cref="CommitGridGeometry"/> mirrors the span onto this model, the way the real
    /// host's <c>PanelGeometryWriter.Apply</c> does — lets a test drive a full
    /// "gesture writes span -&gt; later operation reads it back" round trip.
    /// </summary>
    public ChildWindowModel? Model { get; set; }

    /// <summary>
    /// A ready-made grid-aligned state: origin <c>(0,0)</c>, a 1920x1040 work area and the production
    /// unit/gap constants — the shape <c>PanelGridStateResolver.Resolve</c> produces for a panel on a
    /// single 1080p monitor with a bottom taskbar.
    /// </summary>
    /// <param name="origin">The workspace-grid origin (work-area top-left), in window space.</param>
    /// <param name="minSpan">The minimum span; defaults to <see cref="WorkspaceGridMetrics.MinSpanExpanded"/>.</param>
    /// <param name="workArea">The monitor work area; defaults to <c>(origin, 1920x1040)</c>.</param>
    public static PanelGridState GridAligned(Point? origin = null, GridSpan? minSpan = null, Rect? workArea = null)
    {
        var o = origin ?? new Point(0d, 0d);
        return new PanelGridState(
            IsGridAligned: true,
            Origin: o,
            WorkArea: workArea ?? new Rect(o.X, o.Y, 1920d, 1040d),
            UnitSize: WorkspaceGridMetrics.UnitSize,
            Gap: WorkspaceGridMetrics.DefaultGap,
            MinSpan: minSpan ?? WorkspaceGridMetrics.MinSpanExpanded,
            MonitorDeviceId: @"\\.\DISPLAY1");
    }

    /// <inheritdoc />
    public PanelGridState ResolveGridState(Rect boundsWindowSpace)
    {
        ResolveCallCount++;
        LastResolvedBounds = boundsWindowSpace;

        return MirrorRealResolver && Model is not null
            ? PanelGridStateResolver.Resolve(Model, boundsWindowSpace, Monitors, Model.IsRolledUp)
            : State;
    }

    /// <inheritdoc />
    public void CollectResizeObstacles(IList<Rect> buffer)
    {
        buffer.Clear();
        foreach (var obstacle in Obstacles)
            buffer.Add(obstacle);
    }

    /// <inheritdoc />
    public void CommitGridGeometry(Rect boundsWindowSpace, GridSpan span)
    {
        Commits.Add((boundsWindowSpace, span));

        if (Model is null) return;

        Model.WidthSpanUnits = span.WidthUnits;
        Model.HeightSpanUnits = span.HeightUnits;
    }

    /// <summary>How many times <see cref="NotifyHeightResizeCommitted"/> was called (P06 fix-cycle MAJOR-4, adr/ADR-029-icon-layout-policy.md "Fix-цикл P06").</summary>
    public int NotifyHeightResizeCommittedCallCount { get; private set; }

    /// <inheritdoc />
    public void NotifyHeightResizeCommitted() => NotifyHeightResizeCommittedCallCount++;
}
