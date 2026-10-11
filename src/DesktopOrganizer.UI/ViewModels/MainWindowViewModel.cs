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
using DesktopOrganizer.Services.Diagnostics;
using DesktopOrganizer.Services.Storage;
using DesktopOrganizer.UI.Commands;
using DesktopOrganizer.UI.Localization;
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
    private readonly IPanelCloneFlow _panelCloneFlow;
    private readonly IStatusNotifier _statusNotifier;
    private readonly IDesktopIconsService _desktopIcons;
    private readonly IAppLog? _appLog;
    private ChildWindowModel? _selectedPanel;
    private SettingsSection _selectedSection = SettingsSection.General;
    private LanguageOption _selectedLanguageOption;

    /// <summary>
    /// H-1 fix (backlog 1.x wave 1 review): the <see cref="PageItemViewModel.EditingName"/>
    /// <see cref="BeginRenamePage"/> most recently seeded the editor with — see
    /// <see cref="TryCommitRenamePage"/>'s remarks. At most one page is ever
    /// <see cref="PageItemViewModel.IsEditing"/> at a time, so a single field (rather than a per-page
    /// map) is sufficient.
    /// </summary>
    private string? _renameSeed;

    /// <summary>
    /// ViewModel for the "Layout snapshots" section.
    /// </summary>
    public LayoutSnapshotsViewModel LayoutSnapshots { get; }

    /// <summary>
    /// ViewModel for the About section's "Updates"/"Links" cards (release-hardening-1.0.md §G2) — DI
    /// singleton, injected and stored verbatim (unlike <see cref="GlobalDefaults"/>/<see cref="SystemIconsSettings"/>,
    /// which this class constructs internally): <c>AboutSection.xaml</c> binds against it directly
    /// (<c>{Binding About.X}</c>), the same convention <c>BackupSection.xaml</c> already uses for
    /// <see cref="LayoutSnapshots"/>.
    /// </summary>
    public AboutViewModel About { get; }

    /// <summary>
    /// ViewModel for the Backup section's "Files without a panel" card (ADR-009 amendment 2026-10-10) -
    /// DI-provided, <see langword="null"/> only for a host that never shows the Backup section (unit tests).
    /// </summary>
    public ManagedStorageOrphansViewModel? StorageOrphans { get; }

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
    /// Ordered collection of pages shown in the Settings → "Pages" list and the Panels section's page
    /// selector (ADR-043, docs/PLANS/page-display-names.md §5.2). Kept in sync with
    /// <see cref="IPageManager.PageDescriptors"/> by <see cref="SyncPages"/>.
    /// </summary>
    public ObservableCollection<PageItemViewModel> Pages { get; } = [];

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
            _statusNotifier.Dismiss();
            OnPropertyChanged(nameof(SelectedPanel));
        }
    }

    /// <summary>
    /// The non-blocking status surface (design-system-1x.md §2.8, ADR-047 D-9) <c>MainWindow.xaml</c>'s
    /// <c>InfoBar</c> host binds against — the same DI-singleton instance <see cref="MovePanelToPage"/>/
    /// <see cref="CopyPanelToPage"/> call <see cref="IStatusNotifier.Show"/> on. Replaces the pre-6.3
    /// <c>PanelPageStatusMessage</c> string property (and the MODAL <see cref="IConfirmationService.Notify"/>
    /// success dialog it had itself already replaced, M-E fix — that dialog now only ever fires for a
    /// FAILURE, see <see cref="_panelCloneFlow"/>'s own remarks).
    /// </summary>
    public IStatusNotifier Status => _statusNotifier;

    /// <summary>
    /// Snap-to-grid cell size in pixels. 1 = no snapping, 2–128 = snap.
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

    /// <summary><see langword="false"/> while position snapping is on — the icon-grid size toggle has no effect then.</summary>
    public bool IsSnapResizeToIconGridAvailable => GlobalDefaults.IsSnapResizeToIconGridAvailable;

    /// <summary>The grid step shown by the "Snap panel position to grid" card's slider.</summary>
    public int GridStep
    {
        get => GlobalDefaults.GridStep;
        set => GlobalDefaults.GridStep = value;
    }

    // ── Layout editing (docs/PLANS/phase-6-edit-layout-ux.md §1 D-9 #3) ─────────────────────────────
    // Forwards directly to the injected ILayoutEditModeService singleton — unlike GridSize
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

    /// <summary>
    /// The "New panel defaults" section's accent/text colour editor — forwards
    /// <see cref="GlobalDefaultsViewModel.DefaultColors"/> verbatim (docs/PLANS/color-presets-and-size-step.md
    /// §3.7), the same instance every time (not re-created per read).
    /// </summary>
    public PanelColorEditorViewModel DefaultColors => GlobalDefaults.DefaultColors;

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
    /// Whether Windows itself owns the autostart decision (Microsoft Store / package-identity build): the
    /// General section then shows <see cref="OpenStartupSettingsCommand"/> instead of the
    /// <see cref="AutostartEnabled"/> toggle. A process-lifetime constant, so no change notification.
    /// </summary>
    public bool AutostartManagedBySystem => _autostart.IsManagedBySystem;

    /// <summary>Whether the General section shows the <see cref="AutostartEnabled"/> toggle — the exact complement of <see cref="AutostartManagedBySystem"/>.</summary>
    public bool AutostartToggleAvailable => !_autostart.IsManagedBySystem;

    /// <summary>Opens the Windows "Startup apps" settings page — General section, shown when <see cref="AutostartManagedBySystem"/>.</summary>
    public ICommand OpenStartupSettingsCommand { get; }

    /// <summary>
    /// Whether the real desktop icons (the shell's <c>SHELLDLL_DefView</c>) are currently visible.
    /// Session-only, never persisted: the live state is read from <see cref="IDesktopIconsService"/>
    /// every time, and <see cref="IDesktopIconsService.VisibilityChanged"/> re-raises this property when
    /// the tray item, the desktop double-click gesture or Explorer-restart recovery changes it.
    /// </summary>
    public bool DesktopIconsVisible
    {
        get => _desktopIcons.AreIconsVisible;
        set
        {
            if (_desktopIcons.AreIconsVisible == value) return;
            _desktopIcons.SetIconsVisible(value);

            // Also raised by the VisibilityChanged subscription in the constructor; this explicit raise is
            // kept on purpose so the setter's contract never depends on the service raising its event.
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

    /// <summary>
    /// Begins inline-renaming a page (ADR-043, docs/PLANS/page-display-names.md §5.1/§5.2). Parameter is
    /// the <see cref="PageItemViewModel"/> to edit. Committing (or, failing that, cancelling) any other
    /// page's already-in-progress edit first — at most one page is ever <see cref="PageItemViewModel.IsEditing"/>
    /// at a time.
    /// </summary>
    public ICommand BeginRenamePageCommand { get; }

    /// <summary>
    /// Commits the in-progress inline rename for the given <see cref="PageItemViewModel"/> (parameter) via
    /// <see cref="IPageManager.RenamePage"/>. On success, closes the editor; on failure, sets
    /// <see cref="PageItemViewModel.RenameError"/> and leaves the editor open so the user can correct the
    /// name. Wired to <c>InlineRenameBehavior</c>'s Enter key handling — see
    /// <see cref="CommitRenamePageOnLostFocusCommand"/> for the lost-focus counterpart, which reacts
    /// differently to a failed commit (UX-4 fix, backlog 1.x wave 1 review).
    /// </summary>
    public ICommand CommitRenamePageCommand { get; }

    /// <summary>
    /// The lost-focus counterpart of <see cref="CommitRenamePageCommand"/> (UX-4 fix, backlog 1.x wave 1
    /// review) — wired to <c>InlineRenameBehavior.CommitOnLostFocusCommand</c> in <c>PagesSection.xaml</c>.
    /// Behaves identically to <see cref="CommitRenamePageCommand"/> on success; on a failed commit,
    /// auto-cancels the edit instead of leaving the row open with neither keyboard focus nor a caret —
    /// see <c>CommitRenamePageOnLostFocus</c>'s remarks.
    /// </summary>
    public ICommand CommitRenamePageOnLostFocusCommand { get; }

    /// <summary>
    /// Cancels the in-progress inline rename for the given <see cref="PageItemViewModel"/> (parameter)
    /// without touching <see cref="IPageManager"/>.
    /// </summary>
    public ICommand CancelRenamePageCommand { get; }

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
    /// Creates a fully independent copy of <see cref="SelectedPanel"/> on the destination page (parameter,
    /// a page id string) — the Panels section's "Copy to page ▾" menu (ADR-045, «Panel clone to page»).
    /// Unlike <see cref="MovePanelToPageCommand"/>, the CURRENT page is a valid destination (copying a
    /// panel onto its own page is exactly how a same-page duplicate is made).
    /// </summary>
    public ICommand CopyPanelToPageCommand { get; }

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

    /// <summary>The per-user storage root (Inno channel <c>%LOCALAPPDATA%\DesktopOrganizer</c>, Store channel <c>%USERPROFILE%\DesktopOrganizer</c>), for the About section.</summary>
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
    /// The log this instance writes failures to: the one explicitly supplied, falling back to the
    /// process-wide ambient <see cref="AppLog.Current"/> for tests that construct this type directly
    /// without a log.
    /// </summary>
    private IAppLog Log => _appLog ?? AppLog.Current;

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
    /// <param name="about">See <see cref="About"/>.</param>
    /// <param name="colorPicker">Forwarded to <see cref="GlobalDefaults"/>' <see cref="GlobalDefaultsViewModel.DefaultColors"/> (docs/PLANS/color-presets-and-size-step.md §3.7).</param>
    /// <param name="panelCloneFlow">
    /// Backs <see cref="CopyPanelToPageCommand"/> (H-1 fix, code review, panel clone to page, backlog 1.x
    /// wave 3 review) — the shared busy-cursor/failure-notification/stale-page flow, also used by
    /// <c>ChildWindowViewModel.CopyPanelToPageCommand</c>. See <see cref="IPanelCloneFlow"/>'s own remarks.
    /// </param>
    /// <param name="statusNotifier">See <see cref="Status"/>.</param>
    /// <param name="desktopIcons">Backs <see cref="DesktopIconsVisible"/> (the "Show desktop icons" toggle in General).</param>
    /// <param name="appLog">See <see cref="Log"/>.</param>
    /// <param name="storageOrphans">See <see cref="StorageOrphans"/>. Appended last to keep existing positional call sites compiling.</param>
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
        CultureInfo systemUiCulture,
        AboutViewModel about,
        IColorPickerService colorPicker,
        IPanelCloneFlow panelCloneFlow,
        IStatusNotifier statusNotifier,
        IDesktopIconsService desktopIcons,
        IAppLog? appLog = null,
        ManagedStorageOrphansViewModel? storageOrphans = null)
    {
        StorageOrphans = storageOrphans;
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
        _panelCloneFlow = panelCloneFlow;
        _statusNotifier = statusNotifier;
        _desktopIcons = desktopIcons ?? throw new ArgumentNullException(nameof(desktopIcons));
        _appLog = appLog;
        LayoutSnapshots = layoutSnapshots;
        About = about ?? throw new ArgumentNullException(nameof(about));

        // The desktop icons' real state lives in the shell and is changed from several entry points
        // (tray item, desktop double-click, Explorer-restart recovery) — mirror every change into the
        // General section's toggle. Never unsubscribed: this ViewModel is a DI singleton, same rationale
        // as every other never-unsubscribed subscription in this constructor.
        _desktopIcons.VisibilityChanged += (_, _) => OnPropertyChanged(nameof(DesktopIconsVisible));

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

        GlobalDefaults = new GlobalDefaultsViewModel(appSettings, windowManager, colorPicker);
        SystemIconsSettings = new SystemIconsSettingsViewModel(systemIconsPanelManager);

        // Re-raise every sub-ViewModel's own PropertyChanged verbatim on this ViewModel — the property
        // names match exactly (see the forwarding properties above), so no name translation is needed.
        // Never unsubscribed: both sub-ViewModels are owned for the lifetime of this (DI-singleton)
        // ViewModel.
        GlobalDefaults.PropertyChanged += (_, e) =>
        {
            OnPropertyChanged(e.PropertyName!);

            // ADR-026 D-2/D-5: the Normal-mode gesture preference is the third input of every open
            // panel's resize gate. IAppSettings has no change notification, so relay it over the shared
            // layout-edit service the panels already subscribe to (no new persisted state).
            if (e.PropertyName == nameof(NormalModeLayoutGestures))
                _layoutEditMode.NotifyNormalModeGesturesChanged();
        };
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
        BeginRenamePageCommand = new RelayCommand<PageItemViewModel>(BeginRenamePage);
        CommitRenamePageCommand = new RelayCommand<PageItemViewModel>(CommitRenamePage);
        CommitRenamePageOnLostFocusCommand = new RelayCommand<PageItemViewModel>(CommitRenamePageOnLostFocus);
        CancelRenamePageCommand = new RelayCommand<PageItemViewModel>(CancelRenamePage);
        SelectSectionCommand = new RelayCommand<SettingsSection>(section => SelectedSection = section);
        CustomizePanelCommand = new RelayCommand<ChildWindowModel>(CustomizePanel, CanCustomizePanel);
        MovePanelToPageCommand = new RelayCommand<string>(MovePanelToPage, CanMovePanelToPage);
        CopyPanelToPageCommand = new RelayCommand<string>(CopyPanelToPage, CanCopyPanelToPage);
        TakeDefaultsFromSelectedPanelCommand = new RelayCommand(TakeDefaultsFromSelectedPanel, () => _selectedPanel is not null);
        ResetOneTimeHintsCommand = new RelayCommand(ResetOneTimeHints, () => _appSettings.HasShownMoveHint);
        OpenStorageFolderCommand = new RelayCommand(OpenStorageFolder);
        OpenStartupSettingsCommand = new RelayCommand(_autostart.OpenSystemStartupSettings);

        _windowManager.OnChildWindowAdded += OnPanelAdded;
        _windowManager.OnChildWindowRemoved += OnPanelRemoved;

        // M-D fix (code review, panel clone to page, backlog 1.x wave 3 review): a panel moved via its OWN
        // header context menu (ChildWindowViewModel.MovePanelToPageCommand) never goes through THIS
        // ViewModel at all — without this subscription, PanelsOnCurrentPage kept showing it under its
        // stale page until some unrelated event happened to trigger a refresh.
        _windowManager.OnChildWindowPageChanged += OnPanelPageChanged;
        _pageManager.PageChanged += OnPageChanged;

        // ADR-043: composition/order/name changes (add/remove/rename) — never fired for a mere active-page
        // switch (PageChanged, above, covers that). Never unsubscribed: this ViewModel is a DI singleton,
        // same rationale as every other never-unsubscribed subscription in this constructor.
        _pageManager.PagesChanged += OnPagesChanged;

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
    /// Synchronizes <see cref="Pages"/> with <see cref="IPageManager.PageDescriptors"/> (ADR-043,
    /// docs/PLANS/page-display-names.md §5.2) — called once from this constructor, and on every
    /// subsequent <see cref="IPageManager.PagesChanged"/> (add/remove/rename). Preserves an already-present
    /// page's own <see cref="PageItemViewModel"/> INSTANCE by <see cref="PageItemViewModel.Id"/> (only its
    /// <see cref="PageItemViewModel.Name"/> is updated in place) rather than rebuilding the collection from
    /// scratch, for the same reason <see cref="RefreshPanelsOnCurrentPage"/> preserves
    /// <see cref="ChildWindowModel"/> instances: a live <c>ListBox</c>/<c>ItemsControl</c> row holding an
    /// in-progress rename edit (<see cref="PageItemViewModel.IsEditing"/>) or a live selection must not be
    /// torn down and re-created just because some OTHER page was added/removed/renamed. Repositions
    /// existing rows via <see cref="ObservableCollection{T}.Move"/> to keep order in sync with
    /// <see cref="IPageManager.PageDescriptors"/> — never a remove-then-re-add.
    /// </summary>
    public void SyncPages() => PageItemViewModelSync.Sync(Pages, _pageManager.PageDescriptors);

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
    /// L-9 fix (backlog 1.x wave 1 review): closes whichever <see cref="PageItemViewModel"/> is currently
    /// <see cref="PageItemViewModel.IsEditing"/> (there is at most one — see <see cref="BeginRenamePage"/>'s
    /// remarks), and dismisses <see cref="Status"/> (M-E fix, code review, panel clone to page, backlog
    /// 1.x wave 3 review — a non-blocking "moved/copied to page" status left showing has no reason to
    /// survive the window going away). Called from <c>MainWindow.xaml.cs</c> whenever this window
    /// becomes hidden (right-clicking a panel while mid-rename, Alt+Tab away, or the window's own
    /// close-to-hide, <c>MainWindow.OnClosing</c>). Without the rename half of this, an in-progress rename
    /// editor stayed <see cref="PageItemViewModel.IsEditing"/> across a hide/show cycle — hiding a window
    /// does not, by itself, make WPF fire the rename <see cref="System.Windows.Controls.TextBox"/>'s own
    /// <c>LostKeyboardFocus</c> (there is no "focus" concept for a window that is not visible), and this
    /// <c>MainWindowViewModel</c> is a DI singleton with no other hook of its own that would notice the
    /// window went away — so the row reappeared on the NEXT show already open, sometimes still showing a
    /// stale <see cref="PageItemViewModel.RenameError"/> from before.
    /// </summary>
    /// <remarks>
    /// UX-4b fix (code review, panel clone to page): delegates to <see cref="CommitRenamePageOnLostFocus"/> —
    /// the SAME commit-on-lost-focus semantics an ordinary blur already applies — rather than the plain,
    /// always-discarding <see cref="CancelRenamePage"/> this used to call. Losing the window entirely is not
    /// meaningfully different from losing keyboard focus to any other control: a validly typed name is
    /// committed exactly as it would be on blur; only a FAILED commit falls back to closing the editor with
    /// <see cref="PageItemViewModel.RenameError"/> preserved (not cleared, unlike the user's own explicit
    /// Escape) — the previous behavior silently threw away a valid, already-typed rename just because the
    /// settings window happened to lose visibility before the user tabbed/clicked away themselves.
    /// <para>
    /// LOW-4 fix (code review, panel clone to page, backlog 1.x wave 3 review): renamed from
    /// <c>CancelAnyOpenRename</c> — the previous name no longer matched what this method actually does
    /// (commit-on-lost-focus semantics, not a plain cancel) or, now, its full scope (also clears the panel
    /// page status).
    /// </para>
    /// </remarks>
    public void CloseAnyOpenRename()
    {
        var editing = Pages.FirstOrDefault(p => p.IsEditing);
        if (editing is not null) CommitRenamePageOnLostFocus(editing);

        DismissStatus();
    }

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
    /// Dismisses <see cref="Status"/> (M-E fix, code review, panel clone to page, backlog 1.x wave 3
    /// review; phase 6.3 renamed from <c>ClearPanelPageStatus</c> once the status moved off a
    /// Panels-section-specific string property onto the shared <see cref="IStatusNotifier"/>). Called from
    /// every other Panels-section command's own entry point (<see cref="AddPanel"/>/<see cref="RemovePanel"/>/
    /// <see cref="CustomizePanel"/>/<see cref="TakeDefaultsFromSelectedPanel"/>/<see cref="AlignAllPanels"/>/
    /// <see cref="SwitchPage"/>) and from the <see cref="SelectedPanel"/> setter, so a stale "moved/copied
    /// to page" status never lingers once the user does anything else in the section. <see cref="MovePanelToPage"/>/
    /// <see cref="CopyPanelToPage"/> themselves overwrite it unconditionally on their own success path —
    /// this call at their own entry point still matters for their FAILURE path, where nothing else would
    /// otherwise dismiss a leftover status from an earlier, successful action.
    /// </summary>
    private void DismissStatus() => _statusNotifier.Dismiss();

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
        DismissStatus();

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

        DismissStatus();

        var panelCount = Panels.Count(p => p.PageId == CurrentPage);
        var panelText = _localization.FormatPlural("Plural_Panels", panelCount);

        // L-5 fix (review round, design-system-1x.md §2.8): a bulk re-layout of every panel on the page
        // has no single-step undo — Warning (Enter = "No") instead of the Question default's "Yes".
        if (!_confirmation.Confirm(
                _localization.Format("Settings_Confirm_AlignAll_Message_Format", panelText),
                _localization.GetString("Settings_Confirm_AlignAll_Title"),
                MessageBoxImage.Warning))
        {
            return;
        }

        _windowManager.AlignPanelsToWorkspaceGrid(null);
    }

    private void SwitchPage(string? pageId)
    {
        if (string.IsNullOrWhiteSpace(pageId)) return;
        DismissStatus();
        _pageManager.SwitchTo(pageId);
    }

    /// <summary>
    /// Adds a new page (<see cref="IPageManager.AddNewPage"/> — GUID id, localized unique name, ADR-043
    /// §4.3) and switches to it. Does not add to <see cref="Pages"/> itself:
    /// <see cref="IPageManager.AddNewPage"/> raises <see cref="IPageManager.PagesChanged"/> synchronously
    /// before returning, and <see cref="OnPagesChanged"/> resyncs <see cref="Pages"/> from
    /// <see cref="IPageManager.PageDescriptors"/> — adding it here too would double-add it.
    /// </summary>
    private void AddPage()
    {
        var newId = _pageManager.AddNewPage();
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
        // restarted.
        //
        // L-2 fix (backlog 1.x wave 1 review): no explicit SyncPages() call here on success anymore —
        // PageManagerService.RemovePage already raises PagesChanged synchronously on every successful
        // removal (see its own remarks), which OnPagesChanged (subscribed in this class's constructor)
        // already turns into exactly that SyncPages() call. Calling it again here just re-ran the same
        // no-op diff a second time. The return value still needs consulting, though — SyncPages() (via
        // the event) must NOT run at all on a declined confirmation, and the discarded call below is
        // what still gives IPageManager the chance to skip PagesChanged in that case.
        _ = _pageManager.RemovePage(pageId);
    }

    private bool CanRemovePage(string? pageId) => !string.IsNullOrWhiteSpace(pageId) && pageId != "default";

    private void OnPageChanged(string newPage)
    {
        OnPropertyChanged(nameof(CurrentPage));
        OnPropertyChanged(nameof(SelectedPage));
        RefreshPanelsOnCurrentPage();
    }

    private void OnPagesChanged() => SyncPages();

    /// <summary>
    /// Begins inline-renaming <paramref name="page"/> (ADR-043 §5.1/§5.2). No-op if <paramref name="page"/>
    /// is <see langword="null"/> (defensive — the view always passes an explicit
    /// <see cref="PageItemViewModel"/>) or already <see cref="PageItemViewModel.IsEditing"/> (M-1 fix,
    /// backlog 1.x wave 1 review: <c>PagesSection.xaml.cs</c>'s <c>F2</c> handler can reach this method
    /// again for a row that is already mid-edit — without this guard, that silently reset
    /// <see cref="PageItemViewModel.EditingName"/> back to the seed, discarding whatever the user had
    /// already typed). Commits (or, if that itself fails, force-cancels) any other page already mid-edit
    /// first, so at most one page is ever <see cref="PageItemViewModel.IsEditing"/>.
    /// </summary>
    /// <param name="page">The page to rename.</param>
    private void BeginRenamePage(PageItemViewModel? page)
    {
        if (page is null || page.IsEditing) return;

        // L-3 fix (backlog 1.x wave 1 review): snapshotted via ToList() — CommitRenamePage below can
        // itself raise IPageManager.PagesChanged (a Renamed outcome), which OnPagesChanged turns
        // straight into a SyncPages() call that inserts/removes/reorders this SAME Pages collection out
        // from under a live foreach, throwing InvalidOperationException.
        foreach (var other in Pages.Where(p => p.IsEditing && !ReferenceEquals(p, page)).ToList())
        {
            CommitRenamePage(other);
            if (other.IsEditing) CancelRenamePage(other); // Still editing => the commit above failed.
        }

        page.RenameError = null;
        page.EditingName = PageDisplayNames.Resolve(page.Id, page.Name, _localization.CurrentCulture);
        _renameSeed = page.EditingName;
        page.IsEditing = true;
    }

    /// <summary>
    /// Commits <paramref name="page"/>'s in-progress inline rename — the Enter-triggered counterpart of
    /// <see cref="CommitRenamePageOnLostFocus"/>, both backed by <see cref="TryCommitRenamePage"/>. On a
    /// failed commit, unlike <see cref="CommitRenamePageOnLostFocus"/>, leaves the editor open (the
    /// <see cref="PageItemViewModel.RenameError"/> <see cref="TryCommitRenamePage"/> already set) so the
    /// user, still right there mid-keystroke, can just fix the text and retry.
    /// </summary>
    /// <param name="page">The page whose in-progress edit to commit.</param>
    private void CommitRenamePage(PageItemViewModel? page) => TryCommitRenamePage(page);

    /// <summary>
    /// UX-4 fix (backlog 1.x wave 1 review): the lost-focus-triggered counterpart of
    /// <see cref="CommitRenamePage"/> (wired to <see cref="CommitRenamePageOnLostFocusCommand"/>, itself
    /// bound to <c>InlineRenameBehavior.CommitOnLostFocusCommand</c> in <c>PagesSection.xaml</c>).
    /// Behaves identically on success; on a FAILED commit, unlike <see cref="CommitRenamePage"/>,
    /// auto-cancels the edit (<see cref="PageItemViewModel.IsEditing"/> reset to <see langword="false"/>)
    /// instead of leaving the row open with neither keyboard focus nor a caret — a "stuck" form a user
    /// could otherwise only escape by clicking back into the very row that just rejected their input.
    /// Deliberately does NOT clear <see cref="PageItemViewModel.RenameError"/> the way the explicit,
    /// user-initiated <see cref="CancelRenamePage"/> (Escape) does — <see cref="TryCommitRenamePage"/>'s
    /// own failure path already set it, and it is left in place (even though the view mode this row is
    /// about to switch back to has no element bound to it today) until the next
    /// <see cref="BeginRenamePage"/> on this same row clears it, rather than silently discarding the
    /// reason for the revert.
    /// </summary>
    /// <param name="page">The page whose in-progress edit lost keyboard focus.</param>
    private void CommitRenamePageOnLostFocus(PageItemViewModel? page)
    {
        if (page is null || !page.IsEditing) return;
        if (TryCommitRenamePage(page)) return;

        page.IsEditing = false;
    }

    /// <summary>
    /// Attempts to commit <paramref name="page"/>'s in-progress inline rename via
    /// <see cref="IPageManager.RenamePage"/>, shared by <see cref="CommitRenamePage"/> and
    /// <see cref="CommitRenamePageOnLostFocus"/> — the two differ only in what they do once this method
    /// itself has already updated <paramref name="page"/>'s state and returned.
    /// A no-op (returns <see langword="false"/>) if <paramref name="page"/> is <see langword="null"/> or
    /// not currently <see cref="PageItemViewModel.IsEditing"/>.
    /// </summary>
    /// <remarks>
    /// H-1 fix (backlog 1.x wave 1 review): committing the untouched seed text — what
    /// <see cref="BeginRenamePage"/> pre-filled <see cref="PageItemViewModel.EditingName"/> with, i.e.
    /// the page's own effective display name, tracked in <see cref="_renameSeed"/> — is always treated as
    /// unchanged right here, before <see cref="IPageManager.RenamePage"/> is even called. This is defence
    /// in depth: <c>PageNameRules</c>' own step 7 fix already makes <see cref="IPageManager.RenamePage"/>
    /// itself return <see cref="PageRenameResult.Unchanged"/> (and skip its own persist) for exactly this
    /// case, but this VM-level check additionally means the service method is never even invoked for a
    /// plain "opened the editor, changed nothing (or changed it back)" commit.
    /// </remarks>
    /// <param name="page">The page whose in-progress edit to commit.</param>
    /// <returns><see langword="true"/> if the edit is now closed (an actual rename, or a no-op that was
    /// already correct); <see langword="false"/> if the commit failed and the editor is still open with
    /// <see cref="PageItemViewModel.RenameError"/> set.</returns>
    private bool TryCommitRenamePage(PageItemViewModel? page)
    {
        if (page is null || !page.IsEditing) return false;

        var trimmedEditingName = page.EditingName?.Trim() ?? string.Empty;
        if (string.Equals(trimmedEditingName, _renameSeed, StringComparison.Ordinal))
        {
            page.RenameError = null;
            page.IsEditing = false;
            return true;
        }

        var result = _pageManager.RenamePage(page.Id, page.EditingName);

        if (result is PageRenameResult.Renamed or PageRenameResult.Unchanged)
        {
            page.RenameError = null;
            page.IsEditing = false;
            return true;
        }

        page.RenameError = result;
        return false;
    }

    /// <summary>Cancels <paramref name="page"/>'s in-progress inline rename without touching <see cref="IPageManager"/>.</summary>
    /// <param name="page">The page whose in-progress edit to cancel. A no-op if <see langword="null"/>.</param>
    private void CancelRenamePage(PageItemViewModel? page)
    {
        if (page is null) return;

        page.RenameError = null;
        page.IsEditing = false;
    }

    private void CustomizePanel(ChildWindowModel? panel)
    {
        var target = panel ?? _selectedPanel;
        if (target is null) return;
        DismissStatus();
        _panelCustomizeService.Show(target);
    }

    private bool CanCustomizePanel(ChildWindowModel? panel) => (panel ?? _selectedPanel) is not null;

    private void MovePanelToPage(string? pageId)
    {
        if (_selectedPanel is null || string.IsNullOrWhiteSpace(pageId)) return;

        DismissStatus();

        var target = _selectedPanel;
        _windowManager.MovePanelToPage(target, pageId);

        // NEW-1 fix (final review, panel clone to page, backlog 1.x): IWindowManager.MovePanelToPage
        // silently no-ops for a destination page IPageManager doesn't know about (its own fix-cycle
        // finding 10 remarks) — target.PageId is then still the OLD page, never pageId. Without this
        // check, the caller had no way to tell a real move from a rejected one and always announced
        // "Moved to page X" regardless. Pages is re-synced instead (same defensive move CopyPanelToPage's
        // own onUnknownPage callback already performs for the sibling clone-to-page flow), in case the
        // destination looked unknown only because this ViewModel's own Pages had gone stale; no status is
        // shown for a move that never happened.
        if (target.PageId != pageId)
        {
            SyncPages();
            return;
        }

        // NEW-6 fix: no explicit RefreshPanelsOnCurrentPage() here anymore — the successful move above
        // already raised IWindowManager.OnChildWindowPageChanged synchronously (see its own remarks), and
        // this ViewModel's OnPanelPageChanged handler already turns that into exactly this same
        // RefreshPanelsOnCurrentPage() call (same rationale as RemovePage's own L-2 fix, above, for its
        // sibling PagesChanged/SyncPages pairing).

        // M-E fix (code review, panel clone to page, backlog 1.x wave 3 review); phase 6.3 (design-system-
        // 1x.md §2.8) moved this off PanelPageStatusMessage onto the shared IStatusNotifier/InfoBar — a
        // brief, NON-blocking status for the Settings-window-driven move, replacing the previous MODAL
        // success Notify. Unlike the panel's own header context menu
        // (ChildWindowViewModel.MovePanelToPageCommand), where the effect is already visible on the
        // desktop right away (Fences shows no toast for "Send Fence to Page" either), the destination page
        // here is very likely NOT the one currently shown, so without this the panel just silently
        // disappears from the "Panels on this page" list with no indication of where it went.
        _statusNotifier.Show(
            StatusSeverity.Success,
            _localization.Format("Panel_MovedToPage_Message_Format", ResolvePageDisplayName(pageId)),
            _localization.GetString("Panel_MovedToPage_Title"));
    }

    private bool CanMovePanelToPage(string? pageId)
        => _selectedPanel is not null && !string.IsNullOrWhiteSpace(pageId) && pageId != _selectedPanel.PageId;

    /// <summary>
    /// Resolves <paramref name="pageId"/>'s effective display name from <see cref="Pages"/> (ADR-043) —
    /// backs the "moved/copied to page" success notifications in <see cref="MovePanelToPage"/>/
    /// <see cref="CopyPanelToPage"/>. Falls back to localizing against an unnamed page if, somehow,
    /// <paramref name="pageId"/> is not (yet) in <see cref="Pages"/> — mirrors <see cref="BeginRenamePage"/>'s
    /// own use of <see cref="PageDisplayNames.Resolve"/>.
    /// </summary>
    /// <param name="pageId">The destination page identifier.</param>
    private string ResolvePageDisplayName(string pageId)
    {
        var page = Pages.FirstOrDefault(p => p.Id == pageId);
        return PageDisplayNames.Resolve(pageId, page?.Name, _localization.CurrentCulture);
    }

    /// <summary>
    /// Backs <see cref="CopyPanelToPageCommand"/> (ADR-045). Unlike <see cref="MovePanelToPage"/>, a
    /// same-page destination is expected (that is how a same-page duplicate is requested) so
    /// <see cref="_panelCloneFlow"/> is asked to clone unconditionally rather than early-returning on
    /// <c>pageId == _selectedPanel.PageId</c>. A copy onto the CURRENT page selects the clone so the user
    /// immediately sees what was just created; a copy onto another (currently hidden) page leaves the
    /// existing selection alone.
    /// </summary>
    /// <remarks>
    /// M-4/LOW-3/H-1 fixes (code review, panel clone to page, backlog 1.x wave 3 review): the busy cursor,
    /// the failure notification and the stale-destination-page re-sync all moved into the shared
    /// <see cref="_panelCloneFlow"/> (used by <c>ChildWindowViewModel.CopyPanelToPage</c> too) — see
    /// <see cref="IPanelCloneFlow"/>'s own remarks, including why no apartment-state gate is needed here
    /// anymore. This method's own remaining job is exactly the parts that differ per call site: what to do
    /// with a SUCCESSFUL clone (select it / refresh <see cref="PanelsOnCurrentPage"/> / show the
    /// non-blocking <see cref="Status"/> (M-E fix — replacing the previous MODAL success Notify;
    /// phase 6.3 moved it off <c>PanelPageStatusMessage</c> onto <see cref="IStatusNotifier"/>).
    /// </remarks>
    /// <param name="pageId">The destination page identifier.</param>
    private void CopyPanelToPage(string? pageId)
    {
        if (_selectedPanel is null || string.IsNullOrWhiteSpace(pageId)) return;

        DismissStatus();

        var result = _panelCloneFlow.CopyToPage(_selectedPanel, pageId, onUnknownPage: SyncPages);
        if (result.Outcome != PanelCloneOutcome.Cloned) return;

        RefreshPanelsOnCurrentPage();
        if (result.ClonedPanel is { } clone && clone.PageId == CurrentPage)
            SelectedPanel = clone;

        _statusNotifier.Show(
            StatusSeverity.Success,
            _localization.Format("Panel_CopiedToPage_Message_Format", ResolvePageDisplayName(pageId)),
            _localization.GetString("Panel_CopiedToPage_Title"));
    }

    private bool CanCopyPanelToPage(string? pageId) => _selectedPanel is not null && !string.IsNullOrWhiteSpace(pageId);

    private void TakeDefaultsFromSelectedPanel()
    {
        if (_selectedPanel is null) return;
        DismissStatus();
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
            Log.Error(
                "MainWindowViewModel",
                "OpenStorageFolderFailed",
                ex,
                new KeyValuePair<string, object?>("path", StorageFolderPath));
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

        DismissStatus();

        // CRITICAL C1 fix (P08 fix cycle 1, 2026-09-22): sync every live panel's ViewModel-side state
        // — including any read-time stale-original->managed-copy remap ChildWindowViewModel.LoadFromModel
        // performed (ADR-009 Amendment) — back into its Model BEFORE deciding whether this removal has
        // anything to lose. Without this, a panel removed before its first save still has
        // _selectedPanel.FilePaths pointing at the stale original path (never itself "managed"), so the
        // IsManaged check below is false and the neutral "nothing lost" dialog is shown — but
        // RemoveChildWindow further down calls SyncToModel() again immediately before recycling, which
        // DOES pick up the by-then-remapped managed path and sends it to the Recycle Bin without the
        // warning the user should have seen (ADR-010 Decision 2, amended by ADR-035).
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

        // ADR-035: managed items go to the Recycle Bin; if any of them couldn't, the panel is kept
        // (fail visibly — nothing is ever deleted past the Recycle Bin) and the user is told why.
        var result = _windowManager.RemoveChildWindow(_selectedPanel.Id, releaseManagedItems: true);
        if (result.IsBlocked)
        {
            _confirmation.Notify(
                _localization.Format("Recycle_Failed_Message_Format", result.Remaining.Count),
                _localization.GetString("Recycle_Failed_Title"),
                MessageBoxImage.Warning);
        }
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

    /// <summary>
    /// M-D fix (code review, panel clone to page, backlog 1.x wave 3 review): reacts to
    /// <see cref="IWindowManager.OnChildWindowPageChanged"/> — <see cref="Panels"/> itself already holds the
    /// same <see cref="ChildWindowModel"/> instance <paramref name="model"/> is (its <c>PageId</c> was
    /// mutated in place), so only <see cref="PanelsOnCurrentPage"/> needs recomputing;
    /// <see cref="RefreshPanelsOnCurrentPage"/> already clears <see cref="SelectedPanel"/> on its own when
    /// the panel left the current page.
    /// </summary>
    private void OnPanelPageChanged(object? sender, ChildWindowModel model) => RefreshPanelsOnCurrentPage();
}
