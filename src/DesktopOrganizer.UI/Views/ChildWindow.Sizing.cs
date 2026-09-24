using System.Linq;
using System.Windows.Controls;
using DesktopOrganizer.UI.Behaviors;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;

namespace DesktopOrganizer.UI.Views;

/// <summary>
/// Anti-conflict partial (docs/PLANS/workspace-grid-03-04-integration.md §1.4/§4.2): Stage S created
/// this file empty (a placeholder handler only); P04 owns its contents and implements here both the
/// S/M/L/XL size-preset command (§3.5) and the <see cref="IPanelSizingGridHost"/> seam the resize
/// controllers/<c>RollUpBehavior</c> reach the workspace grid through (§3.3/§3.4/§3.6).
/// </summary>
/// <remarks>
/// Implementing <see cref="IPanelSizingGridHost"/> from THIS partial (rather than wiring three
/// constructor delegates in <c>ChildWindow.xaml.cs</c>) is what keeps P04 out of the constructor region
/// P03 is concurrently editing — see the interface's own remarks. All three members are explicit
/// interface implementations: they are infrastructure for the behaviours this window owns, not part of
/// <c>ChildWindow</c>'s own callable surface.
/// </remarks>
public partial class ChildWindow : IPanelSizingGridHost
{
    /// <summary>
    /// The anti-overlap buffer the "Size" preset command reuses (P04 fix-cycle 1, NIT) — a preset click
    /// is not hot, but allocating a fresh <see cref="List{T}"/> per click for a snapshot that is consumed
    /// and discarded inside the same method is pure garbage; <c>IWindowManager.CollectOtherPanelBounds</c>
    /// clears the buffer itself, so reuse is safe. Deliberately NOT shared with either resize
    /// controller's own buffers: those are per-gesture state this window does not own.
    /// </summary>
    private readonly List<Rect> _presetObstacles = [];

    /// <inheritdoc />
    bool IPanelSizingGridHost.ParticipatesInGrid => PanelGridStateResolver.ParticipatesInGrid(_model);

    /// <inheritdoc />
    PanelGridState IPanelSizingGridHost.ResolveGridState(Rect boundsWindowSpace) => ResolveSizingGridState(boundsWindowSpace);

    /// <inheritdoc />
    void IPanelSizingGridHost.CollectResizeObstacles(IList<Rect> buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        if (_model is null)
        {
            buffer.Clear();
            return;
        }

        _windowManager.CollectOtherPanelBounds(_model.Id, buffer);
    }

    /// <inheritdoc />
    void IPanelSizingGridHost.CommitGridGeometry(Rect boundsWindowSpace, GridSpan span)
        => _geometryWriter.Apply(boundsWindowSpace, span, monitorDeviceId: null);

    /// <inheritdoc />
    /// <remarks>
    /// P06 fix-cycle MAJOR-4: single-line forward to <c>ChildWindow.ContentSizing.cs</c>'s own
    /// <c>OnUserHeightResizeCommitted</c>, which owns the actual <c>SizingMode</c> transition logic — kept
    /// here (rather than in that partial) only because it is the explicit <see cref="IPanelSizingGridHost"/>
    /// implementation, alongside this interface's three other members.
    /// </remarks>
    void IPanelSizingGridHost.NotifyHeightResizeCommitted() => OnUserHeightResizeCommitted();

    /// <summary>
    /// <c>HeaderContextMenu</c>'s "Size" preset submenu items (<c>ChildWindow.xaml</c>) — the clicked
    /// item's <c>Tag</c> names a <see cref="PanelSizePreset"/> member (the same
    /// <c>Tag</c>-plus-<c>Enum.TryParse</c> convention the <c>ResizeOverlay</c> thumbs already use for
    /// <see cref="Interaction.ResizeEdge"/>). §3.5/P04-P1.
    /// </summary>
    /// <remarks>
    /// <b>Gating (ADR-026 "Gating").</b> An explicit menu command, so it is blocked by
    /// <see cref="ILayoutEditModeService.IsLayoutLocked"/> but NOT by
    /// <c>NormalModeLayoutGestures = None/MoveOnly</c> — same rule as "Edit layout"/"Align to workspace
    /// grid". The <c>IsEnabled</c> binding in XAML already hides it; the early return here is the
    /// defence-in-depth half (the same pair <c>EditLayoutMenu_Click</c> uses).
    /// <para>
    /// <b>Rolled-up panels are skipped.</b> Applying a preset height to a collapsed panel would either
    /// expand it behind its own <c>IsRolledUp</c> flag or write the collapsed height into
    /// <c>model.Size</c> (which <c>OnRenderSizeChanged</c> deliberately keeps at the EXPANDED size while
    /// rolled up). Rolling down first is the obvious, discoverable alternative. As of P04 fix-cycle 1
    /// (MV-21) the "Size" submenu is also visually DISABLED in that state — <c>ChildWindow.xaml</c> binds
    /// its <c>IsEnabled</c> to <c>CanEnterEditLayout AND NOT Model.IsRolledUp</c> — so this early return
    /// is the defence-in-depth half rather than the only feedback the user gets.
    /// </para>
    /// <para>
    /// <b>AutoContent is turned off (P04 fix-cycle 1, UX P0).</b> See the <c>SizingMode</c> assignment in
    /// the body.
    /// </para>
    /// </remarks>
    private void SizePresetMenu_Click(object sender, RoutedEventArgs e)
    {
        if (_layoutEditMode.IsLayoutLocked) return;
        if (sender is not MenuItem { Tag: string tag } || !Enum.TryParse<PanelSizePreset>(tag, ignoreCase: false, out var preset)) return;
        if (_model is null || _model.IsRolledUp) return;

        var current = CurrentBoundsWindowSpace();
        if (!double.IsFinite(current.X) || !double.IsFinite(current.Y) || current.Width <= 0d || current.Height <= 0d) return;

        var state = ResolveSizingGridState(current);

        // P04 fix-cycle 1 (UX P0): an explicit preset is a MANUAL sizing decision — leaving the panel in
        // PanelSizingMode.AutoContent (ADR-029 Решение 7) would let the content sizer silently undo the
        // preset's HEIGHT ~300 ms later, when its width-change debounce fires (the preset changes Width,
        // which is exactly what OnWindowSizeChangedForContentSizing listens for). Written BEFORE the
        // geometry below so the PropertyChanged it raises cannot re-enter a fit while the preset is being
        // applied. Idempotent for a panel that was already Manual.
        _model.SizingMode = PanelSizingMode.Manual;

        if (state.IsGridAligned)
        {
            ((IPanelSizingGridHost)this).CollectResizeObstacles(_presetObstacles);

            var target = PanelSizePresetCalculator.ComputeGridAligned(current, preset, state, _presetObstacles);
            var span = WorkspaceLayoutPolicy.BoundsToSpans(target.Size, state.UnitSize, state.Gap);
            _geometryWriter.Apply(target, span, monitorDeviceId: null);
        }
        else
        {
            var target = PanelSizePresetCalculator.ComputeFreeform(current, preset, ResolveWorkAreaWindowSpace(current));
            _geometryWriter.Apply(target, span: null, monitorDeviceId: null);
        }

        _windowManager.SaveWindowsState();
    }

