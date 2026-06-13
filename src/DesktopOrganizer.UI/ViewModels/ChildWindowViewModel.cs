using System.ComponentModel;
using System.IO;
using DesktopOrganizer.Core.Models;
using DesktopOrganizer.UI.Commands;
using DesktopOrganizer.UI.Models;
using Microsoft.Win32;

namespace DesktopOrganizer.UI.ViewModels;

/// <summary>
/// Child window ViewModel. The <see cref="Model"/> is the single source of truth for the
/// panel's title and appearance, shared with the settings window.
/// </summary>
public class ChildWindowViewModel : BaseViewModel, IDisposable
{
    private const string ListViewTemplate = nameof(ListViewTemplate);
    private const string WrapViewTemplate = nameof(WrapViewTemplate);

    private readonly IFileLauncher _fileLauncher;
    private readonly IFolderWatcherFactory _folderWatcherFactory;
    private readonly DataTemplate _listTemplate;
    private readonly DataTemplate _wrapTemplate;

    private DataTemplate _currentViewTemplate;
    private ChildWindowModel _model = new();
    private IFolderWatcher? _watcher;
    private string? _sourceFolderPath;
    private bool _disposed;

    /// <summary>
    /// Gets whether this panel is a Folder Portal (mirrors a folder via FSW).
    /// When <see langword="true"/>, manual add / remove / drag-drop are disabled.
    /// </summary>
    public bool IsPortal => !string.IsNullOrWhiteSpace(_sourceFolderPath);

