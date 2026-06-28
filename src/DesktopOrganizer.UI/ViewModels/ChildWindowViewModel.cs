using System.ComponentModel;
using System.IO;
using DesktopOrganizer.Core.Models;
using DesktopOrganizer.UI.Commands;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;
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

    /// <summary>
    /// Interval for the background existence-liveness poll. Matches the poll cadence already used
    /// by <c>SystemShellService</c>'s Recycle Bin watcher, so panel behavior stays consistent.
    /// </summary>
    private const int ExistenceCheckIntervalMs = 3000;

    private readonly IFileLauncher _fileLauncher;
    private readonly IFolderWatcherFactory _folderWatcherFactory;
    private readonly IPanelItemStorageService _itemStorage;
    private readonly IAppSettings _appSettings;
    private readonly DataTemplate _listTemplate;
    private readonly DataTemplate _wrapTemplate;
    private readonly System.Threading.Timer _existenceTimer;

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

            // A disposed VM must never re-subscribe: Dispose() already unsubscribed from _model once,
            // and doing it again here would attach a live subscription with no matching Dispose() left
            // to remove it (Dispose only runs once, guarded by _disposed).
            if (_disposed) return;

            // Unsubscribe from the outgoing model before attaching to the new one — otherwise a
            // second LoadFromModel call would double-subscribe, and the very first subscription would
            // never be released once this ViewModel is disposed (the Model routinely outlives its
            // ViewModel, e.g. via WindowManager snapshots/state lists).
            _model.PropertyChanged -= OnModelPropertyChanged;
            _model = value;
            _model.PropertyChanged += OnModelPropertyChanged;

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
    /// Sets the panel's content filter. Accepts a <see cref="PanelContentFilter"/> value as the command parameter.
    /// </summary>
    public ICommand SetContentFilterCommand { get; }

    /// <summary>
    /// Raised when the user changes the panel state (removes a file, switches view),
    /// so the view layer can persist it.
    /// </summary>
    public event EventHandler? FilesChanged;

    /// <summary>
    /// Raised at most once per install, the first time a live drag-drop
    /// (<see cref="AddDroppedFiles"/>) actually moves a file/folder into app-managed storage. The
    /// view layer shows a brief, non-blocking hint explaining that the original was removed from
    /// its source location — not just copied. Never raised for Folder Portal content
    /// (<see cref="LoadFromModel"/>/<see cref="OnFolderChanged"/>), which never moves anything.
    /// </summary>
    public event EventHandler? MoveHintRequested;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChildWindowViewModel"/> class.
    /// </summary>
    /// <param name="iconProvider">Provider for fetching file icons.</param>
    /// <param name="fileLauncher">Service for opening files via the Windows shell.</param>
    /// <param name="folderWatcherFactory">Factory that creates per-panel folder watchers.</param>
    /// <param name="itemStorage">
    /// Moves manually dropped files into app-managed storage so panel items survive the user
    /// deleting the original (e.g. a desktop shortcut) after dragging it in.
    /// </param>
    /// <param name="appSettings">
    /// Backs the one-time <see cref="MoveHintRequested"/> guard (<see cref="IAppSettings.HasShownMoveHint"/>).
    /// </param>
    public ChildWindowViewModel(
        IIconProvider iconProvider,
        IFileLauncher fileLauncher,
        IFolderWatcherFactory folderWatcherFactory,
        IPanelItemStorageService itemStorage,
        IAppSettings appSettings)
        : base(iconProvider)
    {
        _fileLauncher = fileLauncher;
        _folderWatcherFactory = folderWatcherFactory;
        _itemStorage = itemStorage;
        _appSettings = appSettings;
        _listTemplate = GetTemplate(ListViewTemplate);
        _wrapTemplate = GetTemplate(WrapViewTemplate);
        _currentViewTemplate = _listTemplate;

        SwitchViewCommand = new RelayCommand(SwitchView);
        OpenFileCommand = new RelayCommand<FileItem>(OpenFile);
        OpenContainingFolderCommand = new RelayCommand<FileItem>(OpenContainingFolder);
        ShowPropertiesCommand = new RelayCommand<FileItem>(ShowProperties);
        RemoveFileCommand = new RelayCommand<FileItem>(RemoveFileItem, _ => !IsPortal);
        SelectSourceFolderCommand = new RelayCommand(SelectSourceFolder);
        SetContentFilterCommand = new RelayCommand<PanelContentFilter>(filter => Model.ContentFilter = filter);

        // Folder Portals already get live deletion events from the FileSystemWatcher (OnFolderChanged).
        // Items added via manual drag-drop have no such watcher, so a deleted/restored file (e.g. a
        // desktop shortcut removed via Explorer after being dropped into the panel) never re-evaluates
        // FileItem.Exists and the item stays looking "alive" — clickable but silently does nothing.
        // This timer keeps every panel's liveness state in sync regardless of source.
        _existenceTimer = new System.Threading.Timer(RefreshExistence, null, ExistenceCheckIntervalMs, ExistenceCheckIntervalMs);
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

        var outcome = _itemStorage.Restore(item.Path);
        if (outcome == RestoreOutcome.OriginalPathUnknown)
            _itemStorage.Release(item.Path); // legacy managed copy with no manifest entry — old behavior
        // NotManaged: Restore already no-op'd. Failed: logged inside the service; item stays removed from UI
        // even though the managed copy is orphaned in storage — acceptable, documented in the plan (risk table).

        FilesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Adds files dropped onto the panel from an external source (desktop, Explorer). Unlike
    /// <see cref="BaseViewModel.AddFiles"/> — which is also used to mirror a live Folder Portal and
    /// must keep pointing at the real files — this moves each dropped file or folder into
    /// app-managed storage first (see <see cref="IPanelItemStorageService"/>), so the original is
    /// removed from its source location immediately (the whole point of dragging it in: decluttering
    /// the desktop) and the panel item survives independently of where it came from. A moved-in
    /// folder keeps its structure but is no longer live-linked to its old location — for a folder the
    /// user wants kept in sync, a Folder Portal (<see cref="SelectSourceFolderCommand"/>) is the right
    /// tool. Whatever <see cref="IPanelItemStorageService"/> can't or won't move (very large folders,
    /// locked files, etc.) falls back to referencing the original path in place — same as before this
    /// fix, still trackable live via <see cref="FileItem.Refresh"/>.
    /// </summary>
    /// <param name="paths">Raw file-system paths from the drop (<c>DataFormats.FileDrop</c>).</param>
    public void AddDroppedFiles(string[] paths)
    {
        var sources = paths.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
        var resolved = new string[sources.Length];
        var anyMoved = false;

        for (var i = 0; i < sources.Length; i++)
        {
            var source = sources[i];
            var result = Directory.Exists(source)
                ? _itemStorage.MoveInDirectory(source) ?? source
                : _itemStorage.MoveIn(source) ?? source;

            resolved[i] = result;

            if (!string.Equals(result, source, StringComparison.OrdinalIgnoreCase) && _itemStorage.IsManaged(result))
                anyMoved = true;
        }

        AddFiles(resolved);

        MaybeRaiseMoveHint(anyMoved);
    }

    /// <summary>
    /// Shows the one-time Move-semantics hint the first time <paramref name="anyMoved"/> is
    /// <see langword="true"/> for a live drag-drop (<see cref="AddDroppedFiles"/>), then flips
    /// <see cref="IAppSettings.HasShownMoveHint"/> permanently so it never shows again — including
    /// across app restarts, since the flag is a user-scoped setting.
    /// </summary>
    /// <param name="anyMoved">
    /// <see langword="true"/> if at least one of the just-dropped paths was actually moved into
    /// app-managed storage (as opposed to falling back to referencing the original path in place).
    /// </param>
    private void MaybeRaiseMoveHint(bool anyMoved)
    {
        if (!anyMoved || _appSettings.HasShownMoveHint) return;

        _appSettings.HasShownMoveHint = true;
        _appSettings.Save();

        MoveHintRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Initializes the ViewModel from a <see cref="ChildWindowModel"/>.
    /// Starts a live folder watcher when the model has a non-empty <see cref="ChildWindowModel.SourceFolderPath"/>.
    /// </summary>
    /// <param name="model">The model to load state from.</param>
    public void LoadFromModel(ChildWindowModel model)
    {
        Model = model; // subscribes to Model.PropertyChanged; unsubscribes any previous model first

        // ReplaceAll (not AddFiles): re-binding to a different model must replace the file set, not
        // accumulate onto whatever was already loaded — otherwise a second LoadFromModel call leaves
        // the panel showing the union of both models' files, and SyncToModel would persist that union
        // back onto the new model's FilePaths.
        ReplaceAll(GetSourceFiles(model));
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

        app?.Dispatcher.Invoke(() =>
        {
            switch (e.Kind)
            {
                case FolderChangeKind.Created:
                    AddFiles([e.Path]);
                    break;

                case FolderChangeKind.Deleted:
                    RemoveFileByPath(e.Path);
                    break;

                case FolderChangeKind.Renamed:
                    if (e.OldPath is not null) RemoveFileByPath(e.OldPath);
                    AddFiles([e.Path]);
                    break;

                case FolderChangeKind.ResyncRequired:
                    if (_sourceFolderPath is not null)
                        ReplaceAll(GetSafeEntries(_sourceFolderPath));
                    break;
            }
        });
    }

    /// <summary>
    /// Re-validates <see cref="FileItem.Exists"/>/<see cref="FileItem.IsDirectory"/> for every item in
    /// this panel against the current file-system state. Runs on a thread-pool timer; marshals to the
    /// UI thread before touching <see cref="FileItem"/> instances (they're bound to the live UI) and
    /// must never throw or it takes the timer down silently.
    /// </summary>
    private void RefreshExistence(object? state)
    {
        if (_disposed) return;

        var app = Application.Current;
        app?.Dispatcher.Invoke(() =>
        {
            if (_disposed) return;

            try
            {
                foreach (var item in AllItems) item.Refresh();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Existence refresh failed: {ex.Message}");
            }
        });
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _model.PropertyChanged -= OnModelPropertyChanged;
        _existenceTimer.Dispose();
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

    /// <inheritdoc/>
    protected override void OnSourceChanged() => ApplyFilter();

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChildWindowModel.ViewMode))
            ApplyViewMode();

        if (e.PropertyName == nameof(ChildWindowModel.ContentFilter))
        {
            ApplyFilter();
            FilesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ApplyViewMode()
        => CurrentViewTemplate = Model.ViewMode == PanelViewMode.Wrap ? _wrapTemplate : _listTemplate;

    /// <summary>
    /// Rebuilds <see cref="BaseViewModel.FileItems"/> from <see cref="BaseViewModel.AllItems"/>
    /// according to the current <see cref="ChildWindowModel.ContentFilter"/>. Uses
    /// <see cref="BaseViewModel.ReplaceFileItems"/> so the rebuild raises a single batched
    /// <c>CollectionChanged</c> (<c>Reset</c>) notification instead of a <c>Clear()</c> + N
    /// individual <c>Add()</c> calls.
    /// </summary>
    private void ApplyFilter()
    {
        var predicate = BuildFilterPredicate(Model.ContentFilter);
        ReplaceFileItems(AllItems.Where(predicate));
    }

    private static Func<FileItem, bool> BuildFilterPredicate(PanelContentFilter filter) =>
        filter switch
        {
            PanelContentFilter.HideAll => _ => false,
            PanelContentFilter.HideShortcuts => item => !item.IsShortcut,
            PanelContentFilter.FoldersOnly => item => item.IsDirectory,
            _ => _ => true, // ShowAll
        };

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
