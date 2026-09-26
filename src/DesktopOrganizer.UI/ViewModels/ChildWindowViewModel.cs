using System.ComponentModel;
using System.IO;
using DesktopOrganizer.Core;
using DesktopOrganizer.UI.Commands;
using DesktopOrganizer.UI.Diagnostics;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;

namespace DesktopOrganizer.UI.ViewModels;

/// <summary>
/// Child window ViewModel. The <see cref="Model"/> is the single source of truth for the
/// panel's title and appearance, shared with the settings window.
/// </summary>
public class ChildWindowViewModel : BaseViewModel, IDisposable
{
    private const string ListViewTemplate = nameof(ListViewTemplate);
    private const string GridViewTemplate = nameof(GridViewTemplate);

    /// <summary>
    /// Interval for the background existence-liveness poll. Matches the poll cadence already used
    /// by <c>SystemShellService</c>'s Recycle Bin watcher, so panel behavior stays consistent.
    /// </summary>
    private const int ExistenceCheckIntervalMs = 3000;

    private readonly IFileLauncher _fileLauncher;
    private readonly IFolderWatcherFactory _folderWatcherFactory;
    private readonly IPanelItemStorageService _itemStorage;
    private readonly IFileItemRenameService _renameService;
    private readonly IAppSettings _appSettings;
    private readonly IPanelSelectionCoordinator _selectionCoordinator;
    private readonly IGestureDiagnostics _diagnostics;
    private readonly DataTemplate _listTemplate;
    private readonly DataTemplate _gridTemplate;
    private readonly System.Threading.Timer _existenceTimer;

    private DataTemplate _currentViewTemplate;
    private ChildWindowModel _model = new();
    private IFolderWatcher? _watcher;
    private string? _sourceFolderPath;

    /// <summary>
    /// The raw, non-blank <see cref="ChildWindowModel.SourceFolderPath"/> value this ViewModel last saw
    /// <see cref="HandleSourceFolderPathChanged"/> process, or <see langword="null"/> once it went blank
    /// — kept separately from <see cref="_sourceFolderPath"/> (MINOR-2, FIX round 2), which only ever
    /// records a path that actually attached a watcher. A portal path pointing at a folder that doesn't
    /// exist never attaches (see the final <see langword="else"/> branch below), so
    /// <see cref="_sourceFolderPath"/> alone can't tell "this panel never had a portal" apart from
    /// "this panel just had a broken one cleared" — the two need different <see cref="FilesChanged"/>
    /// behavior (see <see cref="HandleSourceFolderPathChanged"/>'s remarks).
    /// </summary>
    private string? _lastAssignedSourceFolderPath;

    private bool _disposed;
    private FileItem? _editingItem;
    private string _editingName = string.Empty;
    private RenameOutcome? _renameError;

    /// <summary>
    /// Single-flight guard for <see cref="RefreshExistence"/> (MAJOR M-1, P08 fix cycle 2): 0 when no
    /// dispatched refresh is outstanding, 1 while one has been queued but not yet serviced by the UI
    /// thread. See <see cref="RefreshExistence"/>'s own remarks for why an unguarded queue is dangerous.
    /// </summary>
    private int _existenceRefreshPending;

    /// <summary>
    /// Set for the duration of clearing <see cref="BaseViewModel.SelectedFile"/> from
    /// <see cref="OnSelectionClaimed"/> (a different panel just claimed the selection), so
    /// <see cref="OnSelectedFileChanged"/> knows this particular change is an echo of a coordinator
    /// notification, not a fresh user selection to (re-)broadcast. Required to keep
    /// "coordinator tells panel to clear" and "panel clearing notifies the coordinator" from forming a
    /// cycle (ui-polishing-1 §2.4 Block A2, risk R3) — defense in depth alongside
    /// <see cref="OnSelectedFileChanged"/>'s own "only notify on a non-null selection" guard and
    /// <see cref="PanelSelectionCoordinator"/>'s own idempotent-owner guard.
    /// </summary>
    private bool _suppressCoordinatorEcho;

    /// <summary>
    /// Set for the duration of the <see cref="Model"/>.<see cref="ChildWindowModel.SortMode"/>
    /// assignment inside <see cref="ReorderItem"/> to suppress the <see cref="FilesChanged"/> raise
    /// that <see cref="OnModelPropertyChanged"/> would otherwise perform for that property change.
    /// <see cref="ReorderItem"/> raises <see cref="FilesChanged"/> itself, exactly once, strictly after
    /// its own mutation (the SortMode switch AND the subsequent <see cref="BaseViewModel.MoveItem"/>)
    /// is fully applied — see the remarks on <see cref="ReorderItem"/> (MAJOR-3, panel-item-sorting
    /// review: this used to be an emergent property of "assigning Manual always differs from Name",
    /// not an explicit contract).
    /// </summary>
    private bool _suppressFilesChangedForSortModeSwitch;

    /// <summary>
    /// Gets whether this panel is a Folder Portal (mirrors a folder via FSW).
    /// When <see langword="true"/>, manual add / remove / drag-drop are disabled.
    /// </summary>
    public bool IsPortal => !string.IsNullOrWhiteSpace(_sourceFolderPath);

    /// <summary>
    /// The sort mode actually applied to this panel's visible items. A Folder Portal always sorts by
    /// name (ADR-014 Decision 7) — there's nowhere to persist a manual order for it (
    /// <c>SyncToModel</c> skips <see cref="ChildWindowModel.FilePaths"/> for portals, and the list is
    /// rebuilt wholesale from the file system on every watcher resync), so
    /// <see cref="ChildWindowModel.SortMode"/> is deliberately left untouched while portal — a panel
    /// that's toggled off a portal again resumes whatever manual/name mode it had before.
    /// </summary>
    public PanelSortMode EffectiveSortMode => IsPortal ? PanelSortMode.Name : Model.SortMode;

