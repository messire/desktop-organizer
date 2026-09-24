using DesktopOrganizer.Core;

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
    /// When <see langword="true"/>, panels resize via the native <c>WM_SIZING</c> loop
    /// (<see cref="DesktopOrganizer.UI.Behaviors.NativePanelResizeController"/>, docs/PLANS/phase-2-native-panel-sizing.md)
    /// instead of the legacy WPF <c>Thumb</c>-driven path
    /// (<see cref="DesktopOrganizer.UI.Behaviors.PanelResizeController"/>). Defaults to
    /// <see langword="false"/> so existing behavior is unchanged unless explicitly opted in — the path
    /// is decided once per panel, at window creation, so a change here only takes effect after the app
    /// restarts (or, for already-open panels, after they are re-created). Can be overridden at runtime
    /// via the <c>--native-sizing</c>/<c>--no-native-sizing</c> command-line flags or the
    /// <c>DESKTOPORGANIZER_NATIVESIZING</c> environment variable — see
    /// <see cref="DesktopOrganizer.UI.Interaction.Native.NativeSizingSwitch"/>.
    /// </summary>
    bool NativePanelSizing { get; set; }

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
    /// Seed for <see cref="DesktopOrganizer.UI.Models.ChildWindowModel.AccentColor"/> (hex) applied to
    /// newly created panels. The <c>DefaultBackgroundColor</c> key name predates ADR-025's split of
    /// "background" into a neutral surface fill plus a semantic accent colour (D-3) — renaming this
    /// <c>user.config</c> key was judged not worth a second, non-JSON migration front (ADR-025 D-10);
    /// the mismatch between the config key's name and what it now seeds is tracked as DO-014.
    /// </summary>
    string DefaultBackgroundColor { get; set; }

    /// <summary>
    /// Default text colour (hex) applied to newly created panels.
    /// </summary>
    string DefaultTextColor { get; set; }

    /// <summary>
    /// Default surface opacity (0.0–1.0) applied to newly created panels' <see cref="DesktopOrganizer.UI.Models.ChildWindowModel.SurfaceOpacity"/>.
    /// </summary>
    double DefaultOpacity { get; set; }

    /// <summary>
    /// Default icon size (px) applied to newly created panels.
    /// </summary>
    double DefaultIconSize { get; set; }

    /// <summary>
    /// Default <see cref="DesktopOrganizer.UI.Models.PanelSurfaceStyle"/> (serialised as its enum member
    /// name, e.g. <c>"Ambient"</c>) applied to newly created panels' <c>SurfaceStyle</c>. New setting
    /// added alongside <see cref="DefaultBackgroundColor"/>/<see cref="DefaultOpacity"/> for ADR-025
    /// D-10 — unlike those, this key's name matches its meaning from day one. Parse with
    /// <see cref="DesktopOrganizer.UI.Models.PanelSurfaceStyleParser.ParseOrAmbient"/>, which never
    /// throws on an unrecognised/empty value. Default: <c>"Ambient"</c>.
    /// </summary>
    string DefaultSurfaceStyle { get; set; }

    /// <summary>
    /// Default <see cref="DesktopOrganizer.UI.Models.PanelGeometryMode"/> (serialised as its enum member
    /// name, e.g. <c>"GridAligned"</c>) applied to newly created panels' <c>GeometryMode</c> (ADR-028;
    /// docs/PLANS/workspace-grid-migration.md). Existing panels are never affected by this setting — they
    /// keep whatever <c>GeometryMode</c> the v4→v5 migration assigned them (always
    /// <see cref="DesktopOrganizer.UI.Models.PanelGeometryMode.LegacyFreeform"/>). Parse with
    /// <see cref="DesktopOrganizer.UI.Models.PanelGeometryModeParser.ParseOrLegacyFreeform"/>, which never
    /// throws on an unrecognised/empty value. Default: <c>"GridAligned"</c> (ADR-028 "Открытые вопросы",
    /// Вопрос 3 = B — resolved 2026-09-22).
    /// </summary>
    string DefaultPanelGeometryMode { get; set; }

    /// <summary>
    /// Default <see cref="DesktopOrganizer.UI.Models.IconLayoutPolicy"/> (serialised as its enum member
    /// name, e.g. <c>"FixedGap"</c>) applied to newly created panels' <c>IconLayoutPolicy</c> (ADR-029
    /// Decision 6). Existing panels are never affected by this setting — they keep whatever
    /// <c>IconLayoutPolicy</c> the v4→v5 migration assigned them (always
    /// <see cref="DesktopOrganizer.UI.Models.IconLayoutPolicy.LegacyStretch"/>). Parse with
    /// <see cref="DesktopOrganizer.UI.Models.IconLayoutPolicyParser.ParseOrLegacyStretch"/>, which never
    /// throws on an unrecognised/empty value. Default: <c>"FixedGap"</c>.
    /// </summary>
    string DefaultIconLayoutPolicy { get; set; }

    /// <summary>
    /// <see langword="true"/> when the user has fixed the current panel geometry ("Lock layout",
    /// Fences-parity, docs/PLANS/phase-6-edit-layout-ux.md §1 D-3). Blocks panel move/resize and entry
    /// into <see cref="DesktopOrganizer.UI.Services.ILayoutEditModeService.Mode"/>'s
    /// <c>EditLayout</c> value; does <b>not</b> block roll-up, rename, selection, launching content, or
    /// the internal grid re-normalisation <c>SnapWidthToGrid()</c> performs on load/icon-size change
    /// (D-3's table — those are not "user geometry edits"). Same persistence class as
    /// <see cref="SnapResizeToIconGrid"/>. Default: <see langword="false"/>.
    /// </summary>
    bool IsLayoutLocked { get; set; }

    /// <summary>
    /// Which panel-move/resize gestures stay available in <c>LayoutInteractionMode.Normal</c> (the
    /// default, content-priority mode), serialised as its
    /// <see cref="DesktopOrganizer.UI.Models.NormalModeLayoutGestures"/> enum member name — same
    /// string-not-int convention as <see cref="DefaultSurfaceStyle"/>, so reordering the enum can never
    /// silently change a persisted user's setting. Parse with
    /// <see cref="DesktopOrganizer.UI.Models.NormalModeLayoutGesturesParser.ParseOrMoveOnly"/>, which
    /// never throws on an unrecognised/empty value (docs/PLANS/phase-6-edit-layout-ux.md §1 D-2).
    /// Default: <c>"MoveOnly"</c>.
    /// </summary>
    string NormalModeLayoutGestures { get; set; }

    /// <summary>
    /// JSON-serialised list of <see cref="DesktopOrganizer.UI.Models.ChildWindowModel"/> (panels).
    /// Defaults to <c>""</c> (no panels persisted yet).
    /// </summary>
    /// <remarks>
    /// <b>Legacy, frozen (ADR-024 D-10).</b> No longer written by <c>WindowManager</c> — panels now
    /// live in the versioned JSON application-state document
    /// (<c>DesktopOrganizer.UI.State.IApplicationStateService</c>, ADR-024 C6). This property, and the
    /// four below it, exist solely so <c>LegacySettingsImporter</c> (ADR-024 D-7 rung 5) can read the
    /// frozen <c>user.config</c> values it migrates from a fresh-install-with-no-JSON-yet scenario,
    /// without depending on <c>Properties.Settings</c> directly. Never read or written by anything
    /// else after this phase — see ADR-024 §1 D-10/D-11.
    /// </remarks>
    [Obsolete("legacy, only for LegacySettingsImporter, see ADR-024 D-10")]
    string ChildWindowsState { get; set; }

    /// <summary>JSON-serialised list of page identifiers. Defaults to <c>["default"]</c>.</summary>
    /// <remarks>Legacy, frozen — see <see cref="ChildWindowsState"/>'s remarks.</remarks>
    [Obsolete("legacy, only for LegacySettingsImporter, see ADR-024 D-10")]
    string Pages { get; set; }

    /// <summary>The currently active page identifier.</summary>
    /// <remarks>Legacy, frozen — see <see cref="ChildWindowsState"/>'s remarks.</remarks>
    [Obsolete("legacy, only for LegacySettingsImporter, see ADR-024 D-10")]
    string CurrentPage { get; set; }

    /// <summary>JSON-serialised list of <see cref="LayoutSnapshot"/>. Defaults to <c>"[]"</c>.</summary>
    /// <remarks>Legacy, frozen — see <see cref="ChildWindowsState"/>'s remarks.</remarks>
    [Obsolete("legacy, only for LegacySettingsImporter, see ADR-024 D-10")]
    string LayoutSnapshots { get; set; }

    /// <summary>JSON-serialised state of the system icons panel. Defaults to <c>""</c> (use built-in defaults).</summary>
    /// <remarks>Legacy, frozen — see <see cref="ChildWindowsState"/>'s remarks.</remarks>
    [Obsolete("legacy, only for LegacySettingsImporter, see ADR-024 D-10")]
    string SystemIconsPanel { get; set; }

    /// <summary>
    /// Persists current settings to disk.
    /// </summary>
    void Save();
}
