using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;

namespace DesktopOrganizer.UI.Diagnostics;

/// <summary>
/// UI-facing facade over <see cref="IDiagnosticsLog"/> for the resize/drag gesture lifecycle
/// (docs/Refactoring/ §3.3). Purely observational — see the concrete <see cref="GestureDiagnostics"/>
/// remarks — every call here is a log-only side effect; none of them may influence gesture behavior.
/// </summary>
/// <remarks>
/// One instance is created per <see cref="Views.ChildWindow"/> (transient — see the DI registration in
/// <see cref="Configuration.DiConfig"/>) and shared between that panel's
/// <see cref="Behaviors.PanelResizeController"/> and <see cref="Behaviors.PanelDragMoveBehavior"/>, so
/// per-gesture throttling/aggregation state never needs to be keyed by panel.
/// </remarks>
public interface IGestureDiagnostics
{
    /// <summary>Recorded once, early in application startup, after panels have loaded.</summary>
    /// <param name="appVersion">The running application's version string.</param>
    /// <param name="osVersion">The host OS version string.</param>
    /// <param name="dpiScale">The primary monitor's DPI scale factor at startup.</param>
    /// <param name="monitorCount">The number of monitors detected at startup.</param>
    /// <param name="panelCount">The number of panels loaded at startup.</param>
    void SessionStarted(string appVersion, string osVersion, double dpiScale, int monitorCount, int panelCount);

    /// <summary>Recorded once, during application shutdown, before the diagnostics log is disposed.</summary>
    void SessionExiting();

    /// <summary>Recorded when a resize-thumb drag gesture begins (<c>Thumb.DragStarted</c>).</summary>
    /// <param name="panelId">The panel's id.</param>
    /// <param name="direction"><c>Interaction.ResizeEdge.ToString()</c> — the edge/corner being resized.</param>
    /// <param name="bounds">The panel's geometry when the gesture began.</param>
    /// <param name="viewMode">The panel's current view mode at gesture start.</param>
    /// <param name="iconSize">The panel's current icon size at gesture start.</param>
    /// <param name="snapEnabled">Whether grid-snapping is enabled for this resize.</param>
    /// <param name="snapCellWidth">The resolved grid cell width used to quantise this gesture.</param>
    /// <param name="snapCellHeight">The resolved grid cell height used to quantise this gesture.</param>
    /// <param name="captureOwner">The type name of the element that owns mouse capture, or <see langword="null"/> if nothing holds capture.</param>
    void ResizeStarted(
        Guid? panelId,
        string direction,
        Rect bounds,
        PanelViewMode viewMode,
        double iconSize,
        bool snapEnabled,
        double snapCellWidth,
        double snapCellHeight,
        string? captureOwner);

    /// <summary>
    /// Recorded on <c>Thumb.DragDelta</c>, throttled to at most once per
    /// <see cref="GestureDiagnostics.DefaultDeltaSampleIntervalMs"/> (the first delta of a gesture is
    /// always recorded). <paramref name="rawWidth"/>/<paramref name="rawHeight"/> are the unquantised
    /// accumulators; <paramref name="applied"/> is the actual (possibly grid-snapped) window geometry
    /// after this delta was applied.
    /// </summary>
    /// <param name="panelId">The panel's id.</param>
    /// <param name="direction"><c>Interaction.ResizeEdge.ToString()</c> — the edge/corner being resized.</param>
    /// <param name="hChange">The horizontal delta reported by this <c>Thumb.DragDelta</c>.</param>
    /// <param name="vChange">The vertical delta reported by this <c>Thumb.DragDelta</c>.</param>
    /// <param name="rawWidth">The unquantised accumulated width for this gesture.</param>
    /// <param name="rawHeight">The unquantised accumulated height for this gesture.</param>
    /// <param name="applied">The actual (possibly grid-snapped) window geometry after this delta was applied.</param>
    void ResizeDelta(
        Guid? panelId,
        string direction,
        double hChange,
        double vChange,
        double rawWidth,
        double rawHeight,
        Rect applied);

