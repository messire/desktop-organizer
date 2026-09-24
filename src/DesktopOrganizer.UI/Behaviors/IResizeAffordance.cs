namespace DesktopOrganizer.UI.Behaviors;

/// <summary>
/// Abstracts "is the panel's resize affordance currently active" across the two resize paths
/// (docs/PLANS/phase-2-native-panel-sizing.md §1.6, R-9). <see cref="RollUpBehavior"/> is the single
/// point that toggles this — while rolled up, resize must be disabled regardless of which path is
/// active — without needing to know whether it is looking at the WPF <c>Thumb</c> overlay
/// (<see cref="ThumbOverlayAffordance"/>) or the native <c>WM_SIZING</c> hit-test gate
/// (<see cref="NativeSizingAffordance"/>).
/// </summary>
public interface IResizeAffordance
{
    /// <summary>
    /// Enables or disables the panel's resize affordance. <see langword="false"/> means: no <c>Thumb</c>
    /// should be clickable/visible (Thumb path), and/or <see cref="Interaction.Native.NativeHitTestMapper"/>
    /// must report <see cref="NativeSizingEdge.None"/> everywhere (native path).
    /// </summary>
    /// <param name="enabled"><see langword="true"/> to allow resizing, <see langword="false"/> to disable it.</param>
    void SetEnabled(bool enabled);
}
