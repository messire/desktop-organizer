namespace DesktopOrganizer.UI.Services;

/// <summary>
/// Manages named page layouts (sets of panels). Switching pages hides panels of the
/// outgoing page and shows panels of the incoming page.
/// </summary>
public interface IPageManager
{
    /// <summary>Ordered list of all page identifiers.</summary>
    IReadOnlyList<string> Pages { get; }

    /// <summary>Identifier of the currently active page.</summary>
    string CurrentPage { get; }

    /// <summary>
    /// Raised when the active page changes. The argument is the new page identifier.
    /// </summary>
    event Action<string> PageChanged;

    /// <summary>
    /// Switches the active page to <paramref name="pageId"/>.
    /// Panels belonging to the old page are hidden; panels of the new page are shown.
    /// </summary>
    /// <param name="pageId">The identifier of the page to switch to.</param>
    void SwitchTo(string pageId);

    /// <summary>
    /// Adds a new page with the given identifier. No-op if the page already exists.
    /// </summary>
    /// <param name="pageId">The identifier of the page to add.</param>
    void AddPage(string pageId);

    /// <summary>
    /// Removes <paramref name="pageId"/> and closes all panels that belong to it.
    /// The <c>"default"</c> page cannot be removed.
    /// </summary>
    /// <param name="pageId">The identifier of the page to remove.</param>
    /// <returns>
    /// <see langword="true"/> if the page (and its panels) were actually removed; <see langword="false"/>
    /// if nothing changed — <paramref name="pageId"/> is <c>"default"</c> or unknown, or the user
    /// declined the implementation's own irreversible-deletion confirmation gate (see
    /// <c>PageManagerService.RemovePage</c>'s remarks, ADR-010 Decision 2 / ADR-009 Amendment 2).
    /// CRITICAL-1 fix (P08 fix cycle 2, 2026-09-22): callers that mirror this outcome onto their own
    /// state (e.g. <c>MainWindowViewModel.RemovePage</c>'s <c>Pages</c> collection) must consult this
    /// return value instead of assuming the removal always happened.
    /// </returns>
    bool RemovePage(string pageId);

    /// <summary>
    /// Persists the pages list and the current page to settings.
    /// </summary>
    void Save();
}