    /// <summary>
    /// Recorded whenever a resize gesture actually ends (Phase 1 resize stabilization,
    /// docs/PLANS/phase-1-resize-stabilization.md §3.6, commit C8) — not just on
    /// <c>Thumb.DragCompleted</c> anymore, but for every <c>Interaction.ResizeEndReason</c>
    /// (capture loss, stale-drag self-heal, panel unload/closing, a mid-delta exception, and the
    /// ordinary/canceled mouse-up). <paramref name="canceled"/> is kept for backward compatibility with
    /// the Phase 0 schema (<see langword="true"/> only for <c>Interaction.ResizeEndReason.Canceled</c>);
    /// <paramref name="endReason"/> is the full picture — <c>Interaction.ResizeEndReason.ToString()</c>,
    /// a closed set of names (privacy-safe, see docs/Refactoring/Testing/diagnostics-log-events.md's invariant).
    /// </summary>
    /// <param name="panelId">The panel's id.</param>
    /// <param name="direction">The edge/corner that was being resized.</param>
    /// <param name="canceled">
    /// Mirrors <see cref="System.Windows.Controls.Primitives.DragCompletedEventArgs.Canceled"/> — WPF
    /// raises a canceled <c>DragCompleted</c> when the <see cref="System.Windows.Controls.Primitives.Thumb"/>
    /// loses mouse capture mid-gesture, which is purely observed here, never reacted to.
    /// </param>
    /// <param name="bounds">The panel's geometry when the gesture ended.</param>
    /// <param name="endReason">
    /// <c>Interaction.ResizeEndReason.ToString()</c> — why the gesture ended (a closed set of enum
    /// names, never free text).
    /// </param>
    /// <param name="exceptionType">
    /// When <paramref name="endReason"/> is <c>Failed</c>, the failing exception's
    /// <c>Type.Name</c> — NEVER its <see cref="Exception.Message"/>, which may contain a file
    /// path (privacy invariant, diagnostics-log-events.md). <see langword="null"/> for every other
    /// <paramref name="endReason"/>.
    /// </param>
    void ResizeCompleted(Guid? panelId, string direction, bool canceled, Rect bounds, string endReason, string? exceptionType);

    /// <summary>Recorded when a title-bar drag-to-move gesture begins.</summary>
    /// <param name="panelId">The panel's id.</param>
    /// <param name="startCursor">The cursor's screen position when the gesture began, in physical pixels (<see cref="System.Windows.Media.Visual.PointToScreen(Point)"/>).</param>
    /// <param name="bounds">The panel's geometry when the gesture began, in DIP.</param>
    void DragStarted(Guid? panelId, Point startCursor, Rect bounds);

    /// <summary>Recorded on every title-bar mouse-move while dragging, throttled like <see cref="ResizeDelta"/>.</summary>
    /// <param name="panelId">The panel's id.</param>
    /// <param name="applied">The panel's geometry after this move delta was applied.</param>
    void DragDelta(Guid? panelId, Rect applied);

    /// <summary>Recorded once a title-bar drag-to-move gesture ends (mouse-up or lost capture).</summary>
    /// <param name="panelId">The panel's id.</param>
    /// <param name="bounds">The panel's geometry when the gesture ended.</param>
    void DragCompleted(Guid? panelId, Rect bounds);

    /// <summary>
    /// Recorded from the <c>ResizeOverlay</c>'s bubbling <c>Mouse.GotMouseCapture</c> handler — observes
    /// which element/source actually obtained capture for a resize-thumb gesture.
    /// </summary>
    /// <param name="panelId">The panel's id.</param>
    /// <param name="sourceName">The type name of the element that raised the capture event.</param>
    /// <param name="captureOwner">The type name of the element that now owns mouse capture, or <see langword="null"/> if nothing holds capture.</param>
    void CaptureGot(Guid? panelId, string sourceName, string? captureOwner);

    /// <summary>
    /// Recorded from the <c>ResizeOverlay</c>'s bubbling <c>Mouse.LostMouseCapture</c> handler.
    /// <paramref name="thumbIsDragging"/> observes whether the originating <see cref="System.Windows.Controls.Primitives.Thumb"/>
    /// still considered itself mid-drag at the moment capture was lost — a canceled-gesture signal, not
    /// acted on here.
    /// </summary>
    /// <param name="panelId">The panel's id.</param>
    /// <param name="sourceName">The type name of the element that raised the lost-capture event.</param>
    /// <param name="captureOwner">The type name of the element that now owns mouse capture, or <see langword="null"/> if none.</param>
    /// <param name="thumbIsDragging">Whether the originating <see cref="System.Windows.Controls.Primitives.Thumb"/> still considered itself mid-drag.</param>
    void CaptureLost(Guid? panelId, string sourceName, string? captureOwner, bool thumbIsDragging);

