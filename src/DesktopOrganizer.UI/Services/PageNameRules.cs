using System.Globalization;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.UI.Services;

/// <summary>
/// Pure validation rules for a page's display name (ADR-043, docs/PLANS/page-display-names.md §4.2):
/// shared by <see cref="PageManagerService.ValidatePageName"/> (no side effect) and
/// <see cref="PageManagerService.RenamePage"/> (applies on success). No I/O, no localization lookup —
/// every method here is a pure function over already-resolved strings.
/// </summary>
internal static class PageNameRules
{
    /// <summary>The maximum allowed length (UTF-16 code units) of a trimmed page display name.</summary>
    public const int MaxLength = 40;

    private const string DefaultPageId = "default";

    /// <summary>
    /// Evaluates <paramref name="newName"/> as a candidate name for the page identified by
    /// <paramref name="pageId"/>, against every other page's <em>effective</em> display name
    /// (<see cref="EffectiveName"/>) in <paramref name="allPages"/>. Never mutates anything — the
    /// caller decides what to do with the result (apply it, or merely report it back for a live hint).
    /// </summary>
    /// <param name="pageId">The identifier of the page the name would be applied to.</param>
    /// <param name="newName">The candidate name, exactly as typed (not yet trimmed).</param>
    /// <param name="allPages">Every known page, used to find <paramref name="pageId"/> and to check for a duplicate name.</param>
    /// <param name="localizedDefaultName">The current culture's resolved <c>Page_Default_DisplayName</c>, used as the unnamed <c>"default"</c> page's effective name.</param>
    public static PageRenameResult Validate(
        string pageId,
        string? newName,
        IReadOnlyList<PageDescriptor> allPages,
        string localizedDefaultName)
    {
        ArgumentNullException.ThrowIfNull(allPages);
        ArgumentNullException.ThrowIfNull(localizedDefaultName);

        var page = allPages.FirstOrDefault(p => p.Id == pageId);
        if (page is null)
        {
            return PageRenameResult.NotFound;
        }

        var trimmed = newName?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            if (pageId == DefaultPageId)
            {
                // Empty input for "default" is a valid reset to the localized fallback, not a
                // rejection — Renamed if it currently has a stored name, Unchanged if it is already
                // unnamed (docs/PLANS/page-display-names.md §4.2 step 3).
                return page.Name is null ? PageRenameResult.Unchanged : PageRenameResult.Renamed;
            }

            return PageRenameResult.Empty;
        }

        // L-10 fix (backlog 1.x wave 1 review): char.IsControl alone misses the Unicode "Format" category
        // (Cf) — invisible formatting characters like U+200B (zero-width space) or U+202E (right-to-left
        // override, usable to visually spoof a page name) are not control characters but are exactly as
        // unwanted here as a pasted line break/tab.
        if (trimmed.Any(static c => CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.Control or UnicodeCategory.Format))
        {
            return PageRenameResult.InvalidCharacters;
        }

        if (trimmed.Length > MaxLength)
        {
            return PageRenameResult.TooLong;
        }

        foreach (var other in allPages)
        {
            if (other.Id == pageId)
            {
                continue;
            }

            var otherEffectiveName = EffectiveName(other, localizedDefaultName);
            if (string.Equals(trimmed, otherEffectiveName, StringComparison.OrdinalIgnoreCase))
            {
                return PageRenameResult.Duplicate;
            }
        }

        // Step 7 (docs/PLANS/page-display-names.md §4.2): for a page with a stored Name, compares
        // against that RAW stored value, not its effective name (which is the same string anyway) —
        // retyping the exact current stored name is Unchanged, but a case-only change ("work" -> "Work")
        // is a real Renamed (self-exclusion in the duplicate check above already allows it).
        //
        // H-1 fix (backlog 1.x wave 1 review): for a page with NO stored Name (only ever "default"),
        // the RAW stored value is null, which can never ordinal-equal any non-empty trimmed input — so
        // comparing against it made committing the untouched, pre-filled editor (BeginRenamePage seeds
        // EditingName with EffectiveName, i.e. the localized fallback text) look like a real rename, and
        // RenamePage would persist that literal fallback text as a stored Name, de-localizing the page
        // for every OTHER culture from then on (ADR-032 D-5 violation). Compare against the page's
        // EFFECTIVE name instead in that case, so retyping (or simply not touching) the localized
        // fallback text is correctly Unchanged; any other non-empty input is still a real Renamed.
        var comparand = page.Name ?? EffectiveName(page, localizedDefaultName);
        return string.Equals(trimmed, comparand, StringComparison.Ordinal)
            ? PageRenameResult.Unchanged
            : PageRenameResult.Renamed;
    }

    /// <summary>
    /// The effective display name of <paramref name="page"/>: its stored <see cref="PageDescriptor.Name"/>
    /// when set, otherwise <paramref name="localizedDefaultName"/> for the <c>"default"</c> page, or the
    /// page's own <see cref="PageDescriptor.Id"/> for anything else (defensive — a non-default page
    /// should never actually have a <see langword="null"/> stored name once loaded through
    /// <c>PagesStateMapper.FromDto</c>, which already repairs that case).
    /// </summary>
    /// <param name="page">The page to resolve an effective name for.</param>
    /// <param name="localizedDefaultName">The current culture's resolved <c>Page_Default_DisplayName</c>.</param>
    public static string EffectiveName(PageDescriptor page, string localizedDefaultName)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(localizedDefaultName);

        if (page.Name is not null)
        {
            return page.Name;
        }

        return page.Id == DefaultPageId ? localizedDefaultName : page.Id;
    }
}
