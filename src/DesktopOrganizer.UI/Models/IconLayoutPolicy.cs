namespace DesktopOrganizer.UI.Models;

/// <summary>
/// How icons are distributed inside a panel's content area (ADR-029). Persisted per panel.
/// </summary>
public enum IconLayoutPolicy
{
    /// <summary>
    /// ADR-018 adaptive layout: gaps of a full row stretch to fill the row, a partial last row keeps the
    /// minimum gap. Kept bit-for-bit for panels that existed before migration.
    /// </summary>
    LegacyStretch = 0,

    /// <summary>
    /// Fixed slot size and fixed horizontal/vertical gaps for one icon configuration: icon positions relative
    /// to the content origin do not depend on panel width, surplus space stays on the right.
    /// </summary>
    FixedGap = 1,
}

/// <summary>
/// Parsing helper for <see cref="IconLayoutPolicy"/> — same shape as
/// <see cref="PanelSurfaceStyleParser"/>/<see cref="NormalModeLayoutGesturesParser"/>: case-insensitive,
/// never throws, unrecognised/empty/missing input falls back to <see cref="IconLayoutPolicy.LegacyStretch"/>
/// — the safe, bit-for-bit-today default for panels that existed before this feature (ADR-029 Decision 6;
/// docs/PLANS/workspace-grid-migration.md §8 <c>Migrate_UnknownEnumValue_FallsBackToDefault</c>).
/// </summary>
public static class IconLayoutPolicyParser
{
    /// <summary>
    /// Parses <paramref name="value"/> case-insensitively; unrecognised/empty/<see langword="null"/>
    /// input falls back to <see cref="IconLayoutPolicy.LegacyStretch"/> — never throws. Rejects bare
    /// numeric strings outside the defined range (e.g. <c>"7"</c>) and comma-separated flag-like
    /// combinations (e.g. <c>"LegacyStretch,FixedGap"</c>) that
    /// <see cref="Enum.TryParse{TEnum}(string,bool,out TEnum)"/> alone would otherwise accept for this
    /// non-<c>[Flags]</c> enum — see <c>PanelGeometryModeParser</c>'s identical fix for the full
    /// rationale, including why the comma must be rejected explicitly rather than relying on
    /// <see cref="Enum.IsDefined(Type,object)"/> alone.
    /// </summary>
    /// <param name="value">The candidate string to parse, e.g. from persisted panel state.</param>
    public static IconLayoutPolicy ParseOrLegacyStretch(string? value) =>
        value is not null
        && !value.Contains(',')
        && Enum.TryParse<IconLayoutPolicy>(value, ignoreCase: true, out var parsed)
        && Enum.IsDefined(parsed)
            ? parsed
            : IconLayoutPolicy.LegacyStretch;
}
