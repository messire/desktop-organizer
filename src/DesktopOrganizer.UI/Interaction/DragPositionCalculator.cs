namespace DesktopOrganizer.UI.Interaction;

/// <summary>
/// Pure px-anchored position math for the live title-bar drag gesture
/// (<see cref="Behaviors.PanelDragMoveBehavior"/>, release-hardening F §5 #17, ADR-037): resolves the
/// panel's raw (unsnapped) desktop-DIP top-left from a physical-pixel cursor delta, so a drag that
/// crosses a monitor boundary at a different effective DPI does not jump — the existing
/// snap/clamp/anti-overlap pipeline (<c>GridSnap</c>/<c>PanelCollision</c>/<c>SnapGuideCalculator</c>/
/// <c>PanelDragPipeline</c>) then runs, unchanged, on the desktop-DIP result.
/// </summary>
internal static class DragPositionCalculator
{
    /// <summary>
    /// Anchors the drag at <paramref name="startTopLeftPx"/> (the panel's own top-left, in physical
    /// screen pixels, at the moment the gesture started) and applies the physical-pixel cursor delta
    /// between <paramref name="startScreenPx"/> and <paramref name="currentScreenPx"/> — both are
    /// PHYSICAL pixels, so the delta needs no DPI conversion of its own — then converts the resulting
    /// raw physical-pixel top-left into desktop-DIP through <paramref name="scale"/>, which resolves the
    /// correct monitor (and, on an anchored snapshot, its own scale) for wherever the cursor now is.
    /// </summary>
    /// <param name="startTopLeftPx">The panel's top-left when the gesture started, in physical screen pixels.</param>
    /// <param name="startScreenPx">The cursor's screen position when the gesture started, in physical pixels.</param>
    /// <param name="currentScreenPx">The cursor's current screen position, in physical pixels.</param>
    /// <param name="scale">The current desktop-scale snapshot.</param>
    /// <returns>The panel's raw (unsnapped) top-left, in desktop-DIP.</returns>
    public static Point ResolveDesktopTopLeft(Point startTopLeftPx, Point startScreenPx, Point currentScreenPx, DesktopScaleModel scale)
    {
        var rawPx = new Point(
            startTopLeftPx.X + (currentScreenPx.X - startScreenPx.X),
            startTopLeftPx.Y + (currentScreenPx.Y - startScreenPx.Y));

        return scale.PxToDip(rawPx.X, rawPx.Y);
    }
}