    /// <summary>
    /// The model backing this panel (title, appearance, view mode). Single source of truth.
    /// </summary>
    public ChildWindowModel Model
    {
        get => _model;
        private set
        {
            if (ReferenceEquals(_model, value)) return;
            _model = value;
            OnPropertyChanged(nameof(Model));
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
    /// Command to switch between different views.
    /// </summary>
    public ICommand SwitchViewCommand { get; }

    /// <summary>
    /// Opens the given file with its default shell action.
    /// </summary>
    public ICommand OpenFileCommand { get; }

    /// <summary>
    /// Opens the folder containing the given file, selecting it in Explorer.
    /// </summary>
    public ICommand OpenContainingFolderCommand { get; }

    /// <summary>
    /// Shows the Windows "Properties" dialog for the given file.
    /// </summary>
    public ICommand ShowPropertiesCommand { get; }

    /// <summary>
    /// Removes the given file from this panel.
    /// Disabled when the panel is a Folder Portal (<see cref="IsPortal"/> is <see langword="true"/>).
    /// </summary>
    public ICommand RemoveFileCommand { get; }

    /// <summary>
    /// Opens a folder browser dialog and assigns the chosen folder as this panel's portal source.
    /// Clears the portal when the user cancels or selects the same folder again.
    /// </summary>
    public ICommand SelectSourceFolderCommand { get; }

    /// <summary>
    /// Raised when the user changes the panel state (removes a file, switches view),
    /// so the view layer can persist it.
    /// </summary>
    public event EventHandler? FilesChanged;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChildWindowViewModel"/> class.
    /// </summary>
    /// <param name="iconProvider">Provider for fetching file icons.</param>
    /// <param name="fileLauncher">Service for opening files via the Windows shell.</param>
    /// <param name="folderWatcherFactory">Factory that creates per-panel folder watchers.</param>
    public ChildWindowViewModel(IIconProvider iconProvider, IFileLauncher fileLauncher, IFolderWatcherFactory folderWatcherFactory)
        : base(iconProvider)
    {
        _fileLauncher = fileLauncher;
        _folderWatcherFactory = folderWatcherFactory;
        _listTemplate = GetTemplate(ListViewTemplate);
        _wrapTemplate = GetTemplate(WrapViewTemplate);
        _currentViewTemplate = _listTemplate;

        SwitchViewCommand = new RelayCommand(SwitchView);
        OpenFileCommand = new RelayCommand<FileItem>(OpenFile);
        OpenContainingFolderCommand = new RelayCommand<FileItem>(OpenContainingFolder);
        ShowPropertiesCommand = new RelayCommand<FileItem>(ShowProperties);
        RemoveFileCommand = new RelayCommand<FileItem>(RemoveFileItem, _ => !IsPortal);
        SelectSourceFolderCommand = new RelayCommand(SelectSourceFolder);
    }

    /// <summary>
    /// Opens the given file with its default shell action.
    /// </summary>
    /// <param name="item">The file to open.</param>
    public void OpenFile(FileItem? item)
    {
        if (item is not null) _fileLauncher.Open(item.Path);
    }

    private void OpenContainingFolder(FileItem? item)
    {
        if (item is not null) _fileLauncher.OpenContainingFolder(item.Path);
    }

    private void ShowProperties(FileItem? item)
    {
        if (item is not null) _fileLauncher.ShowProperties(item.Path);
    }

    private void RemoveFileItem(FileItem? item)
    {
        if (item is null) return;

        RemoveFile(item);
        FilesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Initializes the ViewModel from a <see cref="ChildWindowModel"/>.
    /// Starts a live folder watcher when the model has a non-empty <see cref="ChildWindowModel.SourceFolderPath"/>.
    /// </summary>
    /// <param name="model">The model to load state from.</param>
    public void LoadFromModel(ChildWindowModel model)
    {
        Model = model;
        Model.PropertyChanged += OnModelPropertyChanged;

        AddFiles(GetSourceFiles(model));
        ApplyViewMode();

        if (!string.IsNullOrWhiteSpace(model.SourceFolderPath) && Directory.Exists(model.SourceFolderPath))
        {
            AttachWatcher(model.SourceFolderPath);
            OnPropertyChanged(nameof(IsPortal));
        }
    }

    /// <summary>
    /// Opens a folder browser dialog and binds or unbinds the panel as a Folder Portal.
    /// Saves state via <see cref="FilesChanged"/> so the caller can persist it.
    /// </summary>
    private void SelectSourceFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select source folder for Folder Portal",
            Multiselect = false,
        };

        if (dialog.ShowDialog() != true)
            return;

        var chosen = dialog.FolderName;

        // Toggle off if the same folder is chosen again.
        if (string.Equals(chosen, _sourceFolderPath, StringComparison.OrdinalIgnoreCase))
        {
            DetachWatcher();
            _sourceFolderPath = null;
            Model.SourceFolderPath = null;
        }
        else
        {
            AttachWatcher(chosen);
            Model.SourceFolderPath = chosen;

            // Immediately mirror the folder contents.
            ReplaceAll(GetSafeEntries(chosen));
        }

        OnPropertyChanged(nameof(IsPortal));
        FilesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Starts watching <paramref name="folderPath"/> (stops any existing watcher first).
    /// </summary>
    private void AttachWatcher(string folderPath)
    {
        DetachWatcher();
        _sourceFolderPath = folderPath;
        _watcher = _folderWatcherFactory.Create();
        _watcher.Changed += OnFolderChanged;
        _watcher.Start(folderPath);
    }

    /// <summary>
    /// Stops and disposes the current watcher, if any.
    /// </summary>
    private void DetachWatcher()
    {
        if (_watcher is null) return;
        _watcher.Changed -= OnFolderChanged;
        _watcher.Dispose();
        _watcher = null;
    }

    /// <summary>
    /// Returns all file-system entries in <paramref name="folder"/>, or an empty array on error.
    /// </summary>
    private static string[] GetSafeEntries(string folder)
    {
        try
        {
            return Directory.GetFileSystemEntries(folder);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to read folder '{folder}': {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Handles incremental file-system change events from the folder watcher.
    /// All UI mutations are marshalled to the UI thread via <see cref="Application.Current"/> Dispatcher.
    /// Guards against <see cref="Application.Current"/> being <see langword="null"/> during shutdown.
    /// </summary>
    private void OnFolderChanged(object? sender, FolderChangeEventArgs e)
    {
        var app = Application.Current;
        if (app is null) return;

        app.Dispatcher.Invoke(() =>
        {
            switch (e.Kind)
            {
                case FolderChangeKind.Created:
                    AddFiles(new[] { e.Path });
                    break;

                case FolderChangeKind.Deleted:
                    RemoveFileByPath(e.Path);
                    break;

                case FolderChangeKind.Renamed:
                    if (e.OldPath is not null) RemoveFileByPath(e.OldPath);
                    AddFiles(new[] { e.Path });
                    break;

                case FolderChangeKind.ResyncRequired:
                    if (_sourceFolderPath is not null)
                        ReplaceAll(GetSafeEntries(_sourceFolderPath));
                    break;
            }
        });
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        DetachWatcher();
    }

    /// <summary>
    /// A folder-portal panel mirrors its folder; otherwise it uses the saved file list.
    /// </summary>
    private static string[] GetSourceFiles(ChildWindowModel model)
    {
        if (string.IsNullOrWhiteSpace(model.SourceFolderPath) || !System.IO.Directory.Exists(model.SourceFolderPath))
            return model.FilePaths.ToArray();

        try
        {
            return System.IO.Directory.GetFileSystemEntries(model.SourceFolderPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to read folder '{model.SourceFolderPath}': {ex.Message}");
            return [];
        }
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChildWindowModel.ViewMode))
            ApplyViewMode();
    }

    private void ApplyViewMode()
        => CurrentViewTemplate = Model.ViewMode == PanelViewMode.Wrap ? _wrapTemplate : _listTemplate;

    private static DataTemplate GetTemplate(string templateName)
        => Application.Current.FindResource(templateName) as DataTemplate ?? new DataTemplate();

    /// <summary>
    /// Toggles the panel's view mode between list and wrap.
    /// </summary>
    private void SwitchView()
    {
        Model.ViewMode = Model.ViewMode == PanelViewMode.List ? PanelViewMode.Wrap : PanelViewMode.List;
        FilesChanged?.Invoke(this, EventArgs.Empty);
    }
}
