using System.Collections.ObjectModel;
using System.ComponentModel;
using DesktopOrganizer.Core.Models;

namespace DesktopOrganizer.UI.ViewModels;

/// <summary>
/// Base class for all ViewModels.
/// </summary>
public abstract class BaseViewModel(IIconProvider iconProvider) : INotifyPropertyChanged
{
    private readonly IIconProvider _iconProvider = iconProvider;
    private FileItem? _selectedFile;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Collection of file items to be displayed in the UI.
    /// </summary>
    public ObservableCollection<FileItem> FileItems { get; } = [];

    /// <summary>
    /// Text displaying the current number of files.
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
    /// Adds an array of file paths to the collection after validating them, ensuring no duplicates.
    /// </summary>
    /// <param name="files">Array of file paths to be added.</param>
    protected internal void AddFiles(string[] files)
    {
        // Keep entries even if the path is currently missing — they render greyed out
        // (a file on an unmounted drive shouldn't silently vanish from the panel).
        foreach (var file in files.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            if (FileItems.Any(fileItem => fileItem.Path == file)) continue;

            var icon = _iconProvider.GetIcon(file);
            var item = new FileItem(file, icon);
            FileItems.Add(item);
        }

        OnPropertyChanged(nameof(FileCountText));
    }

    /// <summary>
    /// Removes a file item from the collection.
    /// </summary>
    /// <param name="file">File item to be removed.</param>
    protected void RemoveFile(FileItem file)
    {
        if (!FileItems.Contains(file)) return;
        FileItems.Remove(file);
        OnPropertyChanged(nameof(FileCountText));
    }

    /// <summary>
    /// Removes the <see cref="FileItem"/> whose <see cref="FileItem.Path"/> matches
    /// <paramref name="path"/>. No-op when no matching item is found.
    /// </summary>
    /// <param name="path">The file-system path of the item to remove.</param>
    protected internal void RemoveFileByPath(string path)
    {
        var item = FileItems.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
        if (item is null) return;
        FileItems.Remove(item);
        OnPropertyChanged(nameof(FileCountText));
    }

    /// <summary>
    /// Replaces the entire <see cref="FileItems"/> collection with items built from
    /// <paramref name="paths"/>. Used for a full resync after a watcher buffer overflow.
    /// </summary>
    /// <param name="paths">The new set of file-system paths.</param>
    protected internal void ReplaceAll(IEnumerable<string> paths)
    {
        FileItems.Clear();
        AddFiles(paths.ToArray());
    }

    /// <summary>
    /// Notifies about property changes.
    /// </summary>
    /// <param name="propertyName">Name of the property that was changed.</param>
    protected virtual void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
