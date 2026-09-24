namespace DesktopOrganizer.UI.Behaviors;

/// <summary>
/// <see cref="IResizeAffordance"/> for the legacy WPF <c>Thumb</c> path — wraps
/// <c>ChildWindow.xaml</c>'s <c>ResizeOverlay</c> element. Consolidates the
/// <c>Visibility</c>/<c>IsHitTestVisible</c> pair <see cref="RollUpBehavior"/> previously set on it
/// directly (docs/PLANS/phase-2-native-panel-sizing.md §1.6) — behavior is unchanged from before this
/// abstraction was introduced, this is purely a routing indirection.
/// </summary>
public sealed class ThumbOverlayAffordance : IResizeAffordance
{
    private readonly UIElement _resizeOverlay;

    /// <summary>
    /// Initializes a new instance of the <see cref="ThumbOverlayAffordance"/> class.
    /// </summary>
    /// <param name="resizeOverlay">The panel's <c>ResizeOverlay</c> element (8 <c>Thumb</c>s).</param>
    public ThumbOverlayAffordance(UIElement resizeOverlay)
    {
        ArgumentNullException.ThrowIfNull(resizeOverlay);
        _resizeOverlay = resizeOverlay;
    }

    /// <inheritdoc />
    public void SetEnabled(bool enabled)
    {
        // Visibility.Collapsed already stops the overlay from receiving input on its own (WPF does not
        // hit-test collapsed elements), but IsHitTestVisible is set explicitly too — the single point
        // R-9 relies on to guarantee the 8 Thumbs never intercept a click while the native path owns
        // resize (docs/PLANS/phase-2-native-panel-sizing.md §1.6).
        _resizeOverlay.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        _resizeOverlay.IsHitTestVisible = enabled;
    }
}