    /// <summary>
    /// Whether this panel is currently sorted alphabetically (<see cref="EffectiveSortMode"/> is
    /// <see cref="PanelSortMode.Name"/>). Backs the header sort button's highlight
    /// (<c>PanelHeaderToggleButtonStyle</c>'s <c>DataTrigger</c> in Templates.xaml, ui-polishing-1
    /// §4.3) — the button's glyph itself is now constant, so the highlight is the only visual cue
    /// for which mode is active. Always raised together with <see cref="EffectiveSortMode"/> via
    /// <see cref="NotifySortModeChanged"/>: the two properties are logically the same underlying
    /// state and must never go out of sync (see that method's remarks — this repeats the exact
    /// CRITICAL-2 class of bug that already bit <see cref="EffectiveSortMode"/> once).
    /// </summary>
    public bool IsSortedByName => EffectiveSortMode == PanelSortMode.Name;

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
    /// The item currently being inline-renamed, or <see langword="null"/> when no item is being
    /// edited. Lives on the ViewModel, not on <see cref="FileItem"/> (ADR-013 Decision 6): the model
    /// is shared by portals and filters, while "is this item being edited right now" is pure,
    /// single-panel UI state — keeping it here also makes "at most one item editing" an invariant by
    /// construction, instead of something every <see cref="FileItem"/> would need to coordinate.
    /// </summary>
    public FileItem? EditingItem
    {
        get => _editingItem;
        private set
        {
            if (ReferenceEquals(_editingItem, value)) return;
            _editingItem = value;
            OnPropertyChanged(nameof(EditingItem));
        }
    }

    /// <summary>
    /// The in-progress edited name for <see cref="EditingItem"/>. Two-way bound to the inline
    /// <c>TextBox</c> in <c>Templates.xaml</c>.
    /// </summary>
    public string EditingName
    {
        get => _editingName;
        set
        {
            if (_editingName == value) return;
            _editingName = value;
            OnPropertyChanged(nameof(EditingName));
        }
    }

