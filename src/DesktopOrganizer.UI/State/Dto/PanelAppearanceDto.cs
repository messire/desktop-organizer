using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.UI.State.Dto;

/// <summary>
/// Schema-v4 <c>panels[].appearance</c> (ADR-025, docs/PLANS/phase-5-ambient-accent-ui.md §1 D-3/D-5).
/// Mirrors <c>V3ToV4Migration.MapAppearance</c>'s output object field-for-field — see
/// <c>SchemaContractTests</c>. Defaults match <see cref="ChildWindowModel"/>'s own field initializers.
/// </summary>
internal sealed class PanelAppearanceDto
{
    /// <summary>Hex string for the panel's accent color.</summary>
    public string AccentColor { get; set; } = "#6B7280";

    /// <summary>The panel surface's opacity — stored as-is; clamped to <c>[0.35, 1]</c> when rendered.</summary>
    public double SurfaceOpacity { get; set; } = 0.8;

    /// <summary>Which of the ADR-025/ADR-030 surface treatments the panel renders.</summary>
    public PanelSurfaceStyle SurfaceStyle { get; set; } = PanelSurfaceStyle.Ambient;

    /// <summary>Hex string for the panel's title/item text color.</summary>
    public string TextColor { get; set; } = "#DDDDDD";

    /// <summary>The panel's icon size, in DIP.</summary>
    public double IconSize { get; set; } = 48;

    /// <summary>
    /// Schema v5 (ADR-029 Decision 6). Enum member name of <c>IconLayoutPolicy</c>
    /// (<c>"LegacyStretch"</c>/<c>"FixedGap"</c>). Plain <see cref="string"/> — not the real enum type
    /// — for the same "unknown value degrades to default instead of throwing" reason documented on
    /// <see cref="PanelGeometryDto.GeometryMode"/>. Default matches the model's own default and what
    /// an absent field (schema v4 and earlier) means.
    /// </summary>
    public string IconLayoutPolicy { get; set; } = "LegacyStretch";
}
