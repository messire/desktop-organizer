namespace DesktopOrganizer.UI.Behaviors;

/// <summary>
/// <see cref="IResizeAffordance"/> for the native <c>WM_SIZING</c>-loop path
/// (docs/PLANS/phase-2-native-panel-sizing.md §1.6, R-9; ADR-022). Holds the single boolean flag that
/// <see cref="NativePanelResizeController"/>'s <c>isResizeEnabled</c> constructor parameter reads on
/// every <c>WM_NCHITTEST</c> — <see cref="RollUpBehavior"/> flips it via <see cref="SetEnabled"/>.
/// </summary>
/// <remarks>
/// ADR-022 phase 0C (docs/PLANS/native-resize-single-path.md, G-3): optionally also drives the panel's
/// visual-only <c>ResizeGripVisuals</c> element (the Edit Layout accent grips, ADR-026 D-6). Resize
/// hit-testing is native (<c>WM_NCHITTEST</c>), so <see cref="SetEnabled"/> mirrors the flag onto that
/// element's <see cref="UIElement.Visibility"/>; it stays <see cref="UIElement.IsHitTestVisible"/> =
/// <see langword="false"/> at all times — hit-testing remains exclusively <c>WM_NCHITTEST</c>'s job. The
/// flag itself is still the only thing the hook reads (no WPF access from the hook callback).
/// </remarks>
public sealed class NativeSizingAffordance : IResizeAffordance
{
    private readonly UIElement? _gripVisuals;

    // Enabled by default (unless rolled up) so a freshly constructed panel resizes before the first
    // ApplyRollUp call.
    private bool _enabled = true;

    /// <summary>
    /// Initializes a new instance of the <see cref="NativeSizingAffordance"/> class.
    /// </summary>
    /// <param name="gripVisuals">
    /// The panel's visual-only <c>ResizeGripVisuals</c> element, or <see langword="null"/> when there is
    /// none (headless use). When supplied it is made visible immediately (the affordance starts enabled)
    /// and forced non-hit-testable.
    /// </param>
    public NativeSizingAffordance(UIElement? gripVisuals = null)
    {
        _gripVisuals = gripVisuals;
        ApplyGripVisuals();
    }

    /// <summary>
    /// Gets a value indicating whether resizing is currently enabled — read by
    /// <see cref="NativePanelResizeController"/>'s <c>isResizeEnabled</c> callback on every hit-test.
    /// </summary>
    public bool IsEnabled => _enabled;

    /// <inheritdoc />
    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        ApplyGripVisuals();
    }

    private void ApplyGripVisuals()
    {
        if (_gripVisuals is null) return;

        _gripVisuals.IsHitTestVisible = false;
        _gripVisuals.Visibility = _enabled ? Visibility.Visible : Visibility.Collapsed;
    }
}
