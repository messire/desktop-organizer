using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.UI.Services;

/// <summary>
/// Manages named page layouts (sets of panels). Switching pages hides panels of the
/// outgoing page and shows panels of the incoming page.
/// </summary>
public interface IPageManager
{
    /// <summary>Ordered list of all page identifiers.</summary>
    IReadOnlyList<string> Pages { get; }

    /// <summary>
    /// Ordered list of every page — stable id plus optional stored display name (ADR-043,
    /// docs/PLANS/page-display-names.md §4.1) — in the same order as <see cref="Pages"/>. The
    /// authoritative source <see cref="IPageManager"/> consumers should read a page's display
    /// name from; <see cref="Pages"/> itself is kept only for existing id-only call sites.
    /// </summary>
    IReadOnlyList<PageDescriptor> PageDescriptors { get; }

    /// <summary>Identifier of the currently active page.</summary>
    string CurrentPage { get; }

    /// <summary>
    /// Raised when the active page changes. The argument is the new page identifier.
    /// </summary>
    event Action<string> PageChanged;

    /// <summary>
    /// Raised whenever the page list's composition, order, or any page's stored display name changes
    /// (add/remove/rename) — after the change has already been persisted (<see cref="Save"/>).
    /// Carries no arguments; subscribers re-read <see cref="PageDescriptors"/>. Does <b>not</b> fire for
    /// a switch of the <em>active</em> page (see <see cref="PageChanged"/> for that) — a rename, in
    /// particular, raises this event but never <see cref="PageChanged"/>. Laid down for the trey
    /// "Pages" submenu and round-robin switching (docs/PLANS/page-display-names.md §7) — not consumed
    /// by anything in this change itself beyond <c>MainWindowViewModel</c>'s own resync.
    /// </summary>
    event Action? PagesChanged;

    /// <summary>
    /// Switches the active page to <paramref name="pageId"/>.
    /// Panels belonging to the old page are hidden; panels of the new page are shown.
    /// </summary>
    /// <param name="pageId">The identifier of the page to switch to.</param>
    void SwitchTo(string pageId);

    /// <summary>
    /// Adds a new page with the given identifier. No-op if the page already exists. The page's stored
    /// display name is set equal to <paramref name="pageId"/> itself — legacy behavior retained for
    /// existing callers/tests; new UI-driven page creation should use <see cref="AddNewPage"/> instead,
    /// which assigns an opaque id and a properly localized, unique display name.
    /// </summary>
    /// <remarks>
    /// L-5 note (backlog 1.x wave 1 review): no production UI call site as of this writing — kept as a
    /// deliberate seam for backlog-1x-master-plan.md §2's tray "Pages" submenu, not dead code to remove.
    /// </remarks>
    /// <param name="pageId">The identifier of the page to add.</param>
    void AddPage(string pageId);

    /// <summary>
    /// Creates a new page with a fresh, opaque <see cref="Guid"/>-based identifier and a localized,
    /// unique display name (<c>Page_NewName_Format</c>, ADR-043, docs/PLANS/page-display-names.md
    /// §4.3) — resolved once, at creation time, then stored as ordinary data (ADR-032 D-5), never
    /// re-translated if the UI language changes afterward. Persists synchronously (<see cref="Save"/>)
    /// and raises <see cref="PagesChanged"/> before returning.
    /// </summary>
    /// <returns>The new page's identifier.</returns>
    string AddNewPage();

    /// <summary>
    /// Removes <paramref name="pageId"/> and closes all panels that belong to it.
    /// The <c>"default"</c> page cannot be removed.
    /// </summary>
    /// <param name="pageId">The identifier of the page to remove.</param>
    /// <returns>
    /// <see langword="true"/> if the page (and its panels) were actually removed; <see langword="false"/>
    /// if nothing changed — <paramref name="pageId"/> is <c>"default"</c> or unknown, or the user
    /// declined the implementation's own deletion confirmation gate (see
    /// <c>PageManagerService.RemovePage</c>'s remarks, ADR-010 Decision 2 / ADR-009 Amendment 2), or
    /// some of the page's app-managed items could not be moved to the Recycle Bin — the page and its
    /// panels are then kept and the user has already been told why (ADR-035).
    /// CRITICAL-1 fix (P08 fix cycle 2, 2026-09-22): callers that mirror this outcome onto their own
    /// state (e.g. <c>MainWindowViewModel.RemovePage</c>'s <c>Pages</c> collection) must consult this
    /// return value instead of assuming the removal always happened.
    /// </returns>
    bool RemovePage(string pageId);

    /// <summary>
    /// The page's stored display name, exactly as persisted — <see langword="null"/> for an unnamed
    /// <c>"default"</c> page, or when <paramref name="pageId"/> is unknown. Callers wanting the
    /// <em>effective</em> display name (localized fallback for an unnamed <c>"default"</c>) should use
    /// <c>PageDisplayNames.Resolve</c>/<c>PageNameRules.EffectiveName</c> instead — this method
    /// deliberately does not localize anything itself.
    /// </summary>
    /// <remarks>
    /// L-5 note (backlog 1.x wave 1 review): no production UI call site as of this writing — kept for
    /// backlog-1x-master-plan.md §2's tray "Pages" submenu, not dead code to remove.
    /// </remarks>
    /// <param name="pageId">The page identifier to look up.</param>
    string? GetPageName(string pageId);

    /// <summary>
    /// Validates <paramref name="newName"/> as a candidate name for <paramref name="pageId"/> without
    /// applying it — the same rules <see cref="RenamePage"/> applies (<c>PageNameRules</c>, ADR-043,
    /// docs/PLANS/page-display-names.md §4.2), but with no side effect: no mutation, no
    /// <see cref="Save"/>, no <see cref="PagesChanged"/>. Intended for a live validation hint while the
    /// user is still typing.
    /// </summary>
    /// <remarks>
    /// L-5 note (backlog 1.x wave 1 review): no production UI call site as of this writing (the current
    /// inline rename editor commits eagerly via <see cref="RenamePage"/> itself rather than validating
    /// live, keystroke-by-keystroke) — kept for that future live-hint use, not dead code to remove.
    /// </remarks>
    /// <param name="pageId">The page identifier the name would be applied to.</param>
    /// <param name="newName">The candidate name.</param>
    PageRenameResult ValidatePageName(string pageId, string? newName);

    /// <summary>
    /// Renames <paramref name="pageId"/> to <paramref name="newName"/> (<c>PageNameRules</c>, ADR-043,
    /// docs/PLANS/page-display-names.md §4.2). An empty/whitespace <paramref name="newName"/> resets an
    /// unnamed effective name for <c>"default"</c> back to its localized fallback; for any other page it
    /// is rejected (<see cref="PageRenameResult.Empty"/>). On success (<see cref="PageRenameResult.Renamed"/>)
    /// persists synchronously (<see cref="Save"/>) and raises <see cref="PagesChanged"/> — never
    /// <see cref="PageChanged"/>, since the <em>active</em> page selection itself does not change.
    /// </summary>
    /// <param name="pageId">The page identifier to rename.</param>
    /// <param name="newName">The candidate new name.</param>
    PageRenameResult RenamePage(string pageId, string? newName);

    /// <summary>
    /// Persists the pages list and the current page to settings.
    /// </summary>
    void Save();
}
