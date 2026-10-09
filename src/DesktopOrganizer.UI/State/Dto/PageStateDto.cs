using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopOrganizer.UI.State.Dto;

/// <summary>
/// Schema-v6 shape of a single entry in <see cref="PagesStateDto.Items"/> (ADR-043,
/// docs/PLANS/page-display-names.md §2/§3.2): a stable <see cref="Id"/> plus an optional stored
/// display <see cref="Name"/> — the same "stable id + separate display name" shape
/// <see cref="PanelStateDto"/> already uses (<see cref="PanelStateDto.Id"/> + <see cref="PanelStateDto.Title"/>).
/// </summary>
internal sealed class PageStateDto
{
    /// <summary>The page's stable identifier. Never renamed once created (ADR-043).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// The page's stored display name, or <see langword="null"/> when the key is absent on disk — the
    /// only page allowed to have no stored name is <c>"default"</c>, whose unnamed display name is the
    /// localized <c>Page_Default_DisplayName</c> resolved at read time (<c>PagesStateMapper.FromDto</c>),
    /// never persisted as a literal string. <see cref="DesktopOrganizer.UI.State.StateJson"/>'s
    /// <c>WhenWritingNull</c> option already omits this key from the on-disk document whenever it is
    /// <see langword="null"/>, rather than writing an explicit JSON <c>null</c>.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Unknown JSON keys of this object, captured on load and written back verbatim on save (ADR-024
    /// Amendment "Additive revisions"): a field added by a newer additive schema revision survives an edit
    /// by this build. Keys are written exactly as read (no naming policy). <see langword="null"/> when there
    /// are none.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extras { get; set; }
}