    /// <summary>
    /// Set by <see cref="CommitRenameCommand"/> when the most recent rename attempt failed, so the view
    /// layer can show a brief, non-blocking hint (never a <c>MessageBox</c> — the panel window carries
    /// <c>WS_EX_NOACTIVATE</c>, see ADR-013 R1). Cleared back to <see langword="null"/> at the start of
    /// every new rename attempt.
    /// </summary>
    public RenameOutcome? RenameError
    {
        get => _renameError;
        private set
        {
            if (_renameError == value) return;
            _renameError = value;
            OnPropertyChanged(nameof(RenameError));
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
    /// Sets the panel's content filter. Accepts a <see cref="PanelContentFilter"/> value as the command parameter.
    /// </summary>
    public ICommand SetContentFilterCommand { get; }

    /// <summary>
    /// Sets the panel's sort order. Accepts a <see cref="PanelSortMode"/> value as the command
    /// parameter. Disabled in a Folder Portal (<see cref="IsPortal"/>) — see <see cref="EffectiveSortMode"/>.
    /// </summary>
    public ICommand SetSortModeCommand { get; }

    /// <summary>
    /// Sets the panel's <see cref="ChildWindowModel.IconLayoutPolicy"/> (ADR-029, P06 —
    /// <c>docs/fix-prompts/06-fit-content.md</c>). Accepts an <see cref="IconLayoutPolicy"/> value as the
    /// command parameter — the "Icon layout" context-menu radio group's two items both bind to this one
    /// command, mirroring <see cref="SetContentFilterCommand"/>/<see cref="SetSortModeCommand"/>'s exact
    /// shape. A pure model mutation: the resulting resize-snap (<c>ChildWindow.xaml.cs</c>'s
    /// <c>OnModelPropertyChanged</c>, ADR-029 fix-cycle MAJOR-2) and, when
    /// <see cref="ChildWindowModel.SizingMode"/> is <see cref="PanelSizingMode.AutoContent"/>, height
    /// recompute (<c>Views/ChildWindow.ContentSizing.cs</c>) both react to the resulting
    /// <see cref="ChildWindowModel.PropertyChanged"/> independently — this command does not call either
    /// directly.
    /// </summary>
    public ICommand SetIconLayoutPolicyCommand { get; }

    /// <summary>
    /// Toggles the panel's sort order between <see cref="PanelSortMode.Manual"/> and
    /// <see cref="PanelSortMode.Name"/> — bound to the header's sort button, a quicker one-click
    /// alternative to the "Arrange icons" context-menu items backed by <see cref="SetSortModeCommand"/>.
    /// Disabled in a Folder Portal (<see cref="IsPortal"/>), same as <see cref="SetSortModeCommand"/> —
    /// see <see cref="EffectiveSortMode"/>.
    /// </summary>
    public ICommand ToggleSortModeCommand { get; }

    /// <summary>
    /// Begins inline-renaming a panel item. Accepts a <see cref="FileItem"/> as the command parameter;
    /// falls back to <see cref="BaseViewModel.SelectedFile"/> when the parameter is <see langword="null"/>
    /// (the <c>F2</c> keyboard shortcut, which has no item to pass explicitly).
    /// </summary>
    public ICommand BeginRenameCommand { get; }

    /// <summary>
    /// Commits the current inline rename (see <see cref="EditingItem"/>/<see cref="EditingName"/>) by
    /// invoking <see cref="IFileItemRenameService.Rename"/>.
    /// </summary>
    public ICommand CommitRenameCommand { get; }

    /// <summary>
    /// Cancels the current inline rename without touching the file system.
    /// </summary>
    public ICommand CancelRenameCommand { get; }

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
    /// <param name="renameService">
    /// Performs the physical rename backing <see cref="CommitRenameCommand"/> (see ADR-013 Decision 4).
    /// </param>
    /// <param name="appSettings">
    /// Backs the one-time <see cref="MoveHintRequested"/> guard (<see cref="IAppSettings.HasShownMoveHint"/>).
    /// </param>
    /// <param name="selectionCoordinator">
    /// The process-wide singleton (ui-polishing-1 §2.4 Block A2) that lets this panel's selection and
    /// every other panel's selection stay mutually exclusive despite none of these <c>WS_EX_NOACTIVATE</c>
    /// windows ever receiving normal focus.
    /// </param>
    /// <param name="diagnostics">
    /// Records <see cref="RefreshExistence"/> failures (UX P2, P08 fix cycle 1, 2026-09-22) — resolved
    /// from the same <c>AddTransient&lt;IGestureDiagnostics, GestureDiagnostics&gt;</c> DI registration
    /// <c>ChildWindow</c> already uses for gesture-lifecycle events (NIT fix, P08 fix cycle 2,
    /// 2026-09-22: this is a NEW instance per resolution, not the same live object <c>ChildWindow</c>
    /// holds — <c>DiConfig.cs</c> registers <see cref="IGestureDiagnostics"/> transient, not singleton),
    /// reused as a lightweight, already-privacy-audited sink rather than adding a second
    /// <see cref="DesktopOrganizer.Services.Abstractions.IDiagnosticsLog"/> dependency.
    /// </param>
    public ChildWindowViewModel(
        IIconProvider iconProvider,
        IFileLauncher fileLauncher,
        IFolderWatcherFactory folderWatcherFactory,
        IPanelItemStorageService itemStorage,
        IFileItemRenameService renameService,
        IAppSettings appSettings,
        IPanelSelectionCoordinator selectionCoordinator,
        IGestureDiagnostics diagnostics)
        : base(iconProvider)
    {
        _fileLauncher = fileLauncher;
        _folderWatcherFactory = folderWatcherFactory;
        _itemStorage = itemStorage;
        _renameService = renameService;
        _appSettings = appSettings;
        _selectionCoordinator = selectionCoordinator;
        _diagnostics = diagnostics;
        _selectionCoordinator.SelectionClaimed += OnSelectionClaimed;
        _listTemplate = GetTemplate(ListViewTemplate);
        _gridTemplate = GetTemplate(GridViewTemplate);
        _currentViewTemplate = _listTemplate;

        SwitchViewCommand = new RelayCommand(SwitchView);
        OpenFileCommand = new RelayCommand<FileItem>(OpenFile);
        OpenContainingFolderCommand = new RelayCommand<FileItem>(OpenContainingFolder);
        ShowPropertiesCommand = new RelayCommand<FileItem>(ShowProperties);
        RemoveFileCommand = new RelayCommand<FileItem>(RemoveFileItem, _ => !IsPortal);
        SetContentFilterCommand = new RelayCommand<PanelContentFilter>(filter => Model.ContentFilter = filter);
        SetSortModeCommand = new RelayCommand<PanelSortMode>(mode => Model.SortMode = mode, _ => !IsPortal);
        SetIconLayoutPolicyCommand = new RelayCommand<IconLayoutPolicy>(policy => Model.IconLayoutPolicy = policy);
        ToggleSortModeCommand = new RelayCommand(ToggleSortMode, () => !IsPortal);
        BeginRenameCommand = new RelayCommand<FileItem>(BeginRename);
        CommitRenameCommand = new RelayCommand(CommitRename);
        CancelRenameCommand = new RelayCommand(CancelRename);

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

    /// <summary>
    /// Adds files dropped onto the panel from an external source (desktop, Explorer). Unlike
    /// <see cref="BaseViewModel.AddFiles"/> — which is also used to mirror a live Folder Portal and
    /// must keep pointing at the real files — this moves each dropped file or folder into
    /// app-managed storage first (see <see cref="IPanelItemStorageService"/>), so the original is
    /// removed from its source location immediately (the whole point of dragging it in: decluttering
    /// the desktop) and the panel item survives independently of where it came from. A moved-in
    /// folder keeps its structure but is no longer live-linked to its old location — for a folder the
    /// user wants kept in sync, a Folder Portal (<see cref="ChildWindowModel.SourceFolderPath"/>,
    /// assignable from the "Customize panel…" dialog) is the right tool. Whatever
    /// <see cref="IPanelItemStorageService"/> can't or won't move (very large folders,
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
    /// Initializes the ViewModel from a <see cref="ChildWindowModel"/>.
    /// Starts a live folder watcher when the model has a non-empty <see cref="ChildWindowModel.SourceFolderPath"/>.
    /// </summary>
    /// <param name="model">The model to load state from.</param>
    public void LoadFromModel(ChildWindowModel model)
    {
        Model = model; // subscribes to Model.PropertyChanged; unsubscribes any previous model first

        var sourceFiles = GetSourceFiles(model);
        var resolved = ResolveManagedReplacements(sourceFiles);

        // ReplaceAll (not AddFiles): re-binding to a different model must replace the file set, not
        // accumulate onto whatever was already loaded — otherwise a second LoadFromModel call leaves
        // the panel showing the union of both models' files, and SyncToModel would persist that union
        // back onto the new model's FilePaths.
        ReplaceAll(resolved);
        ApplyViewMode();

        // CRITICAL C1 fix, part (b) (P08 fix cycle 1, 2026-09-22): when ResolveManagedReplacements
        // actually remapped a stale path, write it onto Model.FilePaths immediately, in memory — not
        // only whenever some unrelated future SyncToModel() call happens to run — and request a
        // (debounced, not immediate — ADR-012 Decision 1 is unaffected) save so it's durable soon.
        // Without this, any code that reads Model.FilePaths directly before that unrelated trigger
        // fires (e.g. MainWindowViewModel.RemovePanel's/PageManagerService.RemovePage's "does this
        // panel have anything to lose" check) still sees the stale, pre-remap path. A Folder Portal
        // never persists FilePaths at all (SyncToModel skips it — see IsPortal), so this only applies
        // to a non-portal remap; ReferenceEquals is enough to detect one, since
        // ResolveManagedReplacements only ever clones+mutates when it actually changed something.
        if (!ReferenceEquals(resolved, sourceFiles) && string.IsNullOrWhiteSpace(model.SourceFolderPath))
        {
            model.FilePaths = [.. resolved];
            FilesChanged?.Invoke(this, EventArgs.Empty);
        }

        if (!string.IsNullOrWhiteSpace(model.SourceFolderPath) && Directory.Exists(model.SourceFolderPath))
        {
            AttachWatcher(model.SourceFolderPath);
            OnPropertyChanged(nameof(IsPortal));

            // IsPortal flipping to true also forces EffectiveSortMode (and therefore IsSortedByName)
            // to Name (see EffectiveSortMode's doc comment) — the "Manual"/"By name" context-menu
            // checkmarks and the header sort button's highlight both bind to these projections, not
            // Model.SortMode, so they need their own notification here too (CRITICAL-2).
            NotifySortModeChanged();
        }
    }

    /// <summary>
    /// Re-validates <see cref="FileItem.Exists"/>/<see cref="FileItem.IsDirectory"/> for every item in
    /// this panel against the current file-system state, and re-resolves the icon for any item that
    /// just reappeared (see <see cref="BaseViewModel.RefreshItemsLiveness"/>). Runs on a thread-pool
    /// timer; marshals to the UI thread before touching <see cref="FileItem"/> instances (they're bound
    /// to the live UI) and must never throw — an exception escaping a <see cref="System.Threading.Timer"/>
    /// callback is an UNHANDLED exception on a thread-pool thread, which terminates the whole process.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <see langword="try"/>/<see langword="catch"/> around <see cref="BaseViewModel.RefreshItemsLiveness"/>
    /// below is a SEPARATE concern from the marshalling itself: a bare <c>Dispatcher.Invoke</c> onto an
    /// already-shut-down dispatcher rethrows <see cref="System.Threading.Tasks.TaskCanceledException"/> at
    /// this thread-pool timer thread and kills the process (intermittent, order-dependent
    /// "Test Run Aborted" with zero failing tests; in production, a tick landing during application
    /// shutdown). <see cref="UiDispatch.TryBeginInvoke"/> is the fire-and-forget guarded marshal — see its
    /// own remarks.
    /// </para>
    /// <para>
    /// MAJOR M-1 fix (P08 fix cycle 2): this used to call the BLOCKING <see cref="UiDispatch.TryInvoke"/>,
    /// which occupies the calling thread-pool thread until the UI thread services it — indefinitely, if
    /// that dispatcher is busy or never pumps at all. Because this timer keeps firing every
    /// <see cref="ExistenceCheckIntervalMs"/>, a busy/blocked UI thread used to accumulate one occupied
    /// thread-pool thread per tick, which can starve the pool (see
    /// <c>docs/Refactoring/Testing/sta-test-conventions.md</c>'s "second, unrelated flake"). Switching to
    /// <see cref="UiDispatch.TryBeginInvoke"/> alone would still let an UNBOUNDED number of dispatched
    /// operations pile up on a busy dispatcher's queue, so <see cref="_existenceRefreshPending"/> is an
    /// explicit single-flight guard: a tick that finds a previous refresh still outstanding skips queuing
    /// another one entirely, rather than adding to a backlog nobody is servicing yet.
    /// </para>
    /// </remarks>
    /// <param name="state">Unused; present to match the <see cref="System.Threading.TimerCallback"/> signature.</param>
    internal void RefreshExistence(object? state)
    {
        if (_disposed) return;

        // Single-flight guard (MAJOR M-1, see remarks): 0 -> 1 means "no refresh outstanding, this tick
        // claims the slot"; any other tick finding it already 1 backs off instead of queuing a second
        // Dispatcher operation on top of the still-unserviced first one.
        if (System.Threading.Interlocked.CompareExchange(ref _existenceRefreshPending, 1, 0) != 0)
            return;

        var queued = UiDispatch.TryBeginInvoke(Application.Current?.Dispatcher, () =>
        {
            try
            {
                if (_disposed) return;

                try
                {
                    RefreshItemsLiveness();
                }
                catch (Exception ex)
                {
                    // UX P2 fix (P08 fix cycle 1, 2026-09-22): Console.Error is never read by a WinExe
                    // process (see the icon pipeline's own identical fix, IconHandler/IconProvider) — route
                    // to the structured diagnostics log instead. Privacy invariant: exception TYPE NAME
                    // only, never ex.Message, which may embed a file path.
                    _diagnostics.ExistenceRefreshFailed(Model.Id, ex.GetType().Name);
                }
            }
            finally
            {
                // Frees the slot for the NEXT tick only once this one has actually run — not merely been
                // queued. Runs even if RefreshItemsLiveness() itself was skipped (_disposed) or failed.
                System.Threading.Volatile.Write(ref _existenceRefreshPending, 0);
            }
        });

        if (!queued)
        {
            // Dropped before it could even be queued (no Application yet, or its dispatcher is already
            // shutting down/shut down) — nothing will ever run to clear the flag above, so clear it here
            // instead of permanently wedging every future tick into "always pending".
            System.Threading.Volatile.Write(ref _existenceRefreshPending, 0);
        }
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
    /// Enters inline-rename mode for <paramref name="item"/> (or <see cref="BaseViewModel.SelectedFile"/>
    /// when <paramref name="item"/> is <see langword="null"/> — the <c>F2</c> shortcut has nothing to
    /// pass explicitly). No-op if there's nothing to rename.
    /// </summary>
    /// <param name="item">The item to rename, or <see langword="null"/> to use <see cref="BaseViewModel.SelectedFile"/>.</param>
    private void BeginRename(FileItem? item)
    {
        var target = item ?? SelectedFile;
        if (target is null) return;

        // Set EditingName before EditingItem: the inline TextBox's Visibility flips to Visible as a
        // direct (synchronous) consequence of EditingItem changing (see FileNameEditBoxStyle in
        // Templates.xaml) — EditingName must already hold the right value by then, or the editor could
        // flash whatever text was left over from a previous edit for a single binding pass.
        RenameError = null;
        EditingName = target.DisplayName;
        EditingItem = target;
    }

    /// <summary>
    /// Commits the in-progress inline rename. A no-op (blank) or unchanged name simply closes the
    /// editor without touching the file system — matching Explorer's own inline-rename behavior. For a
    /// shortcut, whose <see cref="FileItem.DisplayName"/> hides the <c>.lnk</c> extension, the extension
    /// is transparently re-attached before the physical rename so the shortcut isn't broken (Explorer
    /// parity — same "hide extensions" convention applied both ways). On success, relocates the renamed
    /// <see cref="FileItem"/> in place (see <see cref="BaseViewModel.TryRenameItem"/>), re-applies the
    /// content filter (a rename can change the extension and therefore <see cref="FileItem.IsShortcut"/>
    /// — see ADR-013 risk R6), and raises <see cref="FilesChanged"/> so the caller persists the new
    /// path. On failure, the collection is left completely untouched and <see cref="RenameError"/> is
    /// set for the view layer to show a non-blocking hint.
    /// </summary>
    private void CommitRename()
    {
        var item = EditingItem;
        if (item is null) return;

        var newName = EditingName.Trim();

        if (newName.Length == 0 || string.Equals(newName, item.DisplayName, StringComparison.Ordinal))
        {
            EditingItem = null;
            return;
        }

        RenameError = null;

        // FileItem.DisplayName hides a shortcut's .lnk extension (Explorer parity), so newName here is
        // just the base name the user typed/edited — re-attach .lnk before it reaches the rename
        // service, or the physical rename would strip the extension on disk and break the shortcut.
        // Done strictly after the empty/unchanged comparison above, which must compare what the user
        // actually sees (item.DisplayName, also .lnk-less) against what they typed.
        var targetName = item.IsShortcut && !newName.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
            ? newName + ".lnk"
            : newName;

        var result = _renameService.Rename(item.Path, targetName);

        if (result.Outcome != RenameOutcome.Renamed || result.NewPath is null)
        {
            RenameError = result.Outcome;
            return; // Keep the editor open so the user can correct the name.
        }

        TryRenameItem(item, result.NewPath);
        EditingItem = null;

        OnSourceChanged(); // Re-evaluate the content filter: the extension may have changed.
        FilesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Cancels the in-progress inline rename without touching the file system.
    /// </summary>
    private void CancelRename()
    {
        RenameError = null;
        EditingItem = null;
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
    /// Reacts to <see cref="ChildWindowModel.SourceFolderPath"/> changing — the single place a Folder
    /// Portal is attached or detached (settings-window-redesign.md D-4). Every caller that binds or
    /// unbinds a portal (the "Customize panel…" dialog's Browse…/Clear, or any future caller) only
    /// ever needs to assign <see cref="ChildWindowModel.SourceFolderPath"/> on <see cref="Model"/>
    /// itself — the watcher lifecycle and content-mirroring side effects below run purely as a
    /// reaction to that assignment via <see cref="OnModelPropertyChanged"/>, keeping the model the
    /// single source of truth (CLAUDE.md invariant) instead of duplicating this logic in every caller.
    /// </summary>
    private void HandleSourceFolderPathChanged()
    {
        var path = Model.SourceFolderPath;

        // MINOR-2 (FIX round 2): captured BEFORE this call updates it below, so the blank branch can
        // tell "this panel never had a portal path at all" apart from "it just had one — possibly one
        // that never actually attached a watcher — cleared to blank".
        var previousAssignedPath = _lastAssignedSourceFolderPath;
        _lastAssignedSourceFolderPath = string.IsNullOrWhiteSpace(path) ? null : path;

        if (string.IsNullOrWhiteSpace(path))
        {
            // Idempotent: a caller that clears an already-unbound panel (or LoadFromModel re-entry)
            // must not re-run the detach/re-project side effects for nothing. MINOR-2 (FIX round 2):
            // this must NOT short-circuit when Model.SourceFolderPath just transitioned from a non-blank
            // value to blank — even a portal path that never actually attached (the final `else` branch
            // below: a folder that doesn't/no-longer exists) still needs FilesChanged raised here, so
            // whichever caller just cleared it (the "Customize panel…" dialog) gets its debounced save
            // requested instead of the clear silently never being persisted.
            if (_watcher is null && _sourceFolderPath is null && previousAssignedPath is null) return;

            DetachWatcher();
            _sourceFolderPath = null;

            // P-B4 (docs/PLANS/settings-window-redesign.md §3.2): Model.FilePaths was left untouched
            // the whole time the portal was active — ChildWindow.SyncToModel skips writing it while
            // SourceFolderPath is set — so the panel's own items are still there. Reload from it (the
            // same source LoadFromModel itself reads for a non-portal panel) instead of leaving the
            // mirrored folder's items in FileItems/AllItems, which SyncToModel would otherwise persist
            // right over Model.FilePaths on the very next save and orphan the panel's real items.
            ReplaceAll(ResolveManagedReplacements(GetSourceFiles(Model)));

            // EffectiveSortMode just switched from the portal's forced Name back to Model.SortMode —
            // re-project so the panel immediately reflects whichever mode that is, instead of staying
            // visually sorted-by-name until some unrelated rebuild happens to run.
            ApplyFilterAndSort();
        }
        else if (Directory.Exists(path))
        {
            // Idempotent: re-assigning the SAME already-attached folder (e.g. a defensive re-set from
            // the dialog) must not re-mirror the folder or restart the watcher for nothing.
            if (_watcher is not null && string.Equals(_sourceFolderPath, path, StringComparison.OrdinalIgnoreCase))
                return;

            // MINOR-1 (FIX round 2): only relevant for a genuine non-portal -> portal transition (checked
            // BEFORE AttachWatcher below overwrites _sourceFolderPath). ChildWindow.SyncToModel skips
            // writing Model.FilePaths for as long as a portal is attached, so between this transition and
            // whichever debounced save happens to run next, Model.FilePaths would otherwise still hold
            // whatever it last held as a non-portal panel — stale the moment anything (e.g. a rapid
            // attach/detach within the save-debounce window) reads it. Snapshotting the in-memory item
            // list here, before ReplaceAll below discards it in favor of the mirrored folder contents,
            // keeps Model.FilePaths an accurate "what this panel would restore to if un-portal'd right
            // now" at every point, not just after a save happens to land.
            var wasNonPortal = _sourceFolderPath is null;

            AttachWatcher(path);

            if (wasNonPortal)
                Model.FilePaths = AllItems.Select(item => item.Path).ToList();

            // Immediately mirror the folder contents.
            ReplaceAll(GetSafeEntries(path));

            // ReplaceAll already re-projects via OnSourceChanged, but at that point IsPortal (and
            // therefore EffectiveSortMode) hadn't flipped to Name yet — re-project again now that it
            // has, so the freshly mirrored folder shows sorted immediately (ADR-014 Decision 7),
            // instead of only sorting on the next unrelated rebuild.
            ApplyFilterAndSort();
        }
        else
        {
            // Assigned a folder that doesn't (or no longer) exist, e.g. a removable drive unplugged —
            // behave like "no portal" rather than watching a non-existent path.
            DetachWatcher();
            _sourceFolderPath = null;
        }

        OnPropertyChanged(nameof(IsPortal));

        // IsPortal toggling either direction changes what EffectiveSortMode/IsSortedByName project to
        // (forced Name while portal, Model.SortMode otherwise) — the context-menu checkmarks and the
        // header sort button's highlight bind to these projections, not Model.SortMode, so they need
        // their own notification (CRITICAL-2).
        NotifySortModeChanged();

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
    /// Handles incremental file-system change events from the folder watcher.
    /// All UI mutations are marshalled to the UI thread via <see cref="Application.Current"/> Dispatcher.
    /// </summary>
    /// <remarks>
    /// Arrives on <c>FolderWatcher</c>'s debounce thread-pool timer, never the UI thread, so the
    /// marshalling goes through <see cref="UiDispatch.TryBeginInvoke"/> rather than a bare
    /// <c>Dispatcher.Invoke</c>/<see cref="UiDispatch.TryInvoke"/>: a missing
    /// <see cref="Application.Current"/> (headless test host) AND an already-shut-down dispatcher
    /// (application shutdown) both have to degrade to "drop this notification", because an exception
    /// escaping a thread-pool timer callback takes the entire process down — see <see cref="UiDispatch"/>'s
    /// own remarks for the full mechanism.
    /// <para>
    /// CRITICAL C-1 fix (P08 fix cycle 2): this used to call the BLOCKING <see cref="UiDispatch.TryInvoke"/>
    /// from inside <c>FolderWatcher</c>'s own debounce callback, which can run while that callback thread
    /// holds <c>FolderWatcher</c>'s debounce lock. If the UI thread's very next action is closing this
    /// panel (<see cref="DetachWatcher"/> → <c>IFolderWatcher.Dispose</c>/<c>Stop</c> → the SAME lock), the
    /// callback thread blocks forever waiting for the UI thread to service the queued action while the UI
    /// thread blocks forever waiting for the callback thread to release the lock — the whole application
    /// hangs. <see cref="UiDispatch.TryBeginInvoke"/> is fire-and-forget: this method returns to the
    /// caller (and, by extension, releases whatever lock it might be holding) without ever waiting for the
    /// UI thread at all.
    /// </para>
    /// </remarks>
    private void OnFolderChanged(object? sender, FolderChangeEventArgs e)
    {
        UiDispatch.TryBeginInvoke(Application.Current?.Dispatcher, () =>
        {
            // Fire-and-forget means this can run AFTER the panel was closed on the UI thread (Dispose ->
            // DetachWatcher) — same guard as RefreshExistence's dispatched half: never rebuild the item
            // list (blocking directory IO + icon resolution) on a disposed view-model.
            if (_disposed) return;

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
    /// Test seam only (MAJOR M-1 regression coverage, P08 fix cycle 2): observes whether a dispatched
    /// <see cref="RefreshExistence"/> tick is still waiting to be serviced by the UI thread. Never read by
    /// production code.
    /// </summary>
    internal bool IsExistenceRefreshPending => System.Threading.Volatile.Read(ref _existenceRefreshPending) != 0;

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _model.PropertyChanged -= OnModelPropertyChanged;
        _selectionCoordinator.SelectionClaimed -= OnSelectionClaimed;

        // Releases this panel's claim on the coordinator's single, process-wide _currentOwner slot, if
        // it still holds it — otherwise a closed panel (and its whole FileItems/watcher graph) stays
        // reachable through the coordinator singleton until some other panel happens to claim a
        // selection (finalization MAJOR M-1). Unconditional and idempotent: Release() itself no-ops for
        // a panel that never became the owner.
        _selectionCoordinator.Release(this);

        _existenceTimer.Dispose();
        DetachWatcher();
    }

    /// <summary>
    /// Moves <paramref name="item"/> to the drop position <paramref name="visibleInsertIndex"/> —
    /// the ViewModel-side half of drag-to-reorder (ADR-014 Decisions 5/6). No-op in a Folder Portal
    /// (<see cref="IsPortal"/>): there's nowhere to persist a manual order for a portal (see
    /// <see cref="EffectiveSortMode"/>), so its layout can't be dragged. The actual gesture (mouse
    /// capture, hit-testing, drag threshold) lives in the view layer; this only reacts to "the user
    /// dropped <paramref name="item"/> at this position".
    /// </summary>
    /// <remarks>
    /// <para>
    /// If the panel is currently sorted by name, any manual drag is — by definition — the user
    /// overriding that sort, so this first materializes the current sorted order into
    /// <see cref="BaseViewModel.AllItems"/> (<see cref="BaseViewModel.SortAllItems"/>) and only then
    /// flips <see cref="ChildWindowModel.SortMode"/> to <see cref="PanelSortMode.Manual"/>. The order
    /// here is load-bearing: materializing first means <see cref="ApplyFilterAndSort"/> — triggered by
    /// the <see cref="ChildWindowModel.SortMode"/> change via <see cref="OnModelPropertyChanged"/> —
    /// produces a projection that's reference-identical to what was already on screen, so
    /// <see cref="BaseViewModel.ReplaceFileItems"/>'s early exit (ADR-014 Decision 8) makes that
    /// rebuild a silent no-op instead of a visible <c>Reset</c>-driven "jump". Only after that settles
    /// does <see cref="BaseViewModel.MoveItem"/> run, so it operates on the final, stable
    /// <see cref="BaseViewModel.AllItems"/> order rather than one that's about to be discarded and
    /// rebuilt out from under it (which would silently undo the drop).
    /// </para>
    /// <para>
    /// <b>Exactly one <see cref="FilesChanged"/> per call, raised strictly after the mutation is
    /// complete:</b> this is an explicit contract enforced with <see cref="_suppressFilesChangedForSortModeSwitch"/>,
    /// not an emergent side effect of <c>Model.SortMode</c>'s setter happening to differ when switching
    /// away from <see cref="PanelSortMode.Name"/> (MAJOR-3, panel-item-sorting review — the prior
    /// version relied on exactly that assumption implicitly holding). The <c>Model.SortMode</c>
    /// assignment below is wrapped so <see cref="OnModelPropertyChanged"/> applies the sort-mode side
    /// effects (<see cref="ApplyFilterAndSort"/>, <see cref="EffectiveSortMode"/> notification) but does
    /// NOT also raise <see cref="FilesChanged"/> for it; this method raises <see cref="FilesChanged"/>
    /// itself exactly once, after <see cref="BaseViewModel.MoveItem"/> has actually run — so observers
    /// never see a "changed" notification for a gesture whose move hasn't happened yet.
    /// </para>
    /// </remarks>
    /// <param name="item">The item being dropped. Must currently be visible.</param>
    /// <param name="visibleInsertIndex">The drop position — see <see cref="BaseViewModel.MoveItem"/>.</param>
    public void ReorderItem(FileItem item, int visibleInsertIndex)
    {
        if (IsPortal) return;

        var switchedToManual = false;
        if (EffectiveSortMode == PanelSortMode.Name)
        {
            SortAllItems(PanelItemSortComparer.ByName);

            _suppressFilesChangedForSortModeSwitch = true;
            try
            {
                Model.SortMode = PanelSortMode.Manual;
            }
            finally
            {
                _suppressFilesChangedForSortModeSwitch = false;
            }

            switchedToManual = true;
        }

        var moved = MoveItem(item, visibleInsertIndex);

        if (moved || switchedToManual)
            FilesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    protected override void OnSourceChanged() => ApplyFilterAndSort();

    /// <summary>
    /// Strips a legacy managed-storage GUID suffix from a managed item's on-disk name so it displays
    /// under its original name (see <see cref="ManagedItemName.StripLegacyGuidSuffix"/> and ADR-013
    /// Decision 3). Only applies to items actually inside <see cref="IPanelItemStorageService"/>
    /// storage — a plain user file that happens to end in <c>_&lt;32 hex&gt;</c> is left untouched.
    /// </summary>
    /// <param name="path">The candidate item's file-system path.</param>
    protected override string? ResolveDisplayName(string path) =>
        _itemStorage.IsManaged(path) ? ManagedItemName.StripLegacyGuidSuffix(Path.GetFileName(path)) : null;

    /// <summary>
    /// Claims the global cross-panel selection (<see cref="IPanelSelectionCoordinator.NotifySelected"/>)
    /// whenever <see cref="BaseViewModel.SelectedFile"/> becomes non-null (ui-polishing-1 §2.4 Block
    /// A2). Deliberately does <b>not</b> call <see cref="IPanelSelectionCoordinator.NotifySelected"/>
    /// when the new value is <see langword="null"/> — a panel clearing its own selection isn't "this
    /// panel claiming the selection", and treating it as one would give the coordinator a nonsensical
    /// "owner" for an empty selection. Also skipped entirely while <see cref="_suppressCoordinatorEcho"/>
    /// is set — i.e. while this exact clear is itself an echo of <see cref="OnSelectionClaimed"/>
    /// reacting to some other panel's claim, not a fresh user selection.
    /// </summary>
    protected override void OnSelectedFileChanged()
    {
        if (_suppressCoordinatorEcho) return;
        if (SelectedFile is not null) _selectionCoordinator.NotifySelected(this);
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
    /// Read-time self-heal for stale <see cref="ChildWindowModel.FilePaths"/> entries (ADR-009
    /// Amendment 2026-09-22, P08): any path that no longer exists but has a recorded managed copy
    /// (see <see cref="IPanelItemStorageService.TryFindManagedPath"/>) is swapped for that managed
    /// path before the panel's <see cref="FileItem"/>s are built — so both the icon and a future
    /// launch resolve against the item's real, current location instead of a permanently-broken
    /// placeholder (P00 baseline finding F-9/H2: 20 of 26 real-world items were affected this way).
    /// Folder Portal listings (see <see cref="GetSourceFiles"/>) are already guaranteed to exist and
    /// pass through unchanged — the loop below is a no-op for every already-existing path.
    /// </summary>
    /// <remarks>
    /// Purely a read: no manifest or file-system mutation happens here. The self-heal only becomes
    /// durable once this panel's next save round-trips the (now-corrected) <see cref="FileItem.Path"/>
    /// values back into <see cref="ChildWindowModel.FilePaths"/> (see <c>ChildWindow.xaml.cs</c>'s
    /// sync-back on <c>FilesChanged</c>) — a stale entry with no live managed copy is left exactly as
    /// it was (still shown, still greyed out via <c>Exists == false</c>), never invented or dropped.
    /// </remarks>
    /// <param name="paths">The panel's raw source paths, as returned by <see cref="GetSourceFiles"/>.</param>
    private string[] ResolveManagedReplacements(string[] paths)
    {
        string[]? resolved = null;

        for (var i = 0; i < paths.Length; i++)
        {
            var path = paths[i];
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (System.IO.File.Exists(path) || System.IO.Directory.Exists(path)) continue;
            if (!_itemStorage.TryFindManagedPath(path, out var managedPath) || managedPath is null) continue;

            resolved ??= (string[])paths.Clone();
            resolved[i] = managedPath;
        }

        return resolved ?? paths;
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChildWindowModel.ViewMode))
            ApplyViewMode();

        if (e.PropertyName == nameof(ChildWindowModel.SourceFolderPath))
            HandleSourceFolderPathChanged();

        if (e.PropertyName == nameof(ChildWindowModel.ContentFilter))
        {
            ApplyFilterAndSort();
            FilesChanged?.Invoke(this, EventArgs.Empty);
        }

        if (e.PropertyName == nameof(ChildWindowModel.SortMode))
        {
            ApplyFilterAndSort();

            // Menu-checked state (Templates.xaml binds "Manual"/"By name" IsChecked to
            // EffectiveSortMode, not Model.SortMode) never got a change notification of its own — the
            // menu stayed showing the stale checkmark until something unrelated happened to touch
            // EffectiveSortMode next (CRITICAL-2, panel-item-sorting review). The header sort button's
            // highlight (IsSortedByName) has the exact same dependency, so both go through the shared
            // helper together.
            NotifySortModeChanged();

            // Suppressed while ReorderItem is mid-gesture: it raises FilesChanged itself, exactly
            // once, after its own mutation (SortMode switch + MoveItem) is fully applied — see
            // _suppressFilesChangedForSortModeSwitch and ReorderItem's remarks (MAJOR-3).
            if (!_suppressFilesChangedForSortModeSwitch)
                FilesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ApplyViewMode()
        => CurrentViewTemplate = Model.ViewMode == PanelViewMode.Grid ? _gridTemplate : _listTemplate;

    /// <summary>
    /// Rebuilds <see cref="BaseViewModel.FileItems"/> from <see cref="BaseViewModel.AllItems"/>
    /// according to the current <see cref="ChildWindowModel.ContentFilter"/>, then — when
    /// <see cref="EffectiveSortMode"/> is <see cref="PanelSortMode.Name"/> — sorts the filtered
    /// result with <see cref="PanelItemSortComparer.ByName"/> (ADR-014 Decisions 2/3). This is the
    /// single point every rebuild path (<c>AddFiles</c>, <c>RemoveFile</c>, <c>RemoveFileByPath</c>,
    /// <c>ReplaceAll</c>, a Folder Portal resync tick, post-rename <see cref="OnSourceChanged"/>) goes
    /// through, so all of them automatically pick up sorting without individual changes.
    /// <see cref="BaseViewModel.AllItems"/> itself is <b>never</b> mutated here — sorting is a
    /// non-mutating projection (ADR-014 Decision 2), so switching back to
    /// <see cref="PanelSortMode.Manual"/> restores the prior manual order exactly. Uses
    /// <see cref="BaseViewModel.ReplaceFileItems"/> so the rebuild raises a single batched
    /// <c>CollectionChanged</c> (<c>Reset</c>) notification instead of a <c>Clear()</c> + N
    /// individual <c>Add()</c> calls — or, when the resulting projection is reference-identical to
    /// what's already shown, no notification at all (ADR-014 Decision 8).
    /// </summary>
    private void ApplyFilterAndSort()
    {
        var predicate = BuildFilterPredicate(Model.ContentFilter);
        var visible = AllItems.Where(predicate);

        if (EffectiveSortMode == PanelSortMode.Name)
            visible = visible.OrderBy(item => item, PanelItemSortComparer.ByName);

        ReplaceFileItems(visible);
    }

    /// <summary>
    /// Toggles the panel's view mode between list and grid.
    /// </summary>
    private void SwitchView()
    {
        Model.ViewMode = Model.ViewMode == PanelViewMode.List ? PanelViewMode.Grid : PanelViewMode.List;
        FilesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Toggles <see cref="ChildWindowModel.SortMode"/> between <see cref="PanelSortMode.Manual"/> and
    /// <see cref="PanelSortMode.Name"/> — the third sort mode candidates would need a richer
    /// picker, but with only these two a plain toggle covers the whole space. The <see cref="Model"/>
    /// assignment drives <see cref="OnModelPropertyChanged"/>, which re-applies the sort/projection and
    /// raises <see cref="FilesChanged"/> — this method does neither itself.
    /// </summary>
    private void ToggleSortMode() =>
        Model.SortMode = EffectiveSortMode == PanelSortMode.Name ? PanelSortMode.Manual : PanelSortMode.Name;

    /// <summary>
    /// Raises change notifications for both <see cref="EffectiveSortMode"/> and
    /// <see cref="IsSortedByName"/> together. Neither property has a backing field of its own — both
    /// are pure projections of <see cref="IsPortal"/>/<see cref="ChildWindowModel.SortMode"/> — so
    /// nothing raises their <c>PropertyChanged</c> automatically; every path that changes what they'd
    /// project to must call this. This is the single choke point required by ui-polishing-1 §4.3
    /// (CRITICAL-2 risk): <see cref="EffectiveSortMode"/> already needed manual notification from
    /// multiple call sites before <see cref="IsSortedByName"/> existed, and it would have been trivial
    /// to update one of them for <see cref="EffectiveSortMode"/> while forgetting the other for
    /// <see cref="IsSortedByName"/> — routing every one of those call sites through a single helper
    /// makes that impossible instead of merely unlikely.
    /// </summary>
    private void NotifySortModeChanged()
    {
        OnPropertyChanged(nameof(EffectiveSortMode));
        OnPropertyChanged(nameof(IsSortedByName));
    }

    /// <summary>
    /// Reacts to another owner claiming the global cross-panel selection
    /// (<see cref="IPanelSelectionCoordinator.SelectionClaimed"/>) by clearing this panel's own
    /// selection — the actual cross-panel "selecting here deselects there" behavior (ui-polishing-1
    /// §2.4 Block A2, DoD 4). A no-op when <paramref name="owner"/> is this instance itself (this
    /// panel is the one that just claimed the selection — nothing to clear) or when this panel has no
    /// selection to clear in the first place (avoids an unnecessary <see cref="BaseViewModel.SelectedFile"/>
    /// assignment/notification pair for every other panel on every claim). Wraps the actual clear in
    /// <see cref="_suppressCoordinatorEcho"/> so <see cref="OnSelectedFileChanged"/> doesn't treat this
    /// echo as a fresh claim to re-broadcast (risk R3 — reentrancy guard).
    /// </summary>
    /// <param name="sender">The <see cref="IPanelSelectionCoordinator"/> that raised the event.</param>
    /// <param name="owner">The panel instance that just claimed the selection.</param>
    private void OnSelectionClaimed(object? sender, object owner)
    {
        if (ReferenceEquals(owner, this)) return;

        // Another panel just claimed the selection while this one has an active inline-rename editor
        // open. These panels carry WS_EX_NOACTIVATE, so a click in that other panel never sends this
        // one a normal LostKeyboardFocus — RenameEditBehavior's LostKeyboardFocus handler (which is
        // this project's existing "commit, don't cancel" semantics for click-away) never fires, and the
        // editor would otherwise stay open indefinitely. Commit — never cancel — to match that same
        // semantics, and do it before touching SelectedFile below (finalization MAJOR M-2).
        if (EditingItem is not null) CommitRename();

        if (SelectedFile is null) return;

        _suppressCoordinatorEcho = true;
        try
        {
            SelectedFile = null;
        }
        finally
        {
            _suppressCoordinatorEcho = false;
        }
    }
}
