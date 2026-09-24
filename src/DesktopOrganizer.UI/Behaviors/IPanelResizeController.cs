using DesktopOrganizer.UI.Interaction;

namespace DesktopOrganizer.UI.Behaviors;

/// <summary>
/// Minimal shared contract between the legacy WPF <c>Thumb</c>-driven <see cref="PanelResizeController"/>
/// and the native <c>WM_SIZING</c>-loop-driven <see cref="NativePanelResizeController"/>
/// (docs/PLANS/phase-2-native-panel-sizing.md §1.6) — lets <see cref="Views.ChildWindow"/> call into
/// whichever path <see cref="Interaction.Native.NativeSizingSwitch"/> selected without knowing which one
/// is actually active.
/// </summary>
public interface IPanelResizeController
{
    /// <summary>
    /// Unconditionally ends whatever resize gesture is currently active with <paramref name="reason"/> —
    /// the entry point for callers outside the normal gesture flow (<see cref="Views.ChildWindow.OnClosing"/>,
    /// <c>Unloaded</c>). A no-op if no gesture is active.
    /// </summary>
    /// <param name="reason">Why the gesture is ending.</param>
    void AbortResize(ResizeEndReason reason);

    /// <summary>
    /// Snaps the window width to the nearest whole column in Grid mode — a standalone, drop-time
    /// convenience snap (e.g. after an icon-size change), independent of any in-progress resize gesture.
    /// No-op in List mode or when snap is disabled.
    /// </summary>
    void SnapWidthToGrid();
}
