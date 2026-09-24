using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopOrganizer.UI.State;

/// <summary>
/// The single <see cref="JsonSerializerOptions"/> instance used to serialize/deserialize the
/// versioned JSON application-state document's payload DTOs (ADR-024 §2.3): camelCase property
/// names, string enum values, and nulls omitted rather than written — matching exactly what
/// <c>V2ToV3Migration</c> (Services layer) produces, which <c>SchemaContractTests</c> verifies.
/// </summary>
/// <remarks>
/// Every DTO under <c>DesktopOrganizer.UI.State.Dto</c> and <see cref="PanelStateMapper"/> must be
/// serialized/deserialized through this single options instance — never a locally constructed one —
/// so the JSON dialect never silently drifts between the different sections of the document (ADR-024
/// §0.1's original sin: four independent writers, each with its own dialect).
/// </remarks>
internal static class StateJson
{
    /// <summary>
    /// camelCase property names, string enum values (<see cref="JsonStringEnumConverter"/>), and
    /// null-valued properties omitted on write (<see cref="JsonIgnoreCondition.WhenWritingNull"/>).
    /// </summary>
    /// <remarks>
    /// <b>MINOR N-7, fix-cycle 2 (docs/PLANS/workspace-grid-migration.md §10.8): <c>allowIntegerValues:
    /// false</c>.</b> The typed C# enum DTO properties that go through this converter
    /// (<c>PanelGeometryDto.WindowState</c>, <c>PanelContentDto.ViewMode</c>/<c>SortMode</c>,
    /// <c>PanelAppearanceDto.SurfaceStyle</c> — as opposed to the newer ADR-028/029 fields, which are
    /// deliberately plain <see langword="string"/> DTO properties with their own
    /// <c>Enum.TryParse</c>-based fallback parsers, see <c>docs/PLANS/workspace-grid-migration.md</c>
    /// §10.2) already threw <see cref="System.Text.Json.JsonException"/> for an unrecognized JSON
    /// *string* (e.g. <c>"windowState":"Bogus"</c>) — but <see cref="JsonStringEnumConverter"/>'s default
    /// <c>allowIntegerValues: true</c> silently accepted ANY JSON *number* for these fields, including
    /// one with no matching enum member (e.g. <c>"windowState":999</c> deserializing to the
    /// wrong-and-undefined <c>(WindowState)999</c>) — an asymmetry between the two JSON shapes for the
    /// same logical value. <c>allowIntegerValues: false</c> closes it: a JSON number for any of these
    /// fields now throws the same <see cref="System.Text.Json.JsonException"/> the unrecognized-string
    /// case already threw, which is already caught by <c>ApplicationStateService.Load()</c>'s existing
    /// guard (fix-cycle 1) and degrades to a quarantined, empty structural state instead of propagating
    /// a wrong, undefined enum value into <c>ChildWindowModel</c>/WPF's own <c>Window.WindowState</c>
    /// setter (which throws <see cref="System.ComponentModel.InvalidEnumArgumentException"/> for an
    /// undefined value). Zero regression risk to the migration chain: migrations operate on
    /// <see cref="System.Text.Json.Nodes.JsonObject"/> directly (ADR-024 D-1), never through this
    /// <see cref="JsonSerializerOptions"/> instance, and every migration step (<c>V1ToV2Migration</c>/
    /// <c>V2ToV3Migration</c>) already converts a legacy numeric value to its string name — or a
    /// documented default for an out-of-range one — before a document ever reaches the current schema
    /// version this options instance deserializes.
    /// </remarks>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };
}
