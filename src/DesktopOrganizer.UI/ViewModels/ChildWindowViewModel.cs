using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using DesktopOrganizer.Core;
using DesktopOrganizer.Services.Diagnostics;
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
    /// <summary>
    /// Display size, in DIP, of an item icon in the list view — must match the fixed
    /// <c>Width="16" Height="16"</c> of the list item template's <c>Image</c> (Resources/Templates.xaml,
    /// <c>ListViewTemplate</c>). The grid view uses <see cref="ChildWindowModel.IconSize"/> instead.
    /// </summary>
    internal const double ListIconSizeDip = 16;

    private const string ListViewTemplate = nameof(ListViewTemplate);
    private const string GridViewTemplate = nameof(GridViewTemplate);

    /// <summary>
    /// Interval for the background existence-liveness poll. Matches the poll cadence already used
    /// by <c>SystemShellService</c>'s Recycle Bin watcher, so panel behavior stays consistent.
    /// </summary>
    private const int ExistenceCheckIntervalMs = 3000;

    /// <summary>
    /// How many liveness ticks (<see cref="ExistenceCheckIntervalMs"/> apart — about two minutes) entries created
    /// next to a managed item by a shell command keep being looked for after the command returned — see
    /// <see cref="ProcessPendingSiblingAdoption"/>.
    /// </summary>
    private const int SiblingAdoptionWatchTicks = 40;

    /// <summary>
    /// Canonical shell verbs (<see cref="ShellContextMenuResult.Verb"/>) that never plausibly create a new
    /// file next to the item they were invoked on (M-B, release-polish 1.0 final review) — starting the
    /// <see cref="SiblingAdoptionWatchTicks"/>-tick <see cref="ProcessPendingSiblingAdoption"/> watch for one
    /// of these would only ever cost a directory listing per tick for the rest of the watch window, for
    /// nothing. "Create shortcut", "Send to &gt; Compressed folder", "Copy", "Cut", an archiver's "Add to
    /// archive/Extract" etc. are deliberately NOT in this list — those (and any verb this list doesn't name)
    /// still get the watch, same as before this fix.
    /// </summary>
    private static readonly HashSet<string> NonCreatingVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "open", "opennew", "edit", "print", "printto", "properties", "explore", "runas", "openas",
    };

    /// <summary>
    /// Debounce window for <see cref="UpdateRequestedIconPixelSize"/>'s re-fetch of every tracked item's
    /// icon (M3, release-polish 1.0 review): the per-panel icon-size slider (16–128, step 8) can cross 8+
    /// snapped size buckets while being dragged, and <c>ChildWindow.OnDpiChanged</c> pushes a new
    /// <see cref="IconRenderScale"/> for every panel at once on a monitor DPI change — both used to
    /// re-fetch every item's icon, synchronously, once per crossed bucket/panel. <see cref="BaseViewModel.RequestedIconPixelSize"/>
    /// itself still updates synchronously on every call (new items added mid-drag use the current size —
    /// see <see cref="UpdateRequestedIconPixelSize"/>'s remarks); only the expensive re-fetch of already-
    /// tracked items is collapsed into a single call after this much silence.
    /// </summary>
    private static readonly TimeSpan IconResizeDebounceWindow = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Debounce window for <see cref="OnProbeResultsApplied"/>'s content-filter re-run (ADR-046 §6):
    /// a batch of background icon-load results carrying a directory-flag flip can arrive several times in
    /// quick succession while a large portal keeps loading, and each one would otherwise trigger its own
    /// full <see cref="ApplyFilterAndSort"/>.
    /// </summary>
    private static readonly TimeSpan ProbeRefilterDebounceWindow = TimeSpan.FromMilliseconds(100);

    private readonly IFileLauncher _fileLauncher;
    private readonly IFolderWatcherFactory _folderWatcherFactory;
    private readonly IPanelItemStorageService _itemStorage;
    private readonly IFileItemRenameService _renameService;
    private readonly IAppSettings _appSettings;
    private readonly IPanelSelectionCoordinator _selectionCoordinator;
    private readonly IGestureDiagnostics _diagnostics;

    /// <summary>
    /// Backs <see cref="LivenessTimeProvider"/> and the M-C vanish-confirmation grace window
    /// (<see cref="_pendingVanishedManagedItems"/>'s timestamps) — see the constructor's own remarks.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// M-1 fix (code review, panel clone to page): see the constructor's own remarks. <see langword="null"/>
    /// when not supplied — <see cref="ResolveManagedReplacements"/> then falls back to its pre-fix behavior.
    /// Also backs <see cref="MovePanelToPageCommand"/>'s routing into <see cref="IWindowManager.MovePanelToPage"/>
    /// (ADR-045) — <see cref="CopyPanelToPageCommand"/> instead goes through <see cref="_panelCloneFlow"/>,
    /// which owns its own <see cref="IWindowManager"/> reference (LOW-8, ADR-045).
    /// </summary>
    private readonly IWindowManager? _windowManager;

    /// <summary>
    /// UX review, backlog 1.x item 8 ("panel clone to page" parity pass): backs <see cref="Pages"/> — the
    /// panel's own header context menu's "Move/Copy to page ▸" submenus (<c>ChildWindow.xaml</c>'s
    /// <c>HeaderContextMenu</c>), mirroring the Settings window's "Panels" section (<see cref="MainWindowViewModel.Pages"/>).
    /// <see langword="null"/> only for the handful of tests that construct this type directly without
    /// supplying one — <see cref="Pages"/> then stays permanently empty and <see cref="MovePanelToPageCommand"/>/
    /// <see cref="CopyPanelToPageCommand"/> simply never have anything to execute against, same
    /// degrade-gracefully shape as <see cref="_windowManager"/> being <see langword="null"/>.
    /// </summary>
    private readonly IPageManager? _pageManager;

    /// <summary>
    /// H-1 fix (code review, panel clone to page, backlog 1.x wave 3 review): backs
    /// <see cref="CopyPanelToPageCommand"/> — before this fix, <see cref="CopyPanelToPage"/> called
    /// <see cref="IWindowManager.ClonePanelToPage"/> directly and discarded its result outright, so a
    /// failure (too large, no disk space, copy failed) never reached the user from this panel's own header
    /// menu, unlike the Settings window's identically-named command. This shared flow (also used by
    /// <c>MainWindowViewModel.CopyPanelToPage</c>) owns the busy-cursor bracket, the localized failure
    /// notification and the stale-destination-page silent re-sync — see <see cref="IPanelCloneFlow"/>'s own
    /// remarks. <see langword="null"/> only for the handful of tests that construct this type directly
    /// without supplying one — <see cref="CopyPanelToPageCommand"/> then simply never has anything to
    /// execute against, same degrade-gracefully shape as <see cref="_windowManager"/>/<see cref="_pageManager"/>
    /// being <see langword="null"/>.
    /// </summary>
    private readonly IPanelCloneFlow? _panelCloneFlow;

    private readonly DataTemplate _listTemplate;
    private readonly DataTemplate _gridTemplate;
    private readonly System.Threading.Timer _existenceTimer;

    /// <summary>
    /// Resolves the <see cref="IDebounceTimer"/> backing <see cref="UpdateRequestedIconPixelSize"/>'s
    /// debounced icon re-fetch (M3, release-polish 1.0 review) — same lazily-resolved, degrade-to-
    /// immediate-when-unavailable pattern as <c>ApplicationStateService.ResolveDebounceTimer</c>. Tests
    /// supply a fake via the constructor to trigger the deferred re-fetch deterministically.
    /// </summary>
    private readonly Func<IDebounceTimer?> _iconResizeDebounceTimerProvider;

    /// <summary>
    /// MINOR 8 (release-polish 1.0 final review): <see langword="true"/> when no
    /// <c>iconResizeDebounceTimerProvider</c> was supplied to the constructor — i.e. this instance uses
    /// the lazily-CREATING production default (<see cref="ResolveDefaultIconResizeDebounceTimer"/>), so
    /// <see cref="Dispose"/> knows to stop <see cref="_defaultIconResizeDebounceTimer"/> directly (a plain
    /// field read, never creating) instead of invoking <see cref="_iconResizeDebounceTimerProvider"/> —
    /// which, for this case only, would otherwise construct a brand-new <see cref="DispatcherDebounceTimer"/>
    /// just to immediately stop it whenever no icon resize ever happened during this panel's lifetime.
    /// </summary>
    private readonly bool _usesDefaultIconResizeDebounceTimerProvider;

    /// <summary>
    /// M-6 (review it.2): resolves the <see cref="IDebounceTimer"/> backing <see cref="OnProbeResultsApplied"/>'s
    /// content-filter re-run — same lazily-resolved, degrade-to-immediate-when-unavailable pattern as
    /// <see cref="_iconResizeDebounceTimerProvider"/>, added for the exact same reason: production always
    /// uses the real, <see cref="System.Windows.Application.Current"/>-bound <see cref="DispatcherDebounceTimer"/>
    /// (<see cref="ResolveProbeRefilterDebounceTimer"/>), but a test needs a fake it can fire deterministically
    /// to prove "exactly one <c>ApplyFilterAndSort</c> per debounce window, selection preserved" without a
    /// real <see cref="System.Windows.Threading.DispatcherTimer"/> tick.
    /// </summary>
    private readonly Func<IDebounceTimer?> _probeRefilterDebounceTimerProvider;

    /// <summary>
    /// M-6 (review it.2): the <see cref="OnProbeResultsApplied"/> counterpart of
    /// <see cref="_usesDefaultIconResizeDebounceTimerProvider"/> — see that field's own remarks.
    /// </summary>
    private readonly bool _usesDefaultProbeRefilterDebounceTimerProvider;

    /// <summary>
    /// Managed items <see cref="OnItemsVanished"/> saw go missing, keyed to the <see cref="_timeProvider"/>
    /// timestamp of that observation — still awaiting <see cref="ProcessPendingVanishedManagedItems"/>'s
    /// <see cref="ExistenceCheckIntervalMs"/> grace window before it actually reconciles them (M-C,
    /// release-polish 1.0 final review; ADR-046 7c §7.2 amendment — see that method's remarks for why a
    /// fixed time window replaced the original "one tick" rule once liveness became asynchronous).
    /// </summary>
    private readonly Dictionary<FileItem, DateTimeOffset> _pendingVanishedManagedItems = [];

    private DataTemplate _currentViewTemplate;
    private ChildWindowModel _model = new();
    private double _iconRenderScale = 1.0;
    private IFolderWatcher? _watcher;
    private string? _sourceFolderPath;
    private DispatcherDebounceTimer? _defaultIconResizeDebounceTimer;

    /// <summary>
    /// Lazily created, production-only debounce timer backing <see cref="OnProbeResultsApplied"/>'s
    /// content-filter re-run (ADR-046 §6) — same lazily-creating, degrade-to-immediate-when-no-Application
    /// pattern as <see cref="ResolveDefaultIconResizeDebounceTimer"/>, but a separate instance: the two
    /// debounced actions (icon re-fetch vs. content re-filter) are independent and must not cancel one
    /// another out.
    /// </summary>
    private DispatcherDebounceTimer? _probeRefilterDebounceTimer;

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

    /// <summary>
    /// The managed item the last shell command ran on, with its per-item directory snapshot from before the
    /// command, while entries that command creates asynchronously are still being looked for (see
    /// <see cref="ProcessPendingSiblingAdoption"/>). <see langword="null"/> when nothing is pending.
    /// </summary>
    private SiblingAdoptionWatch? _siblingWatch;

    private bool _disposed;
    private FileItem? _editingItem;
    private string _editingName = string.Empty;
    private RenameOutcome? _renameError;

    /// <summary>
    /// Re-entrancy guard for a shell operation on this panel: incremented for the duration of the one
    /// actual blocking native call each of <see cref="ShowShellContextMenu"/> (the context menu's own modal
    /// loop, <see cref="IFileLauncher.ShowContextMenu"/>) and <see cref="DeleteManagedItem"/> (its own,
    /// separate <c>SHFileOperation</c> progress/confirmation modal loop,
    /// <see cref="IPanelItemStorageService.RecycleManagedItem"/>) makes — never the rest of either method's
    /// own, purely synchronous bookkeeping that runs strictly after the call returns. Both modal loops pump
    /// window messages, including the dispatched operation <see cref="RefreshExistence"/>'s liveness timer
    /// posts every <see cref="ExistenceCheckIntervalMs"/>. While non-zero, that dispatched liveness refresh,
    /// <see cref="OnItemsVanished"/> and <see cref="ProcessPendingSiblingAdoption"/> all return early, and a
    /// re-entrant <see cref="ShowShellContextMenu"/> call is dropped outright (M-A, release-polish 1.0 final
    /// review). Without this, a liveness tick sneaking in through the nested message pump mid-
    /// <c>SHFileOperation</c> could see the managed item's temporarily-vanished path (moved back to its
    /// original location, about to be recycled FROM there) and reconcile/remove it out from under the
    /// in-progress delete — and if the user then cancelled the shell's prompt, the item the storage service
    /// moved back would be silently orphaned from the panel (<see cref="BaseViewModel.TryRenameItem"/> is a
    /// no-op for an item no longer tracked).
    /// </summary>
    private int _shellOperationDepth;

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
    /// DPI scale (1.0 at 96 DPI) of the surface this panel renders on, pushed by the owning
    /// <c>ChildWindow</c> (before the first load, once its HWND exists, and on every <c>WM_DPICHANGED</c>).
    /// Together with the displayed icon size (<see cref="ChildWindowModel.IconSize"/> in the grid view,
    /// <see cref="ListIconSizeDip"/> in the list view) it decides the physical pixel size item icons are
    /// requested at, so icons stay crisp at 125/150/200 % instead of being upscaled.
    /// </summary>
    /// <remarks>
    /// Session state only — never persisted. A non-finite or non-positive value is ignored. A change only
    /// costs a re-fetch when it moves the snapped pixel size to another bucket (see
    /// <see cref="BaseViewModel.SetRequestedIconPixelSize"/>).
    /// </remarks>
    public double IconRenderScale
    {
        get => _iconRenderScale;
        set
        {
            if (!double.IsFinite(value) || value <= 0) return;
            if (value.Equals(_iconRenderScale)) return;

            _iconRenderScale = value;
            UpdateRequestedIconPixelSize(refreshExisting: true);
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
    /// Opens <see cref="BaseViewModel.SelectedFile"/> with its default shell action — the <c>Enter</c>
    /// keyboard shortcut's counterpart to the double-click's <see cref="OpenFile"/>. Cannot execute while
    /// nothing is selected or while an inline rename is in progress (<see cref="EditingItem"/>): Enter
    /// means "commit" to the rename editor, never "launch".
    /// </summary>
    public ICommand OpenSelectedFileCommand { get; }

    /// <summary>
    /// Ordered list of pages backing this panel's own header context menu's "Move to page ▸"/"Copy to
    /// page ▸" submenus (<c>ChildWindow.xaml</c>'s <c>HeaderContextMenu</c>) — UX review, backlog 1.x item
    /// 8 ("panel clone to page" parity pass). Kept in sync with <see cref="IPageManager.PageDescriptors"/>
    /// by <see cref="SyncPages"/>, the same shape as <see cref="MainWindowViewModel.Pages"/> (extracted
    /// into the shared <see cref="PageItemViewModelSync"/> helper so the two never drift apart). This
    /// panel's own <see cref="PageItemViewModel.IsEditing"/>/<see cref="PageItemViewModel.EditingName"/>
    /// fields are never used here — there is no inline-rename UI in this menu — only <see cref="PageItemViewModel.Id"/>/
    /// <see cref="PageItemViewModel.Display"/> are bound.
    /// </summary>
    public ObservableCollection<PageItemViewModel> Pages { get; } = [];

    /// <summary>
    /// Moves this panel to a different page — the header context menu's "Move to page ▸" submenu.
    /// Parameter is the destination page identifier (string). Disabled (see <see cref="CanMovePanelToPage"/>)
    /// for the panel's own current page, matching the Settings window's identically-named
    /// <see cref="MainWindowViewModel.MovePanelToPageCommand"/>.
    /// </summary>
    public ICommand MovePanelToPageCommand { get; }

    /// <summary>
    /// Creates a fully independent copy of this panel on the destination page (parameter, a page id
    /// string) — the header context menu's "Copy to page ▸" submenu (ADR-045). Unlike
    /// <see cref="MovePanelToPageCommand"/>, the CURRENT page is a valid destination — see
    /// <see cref="MainWindowViewModel.CopyPanelToPageCommand"/>'s own remarks.
    /// </summary>
    public ICommand CopyPanelToPageCommand { get; }

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
    /// <param name="iconResizeDebounceTimerProvider">
    /// Resolves the <see cref="IDebounceTimer"/> backing the debounced icon re-fetch (M3, release-polish
    /// 1.0 review — see <see cref="IconResizeDebounceWindow"/>). <see langword="null"/> (the production
    /// default, via DI) resolves lazily against <see cref="System.Windows.Application.Current"/>'s
    /// dispatcher, degrading to an immediate synchronous re-fetch when no dispatcher is available — same
    /// pattern as <c>ApplicationStateService.ResolveDebounceTimer</c>. Tests supply a fake timer here
    /// instead, to trigger the deferred re-fetch deterministically.
    /// </param>
    /// <param name="windowManager">
    /// M-1 fix (code review, panel clone to page): lets <see cref="ResolveManagedReplacements"/> exclude a
    /// candidate already directly referenced by a DIFFERENT live panel (<see cref="IWindowManager.GetManagedPathsClaimedByOtherPanels"/>)
    /// before healing this panel's own stale entry onto it. <see langword="null"/> falls back to the
    /// pre-fix behavior (no cross-panel claim check) — production DI always supplies the real singleton;
    /// tests that don't exercise this specific cross-panel scenario can omit it. Deliberately appended AFTER
    /// <paramref name="iconResizeDebounceTimerProvider"/> (rather than next to the other collaborators
    /// above) so every existing positional-argument call site that already supplies it keeps compiling
    /// unchanged.
    /// </param>
    /// <param name="pageManager">See <see cref="_pageManager"/>. Deliberately appended AFTER
    /// <paramref name="windowManager"/>, for the same "keep existing positional call sites compiling"
    /// reason.</param>
    /// <param name="iconLoadQueue">
    /// The background icon-load queue (ADR-046) — see <see cref="BaseViewModel"/>'s own constructor
    /// parameter. Deliberately appended AFTER <paramref name="pageManager"/>, for the same reason as
    /// <paramref name="windowManager"/>/<paramref name="pageManager"/> themselves.
    /// </param>
    /// <param name="itemProbeQueue">
    /// The background existence/directory-ness probe queue (ADR-046 7c) — see <see cref="BaseViewModel"/>'s
    /// own constructor parameter. Deliberately AFTER <paramref name="iconLoadQueue"/>, for the same
    /// "keep existing positional call sites compiling" reason as <paramref name="windowManager"/>.
    /// </param>
    /// <param name="timeProvider">
    /// Clock <see cref="LivenessTimeProvider"/> exposes and this panel's M-C vanish-confirmation grace
    /// window (<see cref="_pendingVanishedManagedItems"/>) is measured against. <see langword="null"/>
    /// (production DI) resolves to <see cref="TimeProvider.System"/>; tests supply a fake to drive both
    /// deterministically (C-7).
    /// </param>
    /// <param name="probeRefilterDebounceTimerProvider">
    /// M-6 (review it.2): resolves the <see cref="IDebounceTimer"/> backing <see cref="OnProbeResultsApplied"/>'s
    /// debounced content-filter re-run (ADR-046 §6). <see langword="null"/> (the production default)
    /// resolves lazily against <see cref="System.Windows.Application.Current"/>'s dispatcher, same
    /// degrade-to-immediate pattern as <paramref name="iconResizeDebounceTimerProvider"/>. Deliberately
    /// appended AFTER <paramref name="timeProvider"/>, for the same "keep existing positional call sites
    /// compiling" reason as <paramref name="windowManager"/>.
    /// </param>
    /// <param name="panelCloneFlow">See <see cref="_panelCloneFlow"/>. Deliberately the LAST constructor
    /// parameter (after the ADR-046 queue parameters), for the same "keep existing positional call sites
    /// compiling" reason as <paramref name="windowManager"/>/<paramref name="pageManager"/>.</param>
    public ChildWindowViewModel(
        IIconProvider iconProvider,
        IFileLauncher fileLauncher,
        IFolderWatcherFactory folderWatcherFactory,
        IPanelItemStorageService itemStorage,
        IFileItemRenameService renameService,
        IAppSettings appSettings,
        IPanelSelectionCoordinator selectionCoordinator,
        IGestureDiagnostics diagnostics,
        Func<IDebounceTimer?>? iconResizeDebounceTimerProvider = null,
        IWindowManager? windowManager = null,
        IPageManager? pageManager = null,
        IIconLoadQueue? iconLoadQueue = null,
        IItemProbeQueue? itemProbeQueue = null,
        TimeProvider? timeProvider = null,
        Func<IDebounceTimer?>? probeRefilterDebounceTimerProvider = null,
        IPanelCloneFlow? panelCloneFlow = null)
        : base(iconProvider, iconLoadQueue, itemProbeQueue)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _fileLauncher = fileLauncher;
        _folderWatcherFactory = folderWatcherFactory;
        _itemStorage = itemStorage;
        _renameService = renameService;
        _appSettings = appSettings;
        _selectionCoordinator = selectionCoordinator;
        _diagnostics = diagnostics;

        // L-4 (review it.2, ADR-046 D-13): a real queue was passed in (iconLoadQueue/itemProbeQueue), but
        // this constructing thread had no live SynchronizationContext — base's own D-13 rule already
        // degraded to the synchronous legacy pipeline; this is the diagnostic for that otherwise-silent
        // degradation (see BaseViewModel's constructor remarks and the two flags' own remarks). panelId is
        // unknown this early (Model isn't set until LoadFromModel) — null, same as other early diagnostics.
        if (UsedSynchronousIconLoadQueueFallbackDespiteSupplied) _diagnostics.IconPipelineSynchronousFallbackUsed(null, "iconLoadQueue");
        if (UsedSynchronousItemProbeQueueFallbackDespiteSupplied) _diagnostics.IconPipelineSynchronousFallbackUsed(null, "itemProbeQueue");

        _windowManager = windowManager;
        _pageManager = pageManager;
        _panelCloneFlow = panelCloneFlow;
        _iconResizeDebounceTimerProvider = iconResizeDebounceTimerProvider ?? ResolveDefaultIconResizeDebounceTimer;

        // MINOR 8 (release-polish 1.0 final review): remembered so Dispose() can stop the PRODUCTION
        // default timer via the existing field directly (see the remarks there) instead of invoking the
        // lazily-CREATING provider — a test-supplied provider never lazily creates (it always returns the
        // same already-constructed fake), so Dispose keeps calling it as before for that case.
        _usesDefaultIconResizeDebounceTimerProvider = iconResizeDebounceTimerProvider is null;
        _probeRefilterDebounceTimerProvider = probeRefilterDebounceTimerProvider ?? ResolveProbeRefilterDebounceTimer;
        _usesDefaultProbeRefilterDebounceTimerProvider = probeRefilterDebounceTimerProvider is null;
        _selectionCoordinator.SelectionClaimed += OnSelectionClaimed;
        _listTemplate = GetTemplate(ListViewTemplate);
        _gridTemplate = GetTemplate(GridViewTemplate);
        _currentViewTemplate = _listTemplate;

        SwitchViewCommand = new RelayCommand(SwitchView);
        SetContentFilterCommand = new RelayCommand<PanelContentFilter>(filter => Model.ContentFilter = filter);
        SetSortModeCommand = new RelayCommand<PanelSortMode>(mode => Model.SortMode = mode, _ => !IsPortal);
        SetIconLayoutPolicyCommand = new RelayCommand<IconLayoutPolicy>(policy => Model.IconLayoutPolicy = policy);
        ToggleSortModeCommand = new RelayCommand(ToggleSortMode, () => !IsPortal);
        BeginRenameCommand = new RelayCommand<FileItem>(BeginRename);
        CommitRenameCommand = new RelayCommand(CommitRename);
        CancelRenameCommand = new RelayCommand(CancelRename);
        OpenSelectedFileCommand = new RelayCommand(() => OpenFile(SelectedFile), () => SelectedFile is not null && EditingItem is null);
        MovePanelToPageCommand = new RelayCommand<string>(MovePanelToPage, CanMovePanelToPage);
        CopyPanelToPageCommand = new RelayCommand<string>(CopyPanelToPage, CanCopyPanelToPage);

        if (_pageManager is not null)
        {
            _pageManager.PagesChanged += OnPagesChanged;
            SyncPages();
        }

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
    /// Right-click on a panel item: selects it, shows the native Windows shell context menu for its file
    /// (the same menu Explorer shows — the panel has no menu of its own for items), and reacts to how the
    /// menu ended. The shell's "Rename" command starts this panel's own inline rename (the same one
    /// <c>F2</c> starts); any other invoked command is followed by <see cref="ReconcileAfterShellCommand()"/>,
    /// since shell verbs (Delete, Cut, "Create shortcut", …) act directly on the file — for a moved-in item, on
    /// the managed copy under <c>%LOCALAPPDATA%\DesktopOrganizer\PanelItems\{guid}\</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Must be called on the UI thread: blocks in the menu's modal loop. A Folder Portal item gets the same
    /// menu (the shell operates on the real file; the portal watcher picks the change up).
    /// </para>
    /// <para>
    /// For an app-managed item (M5, release-polish 1.0 review): (a) the shell's "Delete" is intercepted
    /// (<see cref="ShellContextMenuOutcome.DeleteRequested"/>) and handled by
    /// <see cref="IPanelItemStorageService.RecycleManagedItem"/> — the item goes back to its original location and
    /// is recycled from THERE, so "Restore" in the Recycle Bin brings it back somewhere visible instead of into
    /// the hidden storage folder; (c) the item's per-item directory is snapshotted before the menu, so entries a
    /// command creates next to it ("Create shortcut", "Send to &gt; Compressed folder", an archiver) are adopted
    /// as new panel items rather than mistaken for a rename.
    /// </para>
    /// </remarks>
    /// <param name="item">The item that was right-clicked.</param>
    /// <param name="ownerHwnd">This panel's window handle.</param>
    /// <param name="screenX">Horizontal screen coordinate of the cursor, in physical pixels.</param>
    /// <param name="screenY">Vertical screen coordinate of the cursor, in physical pixels.</param>
    public void ShowShellContextMenu(FileItem item, IntPtr ownerHwnd, int screenX, int screenY)
    {
        ArgumentNullException.ThrowIfNull(item);

        // M-A guard (release-polish 1.0 final review): see _shellOperationDepth's remarks. A re-entrant
        // call arriving while this panel already has a shell operation in progress (a queued dispatcher
        // operation reaching back here through the modal loop's nested message pump) is dropped outright
        // rather than starting a second, overlapping one.
        if (_shellOperationDepth > 0) return;

        SelectedFile = item;

        // An app-managed (moved-in) item lives in its hidden per-item directory: its Delete is handled here, and
        // that directory is snapshotted so entries the command creates can be told apart from pre-existing ones.
        var managed = !IsPortal && _itemStorage.IsManaged(item.Path);
        var entriesBefore = managed ? _itemStorage.GetItemDirectoryEntries(item.Path) : null;

        // Only the actual blocking native call — the menu's own modal loop — is guarded; the switch below
        // runs strictly after it returns (no message pump involved), so it deliberately runs with the
        // guard already released, and its own trailing RefreshItemsLiveness() calls (via DeleteManagedItem/
        // ReconcileAfterShellCommand) are never mistaken for re-entrancy.
        ShellContextMenuResult result;
        _shellOperationDepth++;
        try
        {
            result = _fileLauncher.ShowContextMenu(item.Path, ownerHwnd, screenX, screenY, interceptDelete: managed);
        }
        finally
        {
            _shellOperationDepth--;
        }

        switch (result.Outcome)
        {
            case ShellContextMenuOutcome.RenameRequested:
                BeginRename(item);
                break;

            case ShellContextMenuOutcome.DeleteRequested:
                DeleteManagedItem(item);
                break;

            case ShellContextMenuOutcome.Invoked:
                ReconcileAfterShellCommand(item, entriesBefore, result.Verb);
                break;

            case ShellContextMenuOutcome.NotShown:
            case ShellContextMenuOutcome.Dismissed:
            default:
                break; // nothing on disk changed through the menu.
        }
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
        // ADR-036 (release-hardening-1.0.md §E): a raw path that's already inside app-managed storage
        // (e.g. dragged directly out of PanelItems via Explorer, rather than through the in-app
        // drag-out session — see AddTransferredItem for the legitimate cross-panel path) is rejected
        // outright, never MoveIn'd again — doing so would re-adopt it under a brand-new GUID directory
        // and orphan whichever manifest entry (and panel) already owns it, producing a duplicate
        // reference to the same managed copy.
        var sources = paths
            .Where(path => !string.IsNullOrWhiteSpace(path) && !_itemStorage.IsManaged(path))
            .ToArray();
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
    /// Adds a single item this panel just received as an in-app drag-out transfer from a DIFFERENT
    /// panel (ADR-036, release-hardening-1.0.md §E — see <c>IPanelItemDragOutSession</c>). Deliberately
    /// does <b>not</b> go through <see cref="IPanelItemStorageService.MoveIn"/>/<c>MoveInDirectory</c>
    /// like <see cref="AddDroppedFiles"/> does — <paramref name="path"/> is either already inside the
    /// shared app-managed storage folder (the source panel's own managed copy — moving it again would
    /// orphan the manifest entry the source panel's <see cref="RemoveTransferredItem"/> is about to
    /// stop referencing) or a plain referenced-in-place path (never managed to begin with, same as any
    /// other reference). Either way, this panel simply starts referencing the exact same path.
    /// </summary>
    /// <param name="path">The transferred item's path (managed or referenced-in-place).</param>
    public void AddTransferredItem(string path)
    {
        // Defense in depth: ChildWindow.OnDrop already refuses a drop entirely for a Folder Portal
        // before this is ever reached.
        if (IsPortal) return;

        AddFiles([path]);
    }

    /// <summary>
    /// Removes <paramref name="item"/> from this panel because a DIFFERENT panel just accepted it as an
    /// in-app drag-out transfer (ADR-036 §E). Never calls
    /// <see cref="IPanelItemStorageService.Restore"/>/<see cref="IPanelItemStorageService.ReleaseToRecycleBin"/>:
    /// the underlying managed copy (if any) is shared storage that now belongs to the target panel's own
    /// reference — this panel only ever stops pointing at it.
    /// </summary>
    /// <param name="item">The item that was just transferred away.</param>
    public void RemoveTransferredItem(FileItem item)
    {
        RemoveFile(item);
        FilesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Removes <paramref name="item"/> from this panel after an external drag-out (Explorer/the desktop,
    /// ADR-036 §E) was independently confirmed to have actually moved the file away from its original
    /// path (see <c>MovedOutVerifier</c>'s 0/500/2000&#160;ms existence-recheck — never trusts the raw
    /// OLE drop effect). Tells the storage service to forget the (now-gone) path via
    /// <see cref="IPanelItemStorageService.ForgetMovedOut"/> — a manifest/GUID-directory-only cleanup
    /// that never deletes a file itself: the file is already gone by the shell's own hand (its move), not
    /// this application's.
    /// </summary>
    /// <param name="item">The item confirmed to have moved out of its original location.</param>
    public void RemoveMovedOutItem(FileItem item)
    {
        _itemStorage.ForgetMovedOut(item.Path);
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
        Model = model; // subscribes to Model.PropertyChanged; unsubscribes any previous model first

        // ADR-046 D-6: the panel's background icon-load priority follows the model's own visibility
        // (current page vs. hidden) — set BEFORE ReplaceAll below submits every item's icon request, so
        // the very first submit already lands in the right priority level instead of a second
        // re-prioritization once ChildWindow.IsVisibleChanged fires.
        IconLoadPriority = model.IsVisible ? IconLoadPriority.Visible : IconLoadPriority.Background;

        // Icon pixel size of the NEW model (view mode/IconSize) before ReplaceAll fetches every icon —
        // without refreshing the outgoing items, which ReplaceAll is about to discard anyway.
        UpdateRequestedIconPixelSize(refreshExisting: false);

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
    /// Updates this panel's background icon-load priority (ADR-046 D-6) to reflect whether its window is
    /// currently shown. Called from <c>ChildWindow.IsVisibleChanged</c> — a page switch hides every panel
    /// not on the destination page (and shows those that are) without going through
    /// <see cref="LoadFromModel"/> again. Becoming visible also gives any item whose placeholder-retry
    /// budget was exhausted while this panel was in the background one more chance (UX-MINOR, review it.2,
    /// M-1 follow-up) — see <see cref="BaseViewModel.ResetExhaustedPlaceholderRetries"/>.
    /// </summary>
    /// <param name="isVisible"><see langword="true"/> when this panel's window just became visible.</param>
    internal void SetIconLoadVisibility(bool isVisible)
    {
        IconLoadPriority = isVisible ? IconLoadPriority.Visible : IconLoadPriority.Background;
        if (isVisible) ResetExhaustedPlaceholderRetries();
    }

    /// <summary>
    /// Re-aligns this panel with whatever a shell command just did to its files, synchronously, on the UI
    /// thread, when the command's target is not known (see the three-argument overload for the details).
    /// </summary>
    internal void ReconcileAfterShellCommand() => ReconcileAfterShellCommand(null, null, null);

    /// <summary>
    /// Keeps looking — for a bounded number of liveness ticks after a shell command on a managed item — for entries
    /// that command creates next to the item AFTER it returned (the zip folder and archivers write on a background
    /// thread/process; an entry that is still open for writing, or a directory still changing, is not adopted until
    /// it settles), and adopts them as new panel items. Called on every liveness tick on the UI thread; costs one
    /// small directory listing per tick while a watch is pending, nothing otherwise.
    /// </summary>
    /// <remarks>
    /// The watch ends when its tick budget is spent, when a new shell command starts one of its own, or when the
    /// item left the panel or vanished (the vanished case is handled by <see cref="OnItemsVanished"/> WITHOUT the
    /// snapshot — an entry created meanwhile must never be taken for the renamed item). Known limitation: an
    /// entry that is still being written when the watch expires stays in the hidden per-item directory until the
    /// next shell command on the same item picks it up.
    /// </remarks>
    internal void ProcessPendingSiblingAdoption()
    {
        // M-3 (review it.2): this is reached through RefreshExistence's dispatched, asynchronous tick —
        // Dispose may have run while this tick's own liveness pass was still in flight. Never touch the
        // watch state (or AddFiles/CompleteReconcile below) on an already-torn-down panel.
        if (_disposed) return;

        // M-A guard (release-polish 1.0 final review): see _shellOperationDepth's remarks — never touch the
        // watch state while a shell operation's own modal loop is mid-flight on this panel.
        if (_shellOperationDepth > 0) return;
        if (_siblingWatch is not { } watch) return;

        watch.TicksRemaining--;
        if (watch.TicksRemaining < 0 || IsPortal || !AllItems.Contains(watch.Item) || !PathExists(watch.Item.Path))
        {
            _siblingWatch = null;
            return;
        }

        var result = _itemStorage.ReconcileExternalChange(watch.Item.Path, watch.EntriesBefore);
        var change = ApplyReconcileResult(watch.Item, result);
        if (result.AdoptedPaths.Count > 0)
        {
            AddFiles([.. result.AdoptedPaths]);
            change |= ReconcileChange.Added;
        }

        // The item itself changed after all (it vanished between the existence check and the reconcile): stop.
        if ((change & (ReconcileChange.Renamed | ReconcileChange.Removed)) != 0) _siblingWatch = null;

        CompleteReconcile(change);
    }

    /// <summary>
    /// M-C fix (release-polish 1.0 final review; ADR-046 7c §7.2 amendment): confirms, before ever
    /// releasing a managed item's per-item directory, that an item <see cref="OnItemsVanished"/> reported
    /// missing is STILL missing at least <see cref="ExistenceCheckIntervalMs"/> after that first
    /// observation — see <see cref="_pendingVanishedManagedItems"/>'s own remarks for why. Must run BEFORE
    /// <see cref="BaseViewModel.RefreshItemsLiveness"/> on every tick: an item <see cref="OnItemsVanished"/>
    /// adds to <see cref="_pendingVanishedManagedItems"/> during THIS tick's own (asynchronous)
    /// <see cref="BaseViewModel.RefreshItemsLiveness"/> call cannot possibly have aged past the grace
    /// window yet (its timestamp IS roughly "now"), so it is naturally left untouched here regardless —
    /// no separate "skip this tick's own additions" bookkeeping is needed. The snapshot-based
    /// post-shell-command path
    /// (<see cref="ReconcileAfterShellCommand(FileItem?, IReadOnlyList{string}?, string?)"/>'s own reconcile
    /// loop) is entirely unaffected — it never touches this set.
    /// </summary>
    /// <remarks>
    /// <b>Why a fixed time window, not "one tick" (ADR-046 7c):</b> liveness became asynchronous in 7c —
    /// a probe pass may complete anywhere from well under a tick to (bounded by
    /// <c>BaseViewModel.LivenessPassTimeout</c>) several ticks later, depending on how busy the probe
    /// queue or a slow network root is. Confirming "still missing" the moment the NEXT tick happens to be
    /// dispatched — rather than after a fixed <see cref="ExistenceCheckIntervalMs"/> has actually
    /// elapsed since the original observation — could shrink an editor's brief delete/recreate-on-save
    /// window to a few milliseconds if two passes happen to land close together, defeating the whole
    /// point of the grace period. Checking <see cref="FileItem.Exists"/> (already kept fresh by every
    /// completed liveness pass) instead of re-probing the file system directly here is also what keeps
    /// this method itself off the UI-thread-fs-access list (ADR-046 C-3).
    /// </remarks>
    internal void ProcessPendingVanishedManagedItems()
    {
        if (_pendingVanishedManagedItems.Count == 0) return;

        var now = _timeProvider.GetUtcNow();
        var change = ReconcileChange.None;
        List<FileItem>? resolved = null;

        foreach (var (item, observedAt) in _pendingVanishedManagedItems)
        {
            if (!AllItems.Contains(item) || item.Exists)
            {
                // Reappeared (a later liveness pass already confirmed it, e.g. an external save-by-recreate)
                // or already left the panel some other way — nothing to reconcile; just stop watching it.
                (resolved ??= []).Add(item);
                continue;
            }

            if (now - observedAt < TimeSpan.FromMilliseconds(ExistenceCheckIntervalMs)) continue; // grace window not elapsed yet

            // Still missing after the grace window — confirmed, not a race with a recreate.
            change |= ApplyReconcileResult(item, _itemStorage.ReconcileExternalChange(item.Path));
            (resolved ??= []).Add(item);
        }

        if (resolved is not null)
        {
            foreach (var item in resolved) _pendingVanishedManagedItems.Remove(item);
        }

        CompleteReconcile(change);
    }

    /// <summary>
    /// Re-validates <see cref="FileItem.Exists"/>/<see cref="FileItem.IsDirectory"/> for every item in
    /// this panel against the current file-system state — off the UI thread, via
    /// <see cref="BaseViewModel.RefreshItemsLivenessAsync"/> (ADR-046 7c) — and re-resolves the icon for
    /// any item that just reappeared. Runs on a thread-pool timer; marshals to the UI thread before
    /// touching <see cref="FileItem"/> instances (they're bound to the live UI) and must never throw — an
    /// exception escaping a <see cref="System.Threading.Timer"/> callback is an UNHANDLED exception on a
    /// thread-pool thread, which terminates the whole process.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <see langword="try"/>/<see langword="catch"/> around <see cref="BaseViewModel.RefreshItemsLivenessAsync"/>
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

        var queued = UiDispatch.TryBeginInvoke(Application.Current?.Dispatcher, async () =>
        {
            try
            {
                // M-A guard (release-polish 1.0 final review): _shellOperationDepth > 0 means a shell
                // operation's own modal loop (the context menu or RecycleManagedItem's SHFileOperation) is
                // still pumping messages on this panel's UI thread RIGHT NOW — this dispatched tick reached
                // us through that same nested pump. Skip the whole pass rather than just OnItemsVanished
                // below: RefreshItemsLivenessAsync would otherwise flip a temporarily-vanished item's Exists
                // to false without ever reporting the transition (a transition is only ever reported once —
                // the very next tick would see it as "already missing", not a new one), permanently losing
                // the M5b reconcile for it. Deferred entirely to the first tick after the shell operation
                // finishes instead.
                if (_disposed || _shellOperationDepth > 0) return;

                try
                {
                    // M-C fix: must run BEFORE RefreshItemsLivenessAsync — see ProcessPendingVanishedManagedItems's
                    // own remarks for why the ordering is load-bearing. ADR-046 7c: the single-flight slot
                    // below is held for the ENTIRE async pass (icon queue's own generation/CanApplyLivenessResults
                    // guards handle a Dispose/shell-operation race mid-await), not just the synchronous prefix.
                    ProcessPendingVanishedManagedItems();
                    await RefreshItemsLivenessAsync();

                    // M-3 (review it.2): re-check after the await, not just before it — Dispose or a new
                    // shell operation may have started while this tick's liveness pass was in flight.
                    // ProcessPendingSiblingAdoption already re-checks _shellOperationDepth itself, but not
                    // _disposed; skip the whole call here rather than let it touch a torn-down panel's
                    // watch state.
                    if (_disposed || _shellOperationDepth > 0) return;

                    ProcessPendingSiblingAdoption();
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
                // queued — and only once the awaited pass above has actually completed (or been discarded/
                // faulted), never merely been dispatched (ADR-046 7c).
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
    /// <see langword="true"/> when a file or a directory exists at <paramref name="path"/>.
    /// </summary>
    private static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>
    /// M-B (release-polish 1.0 final review): <see langword="true"/> when <paramref name="verb"/> is a
    /// known member of <see cref="NonCreatingVerbs"/>. An unreported verb (<see langword="null"/> — a shell
    /// extension that doesn't implement <c>GCS_VERBW</c>) is NOT treated as non-creating: the watch still
    /// starts, same as before this fix, since there's no way to tell it apart from a genuinely
    /// file-creating verb.
    /// </summary>
    private static bool IsNonCreatingVerb(string? verb) => verb is not null && NonCreatingVerbs.Contains(verb);

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
            AppLog.Current.Error(
                "ChildWindowViewModel",
                "ReadFolderFailed",
                ex,
                new KeyValuePair<string, object?>("path", folder));
            return [];
        }
    }

    /// <summary>
    /// Re-aligns this panel with whatever a shell command just did to its files, synchronously, on the UI thread.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The command's own <paramref name="target"/>, when it is a per-item managed item with a before-command
    /// snapshot (<paramref name="targetEntriesBefore"/>), is reconciled against that snapshot
    /// (<see cref="IPanelItemStorageService.ReconcileExternalChange(string, IReadOnlyCollection{string})"/>): entries
    /// the command CREATED next to a still-existing item are adopted and shown as new panel items; a vanished item
    /// with exactly one new entry next to it was renamed and is relocated in place (same
    /// <see cref="BaseViewModel.TryRenameItem"/> + content-filter re-run the inline rename uses); a vanished item
    /// with nothing new was deleted or moved away and leaves the panel. Entries still being written when the
    /// command returns keep being looked for on the next liveness ticks (<see cref="ProcessPendingSiblingAdoption"/>).
    /// </para>
    /// <para>
    /// Every OTHER item whose path no longer exists gets the snapshot-less reconcile
    /// (<see cref="IPanelItemStorageService.ReconcileExternalChange(string)"/>): only "completely gone" is acted on.
    /// Anything else (a referenced-in-place original, a legacy flat item, an ambiguous folder) is left as it is and
    /// keeps showing as missing. Raises <see cref="FilesChanged"/> once when anything changed, then runs the regular
    /// existence refresh (<see cref="BaseViewModel.RefreshItemsLiveness"/>) right away instead of waiting for the
    /// next timer tick. A Folder Portal skips the reconcile (its watcher mirrors the real folder) but still gets
    /// the refresh.
    /// </para>
    /// </remarks>
    /// <param name="target">The item the command ran on, or <see langword="null"/> when unknown.</param>
    /// <param name="targetEntriesBefore">
    /// <paramref name="target"/>'s per-item directory before the command
    /// (<see cref="IPanelItemStorageService.GetItemDirectoryEntries"/>), or <see langword="null"/>/empty when it is not a
    /// per-item managed item.
    /// </param>
    /// <param name="verb">
    /// The invoked command's canonical shell verb (<see cref="ShellContextMenuResult.Verb"/>), or
    /// <see langword="null"/> when unknown/unreported. M-B (release-polish 1.0 final review): only a verb
    /// that could plausibly create a new file next to the item starts the trailing
    /// <see cref="ProcessPendingSiblingAdoption"/> watch below — a verb from <see cref="NonCreatingVerbs"/>
    /// (Open, Properties, …) never does, so there is nothing further to look for after the immediate
    /// snapshot diff above already ran. The immediate diff itself is unaffected by <paramref name="verb"/>:
    /// it always runs for every invoked command, same as before this fix.
    /// </param>
    private void ReconcileAfterShellCommand(FileItem? target, IReadOnlyList<string>? targetEntriesBefore, string? verb)
    {
        var hasSnapshot = target is not null && targetEntriesBefore is { Count: > 0 };
        _siblingWatch = null; // a new command supersedes whatever the previous one might still create.

        if (!IsPortal)
        {
            var change = ReconcileChange.None;
            List<string>? adopted = null;

            foreach (var item in AllItems.ToArray())
            {
                ManagedItemReconcileResult result;
                if (hasSnapshot && ReferenceEquals(item, target))
                {
                    // Also for a target that still exists: that is exactly when created siblings are adopted.
                    result = _itemStorage.ReconcileExternalChange(item.Path, targetEntriesBefore!);
                }
                else if (!_itemStorage.IsManaged(item.Path))
                {
                    // B-7 (ADR-046 7c): a non-managed item's ReconcileExternalChange always returns Unchanged
                    // anyway (PanelItemStorageService.TryGetPerItemLayout rejects a non-managed path up
                    // front) — checking IsManaged first (a cheap, local, string-only prefix check) instead of
                    // PathExists below is what keeps this loop from touching the network for a Z:\ or UNC
                    // item merely referenced in place, which was never a candidate for reconcile to begin
                    // with.
                    continue;
                }
                else if (PathExists(item.Path))
                {
                    continue;
                }
                else
                {
                    result = _itemStorage.ReconcileExternalChange(item.Path);
                }

                change |= ApplyReconcileResult(item, result);
                if (result.AdoptedPaths.Count > 0) (adopted ??= []).AddRange(result.AdoptedPaths);
            }

            if (adopted is not null)
            {
                AddFiles([.. adopted]);
                change |= ReconcileChange.Added;
            }

            CompleteReconcile(change);

            if (hasSnapshot && AllItems.Contains(target!) && PathExists(target!.Path) && !IsNonCreatingVerb(verb))
            {
                _siblingWatch = new SiblingAdoptionWatch(target, targetEntriesBefore!, SiblingAdoptionWatchTicks);
            }
        }

        RefreshItemsLiveness();
    }

    /// <summary>
    /// "Delete" chosen in a managed item's shell context menu (intercepted — see
    /// <see cref="ShowShellContextMenu"/>): <see cref="IPanelItemStorageService.RecycleManagedItem"/> moves the item
    /// back to its original location and recycles it from there. A recycled item leaves the panel; an item the
    /// shell did not recycle (cancelled, declined) stays, following it if it now lives at a different path.
    /// </summary>
    /// <param name="item">The item to delete.</param>
    private void DeleteManagedItem(FileItem item)
    {
        // M-A guard (release-polish 1.0 final review): RecycleManagedItem's own SHFileOperation call pumps
        // window messages through its progress/confirmation dialogs — a separate modal loop from the shell
        // context menu's own (already released by the time ShowShellContextMenu's switch calls this method).
        // Only the actual blocking call is guarded — the switch below and its trailing RefreshItemsLiveness()
        // run strictly after RecycleManagedItem returns, with the guard already released.
        ManagedRecycleResult result;
        _shellOperationDepth++;
        try
        {
            result = _itemStorage.RecycleManagedItem(item.Path);
        }
        finally
        {
            _shellOperationDepth--;
        }

        switch (result.Kind)
        {
            case ManagedRecycleKind.Recycled:
                if (ReferenceEquals(EditingItem, item)) EditingItem = null;
                RemoveFile(item);
                FilesChanged?.Invoke(this, EventArgs.Empty);
                break;

            case ManagedRecycleKind.Kept when result.Path is not null
                                              && !string.Equals(result.Path, item.Path, StringComparison.OrdinalIgnoreCase):
                if (AllItems.Contains(item))
                {
                    TryRenameItem(item, result.Path);
                    OnSourceChanged();
                }
                else
                {
                    // M-A fix: a re-entrant liveness tick that snuck in through the nested message pump
                    // while RecycleManagedItem's own SHFileOperation dialog was still showing (now blocked
                    // by _shellOperationDepth, but defense in depth for any other path that could have
                    // removed `item` mid-flight) already dropped `item` from the panel. TryRenameItem
                    // no-ops for an item no longer tracked, which used to silently strand the file the
                    // storage service just moved back to result.Path — add it back as a fresh item instead
                    // of losing it from the panel.
                    AddFiles([result.Path]);
                }

                FilesChanged?.Invoke(this, EventArgs.Empty);
                break;

            case ManagedRecycleKind.Kept:
            case ManagedRecycleKind.NotManaged:
            default:
                break; // nothing changed for the panel.
        }

        RefreshItemsLiveness();
    }

    /// <summary>
    /// Applies one reconcile verdict to <paramref name="item"/>: <see cref="ManagedItemReconcileKind.Renamed"/>
    /// relocates it in place, <see cref="ManagedItemReconcileKind.Deleted"/> removes it (ending an inline rename
    /// of it). Adopted paths are the caller's business.
    /// </summary>
    /// <param name="item">The reconciled item.</param>
    /// <param name="result">The storage service's verdict.</param>
    /// <returns>What changed.</returns>
    private ReconcileChange ApplyReconcileResult(FileItem item, ManagedItemReconcileResult result)
    {
        switch (result.Kind)
        {
            case ManagedItemReconcileKind.Renamed when result.NewPath is not null:
                TryRenameItem(item, result.NewPath);
                return ReconcileChange.Renamed;

            case ManagedItemReconcileKind.Deleted:
                if (ReferenceEquals(EditingItem, item)) EditingItem = null;
                RemoveFile(item);
                return ReconcileChange.Removed;

            case ManagedItemReconcileKind.Unchanged:
            default:
                return ReconcileChange.None;
        }
    }

    /// <summary>
    /// Finishes a reconcile pass: re-runs the content filter once after a rename (a rename can change the
    /// extension — see <see cref="CommitRename"/>) and raises <see cref="FilesChanged"/> once when anything changed.
    /// </summary>
    /// <param name="change">What the pass changed.</param>
    private void CompleteReconcile(ReconcileChange change)
    {
        if ((change & ReconcileChange.Renamed) != 0) OnSourceChanged();
        if (change != ReconcileChange.None) FilesChanged?.Invoke(this, EventArgs.Empty);
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
    /// Synchronizes <see cref="Pages"/> with <see cref="IPageManager.PageDescriptors"/> — called once from
    /// the constructor (when <see cref="_pageManager"/> is supplied) and on every subsequent
    /// <see cref="IPageManager.PagesChanged"/>. Delegates to <see cref="PageItemViewModelSync"/> — the
    /// exact same add/remove/reorder/rename-in-place logic <see cref="MainWindowViewModel.SyncPages"/> uses
    /// for its own, larger <c>Pages</c> collection.
    /// </summary>
    private void SyncPages()
    {
        if (_pageManager is null) return;
        PageItemViewModelSync.Sync(Pages, _pageManager.PageDescriptors);
    }

    private void OnPagesChanged() => SyncPages();

    /// <summary>
    /// Backs <see cref="MovePanelToPageCommand"/> — the header context menu's "Move to page ▸" submenu.
    /// Mirrors <c>MainWindowViewModel.MovePanelToPage</c>, acting on THIS panel's own <see cref="Model"/>
    /// instead of a <c>SelectedPanel</c>. No confirmation/notification here (UX review, backlog 1.x item
    /// 8): unlike the Settings window's "Panels" section, the effect — this panel disappearing from the
    /// desktop — is immediately visible where the user just clicked, the same as Fences' own "Send Fence to
    /// Page" gives no toast either.
    /// </summary>
    /// <param name="pageId">The destination page identifier.</param>
    private void MovePanelToPage(string? pageId)
    {
        if (_windowManager is null || string.IsNullOrWhiteSpace(pageId)) return;
        _windowManager.MovePanelToPage(Model, pageId);
    }

    private bool CanMovePanelToPage(string? pageId)
        => _windowManager is not null && !string.IsNullOrWhiteSpace(pageId) && pageId != Model.PageId;

    /// <summary>
    /// Backs <see cref="CopyPanelToPageCommand"/> — the header context menu's "Copy to page ▸" submenu
    /// (ADR-045). Mirrors <c>MainWindowViewModel.CopyPanelToPage</c>, acting on THIS panel's own
    /// <see cref="Model"/>; unlike <see cref="MovePanelToPage"/>, a same-page destination is expected (that
    /// is how a same-page duplicate is requested).
    /// </summary>
    /// <remarks>
    /// H-1 fix (code review, panel clone to page, backlog 1.x wave 3 review): this used to call
    /// <see cref="IWindowManager.ClonePanelToPage"/> directly and discard its result outright — a failure
    /// (too large, no disk space, copy failed) never reached the user from this menu, unlike the Settings
    /// window's identically-named command. Delegating to the shared <see cref="_panelCloneFlow"/> instead
    /// gives this call site the exact same busy cursor, localized failure notification and stale-page
    /// silent re-sync as <c>MainWindowViewModel.CopyPanelToPage</c> — see <see cref="IPanelCloneFlow"/>'s
    /// own remarks. A successful clone's own selection/refresh (only meaningful for the Settings window's
    /// panel LIST) is still not this method's concern: the effect here is immediately visible on the
    /// desktop, same no-extra-feedback rationale as <see cref="MovePanelToPage"/>.
    /// </remarks>
    /// <param name="pageId">The destination page identifier.</param>
    private void CopyPanelToPage(string? pageId)
    {
        if (_panelCloneFlow is null || string.IsNullOrWhiteSpace(pageId)) return;
        _panelCloneFlow.CopyToPage(Model, pageId, onUnknownPage: SyncPages);
    }

    private bool CanCopyPanelToPage(string? pageId) => _panelCloneFlow is not null && !string.IsNullOrWhiteSpace(pageId);

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
        if (_pageManager is not null) _pageManager.PagesChanged -= OnPagesChanged;

        // Releases this panel's claim on the coordinator's single, process-wide _currentOwner slot, if
        // it still holds it — otherwise a closed panel (and its whole FileItems/watcher graph) stays
        // reachable through the coordinator singleton until some other panel happens to claim a
        // selection (finalization MAJOR M-1). Unconditional and idempotent: Release() itself no-ops for
        // a panel that never became the owner.
        _selectionCoordinator.Release(this);

        // Cancels a pending debounced icon re-fetch (see ScheduleIconRefetch) — a disposed panel's
        // FileItems are about to become unreachable; a late timer tick re-fetching their icons anyway
        // would just be wasted work, not a crash (RefreshAllItemIcons only touches AllItems), but there's
        // no reason to let it run. Mirrors SaveScheduler.Flush's own `_timerProvider()?.Stop()`.
        //
        // MINOR 8 (release-polish 1.0 final review): the production default provider
        // (ResolveDefaultIconResizeDebounceTimer) LAZILY CREATES a DispatcherDebounceTimer on first call —
        // calling it here unconditionally used to construct one from scratch just to immediately stop it,
        // for every panel that closes having never actually resized an icon. Read the existing field
        // directly for that case instead; a test-supplied provider never lazily creates, so it's still
        // safe (and necessary — there's no shared field to read for it) to just call it.
        if (_usesDefaultIconResizeDebounceTimerProvider)
        {
            _defaultIconResizeDebounceTimer?.Stop();
        }
        else
        {
            _iconResizeDebounceTimerProvider()?.Stop();
        }

        // ADR-046: cancels a pending debounced content-filter re-run the same way — see
        // OnProbeResultsApplied/ResolveProbeRefilterDebounceTimer. M-6 (review it.2): now mirrors the icon
        // resize timer's own provider/default split above — a test-supplied provider never lazily creates,
        // so it's safe to just call it; the production default is read directly to avoid constructing one
        // from scratch just to immediately stop it.
        if (_usesDefaultProbeRefilterDebounceTimerProvider)
        {
            _probeRefilterDebounceTimer?.Stop();
        }
        else
        {
            _probeRefilterDebounceTimerProvider()?.Stop();
        }

        // ADR-046: releases this panel's background icon-load group — cancels anything still pending and
        // guarantees no further delivery to an about-to-be-unreachable ViewModel.
        DisposeIconLoading();

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

    /// <summary>
    /// Notes app-managed items the liveness check just saw go missing (M5b, release-polish 1.0 review): Cut
    /// in an item's shell context menu followed by Paste elsewhere moves the managed copy away AFTER the
    /// menu closed, which <see cref="ReconcileAfterShellCommand()"/> cannot see. Only the present → missing
    /// transition is reported (see <see cref="BaseViewModel.OnItemsVanished"/>), so stable items cost
    /// nothing; only managed paths are tracked.
    /// </summary>
    /// <remarks>
    /// M-C fix (release-polish 1.0 final review): this used to reconcile (and, for a genuinely empty
    /// per-item directory, release/delete it) immediately, on the very first tick an item was seen missing.
    /// Some external editors save "by recreate" — write a temp file, delete the original, then rename the
    /// temp back to the original name — which is a real, if brief, delete/recreate cycle a snapshot-less
    /// liveness check cannot tell apart from an actual Cut+Paste-elsewhere. Reconciling on that very first
    /// tick could delete the per-item GUID directory out from under the recreate landing a moment later.
    /// This method now only ADDS the item to <see cref="_pendingVanishedManagedItems"/> — the actual
    /// reconcile is deferred to <see cref="ProcessPendingVanishedManagedItems"/>, run on the NEXT tick, which
    /// only acts once the item has been confirmed missing across two consecutive ticks.
    /// </remarks>
    /// <param name="items">The items that just went missing.</param>
    protected override void OnItemsVanished(IReadOnlyList<FileItem> items)
    {
        // M-A guard (release-polish 1.0 final review): see _shellOperationDepth's remarks — defense in
        // depth (ADR-046 7c's CanApplyLivenessResults override already refuses to call this at all while
        // a shell operation's own modal loop is mid-flight on this panel, but kept here too for any other
        // future caller).
        if (IsPortal || _shellOperationDepth > 0) return;

        // ADR-046 7c §7.2: the timestamp marks the moment this present→missing transition was first
        // observed (this call) — ProcessPendingVanishedManagedItems' grace window starts here, not at
        // whatever later tick happens to dispatch next.
        //
        // L-6 (review it.2, left as-is by design): under the async pipeline, "this call" happens at RESULT
        // APPLICATION time — after RunSingleLivenessPassAsync's off-thread IItemProbeQueue.ProbeAsync call
        // already returned — not at the moment the probe itself actually read the file system (which could
        // be anywhere from sub-millisecond to, bounded, just under LivenessPassTimeout earlier). This is
        // intentionally NOT "corrected" to the probe's own read time: doing so would only ever SHRINK the
        // effective M-C grace window an external editor's delete/recreate-on-save gets (the apply-time
        // stamp is always >= the true observation time), which is the safe direction for what this window
        // protects against. The window is measured from when THIS ViewModel found out, not from an
        // unknowable-to-it exact file-system-read instant.
        var observedAt = _timeProvider.GetUtcNow();
        foreach (var item in items)
        {
            if (_itemStorage.IsManaged(item.Path)) _pendingVanishedManagedItems.TryAdd(item, observedAt);
        }
    }

    /// <summary>
    /// ADR-046 7c: refuses to apply a just-completed liveness pass while this panel is disposed or a
    /// shell operation's own modal loop (<see cref="ShowShellContextMenu"/>/<see cref="DeleteManagedItem"/>)
    /// is mid-flight — the same two conditions the pre-7c synchronous pipeline checked BEFORE starting a
    /// pass at all (see <see cref="_shellOperationDepth"/>'s remarks), now checked at apply time instead,
    /// since an asynchronous pass may have already started probing before either condition became true.
    /// </summary>
    protected override bool CanApplyLivenessResults => !_disposed && _shellOperationDepth == 0;

    /// <summary>
    /// L-1 (review it.2): routes an otherwise-unobserved <see cref="BaseViewModel.RefreshItemsLiveness"/>
    /// task fault to the same structured diagnostics log/event <see cref="RefreshExistence"/>'s own
    /// dispatched tick already uses for the identical failure mode — privacy invariant unchanged
    /// (exception TYPE NAME only, never <see cref="Exception.Message"/>, which may embed a file path).
    /// </summary>
    /// <param name="exception">The exception the faulted pass completed with.</param>
    protected override void OnLivenessPassFaulted(Exception exception)
        => _diagnostics.ExistenceRefreshFailed(Model.Id, exception.GetType().Name);

    /// <inheritdoc />
    protected override TimeProvider LivenessTimeProvider => _timeProvider;

    /// <summary>
    /// ADR-046 §6 item 10 (M-7.1): records the elapsed UI-thread rebuild time for this panel.
    /// </summary>
    /// <param name="count">The number of items <see cref="BaseViewModel.AllItems"/> holds after the rebuild.</param>
    /// <param name="elapsedMicroseconds">Elapsed wall-clock time of the rebuild, in microseconds.</param>
    protected override void OnItemsBuilt(int count, long elapsedMicroseconds) =>
        _diagnostics.PanelItemsBuilt(Model.Id, count, elapsedMicroseconds);

    /// <summary>
    /// Debounces this panel's content-filter re-run after a batch of background icon-load results flips
    /// at least one item's <see cref="FileItem.IsDirectory"/> (ADR-046 §6) — a large portal's items are
    /// probed in many small batches as the queue drains them, and each one flipping a directory flag would
    /// otherwise re-run <see cref="ApplyFilterAndSort"/> (and disturb the FoldersOnly/"folders first"
    /// projection) on its own. Degrades to an immediate re-filter when no
    /// <see cref="System.Windows.Application.Current"/> exists yet (headless/test host), same as
    /// <see cref="ScheduleIconRefetch"/>.
    /// </summary>
    /// <param name="directoryFlagsChanged">See <see cref="BaseViewModel.OnProbeResultsApplied"/>.</param>
    protected override void OnProbeResultsApplied(bool directoryFlagsChanged)
    {
        if (!directoryFlagsChanged) return;

        var timer = _probeRefilterDebounceTimerProvider();
        if (timer is null)
        {
            ApplyFilterAndSort();
            return;
        }

        timer.Start(ProbeRefilterDebounceWindow, ApplyFilterAndSort);
    }

    /// <summary>
    /// ADR-046 D-6: a candidate is "currently visible" when it would survive this panel's own content
    /// filter — <see cref="BaseViewModel"/> uses this to submit the icon requests of items the user can
    /// actually see before the ones a filter is hiding.
    /// </summary>
    /// <param name="item">The candidate item, already added to <see cref="BaseViewModel.AllItems"/>.</param>
    protected override bool IsItemCurrentlyVisible(FileItem item) => BuildFilterPredicate(Model.ContentFilter)(item);

    /// <summary>
    /// L-5 (review it.2): builds this panel's content-filter predicate ONCE per <c>AddFilesCore</c> batch
    /// instead of <see cref="IsItemCurrentlyVisible"/>'s own per-item <see cref="BuildFilterPredicate"/>
    /// call — a Folder Portal adding a thousand files at once otherwise re-switches on
    /// <see cref="ChildWindowModel.ContentFilter"/> a thousand times for what is, within a single batch,
    /// always the same answer.
    /// </summary>
    protected override Func<FileItem, bool> CreateVisibilityPredicate() => BuildFilterPredicate(Model.ContentFilter);

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
            AppLog.Current.Error(
                "ChildWindowViewModel",
                "ReadFolderFailed",
                ex,
                new KeyValuePair<string, object?>("path", model.SourceFolderPath));
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
    /// <remarks>
    /// M-1 fix (code review, panel clone to page): <see cref="IPanelItemStorageService.TryFindManagedPath"/>
    /// only guarantees the candidate is unambiguous in the MANIFEST — it has no visibility into whether some
    /// OTHER live panel already directly references that exact same managed path (e.g. after
    /// <see cref="IWindowManager.ClonePanelToPage"/> gave a second panel its own live copy of this stale
    /// entry's original path). Without this check, this panel's OWN, unrelated stale entry could be healed
    /// onto a path that in fact belongs to that other panel — silently making two panels reference the same
    /// managed item, defeating <see cref="WindowManager.ResolveStalePanelPaths"/>'s cross-panel "claimed"
    /// guard (which only ever runs once, up front, at startup — this heal runs on every model
    /// load/reload). A candidate already claimed by another panel is treated exactly like "no live
    /// candidate" — the entry is left stale rather than silently pointed at the wrong panel's copy.
    /// </remarks>
    private string[] ResolveManagedReplacements(string[] paths)
    {
        string[]? resolved = null;
        IReadOnlySet<string>? claimedByOtherPanels = null;

        for (var i = 0; i < paths.Length; i++)
        {
            var path = paths[i];
            if (string.IsNullOrWhiteSpace(path)) continue;

            // B-5 (ADR-046 7c): manifest lookup FIRST — TryFindManagedPath is a purely in-memory dictionary
            // scan (its own File.Exists/Directory.Exists calls, if any, only ever touch LOCAL managed
            // candidates under %LOCALAPPDATA%, never `path` itself) — and only THEN check whether the
            // original `path` still exists. The remap condition itself is unchanged (original missing AND
            // exactly one unclaimed candidate — an AND of two checks, whose result cannot depend on the
            // order they're evaluated in), but a path with no managed candidate at all (the common case for
            // a plain, never-moved-in item, and the ONLY case for a `Z:\`/UNC item referenced in place) no
            // longer pays for a network round-trip it was always going to discard the result of.
            if (!_itemStorage.TryFindManagedPath(path, out var managedPath) || managedPath is null) continue;

            if (_windowManager is not null)
            {
                claimedByOtherPanels ??= _windowManager.GetManagedPathsClaimedByOtherPanels(Model.Id);
                if (claimedByOtherPanels.Contains(managedPath)) continue;
            }

            if (System.IO.File.Exists(path) || System.IO.Directory.Exists(path)) continue; // original still valid — nothing to remap

            resolved ??= (string[])paths.Clone();
            resolved[i] = managedPath;
        }

        return resolved ?? paths;
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChildWindowModel.ViewMode))
            ApplyViewMode();

        // The displayed icon size changed (grid IconSize slider, or list <-> grid): icons are re-requested at
        // the new pixel size — only when that crosses a size bucket (BaseViewModel.SetRequestedIconPixelSize).
        if (e.PropertyName is nameof(ChildWindowModel.ViewMode) or nameof(ChildWindowModel.IconSize))
            UpdateRequestedIconPixelSize(refreshExisting: true);

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
    /// Recomputes the physical pixel size item icons are requested at from the displayed icon size (grid:
    /// <see cref="ChildWindowModel.IconSize"/>, list: <see cref="ListIconSizeDip"/>) and
    /// <see cref="IconRenderScale"/>.
    /// </summary>
    /// <remarks>
    /// M3 fix (release-polish 1.0 review): <see cref="BaseViewModel.SetRequestedIconPixelSize"/> is always
    /// called with <c>refreshExisting: false</c> here — <see cref="BaseViewModel.RequestedIconPixelSize"/>
    /// itself updates synchronously on every call, so a new item added mid-drag (<see cref="BaseViewModel.AddFiles"/>/
    /// <see cref="AddDroppedFiles"/>) is fetched at the CURRENT size right away, never a stale
    /// pre-debounce one. Only the actual re-fetch of every already-tracked item — the expensive part,
    /// previously done synchronously once per crossed size bucket — is deferred through
    /// <see cref="ScheduleIconRefetch"/> when <paramref name="refreshExisting"/> is <see langword="true"/>
    /// and the snapped size actually changed. <see cref="LoadFromModel"/>'s own call
    /// (<paramref name="refreshExisting"/>: <see langword="false"/>) needs neither branch — it runs right
    /// before <see cref="BaseViewModel.ReplaceAll"/> discards whatever items are currently tracked anyway.
    /// </remarks>
    /// <param name="refreshExisting">Whether already-loaded items are (debounced-)re-fetched when the size changed.</param>
    private void UpdateRequestedIconPixelSize(bool refreshExisting)
    {
        var sizeDip = Model.ViewMode == PanelViewMode.Grid ? Model.IconSize : ListIconSizeDip;
        var pixelSize = IconPixelSize.FromDip(sizeDip, _iconRenderScale);
        var changed = SetRequestedIconPixelSize(pixelSize, refreshExisting: false);

        if (changed && refreshExisting) ScheduleIconRefetch();
    }

    /// <summary>
    /// Defers <see cref="BaseViewModel.RefreshAllItemIcons"/> to <see cref="IconResizeDebounceWindow"/>
    /// after the last call (M3, release-polish 1.0 review): a burst of calls — the icon-size slider
    /// crossing several buckets while being dragged, or every panel reacting to the same monitor DPI
    /// change — collapses into a single re-fetch, using whatever <see cref="BaseViewModel.RequestedIconPixelSize"/>
    /// happens to be when the debounce window finally elapses. Degrades to an immediate synchronous
    /// re-fetch when <see cref="_iconResizeDebounceTimerProvider"/> has no timer available (e.g. no
    /// <see cref="System.Windows.Application.Current"/> — headless/test host without an injected fake).
    /// </summary>
    private void ScheduleIconRefetch()
    {
        var timer = _iconResizeDebounceTimerProvider();
        if (timer is null)
        {
            RefreshAllItemIcons();
            return;
        }

        timer.Start(IconResizeDebounceWindow, RefreshAllItemIcons);
    }

    /// <summary>
    /// Default <c>iconResizeDebounceTimerProvider</c> (production, via DI): lazily creates — and reuses
    /// for the rest of this ViewModel's lifetime — a single <see cref="DispatcherDebounceTimer"/> bound to
    /// the current <see cref="System.Windows.Application.Current"/> dispatcher. Returns <see langword="null"/>
    /// when no <see cref="System.Windows.Application"/> exists yet, matching
    /// <c>ApplicationStateService.ResolveDebounceTimer</c>'s own degrade-to-immediate-refetch contract.
    /// </summary>
    private IDebounceTimer? ResolveDefaultIconResizeDebounceTimer()
    {
        if (Application.Current is not { } app) return null;
        return _defaultIconResizeDebounceTimer ??= new DispatcherDebounceTimer(app.Dispatcher);
    }

    /// <summary>
    /// Lazily creates — and reuses for the rest of this ViewModel's lifetime — the
    /// <see cref="DispatcherDebounceTimer"/> backing <see cref="OnProbeResultsApplied"/>. Returns
    /// <see langword="null"/> when no <see cref="System.Windows.Application"/> exists yet, same
    /// degrade-to-immediate contract as <see cref="ResolveDefaultIconResizeDebounceTimer"/>.
    /// </summary>
    private IDebounceTimer? ResolveProbeRefilterDebounceTimer()
    {
        if (Application.Current is not { } app) return null;
        return _probeRefilterDebounceTimer ??= new DispatcherDebounceTimer(app.Dispatcher);
    }

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

    /// <summary>
    /// What one reconcile pass changed in the panel (see <see cref="CompleteReconcile"/>).
    /// </summary>
    [Flags]
    private enum ReconcileChange
    {
        /// <summary>Nothing.</summary>
        None = 0,

        /// <summary>An item was relocated in place.</summary>
        Renamed = 1,

        /// <summary>An item was removed.</summary>
        Removed = 2,

        /// <summary>Adopted entries were added as new items.</summary>
        Added = 4,
    }

    /// <summary>
    /// A pending look-out for entries a shell command creates next to a managed item after it returned — see
    /// <see cref="ProcessPendingSiblingAdoption"/>.
    /// </summary>
    /// <param name="item">The item the command ran on.</param>
    /// <param name="entriesBefore">Its per-item directory before the command.</param>
    /// <param name="ticksRemaining">Liveness ticks left before the watch expires.</param>
    private sealed class SiblingAdoptionWatch(FileItem item, IReadOnlyList<string> entriesBefore, int ticksRemaining)
    {
        /// <summary>Gets the item the command ran on.</summary>
        public FileItem Item { get; } = item;

        /// <summary>Gets its per-item directory entries from before the command.</summary>
        public IReadOnlyList<string> EntriesBefore { get; } = entriesBefore;

        /// <summary>Gets or sets the number of liveness ticks left before the watch expires.</summary>
        public int TicksRemaining { get; set; } = ticksRemaining;
    }
}
