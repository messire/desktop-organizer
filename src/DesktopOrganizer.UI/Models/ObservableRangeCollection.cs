using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace DesktopOrganizer.UI.Models;

/// <summary>
/// An <see cref="ObservableCollection{T}"/> that can replace its entire contents in one shot,
/// raising a single <see cref="NotifyCollectionChangedAction.Reset"/> notification instead of a
/// <c>Clear()</c> followed by N individual <c>Add()</c> calls (N + 1 separate
/// <see cref="INotifyCollectionChanged.CollectionChanged"/> events). WPF's items controls handle
/// <c>Reset</c> by re-pulling the collection once, which is both correct and far cheaper for bulk
/// updates (Folder Portal resync, content-filter switch) than a series of Add notifications.
/// </summary>
/// <typeparam name="T">The element type.</typeparam>
public class ObservableRangeCollection<T> : ObservableCollection<T>
{
    private static readonly PropertyChangedEventArgs CountChangedEventArgs = new(nameof(Count));
    private static readonly PropertyChangedEventArgs IndexerChangedEventArgs = new("Item[]");
    private static readonly NotifyCollectionChangedEventArgs ResetEventArgs = new(NotifyCollectionChangedAction.Reset);

    /// <summary>
    /// Replaces the entire contents of the collection with <paramref name="items"/>, raising a
    /// single <see cref="NotifyCollectionChangedAction.Reset"/> notification (plus the standard
    /// <c>Count</c>/<c>Item[]</c> property-changed notifications) instead of one notification per
    /// removed/added element. Mutates the underlying storage directly, bypassing the base class's
    /// per-item notification path.
    /// </summary>
    /// <param name="items">The new contents. Safe to pass a lazy projection over this same collection.</param>
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();

        // Materialize before Clear(): items may be a lazy projection over this same collection
        // (e.g. FileItems.Where(...)) — clearing first would make the source enumerate as empty.
        var buffer = items.ToList();

        Items.Clear();
        foreach (var item in buffer) Items.Add(item);

        OnPropertyChanged(CountChangedEventArgs);
        OnPropertyChanged(IndexerChangedEventArgs);
        OnCollectionChanged(ResetEventArgs);
    }
}
