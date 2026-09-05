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
    private readonly IAutostartService _autostart;
    private readonly IPageManager _pageManager;
    private readonly IConfirmationService _confirmation;
    private readonly IPanelItemStorageService _itemStorage;
    private readonly IDesktopIconsService _desktopIcons;
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

    /// <summary>Background colour (hex) of the selected panel.</summary>
    public string SelectedPanelBackgroundColor
    {
        get => SelectedPanelSettings.SelectedPanelBackgroundColor;
        set => SelectedPanelSettings.SelectedPanelBackgroundColor = value;
    }

    /// <summary>Text colour (hex) of the selected panel.</summary>
    public string SelectedPanelTextColor
    {
        get => SelectedPanelSettings.SelectedPanelTextColor;
        set => SelectedPanelSettings.SelectedPanelTextColor = value;
    }

    /// <summary>Background opacity (0–1) of the selected panel.</summary>
    public double SelectedPanelOpacity
    {
        get => SelectedPanelSettings.SelectedPanelOpacity;
        set => SelectedPanelSettings.SelectedPanelOpacity = value;
    }

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
    /// Magnetic pull distance (px) applied while dragging a panel. 0 disables the live drag-time
    /// magnet (movement stays free until drop, where <see cref="GridSize"/> quantisation still applies).
    /// </summary>
    public int GridSnapDistance
    {
        get => GlobalDefaults.GridSnapDistance;
        set => GlobalDefaults.GridSnapDistance = value;
    }

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
    public MainWindowViewModel(
        IWindowManager windowManager,
        IAppSettings appSettings,
        IAutostartService autostart,
        IPageManager pageManager,
        LayoutSnapshotsViewModel layoutSnapshots,
        ISystemIconsPanelManager systemIconsPanelManager,
        IConfirmationService confirmation,
        IPanelItemStorageService itemStorage,
        IDesktopIconsService desktopIcons)
    {
        _windowManager              = windowManager;
        _autostart                  = autostart;
        _pageManager                = pageManager;
        _confirmation               = confirmation;
        _itemStorage                = itemStorage;
        _desktopIcons               = desktopIcons;
        LayoutSnapshots             = layoutSnapshots;

        SelectedPanelSettings = new SelectedPanelSettingsViewModel(windowManager);
        GlobalDefaults        = new GlobalDefaultsViewModel(appSettings);
        SystemIconsSettings   = new SystemIconsSettingsViewModel(systemIconsPanelManager);

        // Re-raise every sub-ViewModel's own PropertyChanged verbatim on this ViewModel — the property
        // names match exactly (see the forwarding properties above), so no name translation is needed.
        // Never unsubscribed: all three sub-ViewModels are owned for the lifetime of this (DI-singleton)
        // ViewModel.
        SelectedPanelSettings.PropertyChanged += (_, e) => OnPropertyChanged(e.PropertyName!);
        GlobalDefaults.PropertyChanged        += (_, e) => OnPropertyChanged(e.PropertyName!);
        SystemIconsSettings.PropertyChanged   += (_, e) => OnPropertyChanged(e.PropertyName!);

        AddPanelCommand    = new RelayCommand(AddPanel);
        RemovePanelCommand = new RelayCommand(RemovePanel, CanRemovePanel);
        SwitchPageCommand  = new RelayCommand<string>(SwitchPage);
        AddPageCommand     = new RelayCommand(AddPage);
        RemovePageCommand  = new RelayCommand<string>(RemovePage, CanRemovePage);

        _windowManager.OnChildWindowAdded   += OnPanelAdded;
        _windowManager.OnChildWindowRemoved += OnPanelRemoved;
        _pageManager.PageChanged            += OnPageChanged;

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

    private void AddPanel()
    {
        var model = new ChildWindowModel
        {
            Title = "New Panel",
            BackgroundColor = GlobalDefaults.DefaultBackgroundColor,
            TextColor = GlobalDefaults.DefaultTextColor,
            BackgroundOpacity = GlobalDefaults.DefaultOpacity,
            IconSize = GlobalDefaults.DefaultIconSize,
            PageId = _pageManager.CurrentPage,
        };

        _windowManager.CreateChildWindow(model);
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
        _pageManager.RemovePage(pageId);
        Pages.Remove(pageId);
    }

    private bool CanRemovePage(string? pageId) => !string.IsNullOrWhiteSpace(pageId) && pageId != "default";

    private void OnPageChanged(string newPage)
    {
        OnPropertyChanged(nameof(CurrentPage));
    }

    private void RemovePanel()
    {
        if (_selectedPanel is null) return;

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

    /// <summary>
    /// Notifies about property changes.
    /// </summary>
    /// <param name="propertyName">Name of the property that was changed.</param>
    protected virtual void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
