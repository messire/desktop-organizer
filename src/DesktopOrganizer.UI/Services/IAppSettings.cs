namespace DesktopOrganizer.UI.Services;

/// <summary>
/// Provides access to global application settings.
/// </summary>
public interface IAppSettings
{
    /// <summary>
    /// Snap-to-grid cell size in pixels. 1 = no snapping.
    /// </summary>
    int GridSize { get; set; }

    /// <summary>
    /// When <see langword="true"/> (default), resizing a panel snaps its dimensions to the icon-grid
    /// cell size in real time. When <see langword="false"/> the panel resizes pixel-by-pixel.
    /// </summary>
    bool SnapResizeToIconGrid { get; set; }

    /// <summary>
    /// Magnetic pull distance (in pixels) used while dragging a panel: when the panel's raw
    /// position is within this many pixels of a grid line, it snaps to that line. 0 disables the
    /// live drag-time magnet (movement stays free until drop). Default: 12.
    /// </summary>
    /// <remarks>
    /// Not clamped relative to <see cref="GridSize"/>. If set to more than half of
    /// <see cref="GridSize"/>, every position within a cell falls inside the magnet radius of one
    /// grid line or another, so the panel is effectively pulled to a line at all times (equivalent to
    /// unconditional quantisation, no "free" zone between lines). UI should keep this in mind when
    /// exposing the setting (e.g. a slider), but it is not enforced at the model level.
    /// </remarks>
    int GridSnapDistance { get; set; }

    /// <summary>
    /// <see langword="true"/> once the one-time Move-semantics hint (shown the first time a
    /// drag-drop into a panel actually moves a file/folder into app-managed storage) has been
    /// displayed. Defaults to <see langword="false"/>; flips to <see langword="true"/> after the
    /// first show and never resets, so the hint is shown at most once per install.
    /// </summary>
    bool HasShownMoveHint { get; set; }

    /// <summary>
    /// Default background colour (hex) applied to newly created panels.
    /// </summary>
    string DefaultBackgroundColor { get; set; }

    /// <summary>
    /// Default text colour (hex) applied to newly created panels.
    /// </summary>
    string DefaultTextColor { get; set; }

    /// <summary>
    /// Default background opacity (0.0–1.0) applied to newly created panels.
    /// </summary>
    double DefaultOpacity { get; set; }

    /// <summary>
    /// Default icon size (px) applied to newly created panels.
    /// </summary>
    double DefaultIconSize { get; set; }

    /// <summary>
    /// JSON-serialised list of page identifiers. Defaults to <c>["default"]</c>.
    /// </summary>
    string Pages { get; set; }

    /// <summary>
    /// The currently active page identifier.
    /// </summary>
    string CurrentPage { get; set; }

    /// <summary>
    /// JSON-serialised list of <see cref="DesktopOrganizer.Core.Models.LayoutSnapshot"/>. Defaults to <c>"[]"</c>.
    /// </summary>
    string LayoutSnapshots { get; set; }

    /// <summary>
    /// JSON-serialised state of the system icons panel. Defaults to <c>""</c> (use built-in defaults).
    /// </summary>
    string SystemIconsPanel { get; set; }

    /// <summary>
    /// Persists current settings to disk.
    /// </summary>
    void Save();
}
