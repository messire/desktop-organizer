using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.State.Dto;

namespace DesktopOrganizer.UI.State;

/// <summary>
/// The single place the storage schema's <see cref="PagesStateDto"/> (ADR-043,
/// docs/PLANS/page-display-names.md §2.2/§3.2/§3.3) meets the live <see cref="PageDescriptor"/> list
/// <see cref="IApplicationStateService"/>/<see cref="Services.IPageManager"/> work with — mirrors
/// <see cref="PanelStateMapper"/>'s role for panels.
/// </summary>
internal static class PagesStateMapper
{
    private const string DefaultPageId = "default";

    /// <summary>
    /// Maps a schema-v6 <see cref="PagesStateDto"/> to the live, order-preserving page list plus the
    /// active page id. Never throws — every form of corrupted/hand-edited input (a <see langword="null"/>
    /// DTO or items list, a <see langword="null"/> element, a blank id, duplicate ids, a blank current
    /// page id) degrades to a safe result instead (NF-3, docs/PLANS/page-display-names.md §1).
    /// </summary>
    /// <param name="dto">The just-deserialized <c>pages</c> section, or <see langword="null"/> when the document had none.</param>
    public static (IReadOnlyList<PageDescriptor> Pages, string CurrentPageId) FromDto(PagesStateDto? dto)
    {
        if (dto?.Items is not { } items)
        {
            return ([new PageDescriptor(DefaultPageId, null)], DefaultPageId);
        }

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var pages = new List<PageDescriptor>();

        foreach (var item in items)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Id))
            {
                continue;
            }

            // Duplicate ids (ordinal) — first entry wins, matching PanelStateMapper's general "corrupt
            // data degrades, never throws" discipline; a migration/hand-edit that produced duplicates is
            // not this mapper's job to "fix" beyond picking a single, stable winner.
            if (!seenIds.Add(item.Id))
            {
                continue;
            }

            var name = string.IsNullOrWhiteSpace(item.Name) ? null : item.Name;

            // Only "default" is allowed to have no stored name (it self-localizes, see PageDescriptor's
            // remarks) — a missing name on any other page is corrupted/hand-edited data, repaired here
            // to the id itself so the user sees exactly what a v5 reader would have shown for that id.
            if (name is null && item.Id != DefaultPageId)
            {
                name = item.Id;
            }

            pages.Add(new PageDescriptor(item.Id, name));
        }

        if (pages.Count == 0)
        {
            pages.Add(new PageDescriptor(DefaultPageId, null));
        }

        // Deliberately NOT inserting a missing "default" page here when the list is otherwise
        // non-empty — that is PageManagerService.EnsureDefaultPage's job (docs/PLANS/page-display-names.md
        // §3.3), so there is exactly one place responsible for that invariant instead of two that could
        // disagree on where "default" gets inserted.
        var currentPageId = string.IsNullOrWhiteSpace(dto.CurrentPageId) ? DefaultPageId : dto.CurrentPageId;

        return (pages, currentPageId);
    }

    /// <summary>Maps the live page list plus the active page id back to a schema-v6 <see cref="PagesStateDto"/>.</summary>
    /// <param name="pages">The live, order-preserving page list.</param>
    /// <param name="currentPageId">The active page id.</param>
    public static PagesStateDto ToDto(IReadOnlyList<PageDescriptor> pages, string currentPageId)
    {
        ArgumentNullException.ThrowIfNull(pages);

        return new PagesStateDto
        {
            Items = [.. pages.Select(p => new PageStateDto { Id = p.Id, Name = p.Name })],
            CurrentPageId = currentPageId,
        };
    }
}
