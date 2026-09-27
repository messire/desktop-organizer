using System.ComponentModel;
using System.Windows.Input;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Commands;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;

namespace DesktopOrganizer.UI.ViewModels;

/// <summary>
/// ViewModel for the "Customize panel…" dialog (<see cref="Views.PanelCustomizeWindow"/>,
/// docs/prompts/settings-window-redesign.md D-3). Wraps a single <see cref="ChildWindowModel"/> — the
/// <see cref="Target"/> — and edits it live: every property setter below writes straight through to
/// the model and requests a save, with no separate OK/Cancel step (the model is the single source of
/// truth shared with the live panel, per CLAUDE.md). <see cref="Services.IPanelCustomizeService"/>
/// keeps one instance of this class alive for the whole application and reassigns
/// <see cref="Target"/> in place whenever "Customize panel…" is invoked for a different panel, so a
/// second invocation never tears down and rebuilds the dialog.
/// </summary>
public sealed class PanelCustomizeViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IWindowManager _windowManager;
    private readonly ILayoutEditModeService _layoutEditMode;
    private readonly IFolderPickerService _folderPicker;
    private readonly ILocalizationService _localization;
    private readonly IColorPickerService _colorPicker;
    private readonly IAppSettings _appSettings;
    private ChildWindowModel _target;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Initializes a new instance of the <see cref="PanelCustomizeViewModel"/> class.
    /// </summary>
    /// <param name="windowManager">Persists layout state after an edited property is applied.</param>
    /// <param name="layoutEditMode">
    /// Backs <see cref="CanChangeLayout"/> (ADR-026) — gates the Layout section's controls exactly like
    /// the panel's own context menu (FIX-B fix-cycle MAJOR finding M-1).
    /// </param>
    /// <param name="folderPicker">
    /// Shows the folder-browser dialog for <see cref="BrowseFolderCommand"/> (FIX-B fix-cycle minor
    /// finding 5 — extracted so this ViewModel is testable without a real Explorer dialog).
    /// </param>
    /// <param name="localization">
    /// Backs <see cref="WindowTitle"/>/<see cref="SortModeDescription"/>/<see cref="IconLayoutDescription"/>/
    /// <see cref="AutoSizeToContentDescription"/> (docs/PLANS/localization.md §2.9 "re-raise rule") — this
    /// ViewModel is transient (a fresh one is built the first time the dialog opens, or after it was
    /// closed — see <see cref="Services.IPanelCustomizeService"/>'s own remarks), not a singleton, and
    /// subscribes to <see cref="ILocalizationService.CultureChanged"/> only for the lifetime of one such
    /// instance: it unsubscribes in <see cref="Dispose"/>, called by
    /// <c>Views.PanelCustomizeWindow.OnClosed</c>.
    /// </param>
    /// <param name="colorPicker">Backs <see cref="Colors"/>' "Custom…" commands (docs/PLANS/color-presets-and-size-step.md §3.7).</param>
    /// <param name="appSettings">
    /// Read-only source for <see cref="Colors"/>' "Reset to defaults" button (ADR-042): resets this panel's
    /// opacity/surface style/icon size back to the app's CURRENT new-panel defaults. Never mutated by this
    /// class.
    /// </param>
    public PanelCustomizeViewModel(
        IWindowManager windowManager,
        ILayoutEditModeService layoutEditMode,
        IFolderPickerService folderPicker,
        ILocalizationService localization,
        IColorPickerService colorPicker,
        IAppSettings appSettings)
    {
        _windowManager = windowManager;
        _layoutEditMode = layoutEditMode;
        _folderPicker = folderPicker;
        _localization = localization;
        _colorPicker = colorPicker;
        _appSettings = appSettings;
        _localization.CultureChanged += OnCultureChanged;

        // Placeholder target until IPanelCustomizeService.Show/PanelCustomizeWindow.Retarget assigns
        // the real panel — mirrors ChildWindowViewModel's own "_model = new()" default, so Target/
        // Appearance are never null even before the dialog is shown for the first time.
        _target = new ChildWindowModel();
        _target.PropertyChanged += OnTargetPropertyChanged;

        Appearance = new SelectedPanelSettingsViewModel(windowManager);
        Appearance.SetPanel(_target);
        Appearance.PropertyChanged += OnAppearancePropertyChanged;

        Colors = new PanelColorEditorViewModel(
            _colorPicker,
            () => Appearance.SelectedPanelAccentColor,
            v => Appearance.SelectedPanelAccentColor = v,
            () => Appearance.SelectedPanelTextColor,
            v => Appearance.SelectedPanelTextColor = v,
            Appearance.ApplyColors,
            ResetAppearanceToDefaults,
            "Customize_ResetColors_Description");

        _layoutEditMode.PropertyChanged += OnLayoutEditModePropertyChanged;

        BrowseFolderCommand = new RelayCommand(BrowseFolder);
        ClearFolderCommand = new RelayCommand(ClearFolder, () => IsFolderPortal);
    }

    /// <summary>
    /// The panel's editable appearance settings (Title/Accent/Text/Opacity/Surface style/Icon size/
    /// View/Sort order) — reused as-is (see its own remarks) rather than duplicating the same proxy
    /// properties a second time. The dialog's "Appearance" section binds directly to this instance,
    /// e.g. <c>{Binding Appearance.SelectedPanelTitle}</c>.
    /// </summary>
    public SelectedPanelSettingsViewModel Appearance { get; }

    /// <summary>
    /// The panel's accent/text colour editor (docs/PLANS/color-presets-and-size-step.md §3.7) — presets,
    /// "Custom…", and the advanced hex boxes, all proxying <see cref="Appearance"/>'s own
    /// <see cref="SelectedPanelSettingsViewModel.SelectedPanelAccentColor"/>/<see cref="SelectedPanelSettingsViewModel.SelectedPanelTextColor"/>.
    /// The dialog's Appearance section binds directly to this instance, e.g. <c>{Binding Colors.AccentColor}</c>.
    /// </summary>
    public PanelColorEditorViewModel Colors { get; }

    /// <summary>
    /// The panel this dialog is currently editing. Reassigning this property re-targets every section
    /// of the dialog to the new panel: <see cref="Appearance"/> is re-pointed via
    /// <see cref="SelectedPanelSettingsViewModel.SetPanel"/>, this instance's own subscription moves from
    /// the old target's <see cref="INotifyPropertyChanged.PropertyChanged"/> to the new one's (FIX-B
    /// fix-cycle MAJOR finding M-2), and every Content/Layout property below — which all read straight
    /// through to this property — is re-raised.
    /// </summary>
    public ChildWindowModel Target
    {
        get => _target;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (ReferenceEquals(_target, value)) return;

            _target.PropertyChanged -= OnTargetPropertyChanged;
            _target = value;
            _target.PropertyChanged += OnTargetPropertyChanged;

            Appearance.SetPanel(_target);
            RaiseAllTargetDependentProperties();
        }
    }

    /// <summary>The dialog's title-bar text.</summary>
    public string WindowTitle => _localization.Format("Customize_WindowTitle_Format", _target.Title);

    // ── Content ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>All <see cref="PanelContentFilter"/> members, for the Content section's filter selector.</summary>
    public IReadOnlyList<PanelContentFilter> ContentFilters { get; } = Enum.GetValues<PanelContentFilter>();

    /// <summary>Which item types the panel shows.</summary>
    public PanelContentFilter ContentFilter
    {
        get => _target.ContentFilter;
        set => ApplyToTarget(() => _target.ContentFilter = value);
    }

    /// <summary>Whether the panel is currently a Folder Portal.</summary>
    public bool IsFolderPortal => !string.IsNullOrWhiteSpace(_target.SourceFolderPath);

    /// <summary>The Folder Portal's source folder, or an empty string when the panel isn't a portal.</summary>
    public string FolderPortalPath => _target.SourceFolderPath ?? string.Empty;

    /// <summary>
    /// Explains the Appearance section's Sort-order selector: while <see cref="IsFolderPortal"/> is
    /// <see langword="true"/> it always sorts by name; otherwise it describes what each option does
    /// (FIX-B fix-cycle minor finding 9 — this used to show the Folder Portal text unconditionally, even
    /// for a panel that was never a portal).
    /// </summary>
    public string SortModeDescription => IsFolderPortal
        ? _localization.GetString("Customize_SortMode_FolderPortal_Description")
        : _localization.GetString("Customize_SortMode_Manual_Description");

    /// <summary>Opens a folder-browser dialog and assigns the chosen folder as the panel's portal source.</summary>
    public ICommand BrowseFolderCommand { get; }

    /// <summary>Clears the panel's Folder Portal source, reverting it to a manually curated item list.</summary>
    public ICommand ClearFolderCommand { get; }

    // ── Layout ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>All <see cref="IconLayoutPolicy"/> members, for the Layout section's icon-layout selector.</summary>
    public IReadOnlyList<IconLayoutPolicy> IconLayoutPolicies { get; } = Enum.GetValues<IconLayoutPolicy>();

    /// <summary>
    /// How icons are distributed inside the panel's content area. The setter is a no-op while
    /// <see cref="CanChangeLayout"/> is <see langword="false"/> (ADR-026) — mirrors the panel's own
    /// context menu, which disables this same control while the layout is locked (FIX-B fix-cycle MAJOR
    /// finding M-1: this setter used to apply unconditionally).
    /// </summary>
    public IconLayoutPolicy IconLayoutPolicy
    {
        get => _target.IconLayoutPolicy;
        set
        {
            if (!CanChangeLayout) return;
            ApplyToTarget(() => _target.IconLayoutPolicy = value);
        }
    }

    /// <summary>
    /// Explains the Icon layout selector, or why it's disabled (FIX-B fix-cycle MAJOR finding M-1 — the
    /// disabled reason must be a description, not only a tooltip).
    /// </summary>
    public string IconLayoutDescription => CanChangeLayout
        ? _localization.GetString("Customize_IconLayout_Description")
        : _localization.GetString("Common_LockedReason");

    /// <summary>
    /// Whether the panel's height follows its content (<see cref="PanelSizingMode.AutoContent"/>).
    /// Only meaningful in <see cref="PanelViewMode.Grid"/> — see <see cref="CanAutoSizeToContent"/>. The
    /// setter is a no-op unless both <see cref="CanChangeLayout"/> and <see cref="CanAutoSizeToContent"/>
    /// hold (FIX-B fix-cycle MAJOR finding M-1: the layout-lock half of this guard was missing).
    /// </summary>
    public bool IsAutoSizeToContent
    {
        get => _target.SizingMode == PanelSizingMode.AutoContent;
        set
        {
            if (!CanChangeLayout || !CanAutoSizeToContent) return;
            ApplyToTarget(() => _target.SizingMode = value ? PanelSizingMode.AutoContent : PanelSizingMode.Manual);
        }
    }

    /// <summary>
    /// Whether <see cref="IsAutoSizeToContent"/> can be toggled — <see langword="true"/> only in
    /// <see cref="PanelViewMode.Grid"/> (ADR-029 Решение 7 — List view has no fit-to-content formula).
    /// </summary>
    public bool CanAutoSizeToContent => _target.ViewMode == PanelViewMode.Grid;

    /// <summary>
    /// The Auto-size-to-content checkbox's <c>IsEnabled</c> source: both the layout-lock gate (ADR-026)
    /// and the Grid-view-only gate (<see cref="CanAutoSizeToContent"/>) must hold (FIX-B fix-cycle MAJOR
    /// finding M-1).
    /// </summary>
    public bool CanToggleAutoSizeToContent => CanChangeLayout && CanAutoSizeToContent;

    /// <summary>
    /// Explains why <see cref="IsAutoSizeToContent"/> is disabled, or what it does when enabled
    /// (docs/prompts/settings-window-redesign.md D-4 "explain in the description" convention). The
    /// layout-lock reason (ADR-026) takes priority over the Grid-view-only reason when both apply
    /// (FIX-B fix-cycle MAJOR finding M-1).
    /// </summary>
    public string AutoSizeToContentDescription => !CanChangeLayout
        ? _localization.GetString("Common_LockedReason")
        : CanAutoSizeToContent
            ? _localization.GetString("Customize_AutoSizeToContent_Description")
            : _localization.GetString("Customize_AutoSizeToContentGridOnly_Description");

    /// <summary>
    /// Whether the Layout section's controls can be changed right now — <see langword="true"/> unless
    /// the panel layout is locked (ADR-026), exactly like the panel's own context menu's "Icon layout"/
    /// "Auto-size to content" items (FIX-B fix-cycle MAJOR finding M-1).
    /// </summary>
    public bool CanChangeLayout => _layoutEditMode.CanEnterEditLayout;

    /// <summary>
    /// Unsubscribes from <see cref="Appearance"/>, <see cref="Target"/>, and the injected
    /// <see cref="ILayoutEditModeService"/> — called by <see cref="Views.PanelCustomizeWindow"/> when the
    /// window closes (FIX-B fix-cycle MAJOR finding M-2: this used to leak the target/layout-mode
    /// subscriptions, though neither actually outlived the process since both are process-wide
    /// singletons — this is about the dialog no longer reacting to further changes, not a memory leak).
    /// </summary>
    public void Dispose()
    {
        _localization.CultureChanged -= OnCultureChanged;
        _layoutEditMode.PropertyChanged -= OnLayoutEditModePropertyChanged;
        _target.PropertyChanged -= OnTargetPropertyChanged;
        Appearance.PropertyChanged -= OnAppearancePropertyChanged;
        Appearance.Dispose();
    }

    /// <summary>
    /// Backs <see cref="Colors"/>' "Reset to defaults" button (release-polish-1.0 review round M7): resets
    /// <see cref="Target"/>'s accent/text/opacity/surface style/icon size to the app's CURRENT new-panel
    /// defaults — read-only <see cref="IAppSettings"/> use, never mutates settings — batched into a SINGLE
    /// <see cref="SelectedPanelSettingsViewModel.ApplyAppearance"/> call (one save), REPLACING
    /// <see cref="PanelColorEditorViewModel"/>'s own default colour-reset behaviour entirely (unlike the
    /// pre-M7 shape, this Customize-window host does not fall back to <c>PanelColorPresets.Default</c>'s
    /// fixed pair — it always mirrors whatever the user has currently configured as the new-panel defaults).
    /// </summary>
    private void ResetAppearanceToDefaults() =>
        Appearance.ApplyAppearance(
            _appSettings.DefaultBackgroundColor,
            _appSettings.DefaultTextColor,
            _appSettings.DefaultOpacity,
            PanelSurfaceStyleParser.ParseOrAmbient(_appSettings.DefaultSurfaceStyle),
            _appSettings.DefaultIconSize);

    private void BrowseFolder()
    {
        var path = _folderPicker.PickFolder(_localization.GetString("Customize_BrowseFolder_Title"), _target.SourceFolderPath);
        if (path is null) return;

        SetFolderPortalPath(path);
    }

    private void ClearFolder() => SetFolderPortalPath(null);

    /// <summary>
    /// Assigns <see cref="ChildWindowModel.SourceFolderPath"/> on <see cref="Target"/> — nothing else.
    /// docs/prompts/settings-window-redesign.md D-4: watcher attach/detach is
    /// <see cref="ChildWindowViewModel"/>'s reaction to that model property changing
    /// (<c>HandleSourceFolderPathChanged</c>), not this dialog's concern. That same reaction always ends
    /// by raising <see cref="ChildWindowViewModel.FilesChanged"/>, which <c>ChildWindow</c> already turns
    /// into a <see cref="IWindowManager.SaveWindowsState"/> call — so, unlike every other setter in this
    /// class, this one does <b>not</b> request a save of its own (FIX-B fix-cycle minor finding 6: doing
    /// so used to persist the change twice). <see cref="OnTargetPropertyChanged"/>'s
    /// <see cref="ChildWindowModel.SourceFolderPath"/> case re-raises everything this dialog projects
    /// from it (<see cref="IsFolderPortal"/>/<see cref="FolderPortalPath"/>/<see cref="SortModeDescription"/>),
    /// and <see cref="SelectedPanelSettingsViewModel"/>'s own subscription re-raises
    /// <see cref="SelectedPanelSettingsViewModel.SelectedPanelSupportsManualOrder"/> — so no manual
    /// re-projection call is needed here either, unlike before M-2.
    /// </summary>
    /// <param name="path">The new Folder Portal source, or <see langword="null"/> to clear it.</param>
    private void SetFolderPortalPath(string? path)
    {
        if (string.Equals(_target.SourceFolderPath, path, StringComparison.OrdinalIgnoreCase)) return;

        _target.SourceFolderPath = path;
    }

    /// <summary>
    /// Applies a change to <see cref="Target"/> and persists it. FIX-B fix-cycle minor finding 6 audit:
    /// of this method's three call sites, <see cref="ContentFilter"/>'s underlying model change is ALSO
    /// picked up by <c>ChildWindowViewModel.OnModelPropertyChanged</c> (which raises its own
    /// <c>FilesChanged</c>, itself already turned into a save by <c>ChildWindow</c> — the same
    /// double-save shape <see cref="SetFolderPortalPath"/> used to have) — but unlike
    /// <see cref="SetFolderPortalPath"/>'s comparatively expensive folder re-mirror, this is a cheap,
    /// harmless duplicate write, and removing it would make this method's correctness silently depend on
    /// a live <c>ChildWindowViewModel</c> always being subscribed to the same target, which is true today
    /// but not a contract this ViewModel enforces — so the explicit call stays. <see cref="IconLayoutPolicy"/>
    /// and <see cref="IsAutoSizeToContent"/>'s underlying model changes (<see cref="ChildWindowModel.IconLayoutPolicy"/>/
    /// <see cref="ChildWindowModel.SizingMode"/>) have NO such reaction anywhere else, so for them this
    /// call is the only save that ever happens.
    /// </summary>
    /// <param name="mutate">Mutates <see cref="Target"/>.</param>
    private void ApplyToTarget(Action mutate)
    {
        mutate();
        _windowManager.SaveWindowsState();
    }

    private void OnAppearancePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SelectedPanelSettingsViewModel.SelectedPanelTitle))
            OnPropertyChanged(nameof(WindowTitle));

        if (e.PropertyName == nameof(SelectedPanelSettingsViewModel.SelectedPanelViewMode))
        {
            OnPropertyChanged(nameof(CanAutoSizeToContent));
            OnPropertyChanged(nameof(CanToggleAutoSizeToContent));
            OnPropertyChanged(nameof(AutoSizeToContentDescription));
        }

        // §3.7: SetPanel (called from the Target setter, i.e. every retarget) re-raises both of these,
        // so Colors picks up the new panel's colours/selected-preset highlight without its own separate
        // subscription to Target.
        if (e.PropertyName is nameof(SelectedPanelSettingsViewModel.SelectedPanelAccentColor)
            or nameof(SelectedPanelSettingsViewModel.SelectedPanelTextColor))
        {
            Colors.Refresh();
        }
    }

    /// <summary>
    /// Reacts to a change made to <see cref="Target"/> from somewhere other than this dialog — the
    /// panel's own context menu, a rename, a layout-snapshot restore (FIX-B fix-cycle MAJOR finding M-2:
    /// this dialog used to go stale while open, including its own title bar).
    /// </summary>
    private void OnTargetPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ChildWindowModel.Title):
                OnPropertyChanged(nameof(WindowTitle));
                break;
            case nameof(ChildWindowModel.ContentFilter):
                OnPropertyChanged(nameof(ContentFilter));
                break;
            case nameof(ChildWindowModel.SourceFolderPath):
                OnPropertyChanged(nameof(IsFolderPortal));
                OnPropertyChanged(nameof(FolderPortalPath));
                OnPropertyChanged(nameof(SortModeDescription));
                break;
            case nameof(ChildWindowModel.IconLayoutPolicy):
                OnPropertyChanged(nameof(IconLayoutPolicy));
                break;
            case nameof(ChildWindowModel.SizingMode):
                OnPropertyChanged(nameof(IsAutoSizeToContent));
                break;
        }
    }

    /// <summary>
    /// Reacts to <see cref="ILayoutEditModeService.CanEnterEditLayout"/>/<see cref="ILayoutEditModeService.IsLayoutLocked"/>
    /// changing — e.g. the tray icon's "Lock layout" toggle flips while this dialog is open (FIX-B
    /// fix-cycle MAJOR finding M-1).
    /// </summary>
    private void OnLayoutEditModePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        var isRelevant = e.PropertyName == nameof(ILayoutEditModeService.CanEnterEditLayout)
            || e.PropertyName == nameof(ILayoutEditModeService.IsLayoutLocked);
        if (!isRelevant) return;

        OnPropertyChanged(nameof(CanChangeLayout));
        OnPropertyChanged(nameof(IconLayoutDescription));
        OnPropertyChanged(nameof(CanToggleAutoSizeToContent));
        OnPropertyChanged(nameof(AutoSizeToContentDescription));
    }

    /// <summary>
    /// Re-raises every localized string property by its EXPLICIT name (docs/PLANS/localization.md §2.9
    /// "re-raise rule" — never <see cref="string.Empty"/>, which would also re-raise <see cref="Target"/>
    /// and every other unrelated property this dialog exposes) when the language hot-switches while this
    /// dialog is open.
    /// </summary>
    private void OnCultureChanged(object? sender, CultureChangedEventArgs e)
    {
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(SortModeDescription));
        OnPropertyChanged(nameof(IconLayoutDescription));
        OnPropertyChanged(nameof(AutoSizeToContentDescription));
    }

    private void RaiseAllTargetDependentProperties()
    {
        OnPropertyChanged(nameof(Target));
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(ContentFilter));
        OnPropertyChanged(nameof(IsFolderPortal));
        OnPropertyChanged(nameof(FolderPortalPath));
        OnPropertyChanged(nameof(SortModeDescription));
        OnPropertyChanged(nameof(IconLayoutPolicy));
        OnPropertyChanged(nameof(IsAutoSizeToContent));
        OnPropertyChanged(nameof(CanAutoSizeToContent));
        OnPropertyChanged(nameof(CanToggleAutoSizeToContent));
        OnPropertyChanged(nameof(AutoSizeToContentDescription));
    }

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
