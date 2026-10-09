namespace DesktopOrganizer.UI.Behaviors;

/// <summary>
/// Abstracts "is the panel's resize affordance currently active" (docs/PLANS/phase-2-native-panel-sizing.md
/// §1.6, R-9; ADR-022). <see cref="RollUpBehavior"/> is the single point that toggles this — while rolled
/// up, resize must be disabled — without needing to know it is looking at the native <c>WM_SIZING</c>
/// hit-test gate (<see cref="NativeSizingAffordance"/>).
/// </summary>
public interface IResizeAffordance
{
    /// <summary>
    /// Enables or disables the panel's resize affordance. <see langword="false"/> means:
    /// <see cref="Interaction.Native.NativeHitTestMapper"/> must report <see cref="NativeSizingEdge.None"/>
    /// everywhere and the visual-only grips are hidden.
    /// </summary>
    /// <param name="enabled"><see langword="true"/> to allow resizing, <see langword="false"/> to disable it.</param>
    void SetEnabled(bool enabled);
}