    /// <summary>
    /// Recorded from <see cref="Views.ChildWindow.OnClosing"/>. <paramref name="thumbIsDragging"/>
    /// observes whether the panel is closing while a resize-thumb gesture is still (apparently) active.
    /// </summary>
    /// <param name="panelId">The panel's id.</param>
    /// <param name="thumbIsDragging">Whether a resize-thumb gesture still (apparently) considers itself in progress.</param>
    void PanelClosing(Guid? panelId, bool thumbIsDragging);

    /// <summary>
    /// Recorded when <c>ViewModels.ChildWindowViewModel</c>'s background existence-liveness poll
    /// (<c>RefreshExistence</c>/<c>RefreshItemsLiveness</c>) throws — UX P2 (P08 fix cycle 1,
    /// 2026-09-22): replaces a <c>Console.Error.WriteLine</c> the P00 baseline audit found silently
    /// discarded by the <c>WinExe</c> process (nothing ever reads process stderr), matching the same
    /// treatment already given to the icon-pipeline's own failure points (<c>icon</c> category).
    /// </summary>
    /// <param name="panelId">The panel's id.</param>
    /// <param name="exceptionType">
    /// The failing exception's <c>Type.Name</c> only — NEVER <see cref="Exception.Message"/>,
    /// which may embed a file path (privacy invariant, diagnostics-log-events.md).
    /// </param>
    void ExistenceRefreshFailed(Guid? panelId, string exceptionType);

    /// <summary>
    /// L-4 (review it.2, ADR-046 D-13): recorded when a real <c>IIconLoadQueue</c>/<c>IItemProbeQueue</c>
    /// was supplied to <c>BaseViewModel</c>'s constructor, but the constructing thread had no live
    /// <see cref="System.Threading.SynchronizationContext"/> to deliver through — so the instance silently
    /// fell back to the synchronous legacy pipeline (<c>SynchronousIconLoadQueue</c>/
    /// <c>SynchronousItemProbeQueue</c>) instead. Expected for a headless/test caller; in a production
    /// process it usually means a panel ViewModel was constructed before the UI dispatcher's own
    /// <see cref="System.Threading.SynchronizationContext"/> was installed.
    /// </summary>
    /// <param name="panelId">The panel's id, if already known at construction time (often not yet).</param>
    /// <param name="component">Closed set: <c>"iconLoadQueue"</c> or <c>"itemProbeQueue"</c>.</param>
    void IconPipelineSynchronousFallbackUsed(Guid? panelId, string component);

    /// <summary>
    /// Recorded when the explicit "Align to workspace grid" command refuses to align a panel
    /// (P03 fix-cycle 1 final verification): no usable monitor, or the panel's size has not been measured
    /// yet. Replaces a <c>Console.Error</c> write nobody reads in a WinExe process.
    /// </summary>
    /// <param name="panelId">The panel; scope prefix only, never the title or any path.</param>
    /// <param name="reason">A short reason code (e.g. <c>"Unmeasured"</c>, <c>"NoMonitor"</c>), never free text.</param>
    void AlignToWorkspaceGridRefused(Guid? panelId, string reason);

    /// <summary>
    /// Recorded when the native panel-sizing path's <c>WM_ENTERSIZEMOVE</c> handler observes the system
    /// sizing loop starting (docs/PLANS/phase-2-native-panel-sizing.md §1.7) — the raw wire-level signal,
    /// distinct from (and recorded before) <see cref="ResizeStarted"/>, which is only recorded once
    /// <c>Interaction.ResizeGestureSession.TryBegin</c> actually accepts the gesture.
    /// </summary>
    /// <param name="panelId">The panel's id.</param>
    /// <param name="hwnd">The panel's window handle, as a plain integer (never a formatted pointer string).</param>
    /// <param name="edge">
    /// <c>Services.Abstractions.NativeSizingEdge.ToString()</c> — a closed set of enum names, never free
    /// text (privacy invariant, diagnostics-log-events.md).
    /// </param>
    void SizeMoveEntered(Guid? panelId, long hwnd, string edge);

