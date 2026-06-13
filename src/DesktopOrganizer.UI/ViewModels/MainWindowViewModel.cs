using System.Collections.ObjectModel;
using System.ComponentModel;
using DesktopOrganizer.Core.Models;
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
    private readonly ISortingRuleService _sortingRuleService;
    private ChildWindowModel? _selectedPanel;

    /// <summary>
    /// ViewModel for the "Снапшоты раскладки" section.
    /// </summary>
    public LayoutSnapshotsViewModel LayoutSnapshots { get; }

    // Fields for new-rule inline editor.
    private SortingCriterion _newRuleCriterion;
    private string _newRuleValue = string.Empty;
    private ChildWindowModel? _newRuleTargetPanel;

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

    // ── Sorting rules ────────────────────────────────────────────────────────

    /// <summary>Observable collection of current sorting rules (mirrors ISortingRuleService.Rules).</summary>
    public ObservableCollection<SortingRule> SortingRules { get; } = [];

    /// <summary>Available sorting criteria for the new-rule editor.</summary>
    public IReadOnlyList<SortingCriterion> SortingCriteria { get; } = Enum.GetValues<SortingCriterion>();

    /// <summary>Selected criterion for the new rule being created.</summary>
    public SortingCriterion NewRuleCriterion
    {
        get => _newRuleCriterion;
        set { _newRuleCriterion = value; OnPropertyChanged(nameof(NewRuleCriterion)); }
    }

    /// <summary>Pattern value for the new rule being created.</summary>
    public string NewRuleValue
    {
        get => _newRuleValue;
        set { _newRuleValue = value; OnPropertyChanged(nameof(NewRuleValue)); }
    }

    /// <summary>Target panel selected for the new rule.</summary>
    public ChildWindowModel? NewRuleTargetPanel
    {
        get => _newRuleTargetPanel;
        set { _newRuleTargetPanel = value; OnPropertyChanged(nameof(NewRuleTargetPanel)); }
    }

    /// <summary>Command to add a new sorting rule from the inline editor fields.</summary>
    public ICommand AddSortingRuleCommand { get; private set; } = null!;

    /// <summary>Command to remove a sorting rule. Parameter is the rule Id (string).</summary>
    public ICommand RemoveSortingRuleCommand { get; private set; } = null!;

    /// <summary>Command to immediately apply all sorting rules to existing panels.</summary>
    public ICommand ApplySortingRulesCommand { get; private set; } = null!;

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

    /// <summary>
    /// Initializes a new instance of the <see cref="MainWindowViewModel"/> class.
    /// </summary>
    public MainWindowViewModel(
        IWindowManager windowManager,
        IAppSettings appSettings,
        IAutostartService autostart,
        IPageManager pageManager,
        ISortingRuleService sortingRuleService,
        LayoutSnapshotsViewModel layoutSnapshots)
    {
        _windowManager      = windowManager;
        _appSettings        = appSettings;
        _autostart          = autostart;
        _pageManager        = pageManager;
        _sortingRuleService = sortingRuleService;
        LayoutSnapshots     = layoutSnapshots;

        AddPanelCommand    = new RelayCommand(AddPanel);
        RemovePanelCommand = new RelayCommand(RemovePanel, CanRemovePanel);
        SwitchPageCommand  = new RelayCommand<string>(SwitchPage);
        AddPageCommand     = new RelayCommand(AddPage);
        RemovePageCommand  = new RelayCommand<string>(RemovePage, CanRemovePage);

        AddSortingRuleCommand    = new RelayCommand(AddSortingRule, CanAddSortingRule);
        RemoveSortingRuleCommand = new RelayCommand<string>(RemoveSortingRule);
        ApplySortingRulesCommand = new RelayCommand(ApplySortingRules);

        _windowManager.OnChildWindowAdded   += OnPanelAdded;
        _windowManager.OnChildWindowRemoved += OnPanelRemoved;
        _pageManager.PageChanged            += OnPageChanged;

        SyncPages();
        SyncSortingRules();
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
            Title = "Новая панель",
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
        var newId = $"Страница {_pageManager.Pages.Count + 1}";
        // Ensure unique name
        var counter = _pageManager.Pages.Count + 1;
        while (_pageManager.Pages.Contains(newId))
        {
            counter++;
            newId = $"Страница {counter}";
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
        _windowManager.RemoveChildWindow(_selectedPanel.Id);
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
    }

    // ── Sorting rules ────────────────────────────────────────────────────────

    /// <summary>Syncs the observable collection from the service.</summary>
    public void SyncSortingRules()
    {
        SortingRules.Clear();
        foreach (var rule in _sortingRuleService.Rules)
            SortingRules.Add(rule);
    }

    private bool CanAddSortingRule()
        => !string.IsNullOrWhiteSpace(NewRuleValue) && NewRuleTargetPanel is not null;

    private void AddSortingRule()
    {
        if (NewRuleTargetPanel is null) return;

        var rule = new SortingRule
        {
            Criterion     = NewRuleCriterion,
            Value         = NewRuleValue.Trim(),
            TargetPanelId = NewRuleTargetPanel.Id.ToString(),
            IgnoreCase    = true,
        };

        _sortingRuleService.AddRule(rule);
        SyncSortingRules();

        // Reset editor fields.
        NewRuleValue       = string.Empty;
        NewRuleTargetPanel = null;
    }

    private void RemoveSortingRule(string? ruleId)
    {
        if (string.IsNullOrWhiteSpace(ruleId)) return;
        _sortingRuleService.RemoveRule(ruleId);
        SyncSortingRules();
    }

    private void ApplySortingRules() => _sortingRuleService.ApplyRules();

    /// <summary>
    /// Notifies about property changes.
    /// </summary>
    /// <param name="propertyName">Name of the property that was changed.</param>
    protected virtual void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
