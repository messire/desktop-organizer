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
        foreach (var file in files.Where(System.IO.File.Exists))
        {
            if (FileItems.Any(fileItem => fileItem.Path == file)) continue;

            var icon = _iconProvider.GetIcon(file);
            var item = new FileItem(file, icon);
            FileItems.Add(item);
        }

        SaveFileList();
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
        SaveFileList();
        OnPropertyChanged(nameof(FileCountText));
    }

    /// <summary>
    /// Saves the current file list to the application settings.
    /// </summary>
    protected internal void SaveFileList()
    {
        var filePaths = string.Join("\n", FileItems.Select(item => item.Path));
        Properties.Settings.Default.SavedFiles = filePaths;
        Properties.Settings.Default.Save();
    }

    /// <summary>
    /// Loads saved file paths from settings and adds them to the collection.
    /// </summary>
    protected internal void LoadSavedFiles()
    {
        var savedFiles = Properties.Settings.Default.SavedFiles;
        if (string.IsNullOrWhiteSpace(savedFiles)) return;

        var filePaths = savedFiles.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        AddFiles(filePaths);
    }

    /// <summary>
    /// Notifies about property changes.
    /// </summary>
    /// <param name="propertyName">Name of the property that was changed.</param>
    protected virtual void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
