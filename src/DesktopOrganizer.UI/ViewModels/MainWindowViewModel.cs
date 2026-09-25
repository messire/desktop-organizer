using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Commands;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;

namespace DesktopOrganizer.UI.ViewModels;

/// <summary>
/// ViewModel for the Settings/Config window. Owns the panel/page collection and CRUD, autostart, and
/// desktop-icons-visibility; delegates per-concern settings to sub-ViewModels extracted per
/// REFACTORING_PLAN.md §5.3: <see cref="SelectedPanelSettings"/> (selected panel's editable
/// appearance/sort settings), <see cref="GlobalDefaults"/> (grid/snap/default-appearance settings), and
/// <see cref="SystemIconsSettings"/> (the System Icons Panel feature). All three are constructed
/// internally (not DI-injected) and their identically-named properties are forwarded 1:1 by this class,
/// with their <see cref="INotifyPropertyChanged.PropertyChanged"/> notifications re-raised verbatim —
/// so <c>MainWindow.xaml</c>'s existing flat bindings against the root ViewModel, and this class's own
/// public surface/constructor signature, are unchanged by the extraction.
/// </summary>
public class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly IWindowManager _windowManager;
    private readonly IAppSettings _appSettings;
    private readonly IAutostartService _autostart;
    private readonly IPageManager _pageManager;
    private readonly IConfirmationService _confirmation;
    private readonly IPanelItemStorageService _itemStorage;
    private readonly IDesktopIconsService _desktopIcons;
    private readonly ILayoutEditModeService _layoutEditMode;
    private ChildWindowModel? _selectedPanel;

    /// <summary>
    /// ViewModel for the "Layout snapshots" section.
    /// </summary>
    public LayoutSnapshotsViewModel LayoutSnapshots { get; }

    /// <summary>
    /// Property proxy for the selected panel's editable settings (REFACTORING_PLAN.md §5.3).
    /// </summary>
    public SelectedPanelSettingsViewModel SelectedPanelSettings { get; }

    /// <summary>
    /// Global default settings for new panels and grid/snap behavior (REFACTORING_PLAN.md §5.3).
    /// </summary>
    public GlobalDefaultsViewModel GlobalDefaults { get; }

    /// <summary>
    /// ViewModel for the "System Icons Panel" section (REFACTORING_PLAN.md §5.3).
    /// </summary>
    public SystemIconsSettingsViewModel SystemIconsSettings { get; }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Collection of all desktop panels.
    /// </summary>
    public ObservableCollection<ChildWindowModel> Panels { get; } = [];

    /// <summary>
    /// Ordered collection of page identifiers shown as tabs in the UI.
    /// </summary>
    public ObservableCollection<string> Pages { get; } = [];

    /// <summary>
    /// Available view modes for the panel view-mode selector.
    /// </summary>
    public IReadOnlyList<PanelViewMode> ViewModes { get; } = Enum.GetValues<PanelViewMode>();

    /// <summary>
    /// Available sort modes for the panel sort-order selector.
    /// </summary>
    public IReadOnlyList<PanelSortMode> SortModes { get; } = Enum.GetValues<PanelSortMode>();

    /// <summary>The currently active page identifier.</summary>
    public string CurrentPage => _pageManager.CurrentPage;

    /// <summary>
    /// The panel currently selected in the settings list.
    /// </summary>
    public ChildWindowModel? SelectedPanel
    {
        get => _selectedPanel;
        set
        {
            if (_selectedPanel == value) return;
            _selectedPanel = value;
            OnPropertyChanged(nameof(SelectedPanel));
            SelectedPanelSettings.SetPanel(value);
        }
    }

    /// <summary>Whether a panel is selected (drives enabled state of the editor section).</summary>
    public bool HasSelectedPanel => SelectedPanelSettings.HasSelectedPanel;

    /// <summary>Title of the selected panel.</summary>
    public string SelectedPanelTitle
    {
        get => SelectedPanelSettings.SelectedPanelTitle;
        set => SelectedPanelSettings.SelectedPanelTitle = value;
    }

    /// <summary>Accent colour (hex) of the selected panel.</summary>
    public string SelectedPanelAccentColor
    {
        get => SelectedPanelSettings.SelectedPanelAccentColor;
        set => SelectedPanelSettings.SelectedPanelAccentColor = value;
    }

    /// <summary>Text colour (hex) of the selected panel.</summary>
    public string SelectedPanelTextColor
    {
        get => SelectedPanelSettings.SelectedPanelTextColor;
        set => SelectedPanelSettings.SelectedPanelTextColor = value;
    }

    /// <summary>Surface opacity (0–1) of the selected panel.</summary>
    public double SelectedPanelSurfaceOpacity
    {
        get => SelectedPanelSettings.SelectedPanelSurfaceOpacity;
        set => SelectedPanelSettings.SelectedPanelSurfaceOpacity = value;
    }

    /// <summary>How the selected panel's surface blends with its accent colour.</summary>
    public PanelSurfaceStyle SelectedPanelSurfaceStyle
    {
        get => SelectedPanelSettings.SelectedPanelSurfaceStyle;
        set => SelectedPanelSettings.SelectedPanelSurfaceStyle = value;
    }

    /// <summary>Available surface styles for the selected-panel surface-style selector.</summary>
    public IReadOnlyList<PanelSurfaceStyle> SurfaceStyles => SelectedPanelSettings.SurfaceStyles;

    /// <summary>Icon size (px) of the selected panel.</summary>
    public double SelectedPanelIconSize
    {
        get => SelectedPanelSettings.SelectedPanelIconSize;
        set => SelectedPanelSettings.SelectedPanelIconSize = value;
    }

    /// <summary>View mode of the selected panel.</summary>
    public PanelViewMode SelectedPanelViewMode
    {
        get => SelectedPanelSettings.SelectedPanelViewMode;
        set => SelectedPanelSettings.SelectedPanelViewMode = value;
    }

    /// <summary>Sort order of the selected panel.</summary>
    public PanelSortMode SelectedPanelSortMode
    {
        get => SelectedPanelSettings.SelectedPanelSortMode;
        set => SelectedPanelSettings.SelectedPanelSortMode = value;
    }

    /// <summary>
    /// Whether the selected panel supports a manual (drag-to-reorder) sort order. Folder Portal panels
    /// always sort by name (ADR-014 Decision 7) — there's nowhere to persist a manual order for them —
    /// so the sort-order selector is disabled while a folder source is assigned.
    /// </summary>
    public bool SelectedPanelSupportsManualOrder => SelectedPanelSettings.SelectedPanelSupportsManualOrder;

    /// <summary>
    /// Snap-to-grid cell size in pixels. 1 = no snapping, 4–128 = snap.
    /// </summary>
    public int GridSize
    {
        get => GlobalDefaults.GridSize;
        set => GlobalDefaults.GridSize = value;
    }

    /// <summary>
    /// When <see langword="true"/>, resizing snaps panel dimensions to the icon-grid cell size.
    /// </summary>
    public bool SnapResizeToIconGrid
    {
        get => GlobalDefaults.SnapResizeToIconGrid;
        set => GlobalDefaults.SnapResizeToIconGrid = value;
    }

    /// <summary>
    /// When <see langword="true"/>, panels resize via the native <c>WM_SIZING</c> loop instead of the
    /// legacy WPF <c>Thumb</c> path. Experimental — applies to panels created after the app restarts.
    /// </summary>
    public bool NativePanelSizing
    {
        get => GlobalDefaults.NativePanelSizing;
        set => GlobalDefaults.NativePanelSizing = value;
    }

    /// <summary>
    /// Magnetic pull distance (px) applied while dragging a panel. 0 disables the live drag-time
    /// magnet (movement stays free until drop, where <see cref="GridSize"/> quantisation still applies).
    /// </summary>
    public int GridSnapDistance
    {
        get => GlobalDefaults.GridSnapDistance;
        set => GlobalDefaults.GridSnapDistance = value;
    }

    /// <summary>
    /// Which panel move/resize gestures remain available outside Edit Layout mode
    /// (docs/PLANS/phase-6-edit-layout-ux.md §1 D-2).
    /// </summary>
    public NormalModeLayoutGestures NormalModeLayoutGestures
    {
        get => GlobalDefaults.NormalModeLayoutGestures;
        set => GlobalDefaults.NormalModeLayoutGestures = value;
    }

    /// <summary>Available options for the <see cref="NormalModeLayoutGestures"/> selector.</summary>
    public IReadOnlyList<NormalModeLayoutGestures> NormalModeLayoutGesturesOptions => GlobalDefaults.NormalModeLayoutGesturesOptions;

    // ── Layout editing (docs/PLANS/phase-6-edit-layout-ux.md §1 D-9 #3) ─────────────────────────────
    // Forwards directly to the injected ILayoutEditModeService singleton — unlike GridSize/NativePanelSizing
    // above, there is no per-concern sub-ViewModel wrapping it (the service already owns its own
    // persistence for IsLayoutLocked and its own INotifyPropertyChanged — see OnLayoutEditModePropertyChanged
    // below, which re-raises those notifications on this ViewModel exactly like SelectedPanelSettings/
    // GlobalDefaults/SystemIconsSettings do for their own sub-ViewModels).

    /// <summary>
    /// Whether the app-global Edit Layout mode (<see cref="ILayoutEditModeService"/>) is currently active.
    /// Setting <see langword="true"/> attempts <see cref="ILayoutEditModeService.TryEnterEditLayout"/>
    /// (silently no-ops while <see cref="IsLayoutLocked"/>); setting <see langword="false"/> calls
    /// <see cref="ILayoutEditModeService.ExitEditLayout"/>. Stays in sync with the tray icon, a panel's
    /// context menu, and <c>Ctrl+E</c> via the service's own <see cref="INotifyPropertyChanged"/>.
    /// </summary>
    public bool IsEditingLayout
    {
        get => _layoutEditMode.IsEditing;
        set
        {
            if (value) _layoutEditMode.TryEnterEditLayout(LayoutModeChangeSource.SettingsWindow);
            else _layoutEditMode.ExitEditLayout(LayoutModeChangeSource.SettingsWindow);
        }
    }

    /// <summary>Whether panel geometry (move/resize) is currently fixed ("Lock layout", D-3).</summary>
    public bool IsLayoutLocked
    {
        get => _layoutEditMode.IsLayoutLocked;
        set => _layoutEditMode.IsLayoutLocked = value;
    }

    /// <summary><see langword="true"/> iff <see cref="IsLayoutLocked"/> is <see langword="false"/> — whether <see cref="IsEditingLayout"/> can currently be set to <see langword="true"/>.</summary>
    public bool CanEnterEditLayout => _layoutEditMode.CanEnterEditLayout;

    /// <summary>Toggles <see cref="IsEditingLayout"/> — bound to <c>Ctrl+E</c> in <c>MainWindow.xaml.cs</c> (D-9 #3).</summary>
    public ICommand ToggleEditLayoutCommand { get; }

    /// <summary>Default background colour (hex) for new panels.</summary>
    public string DefaultBackgroundColor
    {
        get => GlobalDefaults.DefaultBackgroundColor;
        set => GlobalDefaults.DefaultBackgroundColor = value;
    }

    /// <summary>Default text colour (hex) for new panels.</summary>
    public string DefaultTextColor
    {
        get => GlobalDefaults.DefaultTextColor;
        set => GlobalDefaults.DefaultTextColor = value;
    }

    /// <summary>Default opacity (0–1) for new panels.</summary>
    public double DefaultOpacity
    {
        get => GlobalDefaults.DefaultOpacity;
        set => GlobalDefaults.DefaultOpacity = value;
    }

    /// <summary>Default icon size (px) for new panels.</summary>
    public double DefaultIconSize
    {
        get => GlobalDefaults.DefaultIconSize;
        set => GlobalDefaults.DefaultIconSize = value;
    }

    /// <summary>
    /// Default <see cref="PanelGeometryMode"/> for new panels (ADR-028; P06, <c>docs/fix-prompts/06-fit-content.md</c>) —
    /// forwards <see cref="GlobalDefaultsViewModel.DefaultPanelGeometryMode"/> so <c>MainWindow.xaml</c>'s
    /// existing flat-binding convention (against this root ViewModel, not <c>GlobalDefaults.X</c>) keeps
    /// working for this property too.
    /// </summary>
    public PanelGeometryMode DefaultPanelGeometryMode
    {
        get => GlobalDefaults.DefaultPanelGeometryMode;
        set => GlobalDefaults.DefaultPanelGeometryMode = value;
    }

    /// <summary>Available options for the default-panel-geometry-mode selector (settings window).</summary>
    public IReadOnlyList<PanelGeometryMode> PanelGeometryModeOptions => GlobalDefaults.PanelGeometryModeOptions;

    /// <summary>
    /// Default <see cref="IconLayoutPolicy"/> for new panels (ADR-029 Decision 6; P06) — forwards
    /// <see cref="GlobalDefaultsViewModel.DefaultIconLayoutPolicy"/>, same shape as
    /// <see cref="DefaultPanelGeometryMode"/> above.
    /// </summary>
    public IconLayoutPolicy DefaultIconLayoutPolicy
    {
        get => GlobalDefaults.DefaultIconLayoutPolicy;
        set => GlobalDefaults.DefaultIconLayoutPolicy = value;
    }

    /// <summary>Available options for the default-icon-layout-policy selector (settings window).</summary>
    public IReadOnlyList<IconLayoutPolicy> IconLayoutPolicyOptions => GlobalDefaults.IconLayoutPolicyOptions;

    /// <summary>Whether the app starts with Windows.</summary>
    public bool AutostartEnabled
    {
        get => _autostart.IsEnabled;
        set
        {
            if (_autostart.IsEnabled == value) return;
            _autostart.IsEnabled = value;
            OnPropertyChanged(nameof(AutostartEnabled));
        }
    }

    /// <summary>Whether the real desktop icons are currently visible.</summary>
    public bool DesktopIconsVisible
    {
        get => _desktopIcons.AreIconsVisible;
        set
        {
            if (_desktopIcons.AreIconsVisible == value) return;
            _desktopIcons.SetIconsVisible(value);
            OnPropertyChanged(nameof(DesktopIconsVisible));
        }
    }

    /// <summary>
    /// Command to add a new desktop panel.
    /// </summary>
    public ICommand AddPanelCommand { get; }

    /// <summary>
    /// Runs the explicit "Align to workspace grid" command over EVERY panel (ADR-028 «Явная команда»,
    /// P03 §2.5). Confirmed first: it is a bulk, visible re-layout of the user's whole desktop, and
    /// ADR-028 deliberately provides no bulk inverse. Disabled while the layout is locked (ADR-026
    /// gating — the same rule the per-panel menu item follows).
    /// </summary>
    public ICommand AlignAllPanelsCommand { get; }

    /// <summary>
    /// Command to remove the selected desktop panel.
    /// </summary>
    public ICommand RemovePanelCommand { get; }

    /// <summary>
    /// Command to switch the active page. Parameter is the page identifier (string).
    /// </summary>
    public ICommand SwitchPageCommand { get; }

    /// <summary>
    /// Command to add a new page with an auto-generated name.
    /// </summary>
    public ICommand AddPageCommand { get; }

    /// <summary>
    /// Command to remove a page. Parameter is the page identifier (string).
    /// Cannot remove the <c>"default"</c> page.
    /// </summary>
    public ICommand RemovePageCommand { get; }

    // ── System Icons Panel ───────────────────────────────────────────────────

    /// <summary>Whether the system icons panel is shown on the desktop.</summary>
    public bool SystemIconsPanelEnabled
    {
        get => SystemIconsSettings.SystemIconsPanelEnabled;
        set => SystemIconsSettings.SystemIconsPanelEnabled = value;
    }

    /// <summary>
    /// Whether the panel follows the user across Windows virtual desktops (Task View).
    /// Only meaningful while <see cref="SystemIconsPanelEnabled"/> is <see langword="true"/>;
    /// on systems without a virtual desktop manager the setting is stored but has no effect.
    /// </summary>
    public bool SystemIconsPanelOnAllVirtualDesktops
    {
        get => SystemIconsSettings.SystemIconsPanelOnAllVirtualDesktops;
        set => SystemIconsSettings.SystemIconsPanelOnAllVirtualDesktops = value;
    }

    /// <summary>The ordered list of system icon items (for per-item visibility checkboxes).</summary>
    public ObservableCollection<SystemIconItemModel> SystemIconsItems => SystemIconsSettings.SystemIconsItems;

    /// <summary>Size (Small / Medium / Large) for system icon items.</summary>
    public SystemIconSize SystemIconsSize
    {
        get => SystemIconsSettings.SystemIconsSize;
        set => SystemIconsSettings.SystemIconsSize = value;
    }

    /// <summary>Available icon size options for the ComboBox.</summary>
    public IReadOnlyList<SystemIconSize> SystemIconSizeOptions => SystemIconsSettings.SystemIconSizeOptions;

    /// <summary>
    /// Whether the "Show on all virtual desktops" feature has been silently disabled by the
    /// circuit breaker in <c>SystemIconsPanelManager.PollVirtualDesktopOnce</c> — see
    /// <see cref="SystemIconsSettingsViewModel.SystemIconsVirtualDesktopFeatureIneffective"/>.
    /// </summary>
    public bool SystemIconsVirtualDesktopFeatureIneffective => SystemIconsSettings.SystemIconsVirtualDesktopFeatureIneffective;

    /// <summary>
    /// Initializes a new instance of the <see cref="MainWindowViewModel"/> class.
    /// </summary>
    /// <param name="windowManager">Creates/removes/persists panel windows.</param>
    /// <param name="appSettings">Global application settings.</param>
    /// <param name="autostart">Manages the "start with Windows" registration.</param>
    /// <param name="pageManager">Manages named page layouts.</param>
    /// <param name="layoutSnapshots">Backing ViewModel for the "Layout snapshots" settings section.</param>
    /// <param name="systemIconsPanelManager">Manages the system-icons panel's lifecycle and persisted state.</param>
    /// <param name="confirmation">Gates destructive actions (e.g. removing a panel/page) behind a user confirmation prompt.</param>
    /// <param name="itemStorage">Provides app-managed item storage used when removing panels.</param>
    /// <param name="desktopIcons">Toggles native desktop icon visibility.</param>
    /// <param name="layoutEditMode">The application-global Edit Layout mode service.</param>
    public MainWindowViewModel(
        IWindowManager windowManager,
        IAppSettings appSettings,
        IAutostartService autostart,
        IPageManager pageManager,
        LayoutSnapshotsViewModel layoutSnapshots,
        ISystemIconsPanelManager systemIconsPanelManager,
        IConfirmationService confirmation,
        IPanelItemStorageService itemStorage,
        IDesktopIconsService desktopIcons,
        ILayoutEditModeService layoutEditMode)
    {
        _windowManager = windowManager;
        _appSettings = appSettings;
        _autostart = autostart;
        _pageManager = pageManager;
        _confirmation = confirmation;
        _itemStorage = itemStorage;
        _desktopIcons = desktopIcons;
        _layoutEditMode = layoutEditMode;
        LayoutSnapshots = layoutSnapshots;

        SelectedPanelSettings = new SelectedPanelSettingsViewModel(windowManager);
        GlobalDefaults = new GlobalDefaultsViewModel(appSettings, windowManager);
        SystemIconsSettings = new SystemIconsSettingsViewModel(systemIconsPanelManager);

        // Re-raise every sub-ViewModel's own PropertyChanged verbatim on this ViewModel — the property
        // names match exactly (see the forwarding properties above), so no name translation is needed.
        // Never unsubscribed: all three sub-ViewModels are owned for the lifetime of this (DI-singleton)
        // ViewModel.
        SelectedPanelSettings.PropertyChanged += (_, e) => OnPropertyChanged(e.PropertyName!);
        GlobalDefaults.PropertyChanged += (_, e) => OnPropertyChanged(e.PropertyName!);
        SystemIconsSettings.PropertyChanged += (_, e) => OnPropertyChanged(e.PropertyName!);

        // D-9 #3: ILayoutEditModeService is a DI singleton shared with every ChildWindow — this
        // ViewModel never mutates its own copy of the state, only reacts to whatever the tray icon,
        // a panel's context menu, Ctrl+E, or this window's own toggle already changed on the service.
        // Never unsubscribed, same rationale as the sub-ViewModel subscriptions above.
        _layoutEditMode.PropertyChanged += OnLayoutEditModePropertyChanged;

        ToggleEditLayoutCommand = new RelayCommand(() => _layoutEditMode.Toggle(LayoutModeChangeSource.SettingsWindow));

        AddPanelCommand = new RelayCommand(AddPanel);
        AlignAllPanelsCommand = new RelayCommand(AlignAllPanels, () => !IsLayoutLocked);
        RemovePanelCommand = new RelayCommand(RemovePanel, CanRemovePanel);
        SwitchPageCommand = new RelayCommand<string>(SwitchPage);
        AddPageCommand = new RelayCommand(AddPage);
        RemovePageCommand = new RelayCommand<string>(RemovePage, CanRemovePage);

        _windowManager.OnChildWindowAdded += OnPanelAdded;
        _windowManager.OnChildWindowRemoved += OnPanelRemoved;
        _pageManager.PageChanged += OnPageChanged;

        SyncPages();
    }

    /// <summary>
    /// Synchronizes the Panels collection with the current state of the WindowManager.
    /// Call once after LoadWindowsState().
    /// </summary>
    public void SyncPanels()
    {
        Panels.Clear();
        foreach (var model in _windowManager.GetAllChildWindows())
            Panels.Add(model);
    }

    /// <summary>
    /// Synchronizes the Pages collection with IPageManager.
    /// </summary>
    public void SyncPages()
    {
        Pages.Clear();
        foreach (var page in _pageManager.Pages)
            Pages.Add(page);
    }

    /// <summary>
    /// Notifies about property changes.
    /// </summary>
    /// <param name="propertyName">Name of the property that was changed.</param>
    protected virtual void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private void AddPanel()
    {
        var model = new ChildWindowModel { Title = "New Panel", PageId = _pageManager.CurrentPage };

        // Shared with App.xaml.cs's lasso-gesture creation path (fix-cycle 1 MINOR finding,
        // docs/PLANS/workspace-grid-migration.md §10.5): one source of truth for the appearance/
        // workspace-grid defaults instead of two independently maintained copies.
        PanelDefaults.Apply(model, _appSettings);

        // MAJOR-1(b): this action never assigns Position/Size itself (unlike the lasso gesture) — see
        // PanelDefaults.ReconcileGridAlignedSpans's remarks for how it seeds a preset size in that case.
        PanelDefaults.ReconcileGridAlignedSpans(model);

        // P03-L3 (baseline M8): every panel created by this action used to land on (0,0) — i.e. exactly
        // on top of the previous one. A GridAligned panel now takes the first free slot on its monitor's
        // workspace grid instead; LegacyFreeform behavior is untouched (see the interface's remarks).
        // Runs AFTER ReconcileGridAlignedSpans, which is what gives a brand-new panel its size.
        _windowManager.PlaceNewPanelOnFreeSlot(model);

        _windowManager.CreateChildWindow(model);
    }

    /// <summary>
    /// Backs <see cref="AlignAllPanelsCommand"/>. The lock is re-checked here as well as in
    /// <c>CanExecute</c> (defence in depth, mirroring the panel context menu's own handler).
    /// </summary>
    /// <remarks>
    /// M-2 fix-cycle 1 (docs/PLANS/workspace-grid-03-04-integration.md §2.5): <c>AlignPanelsToWorkspaceGrid(null)</c>
    /// now only ever targets the CURRENT page (<see cref="IWindowManager.AlignPanelsToWorkspaceGrid"/>'s
    /// own remarks) — the confirmation message says so explicitly, with the actual panel count, rather
    /// than implying every panel on every page is about to move.
    /// </remarks>
    private void AlignAllPanels()
    {
        if (IsLayoutLocked) return;

        var panelCount = Panels.Count(p => p.PageId == CurrentPage);
        var panelWord = panelCount == 1 ? "panel" : "panels";
        if (!_confirmation.Confirm(
                $"Every panel on the current page ({panelCount} {panelWord}) will be moved onto the " +
                "workspace grid and resized to whole grid units. There is no single-step undo for this. " +
                "Continue?",
                "Align all panels to grid"))
        {
            return;
        }

        _windowManager.AlignPanelsToWorkspaceGrid(null);
    }

    private void SwitchPage(string? pageId)
    {
        if (string.IsNullOrWhiteSpace(pageId)) return;
        _pageManager.SwitchTo(pageId);
    }

    private void AddPage()
    {
        var newId = $"Page {_pageManager.Pages.Count + 1}";

        // Ensure unique name
        var counter = _pageManager.Pages.Count + 1;
        while (_pageManager.Pages.Contains(newId))
        {
            counter++;
            newId = $"Page {counter}";
        }

        _pageManager.AddPage(newId);
        Pages.Add(newId);
        SwitchPage(newId);
    }

    private void RemovePage(string? pageId)
    {
        if (string.IsNullOrWhiteSpace(pageId) || pageId == "default") return;

        // CRITICAL-1 fix (P08 fix cycle 2, 2026-09-22): IPageManager.RemovePage now reports whether it
        // actually removed the page — PageManagerService's own confirm gate (ADR-010 Decision 2 /
        // ADR-009 Amendment 2) can decline. The previous unconditional Pages.Remove(pageId) below ran
        // regardless of that outcome: on Cancel, the page (and every panel on it) was still fully
        // alive in IPageManager/IWindowManager, yet it vanished from this ViewModel's own Pages
        // collection — and therefore from the page switcher UI bound to it — until the app was
        // restarted. Resyncing from IPageManager.Pages (the authoritative source) on success, and doing
        // nothing at all on decline, keeps this collection truthful either way.
        if (_pageManager.RemovePage(pageId))
        {
            SyncPages();
        }
    }

    private bool CanRemovePage(string? pageId) => !string.IsNullOrWhiteSpace(pageId) && pageId != "default";

    private void OnPageChanged(string newPage)
    {
        OnPropertyChanged(nameof(CurrentPage));
    }

    /// <summary>
    /// Re-raises <see cref="ILayoutEditModeService"/>'s own <see cref="INotifyPropertyChanged.PropertyChanged"/>
    /// as this ViewModel's forwarding properties (D-9 #3), so <c>MainWindow</c>'s Edit-layout checkbox
    /// stays in sync even when the mode/lock was changed elsewhere (tray, panel menu, <c>Ctrl+E</c>).
    /// <see cref="ILayoutEditModeService.Mode"/> itself is not forwarded — no property here exposes it
    /// directly — but every transition through it also raises <c>IsEditing</c> (see
    /// <see cref="LayoutEditModeService.TransitionTo"/>), which <see cref="IsEditingLayout"/> already
    /// covers, including the forced-exit-while-locking case (D-3/R-9).
    /// </summary>
    private void OnLayoutEditModePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ILayoutEditModeService.IsEditing):
                OnPropertyChanged(nameof(IsEditingLayout));
                break;
            case nameof(ILayoutEditModeService.IsLayoutLocked):
                OnPropertyChanged(nameof(IsLayoutLocked));
                OnPropertyChanged(nameof(CanEnterEditLayout));
                break;
        }
    }

    private void RemovePanel()
    {
        if (_selectedPanel is null) return;

        // CRITICAL C1 fix (P08 fix cycle 1, 2026-09-22): sync every live panel's ViewModel-side state
        // — including any read-time stale-original->managed-copy remap ChildWindowViewModel.LoadFromModel
        // performed (ADR-009 Amendment) — back into its Model BEFORE deciding whether this removal has
        // anything to lose. Without this, a panel removed before its first save still has
        // _selectedPanel.FilePaths pointing at the stale original path (never itself "managed"), so the
        // IsManaged check below is false and the neutral "nothing lost" dialog is shown — but
        // RemoveChildWindow further down calls SyncToModel() again immediately before Release(), which
        // DOES pick up the by-then-remapped managed path and deletes it past the Recycle Bin without the
        // warning the user should have seen (ADR-010 Decision 2).
        _windowManager.SyncAllWindowsToModels();

        var hasManagedFiles = _selectedPanel.FilePaths.Any(_itemStorage.IsManaged);

        var confirmed = hasManagedFiles
            ? _confirmation.Confirm(
                "The panel will be permanently removed. Files moved into it will be deleted from disk " +
                "without the ability to restore them via the Recycle Bin. Continue?",
                "Remove Panel",
                MessageBoxImage.Warning)
            : _confirmation.Confirm(
                "The panel will be removed. Continue?",
                "Remove Panel");

        if (!confirmed) return;

        _windowManager.RemoveChildWindow(_selectedPanel.Id, releaseManagedItems: true);
    }

    private bool CanRemovePanel() => _selectedPanel is not null;

    private void OnPanelAdded(object? sender, ChildWindowModel model) => Panels.Add(model);

    private void OnPanelRemoved(object? sender, ChildWindowModel model)
    {
        var existing = Panels.FirstOrDefault(p => p.Id == model.Id);
        if (existing is not null)
            Panels.Remove(existing);

        if (_selectedPanel?.Id == model.Id)
            SelectedPanel = null;
    }
}
