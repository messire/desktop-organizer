using System.Windows;

namespace DesktopOrganizer.UI.State.Dto;

/// <summary>
/// Schema-v3 <c>panels[].geometry</c> (ADR-024 D-9): position, size, monitor binding, window state
/// and the two boolean flags that decide what is actually visible on screen. Mirrors
/// <c>V2ToV3Migration.GroupPanel</c>'s <c>geometry</c> object field-for-field — see
/// <c>SchemaContractTests</c>.
/// </summary>
/// <remarks>
/// <see cref="GeometryMode"/>/<see cref="WidthSpanUnits"/>/<see cref="HeightSpanUnits"/>/
/// <see cref="SizingMode"/> are schema-v5 additive fields (ADR-028 workspace grid / ADR-029 icon
/// layout policy, docs/PLANS/workspace-grid-migration.md), mirrored field-for-field by
/// <c>V4ToV5Migration</c>. Deliberately plain <see cref="string"/> — not the real
/// <c>DesktopOrganizer.UI.Models.PanelGeometryMode</c>/<c>PanelSizingMode</c> enum types — so an
/// unrecognised/corrupted persisted value degrades to a safe default at deserialization
/// (<see cref="PanelStateMapper.FromDto"/>, via <c>PanelGeometryModeParser</c>/
/// <c>PanelSizingModeParser</c>) instead of a <see cref="System.Text.Json.JsonException"/> thrown by
/// <see cref="System.Text.Json.Serialization.JsonStringEnumConverter"/> for every other enum on this
/// DTO — see docs/PLANS/workspace-grid-migration.md §8
/// <c>Migrate_UnknownEnumValue_FallsBackToDefault</c>.
/// </remarks>
internal sealed class PanelGeometryDto
{
    /// <summary>The panel's left position, in DIP.</summary>
    public int X { get; set; }

    /// <summary>The panel's top position, in DIP.</summary>
    public int Y { get; set; }

    /// <summary>The panel's expanded width, in DIP.</summary>
    public int Width { get; set; }

    /// <summary>The panel's expanded height, in DIP.</summary>
    public int Height { get; set; }

    /// <summary>The device id of the monitor the panel was last placed on, or <see langword="null"/> if unknown.</summary>
    public string? MonitorDeviceId { get; set; }

    /// <summary>The panel window's <see cref="System.Windows.WindowState"/>.</summary>
    public WindowState WindowState { get; set; } = WindowState.Normal;

    /// <summary>Whether the panel is collapsed to its title bar.</summary>
    public bool IsRolledUp { get; set; }

    /// <summary>Whether the panel is currently shown.</summary>
    public bool IsVisible { get; set; } = true;

    /// <summary>
    /// Schema v5 (ADR-028). Enum member name of <c>PanelGeometryMode</c>
    /// (<c>"LegacyFreeform"</c>/<c>"GridAligned"</c>). Default matches the model's own default and
    /// what an absent field (schema v4 and earlier) means.
    /// </summary>
    public string GeometryMode { get; set; } = "LegacyFreeform";

    /// <summary>
    /// Schema v5 (ADR-028). Meaningful only when <see cref="GeometryMode"/> is
    /// <c>"GridAligned"</c> — <see langword="null"/> otherwise, and omitted from the on-disk JSON when
    /// null (<c>StateJson.Options</c>'s <c>WhenWritingNull</c>).
    /// </summary>
    public int? WidthSpanUnits { get; set; }

    /// <summary>Schema v5 (ADR-028). Symmetric to <see cref="WidthSpanUnits"/> for height.</summary>
    public int? HeightSpanUnits { get; set; }

    /// <summary>
    /// Schema v5 (ADR-029 Decision 7). Enum member name of <c>PanelSizingMode</c>
    /// (<c>"Manual"</c>/<c>"AutoContent"</c>). Default matches the model's own default and what an
    /// absent field (schema v4 and earlier) means.
    /// </summary>
    public string SizingMode { get; set; } = "Manual";
}