    /// <summary>
    /// The panel's live bounds in <c>Window.Left</c>/<c>Top</c> space, preferring the measured
    /// <see cref="FrameworkElement.ActualWidth"/>/<see cref="FrameworkElement.ActualHeight"/> (the same
    /// "live geometry, never the model" rule <c>WindowManager.CollectOtherPanelBounds</c> follows) and
    /// falling back to <see cref="FrameworkElement.Width"/>/<see cref="FrameworkElement.Height"/> before
    /// the first layout pass.
    /// </summary>
    private Rect CurrentBoundsWindowSpace() => new(
        Left,
        Top,
        ActualWidth > 0d ? ActualWidth : Width,
        ActualHeight > 0d ? ActualHeight : Height);

    /// <summary>
    /// The work area of the monitor this panel is on, in window space — the
    /// <see cref="PanelGeometryMode.LegacyFreeform"/> counterpart of <see cref="PanelGridState.WorkArea"/>
    /// (which only exists for a grid-aligned panel). Monitor choice uses the panel's CENTER, the same
    /// rule <see cref="PanelGridStateResolver.Resolve"/> uses, rather than <c>MonitorService.FindMonitorAt</c>'s
    /// top-left heuristic, which misfires when the top-left corner is outside every monitor.
    /// </summary>
    private Rect ResolveWorkAreaWindowSpace(Rect boundsWindowSpace)
    {
        var monitors = _monitorService.GetMonitors();
        if (monitors is null || monitors.Count == 0) return Rect.Empty;

        var screen = DesktopCoordinateSpace.ToScreenSpace(boundsWindowSpace);
        var center = new Point(screen.X + (screen.Width / 2d), screen.Y + (screen.Height / 2d));

        var monitor = monitors.FirstOrDefault(m => m.Bounds.Contains(center))
                      ?? monitors.FirstOrDefault(m => m.IsPrimary)
                      ?? monitors[0];

        return DesktopCoordinateSpace.ToWindowSpace(monitor.WorkArea);
    }

    /// <summary>
    /// The panel's own <see cref="PanelGridStateResolver.Resolve"/> call — the single place this window
    /// snapshots monitors for the SIZING side (P03 owns the drag side's own snapshot inside
    /// <c>PanelDragMoveBehavior</c>). Primary monitor first, because
    /// <see cref="PanelGridStateResolver.Resolve"/> documents "first entry" as its
    /// no-match/primary fallback and the <c>(DeviceName, Bounds, WorkArea)</c> tuple it takes cannot
    /// carry <c>IsPrimary</c> itself.
    /// </summary>
    private PanelGridState ResolveSizingGridState(Rect boundsWindowSpace)
    {
        // IMonitorService.GetMonitors already swallows native failures and returns an empty list, but a
        // test double (or a future implementation) can still hand back null — treat both exactly like
        // PanelGridStateResolver treats an empty list: fail safe to LegacyFreeform for this frame,
        // because without a monitor origin GridAligned is not even defined (§1.3 rule 2).
        var monitors = _monitorService.GetMonitors();
        if (monitors is null || monitors.Count == 0) return PanelGridState.Legacy;

        var snapshot = monitors
            .OrderByDescending(m => m.IsPrimary)
            .Select(m => (m.DeviceName, m.Bounds, m.WorkArea))
            .ToList();

        return PanelGridStateResolver.Resolve(_model, boundsWindowSpace, snapshot, _model?.IsRolledUp ?? false);
    }
}
