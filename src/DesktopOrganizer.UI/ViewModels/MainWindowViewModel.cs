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
/// ViewModel for the Settings/Config window. Manages the list of panels, per-panel
/// appearance, global defaults and autostart.
/// </summary>
public class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly IWindowManager _windowManager;
    private readonly IAppSettings _appSettings;
    private readonly IAutostartService _autostart;
    private readonly IPageManager _pageManager;
    private readonly ISystemIconsPanelManager _systemIconsPanelManager;
    private readonly IConfirmationService _confirmation;
    private readonly IPanelItemStorageService _itemStorage;
    private readonly IDesktopIconsService _desktopIcons;
    private ChildWindowModel? _selectedPanel;

    /// <summary>
    /// ViewModel for the "Layout snapshots" section.
    /// </summary>
    public LayoutSnapshotsViewModel LayoutSnapshots { get; }

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
            RaiseSelectedPanelProperties();
        }
    }

    /// <summary>Whether a panel is selected (drives enabled state of the editor section).</summary>
    public bool HasSelectedPanel => _selectedPanel is not null;

    /// <summary>Title of the selected panel.</summary>
    public string SelectedPanelTitle
    {
        get => _selectedPanel?.Title ?? string.Empty;
        set => SetSelectedPanel(p => p.Title = value);
    }

    /// <summary>Background colour (hex) of the selected panel.</summary>
    public string SelectedPanelBackgroundColor
    {
        get => _selectedPanel?.BackgroundColor ?? string.Empty;
        set => SetSelectedPanel(p => p.BackgroundColor = value);
    }

    /// <summary>Text colour (hex) of the selected panel.</summary>
    public string SelectedPanelTextColor
    {
        get => _selectedPanel?.TextColor ?? string.Empty;
        set => SetSelectedPanel(p => p.TextColor = value);
    }

    /// <summary>Background opacity (0–1) of the selected panel.</summary>
    public double SelectedPanelOpacity
    {
        get => _selectedPanel?.BackgroundOpacity ?? 1.0;
        set => SetSelectedPanel(p => p.BackgroundOpacity = value);
    }

    /// <summary>Icon size (px) of the selected panel.</summary>
    public double SelectedPanelIconSize
    {
        get => _selectedPanel?.IconSize ?? 48;
        set => SetSelectedPanel(p => p.IconSize = value);
    }

    /// <summary>View mode of the selected panel.</summary>
    public PanelViewMode SelectedPanelViewMode
    {
        get => _selectedPanel?.ViewMode ?? PanelViewMode.List;
        set => SetSelectedPanel(p => p.ViewMode = value);
    }

    /// <summary>Sort order of the selected panel.</summary>
    public PanelSortMode SelectedPanelSortMode
    {
        get => _selectedPanel?.SortMode ?? PanelSortMode.Manual;
        set => SetSelectedPanel(p => p.SortMode = value);
    }

    /// <summary>
    /// Whether the selected panel supports a manual (drag-to-reorder) sort order. Folder Portal panels
    /// always sort by name (ADR-014 Decision 7) — there's nowhere to persist a manual order for them —
    /// so the sort-order selector is disabled while a folder source is assigned.
    /// </summary>
    public bool SelectedPanelSupportsManualOrder =>
        _selectedPanel is not null && string.IsNullOrWhiteSpace(_selectedPanel.SourceFolderPath);

    /// <summary>
    /// Snap-to-grid cell size in pixels. 1 = no snapping, 4–128 = snap.
    /// </summary>
    public int GridSize
    {
        get => _appSettings.GridSize;
        set
        {
            if (_appSettings.GridSize == value) return;
            _appSettings.GridSize = value;
            _appSettings.Save();
            OnPropertyChanged(nameof(GridSize));
        }
    }

    /// <summary>
    /// When <see langword="true"/>, resizing snaps panel dimensions to the icon-grid cell size.
    /// </summary>
    public bool SnapResizeToIconGrid
    {
        get => _appSettings.SnapResizeToIconGrid;
        set
        {
            if (_appSettings.SnapResizeToIconGrid == value) return;
            _appSettings.SnapResizeToIconGrid = value;
            _appSettings.Save();
            OnPropertyChanged(nameof(SnapResizeToIconGrid));
        }
    }

    /// <summary>
    /// Magnetic pull distance (px) applied while dragging a panel. 0 disables the live drag-time
    /// magnet (movement stays free until drop, where <see cref="GridSize"/> quantisation still applies).
    /// </summary>
    public int GridSnapDistance
    {
        get => _appSettings.GridSnapDistance;
        set
        {
            if (_appSettings.GridSnapDistance == value) return;
            _appSettings.GridSnapDistance = value;
            _appSettings.Save();
            OnPropertyChanged(nameof(GridSnapDistance));
        }
    }

    /// <summary>Default background colour (hex) for new panels.</summary>
    public string DefaultBackgroundColor
    {
        get => _appSettings.DefaultBackgroundColor;
        set => SetGlobal(v => _appSettings.DefaultBackgroundColor = v, value, _appSettings.DefaultBackgroundColor, nameof(DefaultBackgroundColor));
    }

    /// <summary>Default text colour (hex) for new panels.</summary>
    public string DefaultTextColor
    {
        get => _appSettings.DefaultTextColor;
        set => SetGlobal(v => _appSettings.DefaultTextColor = v, value, _appSettings.DefaultTextColor, nameof(DefaultTextColor));
    }

    /// <summary>Default opacity (0–1) for new panels.</summary>
    public double DefaultOpacity
    {
        get => _appSettings.DefaultOpacity;
        set => SetGlobal(v => _appSettings.DefaultOpacity = v, value, _appSettings.DefaultOpacity, nameof(DefaultOpacity));
    }

    /// <summary>Default icon size (px) for new panels.</summary>
    public double DefaultIconSize
    {
        get => _appSettings.DefaultIconSize;
        set => SetGlobal(v => _appSettings.DefaultIconSize = v, value, _appSettings.DefaultIconSize, nameof(DefaultIconSize));
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
        get => _systemIconsPanelManager.Model.IsEnabled;
        set
        {
            if (_systemIconsPanelManager.Model.IsEnabled == value) return;
            _systemIconsPanelManager.Model.IsEnabled = value;
            _systemIconsPanelManager.Apply();
            _systemIconsPanelManager.Save();
            OnPropertyChanged(nameof(SystemIconsPanelEnabled));
        }
    }

    /// <summary>
    /// Whether the panel follows the user across Windows virtual desktops (Task View).
    /// Only meaningful while <see cref="SystemIconsPanelEnabled"/> is <see langword="true"/>;
    /// on systems without a virtual desktop manager the setting is stored but has no effect.
    /// </summary>
    public bool SystemIconsPanelOnAllVirtualDesktops
    {
        get => _systemIconsPanelManager.Model.ShowOnAllVirtualDesktops;
        set
        {
            if (_systemIconsPanelManager.Model.ShowOnAllVirtualDesktops == value) return;
            _systemIconsPanelManager.Model.ShowOnAllVirtualDesktops = value;

            // Deliberately NOT Apply(): this toggle only starts/stops the poll timer / anchor
            // placement, and Apply() would tear down and recreate the panel window (visible flicker
            // for a setting that never touches the window itself — see ApplyVirtualDesktopSetting).
            _systemIconsPanelManager.ApplyVirtualDesktopSetting();
            _systemIconsPanelManager.Save();
            OnPropertyChanged(nameof(SystemIconsPanelOnAllVirtualDesktops));
        }
    }

    /// <summary>The ordered list of system icon items (for per-item visibility checkboxes).</summary>
    public ObservableCollection<SystemIconItemModel> SystemIconsItems
        => _systemIconsPanelManager.Model.Items;

    /// <summary>Size (Small / Medium / Large) for system icon items.</summary>
    public SystemIconSize SystemIconsSize
    {
        get => _systemIconsPanelManager.Model.IconSize;
        set
        {
            if (_systemIconsPanelManager.Model.IconSize == value) return;
            _systemIconsPanelManager.Model.IconSize = value;
            _systemIconsPanelManager.Apply();
            _systemIconsPanelManager.Save();
            OnPropertyChanged(nameof(SystemIconsSize));
        }
    }

    /// <summary>Available icon size options for the ComboBox.</summary>
    public IReadOnlyList<SystemIconSize> SystemIconSizeOptions { get; } = Enum.GetValues<SystemIconSize>();

    /// <summary>
    /// Whether the "Show on all virtual desktops" feature has been silently disabled by the
    /// circuit breaker in <c>SystemIconsPanelManager.PollVirtualDesktopOnce</c> (three consecutive
    /// no-effect move attempts — see <see cref="SystemIconsPanelModel.VirtualDesktopFeatureIneffective"/>
    /// and ADR-016) — i.e. whether <see cref="SystemIconsPanelOnAllVirtualDesktops"/>'s checkbox is
    /// checked but no longer actually doing anything on this system. Drives a warning hint next to that
    /// checkbox in <c>MainWindow.xaml</c> (finalization MAJOR M-3) — without it, this state was
    /// previously invisible: the checkbox stayed checked while the manager had already given up
    /// polling. Unlike the other System Icons Panel properties above, this one is never set by the user
    /// — it only ever changes from the manager's own background poll — so there is no setter, only a
    /// <see cref="SystemIconsPanelModel.PropertyChanged"/> subscription (see the constructor) forwarding
    /// the model's own change notification.
    /// </summary>
    public bool SystemIconsVirtualDesktopFeatureIneffective
        => _systemIconsPanelManager.Model.VirtualDesktopFeatureIneffective;

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
        _appSettings                = appSettings;
        _autostart                  = autostart;
        _pageManager                = pageManager;
        _systemIconsPanelManager    = systemIconsPanelManager;
        _confirmation               = confirmation;
        _itemStorage                = itemStorage;
        _desktopIcons               = desktopIcons;
        LayoutSnapshots             = layoutSnapshots;

        AddPanelCommand    = new RelayCommand(AddPanel);
        RemovePanelCommand = new RelayCommand(RemovePanel, CanRemovePanel);
        SwitchPageCommand  = new RelayCommand<string>(SwitchPage);
        AddPageCommand     = new RelayCommand(AddPage);
        RemovePageCommand  = new RelayCommand<string>(RemovePage, CanRemovePage);

        _windowManager.OnChildWindowAdded   += OnPanelAdded;
        _windowManager.OnChildWindowRemoved += OnPanelRemoved;
        _pageManager.PageChanged            += OnPageChanged;

        // Forwards the manager's own background-poll changes to VirtualDesktopFeatureIneffective (the
        // circuit breaker tripping/resetting) into a PropertyChanged for the read-only proxy above —
        // nothing else on this ViewModel ever sets that field, so without this subscription the UI
        // would never learn it changed (finalization MAJOR M-3). Never unsubscribed: both this
        // ViewModel and the manager (and its Model) are process-wide DI singletons (DiConfig), so there
        // is no shorter-lived side to leak against.
        _systemIconsPanelManager.Model.PropertyChanged += OnSystemIconsPanelModelPropertyChanged;

        SyncPages();
    }

    private void OnSystemIconsPanelModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemIconsPanelModel.VirtualDesktopFeatureIneffective))
            OnPropertyChanged(nameof(SystemIconsVirtualDesktopFeatureIneffective));
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
            BackgroundColor = _appSettings.DefaultBackgroundColor,
            TextColor = _appSettings.DefaultTextColor,
            BackgroundOpacity = _appSettings.DefaultOpacity,
            IconSize = _appSettings.DefaultIconSize,
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
    /// Applies a change to the selected panel and persists it. The model's own property
    /// setter guards equality, so re-applying an unchanged value is harmless.
    /// </summary>
    private void SetSelectedPanel(Action<ChildWindowModel> apply, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (_selectedPanel is null) return;

        apply(_selectedPanel);
        _windowManager.SaveWindowsState();
        OnPropertyChanged(propertyName!);
    }

    private void SetGlobal<T>(Action<T> apply, T newValue, T current, string propertyName)
    {
        if (EqualityComparer<T>.Default.Equals(current, newValue)) return;
        apply(newValue);
        _appSettings.Save();
        OnPropertyChanged(propertyName);
    }

    private void RaiseSelectedPanelProperties()
    {
        OnPropertyChanged(nameof(HasSelectedPanel));
        OnPropertyChanged(nameof(SelectedPanelTitle));
        OnPropertyChanged(nameof(SelectedPanelBackgroundColor));
        OnPropertyChanged(nameof(SelectedPanelTextColor));
        OnPropertyChanged(nameof(SelectedPanelOpacity));
        OnPropertyChanged(nameof(SelectedPanelIconSize));
        OnPropertyChanged(nameof(SelectedPanelViewMode));
        OnPropertyChanged(nameof(SelectedPanelSortMode));
        OnPropertyChanged(nameof(SelectedPanelSupportsManualOrder));
    }

    /// <summary>
    /// Notifies about property changes.
    /// </summary>
    /// <param name="propertyName">Name of the property that was changed.</param>
    protected virtual void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
