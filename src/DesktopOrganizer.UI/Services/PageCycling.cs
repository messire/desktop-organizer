namespace DesktopOrganizer.UI.Services;

/// <summary>
/// Pure decision function behind the tray icon's double-click round-robin page switch
/// (backlog-1x-master-plan.md §1.1, ADR-044). Kept free of any dependency on <see cref="IPageManager"/>
/// or WPF so <c>PageCyclingTests</c> can exercise every rule with plain lists/strings, and so
/// <c>TrayPagesAdapter</c> (the only caller) stays a thin orchestration shell around it.
/// </summary>
internal static class PageCycling
{
    /// <summary>
    /// Chooses which page the tray icon's double-click gesture should switch to next.
    /// </summary>
    /// <param name="pages">
    /// All known page identifiers in canonical order (<see cref="IPageManager.Pages"/>). Blank/empty
    /// entries are ignored — defensive only, <see cref="IPageManager"/> is not expected to ever produce
    /// one.
    /// </param>
    /// <param name="current">
    /// The identifier of the currently active page (<see cref="IPageManager.CurrentPage"/>). Compared
    /// against <paramref name="pages"/> ordinally — page identifiers are opaque ids/GUIDs, never a
    /// user-facing display name, so case must never be folded.
    /// </param>
    /// <returns>
    /// The page immediately after <paramref name="current"/> in <paramref name="pages"/>, wrapping from
    /// the last entry back to the first; the first entry when <paramref name="current"/> is not present
    /// in <paramref name="pages"/> at all; or <see langword="null"/> when there are fewer than two pages
    /// to cycle between — the caller's cue to do nothing.
    /// </returns>
    public static string? Next(IReadOnlyList<string> pages, string current)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(current);

        var ordered = pages.Where(page => !string.IsNullOrEmpty(page)).ToList();
        if (ordered.Count <= 1)
        {
            return null;
        }

        var currentIndex = ordered.FindIndex(page => string.Equals(page, current, StringComparison.Ordinal));
        if (currentIndex < 0)
        {
            return ordered[0];
        }

        return ordered[(currentIndex + 1) % ordered.Count];
    }
}
