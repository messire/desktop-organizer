using System.Collections.ObjectModel;
using System.ComponentModel;
using DesktopOrganizer.Core.Models;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.UI.ViewModels;

/// <summary>
/// Base class for all ViewModels.
/// </summary>
public abstract class BaseViewModel(IIconProvider iconProvider) : INotifyPropertyChanged
{
    private readonly IIconProvider _iconProvider = iconProvider;
    private readonly List<FileItem> _allItems = [];

    /// <summary>
    /// O(1) membership index mirroring <see cref="_allItems"/>, keyed by path. Windows paths are
    /// case-insensitive, so <c>C:\A.txt</c> and <c>c:\a.txt</c> are treated as the same entry —
    /// consistent with the comparer already used by <see cref="RemoveFileByPath"/>.
    /// </summary>
    private readonly HashSet<string> _allItemPaths = new(StringComparer.OrdinalIgnoreCase);

    private readonly ObservableRangeCollection<FileItem> _fileItems = [];
    private FileItem? _selectedFile;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Collection of file items to be displayed in the UI.
    /// This is a filtered projection of <see cref="AllItems"/>; do not mutate directly.
    /// </summary>
    public ObservableCollection<FileItem> FileItems => _fileItems;

    /// <summary>
    /// The full, unfiltered set of items managed by this panel.
    /// Subclasses use this as the source when computing filter projections.
    /// </summary>
    protected internal IReadOnlyList<FileItem> AllItems => _allItems;

    /// <summary>
    /// Text displaying the current number of visible files.
    /// </summary>
    public string FileCountText => $"File Count: {FileItems.Count}";

    /// <summary>
    /// The file item currently selected by the user.
    /// </summary>
    public FileItem? SelectedFile
    {
        get => _selectedFile;
        set
        {
            if (_selectedFile == value) return;
            _selectedFile = value;
            OnPropertyChanged(nameof(SelectedFile));
        }
    }

    /// <summary>
    /// Adds an array of file paths to the full collection after validating them, ensuring no duplicates.
    /// Calls <see cref="OnSourceChanged"/> after mutation so subclasses can re-apply their filter —
    /// skipped entirely when nothing was actually added (e.g. a watcher event for a path already
    /// tracked), which avoids an unnecessary full <see cref="FileItems"/> rebuild.
    /// </summary>
    /// <param name="files">Array of file paths to be added.</param>
    protected internal void AddFiles(string[] files)
    {
        if (AddFilesCore(files)) OnSourceChanged();
    }

    /// <summary>
    /// Removes a file item from the full collection.
    /// </summary>
    /// <param name="file">File item to be removed.</param>
    protected void RemoveFile(FileItem file)
    {
        if (!_allItems.Remove(file)) return;
        _allItemPaths.Remove(file.Path);
        OnSourceChanged();
    }

    /// <summary>
    /// Removes the <see cref="FileItem"/> whose <see cref="FileItem.Path"/> matches
    /// <paramref name="path"/>. No-op when no matching item is found.
    /// </summary>
    /// <param name="path">The file-system path of the item to remove.</param>
    protected internal void RemoveFileByPath(string path)
    {
        var item = _allItems.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
        if (item is null) return;
        _allItems.Remove(item);
        _allItemPaths.Remove(item.Path);
        OnSourceChanged();
    }

    /// <summary>
    /// Replaces the entire item set with items built from <paramref name="paths"/>.
    /// Used for a full resync after a watcher buffer overflow. Unlike <see cref="AddFiles"/>, this
    /// always triggers <see cref="OnSourceChanged"/> — even when the new set is empty or identical —
    /// because the underlying set was unconditionally cleared first.
    /// </summary>
    /// <param name="paths">The new set of file-system paths.</param>
    protected internal void ReplaceAll(IEnumerable<string> paths)
    {
        _allItems.Clear();
        _allItemPaths.Clear();
        AddFilesCore(paths.ToArray());
        OnSourceChanged();
    }

    /// <summary>
    /// Called whenever <see cref="AllItems"/> changes. The default implementation
    /// projects all items into <see cref="FileItems"/> unchanged (no filtering).
    /// Override in subclasses to apply a filter predicate.
    /// </summary>
    protected virtual void OnSourceChanged() => ReplaceFileItems(_allItems);

    /// <summary>
    /// Replaces <see cref="FileItems"/> with <paramref name="items"/> in a single batch, raising
    /// exactly one <see cref="System.Collections.Specialized.NotifyCollectionChangedEventArgs"/>
    /// (<c>Reset</c>) and one <see cref="FileCountText"/> change notification, regardless of how many
    /// elements are added or removed. Subclasses that override <see cref="OnSourceChanged"/> to apply
    /// a filter (e.g. content-filter projections) should call this instead of mutating
    /// <see cref="FileItems"/> by hand with <c>Clear()</c> + <c>Add()</c>.
    /// </summary>
    /// <param name="items">The new, already-filtered contents of <see cref="FileItems"/>.</param>
    protected void ReplaceFileItems(IEnumerable<FileItem> items)
    {
        _fileItems.ReplaceAll(items);
        OnPropertyChanged(nameof(FileCountText));
    }

    /// <summary>
    /// Adds new, not-yet-tracked entries from <paramref name="files"/> to <see cref="_allItems"/> /
    /// <see cref="_allItemPaths"/> without raising <see cref="OnSourceChanged"/>. The O(1)
    /// <see cref="_allItemPaths"/> lookup (instead of scanning <see cref="_allItems"/> per candidate)
    /// is what keeps <see cref="ReplaceAll"/> O(n) instead of O(n²) on a full resync.
    /// </summary>
    /// <param name="files">Candidate file-system paths.</param>
    /// <returns><see langword="true"/> if at least one new item was added.</returns>
    private bool AddFilesCore(string[] files)
    {
        var added = false;

        // Keep entries even if the path is currently missing — they render greyed out
        // (a file on an unmounted drive shouldn't silently vanish from the panel).
        foreach (var file in files.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            if (_allItemPaths.Contains(file)) continue;

            // Mark the path as tracked only after the item is fully constructed and added — if
            // GetIcon throws, the path must not be left "claimed" in _allItemPaths with no
            // corresponding _allItems entry, or this file could never be added again.
            var icon = _iconProvider.GetIcon(file);
            _allItems.Add(new FileItem(file, icon));
            _allItemPaths.Add(file);
            added = true;
        }

        return added;
    }

    /// <summary>
    /// Notifies about property changes.
    /// </summary>
    /// <param name="propertyName">Name of the property that was changed.</param>
    protected virtual void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
