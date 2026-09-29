namespace DesktopOrganizer.UI.Models;

/// <summary>
/// The outcome of validating or applying a page rename (<c>IPageManager.ValidatePageName</c>/
/// <c>RenamePage</c>, ADR-043, docs/PLANS/page-display-names.md §3.1/§4.2). A closed set — never a free-form
/// error message — so the UI layer, not the service, owns every localized string shown to the user.
/// </summary>
public enum PageRenameResult
{
    /// <summary>The name was accepted and (for <c>RenamePage</c>) applied and persisted.</summary>
    Renamed,

    /// <summary>The trimmed name is ordinal-equal to the page's current effective name — a no-op.</summary>
    Unchanged,

    /// <summary>The page id does not exist.</summary>
    NotFound,

    /// <summary>The trimmed name is empty for a non-<c>"default"</c> page (empty for <c>"default"</c> is a valid reset — see <see cref="Renamed"/>/<see cref="Unchanged"/> instead).</summary>
    Empty,

    /// <summary>The trimmed name is longer than the maximum allowed length (<c>PageNameRules.MaxLength</c>).</summary>
    TooLong,

    /// <summary>The trimmed name matches (ordinal, case-insensitive) another page's effective display name.</summary>
    Duplicate,

    /// <summary>The trimmed name contains a control character (e.g. a pasted line break).</summary>
    InvalidCharacters,
}
