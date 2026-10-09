using System.Text.Json;
using System.Text.Json.Serialization;
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

    /// <summary>
    /// Member name of <see cref="PanelSurfaceStyle"/> (which of the ADR-025/ADR-030 surface treatments the panel
    /// renders). Plain <see cref="string"/> for the same reason as <see cref="PanelGeometryDto.GeometryMode"/>:
    /// an unknown member written by a newer build degrades to the default instead of throwing.
    /// </summary>
    public string SurfaceStyle { get; set; } = nameof(PanelSurfaceStyle.Ambient);

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

    /// <summary>
    /// Unknown JSON keys of this object, captured on load and written back verbatim on save (ADR-024
    /// Amendment "Additive revisions"): a field added by a newer additive schema revision survives an edit
    /// by this build. Keys are written exactly as read (no naming policy). <see langword="null"/> when there
    /// are none.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extras { get; set; }
}
