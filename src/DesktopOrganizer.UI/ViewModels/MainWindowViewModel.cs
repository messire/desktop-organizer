using System.Collections.ObjectModel;
using System.ComponentModel;
using DesktopOrganizer.Core.Models;
using DesktopOrganizer.UI.Commands;

namespace DesktopOrganizer.UI.ViewModels;

/// <summary>
/// ViewModel for the main application window. Manages files and switches between different view templates.
/// </summary>
public class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly IIconProvider _iconProvider;
    private DataTemplate _listViewTemplate = new();
    private DataTemplate _wrapViewTemplate = new();
    private DataTemplate _currentViewTemplate = new();
    private FileItem? _selectedFile;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Command to switch between different views.
    /// </summary>
    public ICommand SwitchViewCommand { get; }

    /// <summary>
    /// Collection of file items to be displayed in the UI.
    /// </summary>
    public ObservableCollection<FileItem> FileItems { get; } = new();

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
    /// The current view template being applied to the UI.
    /// </summary>
    public DataTemplate CurrentViewTemplate
    {
        get => _currentViewTemplate;
        set
        {
            if (_currentViewTemplate == value) return;
            _currentViewTemplate = value;
            OnPropertyChanged(nameof(CurrentViewTemplate));
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="MainWindowViewModel"/> class.
    /// </summary>
    /// <param name="iconProvider">Provider for fetching file icons.</param>
    public MainWindowViewModel(IIconProvider iconProvider)
    {
        _iconProvider = iconProvider;
        SwitchViewCommand = new RelayCommand(SwitchView);
        InitializeTemplates();
        CurrentViewTemplate = _listViewTemplate;
    }

    /// <summary>
    /// Adds an array of file paths to the collection after validating them, ensuring no duplicates.
    /// </summary>
    /// <param name="files">Array of file paths to be added.</param>
    public void AddFiles(string[] files)
    {
        foreach (var file in files.Where(System.IO.File.Exists))
        {
            if (FileItems.Any(fileItem => fileItem.Path == file)) continue;

            var icon = _iconProvider.GetIcon(file);
            var item = new FileItem(file, icon);
            FileItems.Add(item);
        }

        SaveFileList();
        UpdateFileCount();
    }

    /// <summary>
    /// Removes a file item from the collection.
    /// </summary>
    /// <param name="file">File item to be removed.</param>
    public void RemoveFile(FileItem file)
    {
        if (!FileItems.Contains(file)) return;
        FileItems.Remove(file);
        SaveFileList();
        UpdateFileCount();
    }

    /// <summary>
    /// Loads saved file paths from settings and adds them to the collection.
    /// </summary>
    public void LoadSavedFiles()
    {
        var savedFiles = Properties.Settings.Default.SavedFiles;
        if (string.IsNullOrWhiteSpace(savedFiles)) return;

        var filePaths = savedFiles.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        AddFiles(filePaths);
    }

    /// <summary>
    /// Saves the current file list to the application settings.
    /// </summary>
    public void SaveFileList()
    {
        var filePaths = string.Join("\n", FileItems.Select(item => item.Path));
        Properties.Settings.Default.SavedFiles = filePaths;
        Properties.Settings.Default.Save();
    }

    /// <summary>
    /// Notifies listeners about property value changes.
    /// </summary>
    /// <param name="propertyName">The name of the property that changed.</param>
    protected virtual void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>
    /// Initializes the templates for the list and wrap views.
    /// </summary>
    private void InitializeTemplates()
    {
        _listViewTemplate = Application.Current.FindResource("ListViewTemplate") as DataTemplate ?? new DataTemplate();
        _wrapViewTemplate = Application.Current.FindResource("WrapViewTemplate") as DataTemplate ?? new DataTemplate();
    }

    /// <summary>
    /// Toggles the current view template between list and wrap views.
    /// </summary>
    private void SwitchView()
        => CurrentViewTemplate = CurrentViewTemplate == _listViewTemplate ? _wrapViewTemplate : _listViewTemplate;

    /// <summary>
    /// Updates the file count text when the collection changes.
    /// </summary>
    private void UpdateFileCount()
        => OnPropertyChanged(nameof(FileCountText));
}
