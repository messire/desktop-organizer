namespace DesktopOrganizer.UI.Models;

/// <summary>
/// How a panel's window bounds relate to the workspace panel grid (ADR-028). Persisted per panel;
/// a missing value in stored state always means <see cref="LegacyFreeform"/> so existing layouts never
/// jump on first launch after an upgrade.
/// </summary>
public enum PanelGeometryMode
{
    /// <summary>
    /// Pre-ADR-028 behavior: free position/size, optional legacy <c>GridSize</c> magnet and
    /// <c>SnapResizeToIconGrid</c> width snapping. Default for every panel that existed before migration.
    /// </summary>
    LegacyFreeform = 0,

    /// <summary>
    /// Position snaps to the work-area-origin workspace grid of the panel's monitor and size is an integer
    /// span of workspace units; icon size and view mode never change the bounds.
    /// </summary>
    GridAligned = 1,
}

/// <summary>
/// Parsing helper for <see cref="PanelGeometryMode"/> — same shape as
/// <see cref="PanelSurfaceStyleParser"/>/<see cref="NormalModeLayoutGesturesParser"/> (ADR-025 D-10
/// precedent): case-insensitive, never throws, unrecognised/empty/missing input falls back to
/// <see cref="PanelGeometryMode.LegacyFreeform"/> — the same safe default a JSON document that never
/// had this field (schema v4 and earlier, or a corrupted/hand-edited v5 value) already gets from the
/// enum's own <see langword="default"/> value (ADR-028; docs/PLANS/workspace-grid-migration.md §8
/// <c>Migrate_UnknownEnumValue_FallsBackToDefault</c>).
/// </summary>
public static class PanelGeometryModeParser
{
    /// <summary>
    /// Parses <paramref name="value"/> case-insensitively; unrecognised/empty/<see langword="null"/>
    /// input falls back to <see cref="PanelGeometryMode.LegacyFreeform"/> — never throws. Two extra
    /// guards (fix-cycle 1 MINOR finding) reject inputs
    /// <see cref="Enum.TryParse{TEnum}(string,bool,out TEnum)"/> alone would otherwise accept for a
    /// non-<c>[Flags]</c> enum: (1) a bare numeric string outside the defined range (e.g. <c>"7"</c>,
    /// <c>"-1"</c>) parses to an undefined member value without complaint — rejected by
    /// <see cref="Enum.IsDefined(Type,object)"/>; (2) a comma-separated list (e.g.
    /// <c>"LegacyFreeform,GridAligned"</c>) parses as a bitwise-OR combination even though this enum was
    /// never meant to be combinable — <see cref="Enum.IsDefined(Type,object)"/> alone does NOT catch
    /// this for a two-member <c>{0, 1}</c> enum, because OR-ing both members together always lands
    /// exactly on the higher member's own value (a coincidence of this enum's specific member count,
    /// not something to rely on), so the comma is rejected explicitly up front instead. Both are
    /// hand-edited/corrupted-file inputs that must degrade to the safe default, not silently produce a
    /// value that never appeared as a single named member in the document.
    /// </summary>
    /// <param name="value">The candidate string to parse, e.g. from persisted panel state.</param>
    public static PanelGeometryMode ParseOrLegacyFreeform(string? value) =>
        value is not null
        && !value.Contains(',')
        && Enum.TryParse<PanelGeometryMode>(value, ignoreCase: true, out var parsed)
        && Enum.IsDefined(parsed)
            ? parsed
            : PanelGeometryMode.LegacyFreeform;
}
