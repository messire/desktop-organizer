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
    /// JSON-serialised list of <see cref="DesktopOrganizer.Core.Models.SortingRule"/>. Defaults to <c>"[]"</c>.
    /// </summary>
    string SortingRules { get; set; }

    /// <summary>
    /// JSON-serialised list of <see cref="DesktopOrganizer.Core.Models.LayoutSnapshot"/>. Defaults to <c>"[]"</c>.
    /// </summary>
    string LayoutSnapshots { get; set; }

    /// <summary>
    /// Persists current settings to disk.
    /// </summary>
    void Save();
}
