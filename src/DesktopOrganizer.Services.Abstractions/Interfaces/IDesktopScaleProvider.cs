namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// Single source of truth for px↔DIP conversion of desktop-layer geometry under PerMonitorV2
/// (release-hardening F, ADR-037): owns the current <see cref="DesktopScaleModel"/> snapshot and rebuilds
/// it when monitors, DPI or the desktop layer mode change.
/// </summary>
/// <remarks>
/// <para>
/// UI-thread affine: every member is meant to be called from the WPF UI thread, and
/// <see cref="Changed"/> is raised synchronously on the thread that called <see cref="Refresh"/> or
/// <see cref="ReportPanelWindow"/>. No member ever throws — a failed rebuild keeps the previous snapshot.
/// </para>
/// <para>
/// Session state only: the mode and the scales are never persisted (persistence freeze, ADR-024).
/// </para>
/// </remarks>
public interface IDesktopScaleProvider
{
    /// <summary>
    /// Raised after the snapshot was rebuilt and the new one differs from the old one
    /// (<see cref="DesktopScaleModel.IsEquivalentTo"/>). Never raised for a rebuild that changed nothing,
    /// nor for the very first build.
    /// </summary>
    event EventHandler<DesktopScaleChangedEventArgs>? Changed;

    /// <summary>
    /// Gets the current snapshot. Cached: built lazily on first access and rebuilt only by
    /// <see cref="Refresh"/>/<see cref="ReportPanelWindow"/>. Never <see langword="null"/>.
    /// </summary>
    DesktopScaleModel Current { get; }

    /// <summary>Gets the desktop layer mode of <see cref="Current"/>.</summary>
    DesktopLayerMode Mode { get; }

    /// <summary>
    /// Reports a panel window after it was embedded (<see cref="IDesktopIntegrationService.EmbedInDesktop"/>),
    /// so the mode is taken from the fact (the panel's root window) rather than predicted; then rebuilds
    /// the snapshot like <see cref="Refresh"/>. <see cref="IntPtr.Zero"/> is ignored.
    /// </summary>
    /// <param name="hwnd">Handle of a panel window.</param>
    void ReportPanelWindow(IntPtr hwnd);

    /// <summary>
    /// Rebuilds the snapshot from the current monitors, DPI values and desktop layer mode. Call on
    /// <c>WM_DISPLAYCHANGE</c>/<c>WM_SETTINGCHANGE</c>/<c>WM_DPICHANGED</c>, after an Explorer-restart
    /// recovery re-embedded the panels, and from a window's <c>DpiChanged</c>.
    /// </summary>
    void Refresh();

    /// <summary>
    /// Opens a batch scope (M10, release-hardening 1.0 review): every <see cref="ReportPanelWindow"/>/
    /// <see cref="Refresh"/> call made while a scope returned by this method is still open only marks the
    /// snapshot dirty — it neither rebuilds it nor raises <see cref="Changed"/> — until the scope is
    /// disposed. Disposing the OUTERMOST open scope then performs at most ONE rebuild (only if something
    /// was actually marked dirty while any scope was open) and, only if that rebuild's result actually
    /// differs from the pre-batch snapshot, raises <see cref="Changed"/> exactly once.
    /// </summary>
    /// <remarks>
    /// Exists for <c>WindowManager.RecoverDesktopLayer</c> (via <c>ShellRecoveryCoordinator</c>), which
    /// re-embeds every panel in a loop, each panel in turn calling <see cref="ReportPanelWindow"/> — each
    /// call, individually, is a legitimate reason to rebuild, but rebuilding (and potentially raising
    /// <see cref="Changed"/>, which every subscriber, including every live panel window, reacts to
    /// synchronously) after EVERY panel in that loop let a mid-loop <see cref="Changed"/> write geometry
    /// against a panel window later in the same loop that had already been torn down (destroyed, not yet
    /// rebuilt) — see <c>ChildWindow.ShellRecovery.cs</c>'s own remarks. Scopes nest: an inner
    /// <see cref="BeginBatch"/> call while an outer one is still open shares the same dirty flag and defers
    /// to the outer scope's own disposal.
    /// </remarks>
    /// <returns>A disposable scope handle — dispose it to close the batch.</returns>
    IDisposable BeginBatch();
}
