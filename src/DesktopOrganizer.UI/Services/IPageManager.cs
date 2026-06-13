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
    void SwitchTo(string pageId);

    /// <summary>
    /// Adds a new page with the given identifier. No-op if the page already exists.
    /// </summary>
    void AddPage(string pageId);

    /// <summary>
    /// Removes <paramref name="pageId"/> and closes all panels that belong to it.
    /// The <c>"default"</c> page cannot be removed.
    /// </summary>
    void RemovePage(string pageId);

    /// <summary>
    /// Persists the pages list and the current page to settings.
    /// </summary>
    void Save();
}
