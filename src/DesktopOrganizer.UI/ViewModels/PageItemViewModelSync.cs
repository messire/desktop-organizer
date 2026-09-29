using System.Collections.ObjectModel;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.UI.ViewModels;

/// <summary>
/// Shared in-place sync of an <see cref="ObservableCollection{T}"/> of <see cref="PageItemViewModel"/>
/// against the authoritative <see cref="Services.IPageManager.PageDescriptors"/> list (ADR-043,
/// docs/PLANS/page-display-names.md §5.2) — extracted from <see cref="MainWindowViewModel.SyncPages"/> so
/// <see cref="ChildWindowViewModel"/>'s own, much smaller "Move/Copy to page ▸" submenu list (backlog 1.x,
/// panel-header page routing) can keep an equally live <see cref="PageItemViewModel"/> collection without
/// duplicating this add/remove/reorder/rename logic.
/// </summary>
internal static class PageItemViewModelSync
{
    /// <summary>
    /// Synchronizes <paramref name="pages"/> with <paramref name="descriptors"/>, preserving an
    /// already-present page's own <see cref="PageItemViewModel"/> INSTANCE by <see cref="PageItemViewModel.Id"/>
    /// (only its <see cref="PageItemViewModel.Name"/> is updated in place) rather than rebuilding the
    /// collection from scratch — see <see cref="MainWindowViewModel.SyncPages"/>'s own remarks for why
    /// that matters for a live selection/in-progress edit. Repositions existing rows via
    /// <see cref="ObservableCollection{T}.Move"/> to keep order in sync with <paramref name="descriptors"/>
    /// — never a remove-then-re-add.
    /// </summary>
    /// <param name="pages">The collection to update in place.</param>
    /// <param name="descriptors">The authoritative, ordered page list to sync against.</param>
    public static void Sync(ObservableCollection<PageItemViewModel> pages, IReadOnlyList<PageDescriptor> descriptors)
    {
        for (var i = pages.Count - 1; i >= 0; i--)
        {
            if (!descriptors.Any(d => d.Id == pages[i].Id))
                pages.RemoveAt(i);
        }

        for (var i = 0; i < descriptors.Count; i++)
        {
            var descriptor = descriptors[i];

            if (i < pages.Count && pages[i].Id == descriptor.Id)
            {
                pages[i].Name = descriptor.Name;
                continue;
            }

            var existingIndex = FindPageIndex(pages, descriptor.Id);
            if (existingIndex >= 0)
            {
                pages[existingIndex].Name = descriptor.Name;
                pages.Move(existingIndex, i);
            }
            else
            {
                pages.Insert(i, new PageItemViewModel(descriptor.Id, descriptor.Name));
            }
        }
    }

    private static int FindPageIndex(ObservableCollection<PageItemViewModel> pages, string id)
    {
        for (var i = 0; i < pages.Count; i++)
        {
            if (pages[i].Id == id) return i;
        }

        return -1;
    }
}
