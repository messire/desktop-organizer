using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.UI.Behaviors;

/// <summary>
/// The narrow seam through which the resize/roll-up behaviours reach the ADR-028 workspace grid
/// (System B) without any of them acquiring an <c>IMonitorService</c>/<see cref="PanelGeometryWriter"/>
/// dependency of their own (docs/PLANS/workspace-grid-03-04-integration.md §3.3/§3.4/§3.6, P04).
/// Implemented by <c>Views.ChildWindow</c> (in its <c>ChildWindow.Sizing.cs</c> partial, P04's own
/// file), so the panel window stays the single place that owns the monitor snapshot, the obstacle
/// snapshot and the <see cref="PanelGeometryWriter"/> re-entrancy guard.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why an interface on the window rather than three constructor delegates.</b> Both resize
/// controllers and <see cref="RollUpBehavior"/> are constructed inside <c>ChildWindow</c>'s constructor
/// (<c>ChildWindow.xaml.cs</c>), which P03 is concurrently editing for its own drag-pipeline wiring —
/// the integration plan (§4.2) allocates exactly ONE region of that file to P04
/// (<c>OnModelPropertyChanged</c>) and warns that a second edited region invites a merge conflict.
/// Deriving the host from the <see cref="Window"/> the three behaviours ALREADY receive
/// (<c>window as IPanelSizingGridHost</c>) keeps P04's whole feature out of that file: the production
/// wiring is a cast, and tests inject a fake host through the behaviours' own optional test seams.
/// </para>
/// <para>
/// <b>A null host means <see cref="PanelGridState.Legacy"/>.</b> Every consumer treats "no host"
/// exactly like <see cref="PanelGeometryMode.LegacyFreeform"/> — that is what keeps the legacy resize
/// path bit-for-bit unchanged in the existing headless tests, which construct the controllers over a
/// plain <see cref="Window"/> (or over bare delegates) that does not implement this interface.
/// </para>
/// </remarks>
public interface IPanelSizingGridHost
{
    /// <summary>
    /// P04 fix-cycle 1 (MINOR): the cheap pre-check of
    /// <see cref="PanelGridStateResolver.ParticipatesInGrid"/>, surfaced on the host so a caller reached
    /// from a NON-grid path can skip <see cref="ResolveGridState"/> — and with it the
    /// <c>IMonitorService.GetMonitors()</c> enumeration it performs — entirely for a
    /// <see cref="PanelGeometryMode.LegacyFreeform"/> panel.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The motivating call site is <c>PanelSizingGridSupport.IsGridAligned</c>, which both resize
    /// controllers' <c>SnapWidthToGrid</c> consults on EVERY <c>IconSize</c>/<c>ViewMode</c>/
    /// <c>IconLayoutPolicy</c> change of EVERY panel (§3.7's F-2 gate) — a monitor enumeration per panel
    /// per slider tick, for an answer that is statically <see langword="false"/> for a legacy panel.
    /// </para>
    /// <para>
    /// May ONLY be used as a fast path TOWARDS <see cref="PanelGridState.Legacy"/>, never as a substitute
    /// for <see cref="ResolveGridState"/> when it is <see langword="true"/> — see
    /// <see cref="PanelGridStateResolver.ParticipatesInGrid"/>'s own remarks. Defaulted (rather than
    /// declared abstract) so adding it cannot break an existing implementer: the default is the
    /// conservative <see langword="true"/>, i.e. "ask the resolver", which is exactly the pre-P04-fix
    /// behaviour.
    /// </para>
    /// </remarks>
    bool ParticipatesInGrid => true;

    /// <summary>
    /// Resolves the panel's workspace-grid state for ONE gesture/operation (the once-per-gesture
    /// snapshot contract of <see cref="PanelGridStateResolver.Resolve"/>, §1.3): monitor origin/work
    /// area in <see cref="Window.Left"/>/<see cref="Window.Top"/> space, unit size, gap and the
    /// rolled-up-aware minimum span. Implementations read <c>IsRolledUp</c> from the panel's own model —
    /// callers never pass it, so a collapsed panel automatically gets
    /// <see cref="WorkspaceGridMetrics.MinSpanCollapsed"/>.
    /// </summary>
    /// <param name="boundsWindowSpace">The panel's live bounds, in <c>Window.Left</c>/<c>Top</c> space.</param>
    /// <returns><see cref="PanelGridState.Legacy"/> when the panel is not (or cannot be) grid-aligned this frame.</returns>
    PanelGridState ResolveGridState(Rect boundsWindowSpace);

    /// <summary>
    /// Fills <paramref name="buffer"/> with the OTHER live panels' bounds — the anti-overlap obstacle
    /// snapshot a <see cref="PanelGeometryMode.GridAligned"/> resize takes once per gesture
    /// (§3.4/P04-G3, closing ADR-017's resize backlog item for this mode only). The buffer is cleared
    /// first, mirroring <c>IWindowManager.CollectOtherPanelBounds</c>'s own contract.
    /// </summary>
    /// <param name="buffer">The caller-owned, reused list to fill.</param>
    void CollectResizeObstacles(IList<Rect> buffer);

    /// <summary>
    /// Writes <paramref name="boundsWindowSpace"/> and <paramref name="span"/> onto the window and its
    /// model in one atomic act, through the panel's <see cref="PanelGeometryWriter"/> (§1.5) — the ONLY
    /// way P04 code persists a span, so the <c>OnRenderSizeChanged -&gt; model -&gt; window</c>
    /// re-entrancy guard always covers it (baseline risk 17).
    /// </summary>
    /// <param name="boundsWindowSpace">The panel's bounds, in <c>Window.Left</c>/<c>Top</c> space.</param>
    /// <param name="span">The span describing <paramref name="boundsWindowSpace"/>'s size.</param>
    void CommitGridGeometry(Rect boundsWindowSpace, GridSpan span);

    /// <summary>
    /// P06 fix-cycle MAJOR-4 (adr/ADR-029-icon-layout-policy.md "Fix-цикл P06"): notifies the panel that
    /// a user resize gesture affecting its HEIGHT (<see cref="Interaction.ResizeEdgeParser.AffectsHeight"/>)
    /// has just been committed. This is the single additive seam <see cref="PanelResizeController"/>
    /// (Thumb) and <see cref="NativePanelResizeController"/> (native <c>WM_SIZING</c>) both call through
    /// <see cref="PanelSizingGridSupport.NotifyHeightResizeCommitted"/> at their shared end-of-gesture
    /// point, so neither controller needs its own copy of the "was this a vertical resize" check — a
    /// width-only resize (<see cref="Interaction.ResizeEdge.Left"/>/<see cref="Interaction.ResizeEdge.Right"/>)
    /// never reaches this member. The implementation (<c>Views.ChildWindow.ContentSizing.cs</c>)
    /// transitions <c>ChildWindowModel.SizingMode</c> from <see cref="Models.PanelSizingMode.AutoContent"/>
    /// to <see cref="Models.PanelSizingMode.Manual"/> — Fences-like: an explicit, deliberate vertical
    /// resize is a stronger signal than the passive auto-fit — and is a no-op when the panel is already
    /// <see cref="Models.PanelSizingMode.Manual"/>.
    /// </summary>
    void NotifyHeightResizeCommitted();
}
