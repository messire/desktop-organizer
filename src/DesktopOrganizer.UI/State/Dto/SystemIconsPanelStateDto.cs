using DesktopOrganizer.Core;

namespace DesktopOrganizer.UI.State.Dto;

/// <summary>
/// Schema-v3 <c>systemIconsPanel</c> (ADR-024 D-9). Replaces
/// <c>SystemIconsPanelManager</c>'s former private <c>PanelDto</c>, now shared through the common
/// <see cref="StateJson"/> options instead of a locally constructed <see cref="System.Text.Json.JsonSerializerOptions"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberate, narrow exception to ADR-024 D-13's "DTOs are internal" rule.</b> Every other DTO
/// under this namespace is <see langword="internal"/> — this one is <see langword="public"/> solely
/// because it is the type of <see cref="IApplicationStateService.SystemIconsPanel"/>, and that
/// interface itself must be public (its implementing types, <c>WindowManager</c>/
/// <c>PageManagerService</c>, are public classes resolved by DI's reflection-based
/// <c>AddSingleton&lt;TService,TImplementation&gt;</c>, which requires a public constructor — and a
/// public constructor cannot take a less-accessible parameter type, CS0051/CS0053). D-13's actual
/// concern — the internal store contract becoming a de facto public import/export format by
/// accident — does not apply to this single section type in isolation: the root
/// <see cref="ApplicationStateDocumentDto"/>, the envelope, and every panel-shaped DTO
/// (<see cref="PanelStateDto"/> and its three sub-DTOs) all stay <see langword="internal"/>.
/// </para>
/// <para>
/// <see cref="AnchorVirtualDesktopId"/> stays a <see cref="string"/>, not a <see cref="Guid"/>?,
/// deliberately: a single malformed value here must degrade to "no anchor" without ever throwing
/// during <c>ApplicationStateDocumentDto</c>'s deserialization and discarding the panels/pages/
/// snapshots sections that happen to share the same document (see
/// <c>SystemIconsPanelManager</c>'s former <c>PanelDto.AnchorVirtualDesktopId</c> remarks for the
/// original rationale, which still applies one layer up now that the whole document is one payload).
/// </para>
/// </remarks>
public sealed class SystemIconsPanelStateDto
{
    /// <summary>Whether the system icons panel is switched on.</summary>
    public bool IsEnabled { get; set; }

    /// <summary>Whether the panel should follow the user across virtual desktops.</summary>
    public bool ShowOnAllVirtualDesktops { get; set; }

    /// <summary>
    /// Enum member name of the panel's <see cref="Models.SystemIconSize"/> (e.g. <c>"Medium"</c>).
    /// Parsed case-sensitively; an invalid or absent value degrades to the model's own default.
    /// </summary>
    public string IconSize { get; set; } = "Medium";

    /// <summary>
    /// String representation of the <see cref="Guid"/> of the virtual desktop the panel anchors to
    /// while <see cref="ShowOnAllVirtualDesktops"/> is <see langword="false"/>, or <see langword="null"/>
    /// if unresolved. A malformed value silently resolves to "no anchor" on load
    /// (<c>SystemIconsPanelStateMapper.ApplyTo</c>); when resolving the anchor, the system's own
    /// "Desktop 1" (R2) always takes priority over this persisted value
    /// (<c>SystemIconsPanelManager.ResolveAnchorDesktopId</c>).
    /// </summary>
    public string? AnchorVirtualDesktopId { get; set; }

    /// <summary>The panel's system-icon items, in display order.</summary>
    public List<SystemIconsPanelItemStateDto> Items { get; set; } = [];
}

/// <summary>
/// One entry of <see cref="SystemIconsPanelStateDto.Items"/>. Public for the same reason as its
/// containing type — see <see cref="SystemIconsPanelStateDto"/>'s remarks.
/// </summary>
public sealed class SystemIconsPanelItemStateDto
{
    /// <summary>Which well-known system icon this entry represents.</summary>
    public SystemIconKind Kind { get; set; }

    /// <summary>Whether this icon is currently shown.</summary>
    public bool IsVisible { get; set; }

    /// <summary>The icon's display order, relative to its siblings.</summary>
    public int Order { get; set; }
}
