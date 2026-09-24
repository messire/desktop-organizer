namespace DesktopOrganizer.UI.Behaviors;

/// <summary>
/// <see cref="IResizeAffordance"/> for the native <c>WM_SIZING</c>-loop path
/// (docs/PLANS/phase-2-native-panel-sizing.md §1.6, R-9). Holds the single boolean flag that
/// <see cref="NativePanelResizeController"/>'s <c>isResizeEnabled</c> constructor parameter reads on
/// every <c>WM_NCHITTEST</c> — <see cref="RollUpBehavior"/> flips it via <see cref="SetEnabled"/> the
/// same way it flips <see cref="ThumbOverlayAffordance"/> for the Thumb path, without either needing to
/// know which one is actually wired up.
/// </summary>
public sealed class NativeSizingAffordance : IResizeAffordance
{
    // Mirrors ResizeOverlay's default XAML-authored visibility (visible/enabled unless rolled up) so a
    // freshly constructed panel behaves the same regardless of which path is active before the first
    // ApplyRollUp call.
    private bool _enabled = true;

    /// <summary>
    /// Gets a value indicating whether resizing is currently enabled — read by
    /// <see cref="NativePanelResizeController"/>'s <c>isResizeEnabled</c> callback on every hit-test.
    /// </summary>
    public bool IsEnabled => _enabled;

    /// <inheritdoc />
    public void SetEnabled(bool enabled) => _enabled = enabled;
}
