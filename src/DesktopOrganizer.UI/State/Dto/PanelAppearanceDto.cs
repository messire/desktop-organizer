using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.UI.State.Dto;

/// <summary>
/// Schema-v4 <c>panels[].appearance</c> (ADR-025, docs/PLANS/phase-5-ambient-accent-ui.md §1 D-3/D-5).
/// Mirrors <c>V3ToV4Migration.MapAppearance</c>'s output object field-for-field — see
/// <c>SchemaContractTests</c>. Defaults match <see cref="ChildWindowModel"/>'s own field initializers.
/// </summary>
internal sealed class PanelAppearanceDto
{
    public string AccentColor { get; set; } = "#6B7280";

    public double SurfaceOpacity { get; set; } = 0.8;

    public PanelSurfaceStyle SurfaceStyle { get; set; } = PanelSurfaceStyle.Ambient;

    public string TextColor { get; set; } = "#DDDDDD";

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
