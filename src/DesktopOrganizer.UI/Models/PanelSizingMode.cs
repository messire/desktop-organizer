namespace DesktopOrganizer.UI.Models;

/// <summary>
/// Whether a panel's height follows its content (ADR-029, Decision 7). Persisted per panel.
/// </summary>
public enum PanelSizingMode
{
    /// <summary>
    /// The user owns the size: adding or removing icons never changes the panel height. Default for every
    /// existing panel.
    /// </summary>
    Manual = 0,

    /// <summary>
    /// Grid view only: height is derived one-way from the current width and item count (header + rows),
    /// rounded up to the workspace row span.
    /// </summary>
    AutoContent = 1,
}

/// <summary>
/// Parsing helper for <see cref="PanelSizingMode"/> — same shape as
/// <see cref="PanelSurfaceStyleParser"/>/<see cref="NormalModeLayoutGesturesParser"/>: case-insensitive,
/// never throws, unrecognised/empty/missing input falls back to <see cref="PanelSizingMode.Manual"/> —
/// the product default (ADR-029 Decision 7; docs/PLANS/workspace-grid-migration.md §8
/// <c>Migrate_UnknownEnumValue_FallsBackToDefault</c>).
/// </summary>
public static class PanelSizingModeParser
{
    /// <summary>
    /// Parses <paramref name="value"/> case-insensitively; unrecognised/empty/<see langword="null"/>
    /// input falls back to <see cref="PanelSizingMode.Manual"/> — never throws. Rejects bare numeric
    /// strings outside the defined range (e.g. <c>"7"</c>) and comma-separated flag-like combinations
    /// (e.g. <c>"Manual,AutoContent"</c>) that <see cref="Enum.TryParse{TEnum}(string,bool,out TEnum)"/>
    /// alone would otherwise accept for this non-<c>[Flags]</c> enum — see
    /// <c>PanelGeometryModeParser</c>'s identical fix for the full rationale, including why the comma
    /// must be rejected explicitly rather than relying on <see cref="Enum.IsDefined(Type,object)"/> alone.
    /// </summary>
    /// <param name="value">The candidate string to parse, e.g. from persisted panel state.</param>
    public static PanelSizingMode ParseOrManual(string? value) =>
        value is not null
        && !value.Contains(',')
        && Enum.TryParse<PanelSizingMode>(value, ignoreCase: true, out var parsed)
        && Enum.IsDefined(parsed)
            ? parsed
            : PanelSizingMode.Manual;
}
