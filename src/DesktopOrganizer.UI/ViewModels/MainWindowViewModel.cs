using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows;
using DesktopOrganizer.Localization;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Storage;
using DesktopOrganizer.UI.Commands;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;

namespace DesktopOrganizer.UI.ViewModels;

/// <summary>
/// ViewModel for the Settings/Config window. Owns the panel/page collection and CRUD, autostart, and the
/// sidebar navigation (<see cref="SelectedSection"/>, docs/prompts/settings-window-redesign.md); delegates
/// per-concern settings to sub-ViewModels extracted per REFACTORING_PLAN.md §5.3:
/// <see cref="GlobalDefaults"/> (grid/snap/default-appearance settings) and <see cref="SystemIconsSettings"/>
/// (the System Icons Panel feature). Per-panel appearance/sort editing (title, colours, opacity, icon
/// size, view/sort mode) moved entirely out of this class into the "Customize panel…" dialog's own
/// <c>PanelCustomizeViewModel</c> (docs/prompts/settings-window-redesign.md) — this class only tracks
/// <see cref="SelectedPanel"/> itself, for the Panels section's list selection and the commands that act
/// on it (<see cref="CustomizePanelCommand"/>/<see cref="MovePanelToPageCommand"/>/
/// <see cref="RemovePanelCommand"/>/<see cref="TakeDefaultsFromSelectedPanelCommand"/>). Both remaining
/// sub-ViewModels are constructed internally (not DI-injected) and their identically-named properties are
/// forwarded 1:1 by this class, with their <see cref="INotifyPropertyChanged.PropertyChanged"/>
/// notifications re-raised verbatim — so <c>MainWindow.xaml</c>'s existing flat bindings against the root
/// ViewModel, and this class's own public surface/constructor signature, are unchanged by the extraction.
/// </summary>
public class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly IWindowManager _windowManager;
    private readonly IAppSettings _appSettings;
    private readonly IAutostartService _autostart;
    private readonly IPageManager _pageManager;
    private readonly IConfirmationService _confirmation;
    private readonly IPanelItemStorageService _itemStorage;
    private readonly ILayoutEditModeService _layoutEditMode;
    private readonly IPanelCustomizeService _panelCustomizeService;
    private readonly ILocalizationService _localization;
    private readonly CultureInfo _systemUiCulture;
    private ChildWindowModel? _selectedPanel;
    private SettingsSection _selectedSection = SettingsSection.General;
    private LanguageOption _selectedLanguageOption;

    /// <summary>
    /// ViewModel for the "Layout snapshots" section.
    /// </summary>
    public LayoutSnapshotsViewModel LayoutSnapshots { get; }

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

    // ── Settings-window sidebar navigation (docs/prompts/settings-window-redesign.md D-1/D-2) ──────
    // This ViewModel is a DI singleton (Configuration/DiConfig.cs), so SelectedSection simply lives as
    // an in-memory field: it survives MainWindow being Hidden/re-Shown (the settings window is never
    // recreated) without needing to be written to IAppSettings/user.config (D-1 — persistence is frozen
    // and the last-open section is explicitly NOT one of the things this feature persists).

    /// <summary>The seven sections shown in the sidebar's main scrolling list, in display order.</summary>
    public IReadOnlyList<SettingsSectionInfo> Sections => SettingsSectionCatalog.Primary;

    /// <summary>The "About" entry, rendered as a separate control pinned to the bottom of the sidebar.</summary>
    public SettingsSectionInfo AboutSectionInfo => SettingsSectionCatalog.About;

    /// <summary>The section currently shown in the settings window's content area.</summary>
    public SettingsSection SelectedSection
    {
        get => _selectedSection;
        set
        {
            if (_selectedSection == value) return;
            _selectedSection = value;
            OnPropertyChanged(nameof(SelectedSection));
        }
    }

    /// <summary>
    /// Selects a sidebar section. Exists (rather than binding <see cref="SelectedSection"/> directly)
    /// only for the pinned "About" entry, which is a standalone <c>ToggleButton</c>, not a
    /// <c>ListBoxItem</c> whose <c>SelectedValue</c> binding could set the property itself.
    /// </summary>
    public ICommand SelectSectionCommand { get; }

    /// <summary>
    /// Collection of all desktop panels.
    /// </summary>
    public ObservableCollection<ChildWindowModel> Panels { get; } = [];

    /// <summary>
    /// The subset of <see cref="Panels"/> whose <see cref="ChildWindowModel.PageId"/> is
    /// <see cref="CurrentPage"/> — what the Panels settings section actually lists. Refreshed by
    /// <see cref="RefreshPanelsOnCurrentPage"/> whenever <see cref="Panels"/>, the current page, or a
    /// panel's page assignment changes.
    /// </summary>
    public ObservableCollection<ChildWindowModel> PanelsOnCurrentPage { get; } = [];

    /// <summary>
    /// Ordered collection of page identifiers shown as tabs in the UI.
    /// </summary>
    public ObservableCollection<string> Pages { get; } = [];

    /// <summary>The currently active page identifier.</summary>
    public string CurrentPage => _pageManager.CurrentPage;

    /// <summary>
    /// Two-way facade over <see cref="CurrentPage"/> for the Panels section's page selector
    /// <c>ComboBox</c>: reads <see cref="CurrentPage"/>, and setting it runs
    /// <see cref="SwitchPageCommand"/> — the same page-switch semantics the old page-tab strip used.
    /// </summary>
    public string SelectedPage
    {
        get => CurrentPage;
        set => SwitchPageCommand.Execute(value);
    }

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
        }
    }

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

    /// <summary>The "Snap panel position to grid" toggle (Layout &amp; snapping section).</summary>
    public bool SnapToGridEnabled
    {
        get => GlobalDefaults.SnapToGridEnabled;
        set => GlobalDefaults.SnapToGridEnabled = value;
    }

    /// <summary>The grid step shown by the "Snap panel position to grid" card's slider.</summary>
    public int GridStep
    {
        get => GlobalDefaults.GridStep;
        set => GlobalDefaults.GridStep = value;
    }

    // ── Layout editing (docs/PLANS/phase-6-edit-layout-ux.md §1 D-9 #3) ─────────────────────────────
    // Forwards directly to the injected ILayoutEditModeService singleton — unlike GridSize/NativePanelSizing
    // above, there is no per-concern sub-ViewModel wrapping it (the service already owns its own
    // persistence for IsLayoutLocked and its own INotifyPropertyChanged — see OnLayoutEditModePropertyChanged
    // below, which re-raises that notification on this ViewModel exactly like GlobalDefaults/
    // SystemIconsSettings do for their own sub-ViewModels). The settings window itself no longer exposes
    // an "editing" toggle (docs/prompts/settings-window-redesign.md §3 — that checkbox left the window
    // entirely; only Ctrl+E and IsLayoutLocked below remain here).

    /// <summary>Whether panel geometry (move/resize) is currently fixed ("Lock layout", D-3).</summary>
    public bool IsLayoutLocked
    {
        get => _layoutEditMode.IsLayoutLocked;
        set => _layoutEditMode.IsLayoutLocked = value;
    }

    /// <summary>Toggles Edit Layout mode — bound to <c>Ctrl+E</c> in <c>MainWindow.xaml.cs</c> (D-9 #3).</summary>
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

    /// <summary>Default <see cref="PanelSurfaceStyle"/> for new panels (New panel defaults section).</summary>
    public PanelSurfaceStyle DefaultSurfaceStyle
    {
        get => GlobalDefaults.DefaultSurfaceStyle;
        set => GlobalDefaults.DefaultSurfaceStyle = value;
    }

    /// <summary>Available options for the default-surface-style selector (settings window).</summary>
    public IReadOnlyList<PanelSurfaceStyle> SurfaceStyleOptions => GlobalDefaults.SurfaceStyleOptions;

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

    /// <summary>
    /// Opens the "Customize panel…" dialog (<see cref="IPanelCustomizeService"/>) for the given panel, or
    /// <see cref="SelectedPanel"/> when the parameter is <see langword="null"/> (the Panels section's own
    /// "Customize…" button always passes <see langword="null"/> and relies on this fallback).
    /// </summary>
    public ICommand CustomizePanelCommand { get; }

    /// <summary>
    /// Moves <see cref="SelectedPanel"/> to a different page. Parameter is the destination page
    /// identifier (string) — the Panels section's "Move to page ▾" menu.
    /// </summary>
    public ICommand MovePanelToPageCommand { get; }

    /// <summary>
    /// Copies <see cref="SelectedPanel"/>'s appearance/geometry into <see cref="GlobalDefaults"/>
    /// (New panel defaults section's "Take from selected panel" button). Disabled when no panel is
    /// selected.
    /// </summary>
    public ICommand TakeDefaultsFromSelectedPanelCommand { get; }

    /// <summary>
    /// Clears every one-time UI hint flag (currently just <see cref="IAppSettings.HasShownMoveHint"/>) so
    /// it is shown again — General section's Advanced sub-panel "Reset one-time hints" button. Disabled
    /// (<see cref="HasOneTimeHintsToReset"/> is <see langword="false"/>) when there is nothing to reset.
    /// </summary>
    public ICommand ResetOneTimeHintsCommand { get; }

    /// <summary>
    /// Whether at least one one-time UI hint is currently marked "already shown" — backs
    /// <see cref="ResetOneTimeHintsCommand"/>'s enabled state and the General section's "There are no
    /// hints to reset." disabled-reason text.
    /// </summary>
    public bool HasOneTimeHintsToReset => _appSettings.HasShownMoveHint;

    /// <summary>Opens <see cref="StorageFolderPath"/> in Windows Explorer — About section.</summary>
    public ICommand OpenStorageFolderCommand { get; }

    // ── Language picker (docs/PLANS/localization.md §2.9 contract C-5) ─────────────────────────────

    /// <summary>
    /// The three language choices shown by the General section's Language <c>ComboBox</c>: "Follow
    /// Windows" (<see cref="LanguageOption.Tag"/> <c>""</c>), <c>"en"</c>, <c>"ru"</c>, in that display
    /// order. Each option's own <see cref="LanguageOption.DisplayName"/> is re-resolved in place on
    /// <see cref="ILocalizationService.CultureChanged"/> — see this constructor.
    /// </summary>
    public IReadOnlyList<LanguageOption> LanguageOptions { get; }

    /// <summary>
    /// The currently selected language option. Setting it to an option with a different
    /// <see cref="LanguageOption.Tag"/> writes <see cref="IAppSettings.Language"/>, saves it, and applies
    /// it via <see cref="ILocalizationService.ApplyLanguage"/> immediately (D-3 hot switch); re-selecting
    /// the option that is already current is a no-op (no save, no re-apply). Setting
    /// <see langword="null"/> is also a no-op (fix cycle 1 NIT) rather than throwing —
    /// <c>ComboBox.SelectedItem</c> bindings can pass <see langword="null"/> transiently (e.g. while the
    /// items collection is being reset), and this setter is invoked directly by that binding.
    /// </summary>
    [AllowNull]
    public LanguageOption SelectedLanguageOption
    {
        get => _selectedLanguageOption;
        set
        {
            if (value is null) return;
            if (ReferenceEquals(_selectedLanguageOption, value)) return;

            var tagChanged = _selectedLanguageOption.Tag != value.Tag;
            _selectedLanguageOption = value;
            OnPropertyChanged(nameof(SelectedLanguageOption));

            if (!tagChanged) return;

            _appSettings.Language = value.Tag;
            _appSettings.Save();
            _localization.ApplyLanguage(value.Tag);
        }
    }

    /// <summary>The app's informational version string, for the About section.</summary>
    public string AppVersion { get; } = ResolveAppVersion();

    /// <summary>The per-user storage root (<c>%LOCALAPPDATA%\DesktopOrganizer</c>), for the About section.</summary>
    public string StorageFolderPath { get; } = AppStorageRoot.Default;

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
    /// <param name="layoutEditMode">The application-global Edit Layout mode service.</param>
    /// <param name="panelCustomizeService">Opens the "Customize panel…" dialog (WP-B).</param>
    /// <param name="localization">
    /// Backs <see cref="LanguageOptions"/>/<see cref="SelectedLanguageOption"/> (docs/PLANS/localization.md
    /// §2.9 contract C-5) and localizes this ViewModel's own confirmation dialogs.
    /// </param>
    /// <param name="systemUiCulture">
    /// The Windows UI culture the "Follow Windows" <see cref="LanguageOption"/> names (fix cycle 1
    /// MINOR-2: production — <see cref="SystemCulture.OriginalUiCulture"/>, wired in
    /// <c>Configuration/DiConfig.cs</c> — same snapshot <paramref name="localization"/>'s own
    /// implementation resolves "follow Windows" against). Passed through to every
    /// <see cref="LanguageOption"/> instead of each one reaching for that static culture itself, so
    /// tests can exercise both the "en" and "ru" branches deterministically.
    /// </param>
    public MainWindowViewModel(
        IWindowManager windowManager,
        IAppSettings appSettings,
        IAutostartService autostart,
        IPageManager pageManager,
        LayoutSnapshotsViewModel layoutSnapshots,
        ISystemIconsPanelManager systemIconsPanelManager,
        IConfirmationService confirmation,
        IPanelItemStorageService itemStorage,
        ILayoutEditModeService layoutEditMode,
        IPanelCustomizeService panelCustomizeService,
        ILocalizationService localization,
        CultureInfo systemUiCulture)
    {
        _windowManager = windowManager;
        _appSettings = appSettings;
        _autostart = autostart;
        _pageManager = pageManager;
        _confirmation = confirmation;
        _itemStorage = itemStorage;
        _layoutEditMode = layoutEditMode;
        _panelCustomizeService = panelCustomizeService;
        _localization = localization;
        _systemUiCulture = systemUiCulture ?? throw new ArgumentNullException(nameof(systemUiCulture));
        LayoutSnapshots = layoutSnapshots;

        // Language picker (§2.9 C-5): three fixed options, in display order — never rebuilt; each
        // option's own DisplayName is re-resolved in place on CultureChanged below, so the ComboBox's
        // bound SelectedItem instance never needs to change out from under a live selection.
        LanguageOptions = [
            new LanguageOption(SupportedLanguages.FollowSystem, _localization, _systemUiCulture),
            new LanguageOption("en", _localization, _systemUiCulture),
            new LanguageOption("ru", _localization, _systemUiCulture),
        ];
        _selectedLanguageOption = LanguageOptions.FirstOrDefault(o =>
                string.Equals(o.Tag, _localization.LanguagePreference, StringComparison.OrdinalIgnoreCase))
            ?? LanguageOptions[0];
        _localization.CultureChanged += OnLocalizationCultureChanged;

        GlobalDefaults = new GlobalDefaultsViewModel(appSettings, windowManager);
        SystemIconsSettings = new SystemIconsSettingsViewModel(systemIconsPanelManager);

        // Re-raise every sub-ViewModel's own PropertyChanged verbatim on this ViewModel — the property
        // names match exactly (see the forwarding properties above), so no name translation is needed.
        // Never unsubscribed: both sub-ViewModels are owned for the lifetime of this (DI-singleton)
        // ViewModel.
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
        SelectSectionCommand = new RelayCommand<SettingsSection>(section => SelectedSection = section);
        CustomizePanelCommand = new RelayCommand<ChildWindowModel>(CustomizePanel, CanCustomizePanel);
        MovePanelToPageCommand = new RelayCommand<string>(MovePanelToPage, CanMovePanelToPage);
        TakeDefaultsFromSelectedPanelCommand = new RelayCommand(TakeDefaultsFromSelectedPanel, () => _selectedPanel is not null);
        ResetOneTimeHintsCommand = new RelayCommand(ResetOneTimeHints, () => _appSettings.HasShownMoveHint);
        OpenStorageFolderCommand = new RelayCommand(OpenStorageFolder);

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

        RefreshPanelsOnCurrentPage();
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
    /// Re-raises <see cref="PropertyChanged"/> for every property that reads live, externally-mutable
    /// settings state but otherwise has no notification source of its own (MINOR-6, FIX round 2) —
    /// currently just <see cref="HasOneTimeHintsToReset"/>. This ViewModel is a DI singleton
    /// (Configuration/DiConfig.cs) whose <c>MainWindow</c> is hidden, not recreated, across Settings
    /// window open/close cycles (ADR-002), so a change to <see cref="IAppSettings.HasShownMoveHint"/>
    /// made while the window was hidden — the only other writer besides
    /// <see cref="ResetOneTimeHintsCommand"/> itself is <c>ChildWindowViewModel.MaybeRaiseMoveHint</c>,
    /// reachable from any panel regardless of whether this settings window is open — would otherwise
    /// leave the General section's "Reset one-time hints" button/disabled-reason showing whatever state
    /// was true the last time this ViewModel happened to raise that notification, until the user toggled
    /// something else in the same session. Called from <c>MainWindow.xaml.cs</c> whenever the window
    /// becomes visible/activated, i.e. exactly when a user could next look at that button.
    /// </summary>
    public void RefreshTransientState() => OnPropertyChanged(nameof(HasOneTimeHintsToReset));

    /// <summary>
    /// Notifies about property changes.
    /// </summary>
    /// <param name="propertyName">Name of the property that was changed.</param>
    protected virtual void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static string ResolveAppVersion()
    {
        var infoVersion = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        return string.IsNullOrWhiteSpace(infoVersion) ? "unknown" : infoVersion;
    }

    /// <summary>
    /// Rebuilds <see cref="PanelsOnCurrentPage"/> from <see cref="Panels"/>/<see cref="CurrentPage"/>.
    /// </summary>
    /// <remarks>
    /// M-7 fix (docs/prompts/settings-window-redesign.md fix-cycle): updates the collection in place
    /// (remove/add only the panels that actually left/joined the current page) instead of
    /// <c>Clear()</c>-then-refill. A <c>Clear()</c> deselects the bound <c>ListBox</c> for one
    /// collection-changed tick — and since <c>Selector.SelectedItem</c> binds two-way by default, that
    /// momentary deselection writes <see langword="null"/> straight into <see cref="SelectedPanel"/>
    /// before the correct panel is re-added a moment later, permanently losing the selection on every
    /// single refresh (adding another panel, a page-unrelated <see cref="Panels"/> change, etc.).
    /// Preserving already-present panel instances keeps their <c>ListBoxItem</c> — and therefore any live
    /// selection — untouched. <see cref="SelectedPanel"/> is explicitly cleared only when it is no longer
    /// one of the panels on the current page (e.g. it just got moved to a different page, or the page was
    /// switched away from it) — a real, needed reset rather than an accidental byproduct of rebuilding.
    /// <para>
    /// MINOR-4 (FIX round 2): after the removal pass, the insertion pass below also keeps
    /// <see cref="PanelsOnCurrentPage"/>'s ORDER in sync with <see cref="Panels"/> (the authoritative
    /// order, e.g. after a drag-reorder in the Panels list) instead of leaving an already-present panel
    /// wherever it happened to be. It walks the desired sequence by index and, for any position that
    /// doesn't already hold the right panel, either <see cref="ObservableCollection{T}.Move"/>s the
    /// existing item into place or inserts a brand-new one — never
    /// removes-then-re-adds an existing item (which is exactly what would drop the very selection this
    /// method's in-place removal/add split was written to protect). A panel moved away and back to the
    /// current page therefore lands at its <see cref="Panels"/>-order position, not appended at the end.
    /// </para>
    /// </remarks>
    private void RefreshPanelsOnCurrentPage()
    {
        var currentPage = CurrentPage;
        var stillOnCurrentPage = Panels.Where(p => p.PageId == currentPage).ToList();

        for (var i = PanelsOnCurrentPage.Count - 1; i >= 0; i--)
        {
            if (!stillOnCurrentPage.Contains(PanelsOnCurrentPage[i]))
                PanelsOnCurrentPage.RemoveAt(i);
        }

        for (var i = 0; i < stillOnCurrentPage.Count; i++)
        {
            var panel = stillOnCurrentPage[i];
            if (i < PanelsOnCurrentPage.Count && ReferenceEquals(PanelsOnCurrentPage[i], panel))
                continue;

            var existingIndex = PanelsOnCurrentPage.IndexOf(panel);
            if (existingIndex >= 0)
                PanelsOnCurrentPage.Move(existingIndex, i);
            else
                PanelsOnCurrentPage.Insert(i, panel);
        }

        if (_selectedPanel is not null && !stillOnCurrentPage.Contains(_selectedPanel))
            SelectedPanel = null;
    }

    private void AddPanel()
    {
        // PanelDefaults.CreateNew (fix-cycle finding 11, docs/prompts/settings-window-redesign.md) is
        // shared with App.xaml.cs's lasso-gesture creation path: one source of truth for both the page
        // assignment and the appearance/workspace-grid defaults, instead of two independently maintained
        // copies (fix-cycle 1 MINOR finding, docs/PLANS/workspace-grid-migration.md §10.5, had already
        // unified the defaults half of this; this unifies the page-assignment half too).
        var model = PanelDefaults.CreateNew(_appSettings, _pageManager.CurrentPage);

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
        var panelText = _localization.FormatPlural("Plural_Panels", panelCount);
        if (!_confirmation.Confirm(
                _localization.Format("Settings_Confirm_AlignAll_Message_Format", panelText),
                _localization.GetString("Settings_Confirm_AlignAll_Title")))
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
        OnPropertyChanged(nameof(SelectedPage));
        RefreshPanelsOnCurrentPage();
    }

    private void CustomizePanel(ChildWindowModel? panel)
    {
        var target = panel ?? _selectedPanel;
        if (target is null) return;
        _panelCustomizeService.Show(target);
    }

    private bool CanCustomizePanel(ChildWindowModel? panel) => (panel ?? _selectedPanel) is not null;

    private void MovePanelToPage(string? pageId)
    {
        if (_selectedPanel is null || string.IsNullOrWhiteSpace(pageId)) return;

        _windowManager.MovePanelToPage(_selectedPanel, pageId);
        RefreshPanelsOnCurrentPage();
    }

    private bool CanMovePanelToPage(string? pageId)
        => _selectedPanel is not null && !string.IsNullOrWhiteSpace(pageId) && pageId != _selectedPanel.PageId;

    private void TakeDefaultsFromSelectedPanel()
    {
        if (_selectedPanel is null) return;
        GlobalDefaults.ApplyFromPanel(_selectedPanel);
    }

    private void ResetOneTimeHints()
    {
        _appSettings.HasShownMoveHint = false;
        _appSettings.Save();
        OnPropertyChanged(nameof(HasOneTimeHintsToReset));
    }

    /// <summary>
    /// Opens <see cref="StorageFolderPath"/> in Explorer. Failures (e.g. Explorer unavailable) are
    /// logged, never surfaced as a crash — opening a folder is a convenience action, not a critical path.
    /// </summary>
    private void OpenStorageFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = StorageFolderPath,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine($"Failed to open storage folder '{StorageFolderPath}': {ex.Message}");
        }
    }

    /// <summary>
    /// Re-raises <see cref="ILayoutEditModeService"/>'s own <see cref="INotifyPropertyChanged.PropertyChanged"/>
    /// as this ViewModel's own <see cref="IsLayoutLocked"/> forwarding property (D-9 #3), so
    /// <c>MainWindow</c>'s "Lock layout" toggle stays in sync even when the lock was changed elsewhere
    /// (tray, panel menu). The settings window no longer exposes an "editing" toggle of its own — see this
    /// class's remarks — so <see cref="ILayoutEditModeService.IsEditing"/> changes have nothing left here
    /// to forward to.
    /// </summary>
    private void OnLayoutEditModePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ILayoutEditModeService.IsLayoutLocked))
            OnPropertyChanged(nameof(IsLayoutLocked));
    }

    /// <summary>
    /// Re-resolves every <see cref="LanguageOptions"/> entry's own <see cref="LanguageOption.DisplayName"/>
    /// for the new current culture (docs/PLANS/localization.md §2.9 C-5) — including the "Follow Windows"
    /// entry's parenthetical, which always names the language Windows itself resolves to. Never
    /// unsubscribed: this ViewModel is a DI singleton, same rationale as the sub-ViewModel/
    /// <see cref="ILayoutEditModeService"/> subscriptions above.
    /// </summary>
    private void OnLocalizationCultureChanged(object? sender, CultureChangedEventArgs e)
    {
        foreach (var option in LanguageOptions)
            option.Refresh(_localization);
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
        var title = _localization.GetString("Settings_Confirm_RemovePanel_Title");

        var confirmed = hasManagedFiles
            ? _confirmation.Confirm(
                _localization.GetString("Settings_Confirm_RemovePanel_ManagedFiles_Message"),
                title,
                MessageBoxImage.Warning)
            : _confirmation.Confirm(
                _localization.GetString("Settings_Confirm_RemovePanel_Message"),
                title);

        if (!confirmed) return;

        _windowManager.RemoveChildWindow(_selectedPanel.Id, releaseManagedItems: true);
    }

    private bool CanRemovePanel() => _selectedPanel is not null;

    private void OnPanelAdded(object? sender, ChildWindowModel model)
    {
        Panels.Add(model);
        RefreshPanelsOnCurrentPage();
    }

    private void OnPanelRemoved(object? sender, ChildWindowModel model)
    {
        var existing = Panels.FirstOrDefault(p => p.Id == model.Id);
        if (existing is not null)
            Panels.Remove(existing);

        if (_selectedPanel?.Id == model.Id)
            SelectedPanel = null;

        RefreshPanelsOnCurrentPage();
    }
}