    /// <summary>
    /// Recorded when the native panel-sizing path's <c>WM_EXITSIZEMOVE</c> handler observes the system
    /// sizing loop ending (docs/PLANS/phase-2-native-panel-sizing.md §1.7) — <paramref name="finalBounds"/>
    /// is the system's own <c>GetWindowRect</c> result (converted to DIP), independent of what
    /// <see cref="ResizeCompleted"/> separately records for the shared cross-path gesture lifecycle.
    /// </summary>
    /// <param name="panelId">The panel's id.</param>
    /// <param name="hwnd">The panel's window handle, as a plain integer.</param>
    /// <param name="finalBounds">The panel's final geometry, in DIP.</param>
    void SizeMoveExited(Guid? panelId, long hwnd, Rect finalBounds);

    /// <summary>
    /// Recorded by <see cref="Services.ILayoutEditModeService"/> on every real
    /// <c>LayoutInteractionMode</c> transition (never for a no-op re-entry into the already-active
    /// mode) — docs/PLANS/phase-6-edit-layout-ux.md §1 D-11. Purely observational, like every other
    /// member of this interface.
    /// </summary>
    /// <param name="from">The mode being left.</param>
    /// <param name="to">The mode being entered.</param>
    /// <param name="source">
    /// <c>Services.LayoutModeChangeSource.ToString()</c> — a closed set of enum names, never free text
    /// (privacy invariant, diagnostics-log-events.md).
    /// </param>
    /// <param name="isLocked">The current <c>IAppSettings.IsLayoutLocked</c> value at the time of the transition.</param>
    void LayoutModeChanged(LayoutInteractionMode from, LayoutInteractionMode to, LayoutModeChangeSource source, bool isLocked);

    /// <summary>
    /// Recorded whenever <c>ChildWindowModel.SizingMode</c> changes as a SIDE EFFECT of something other
    /// than the user's own "Auto-size to content" menu toggle — P06 fix-cycle MAJOR-4
    /// (adr/ADR-029-icon-layout-policy.md "Fix-цикл P06"): today the only such source is a completed,
    /// HEIGHT-affecting resize gesture demoting <see cref="PanelSizingMode.AutoContent"/> back to
    /// <see cref="PanelSizingMode.Manual"/>. Purely observational, like every other member of this
    /// interface — never itself decides whether the transition happens.
    /// </summary>
    /// <param name="panelId">The panel's id.</param>
    /// <param name="from">The <see cref="PanelSizingMode"/> being left.</param>
    /// <param name="to">The <see cref="PanelSizingMode"/> being entered.</param>
    /// <param name="reason">
    /// A closed set of short, code-like names (never free text, privacy invariant,
    /// diagnostics-log-events.md) describing what triggered the transition — currently only
    /// <c>"HeightResizeCommitted"</c>.
    /// </param>
    void SizingModeChanged(Guid? panelId, PanelSizingMode from, PanelSizingMode to, string reason);

    /// <summary>
    /// Recorded once per <c>ChildWindowViewModel.LoadFromModel</c>/Folder-Portal-resync rebuild (ADR-046
    /// §6 item 10, manual-verify M-7.1) with the elapsed UI-thread time of rebuilding the whole item set —
    /// building every <c>FileItem</c> (no file-system/shell access — D-5) plus the immediately following
    /// content-filter/sort pass. This is exactly (and only) the UI-thread cost the "not longer than one
    /// frame" metric cares about; icon loading and liveness probing themselves are asynchronous and never
    /// block this rebuild (see <c>docs/PLANS/background-icon-loading.md</c> §8.4's "honest caveat" about
    /// container generation/layout being a SEPARATE, non-ADR-046 cost this event does not capture).
    /// </summary>
    /// <param name="panelId">The panel's id.</param>
    /// <param name="count">The number of items the panel holds after the rebuild.</param>
    /// <param name="elapsedMicroseconds">Elapsed wall-clock time of the rebuild, in microseconds.</param>
    void PanelItemsBuilt(Guid panelId, int count, long elapsedMicroseconds);
}
