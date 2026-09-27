using System.ComponentModel;
using System.Windows.Input;
using DesktopOrganizer.UI.Commands;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;

namespace DesktopOrganizer.UI.ViewModels;

/// <summary>
/// Property proxy for a panel's editable appearance settings (title/accent/text/opacity/surface
/// style/icon-size/view-mode/sort), extracted from <see cref="MainWindowViewModel"/>
/// (REFACTORING_PLAN.md §5.3). Constructed internally by <see cref="MainWindowViewModel"/>, which
/// forwards its own identically-named properties to that instance and re-raises its
/// <see cref="PropertyChanged"/> notifications verbatim — so <c>MainWindow.xaml</c>'s existing
/// <c>{Binding SelectedPanelTitle}</c>-style bindings (against the root <see cref="MainWindowViewModel"/>)
/// keep working unchanged. Also composed directly by
/// <see cref="PanelCustomizeViewModel"/> (settings-window-redesign.md D-3/WP-B), which binds its
/// "Appearance" section straight to this instance (<c>{Binding Appearance.SelectedPanelTitle}</c>)
/// instead of duplicating the same proxy properties a second time.
/// </summary>
public class SelectedPanelSettingsViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IWindowManager _windowManager;
    private ChildWindowModel? _panel;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Initializes a new instance of the <see cref="SelectedPanelSettingsViewModel"/> class.
    /// </summary>
    /// <param name="windowManager">Used to persist layout state after an edited property is applied.</param>
    public SelectedPanelSettingsViewModel(IWindowManager windowManager)
    {
        _windowManager = windowManager;

        SetViewModeCommand = new RelayCommand<PanelViewMode>(mode => SelectedPanelViewMode = mode);
        SetSortModeCommand = new RelayCommand<PanelSortMode>(mode => SelectedPanelSortMode = mode, _ => SelectedPanelSupportsManualOrder);
        SetSurfaceStyleCommand = new RelayCommand<PanelSurfaceStyle>(style => SelectedPanelSurfaceStyle = style);
    }

    /// <summary>Whether a panel is selected (drives enabled state of the editor section).</summary>
    public bool HasSelectedPanel => _panel is not null;

    /// <summary>Title of the selected panel.</summary>
    public string SelectedPanelTitle
    {
        get => _panel?.Title ?? string.Empty;
        set => Apply(p => p.Title = value);
    }

    /// <summary>
    /// Semantic accent colour (hex) of the selected panel — proxies <see cref="ChildWindowModel.AccentColor"/>
    /// (ADR-025 D-10; renamed from <c>SelectedPanelBackgroundColor</c> now that the settings-UI-facing
    /// name matches the model property it edits).
    /// </summary>
    public string SelectedPanelAccentColor
    {
        get => _panel?.AccentColor ?? string.Empty;
        set => Apply(p => p.AccentColor = value);
    }

    /// <summary>Text colour (hex) of the selected panel.</summary>
    public string SelectedPanelTextColor
    {
        get => _panel?.TextColor ?? string.Empty;
        set => Apply(p => p.TextColor = value);
    }

    /// <summary>
    /// Applies a colour-preset pair atomically (docs/PLANS/color-presets-and-size-step.md §3.7): both
    /// <see cref="ChildWindowModel.AccentColor"/> and <see cref="ChildWindowModel.TextColor"/> are set
    /// inside a single <see cref="Apply"/> call, so choosing a preset persists with exactly one
    /// <see cref="IWindowManager.SaveWindowsState"/> instead of the two <see cref="SelectedPanelAccentColor"/>/
    /// <see cref="SelectedPanelTextColor"/> setters would cost if called separately.
    /// </summary>
    /// <param name="accent">The preset's accent colour, canonical uppercase <c>#RRGGBB</c>.</param>
    /// <param name="text">The preset's text colour, canonical uppercase <c>#RRGGBB</c>.</param>
    public void ApplyColors(string accent, string text) =>
        Apply(p =>
        {
            p.AccentColor = accent;
            p.TextColor = text;
        });

    /// <summary>
    /// Applies a full appearance reset atomically (release-polish-1.0 review round M7, Customize window's
    /// own "Reset to defaults"): accent, text, surface opacity, surface style, and icon size are all set
    /// inside a SINGLE <see cref="Apply"/> call, so resetting the panel persists with exactly one
    /// <see cref="IWindowManager.SaveWindowsState"/> instead of the five separate setters would cost if
    /// called independently.
    /// </summary>
    /// <param name="accent">The default accent colour, canonical uppercase <c>#RRGGBB</c>.</param>
    /// <param name="text">The default text colour, canonical uppercase <c>#RRGGBB</c>.</param>
    /// <param name="surfaceOpacity">The default surface opacity (0-1).</param>
    /// <param name="surfaceStyle">The default surface style.</param>
    /// <param name="iconSize">The default icon size (px).</param>
    public void ApplyAppearance(string accent, string text, double surfaceOpacity, PanelSurfaceStyle surfaceStyle, double iconSize) =>
        Apply(p =>
        {
            p.AccentColor = accent;
            p.TextColor = text;
            p.SurfaceOpacity = surfaceOpacity;
            p.SurfaceStyle = surfaceStyle;
            p.IconSize = iconSize;
        });

    /// <summary>
    /// Surface opacity (0–1) of the selected panel — proxies <see cref="ChildWindowModel.SurfaceOpacity"/>
    /// (ADR-025 D-10; renamed from <c>SelectedPanelOpacity</c>, see the remarks on
    /// <see cref="SelectedPanelAccentColor"/>).
    /// </summary>
    public double SelectedPanelSurfaceOpacity
    {
        get => _panel?.SurfaceOpacity ?? 1.0;
        set => Apply(p => p.SurfaceOpacity = value);
    }

    /// <summary>
    /// How the selected panel's surface blends with <see cref="SelectedPanelAccentColor"/> — proxies
    /// <see cref="ChildWindowModel.SurfaceStyle"/> (ADR-025 D-10).
    /// </summary>
    public PanelSurfaceStyle SelectedPanelSurfaceStyle
    {
        get => _panel?.SurfaceStyle ?? PanelSurfaceStyle.Ambient;
        set => Apply(p => p.SurfaceStyle = value);
    }

    /// <summary>
    /// Sets <see cref="SelectedPanelSurfaceStyle"/> from a <see cref="PanelSurfaceStyle"/> command
    /// parameter — backs the "Customize panel…" dialog's Style segmented control (ADR-042), the same
    /// Command+CommandParameter shape as <see cref="SetViewModeCommand"/>/<see cref="SetSortModeCommand"/>.
    /// </summary>
    public ICommand SetSurfaceStyleCommand { get; }

    /// <summary>Icon size (px) of the selected panel.</summary>
    public double SelectedPanelIconSize
    {
        get => _panel?.IconSize ?? 48;
        set => Apply(p => p.IconSize = value);
    }

    /// <summary>View mode of the selected panel.</summary>
    public PanelViewMode SelectedPanelViewMode
    {
        get => _panel?.ViewMode ?? PanelViewMode.List;
        set => Apply(p => p.ViewMode = value);
    }

    /// <summary>All <see cref="PanelViewMode"/> members, for the settings UI's view-mode selector.</summary>
    public IReadOnlyList<PanelViewMode> ViewModes { get; } = Enum.GetValues<PanelViewMode>();

    /// <summary>
    /// Sets <see cref="SelectedPanelViewMode"/> from a <see cref="PanelViewMode"/> command parameter —
    /// backs the "Customize panel…" dialog's View radio group (FIX-B fix-cycle minor finding 11; a plain
    /// two-way <c>SelectedItem</c> binding is kept for <see cref="MainWindowViewModel"/>'s own combo box).
    /// </summary>
    public ICommand SetViewModeCommand { get; }

    /// <summary>Sort order of the selected panel.</summary>
    public PanelSortMode SelectedPanelSortMode
    {
        get => _panel?.SortMode ?? PanelSortMode.Manual;
        set => Apply(p => p.SortMode = value);
    }

    /// <summary>All <see cref="PanelSortMode"/> members, for the settings UI's sort-order selector.</summary>
    public IReadOnlyList<PanelSortMode> SortModes { get; } = Enum.GetValues<PanelSortMode>();

    /// <summary>
    /// Sets <see cref="SelectedPanelSortMode"/> from a <see cref="PanelSortMode"/> command parameter —
    /// backs the "Customize panel…" dialog's Sort radio group (FIX-B fix-cycle minor finding 11).
    /// <see cref="ICommand.CanExecute"/> mirrors <see cref="SelectedPanelSupportsManualOrder"/> — a Folder
    /// Portal panel always sorts by name.
    /// </summary>
    public ICommand SetSortModeCommand { get; }

    /// <summary>
    /// Whether the selected panel supports a manual (drag-to-reorder) sort order. Folder Portal panels
    /// always sort by name (ADR-014 Decision 7) — there's nowhere to persist a manual order for them —
    /// so the sort-order selector is disabled while a folder source is assigned.
    /// </summary>
    public bool SelectedPanelSupportsManualOrder =>
        _panel is not null && string.IsNullOrWhiteSpace(_panel.SourceFolderPath);

    /// <summary>
    /// Switches the panel this instance proxies settings for, raising <see cref="PropertyChanged"/> for
    /// every proxied property (mirrors the original <c>MainWindowViewModel.RaiseSelectedPanelProperties</c>).
    /// Also unsubscribes from the previous panel's <see cref="INotifyPropertyChanged.PropertyChanged"/> and
    /// subscribes to the new one's, so a change made elsewhere (the panel's own context menu, a rename, a
    /// layout-snapshot restore) is reflected here too — not just edits made through this instance itself
    /// (FIX-B fix-cycle MAJOR finding M-2: the "Customize panel…" dialog used to go stale while open).
    /// </summary>
    /// <param name="panel">The newly selected panel, or <see langword="null"/> when nothing is selected.</param>
    public void SetPanel(ChildWindowModel? panel)
    {
        if (_panel is not null) _panel.PropertyChanged -= OnPanelPropertyChanged;

        _panel = panel;

        if (_panel is not null) _panel.PropertyChanged += OnPanelPropertyChanged;

        OnPropertyChanged(nameof(HasSelectedPanel));
        OnPropertyChanged(nameof(SelectedPanelTitle));
        OnPropertyChanged(nameof(SelectedPanelAccentColor));
        OnPropertyChanged(nameof(SelectedPanelTextColor));
        OnPropertyChanged(nameof(SelectedPanelSurfaceOpacity));
        OnPropertyChanged(nameof(SelectedPanelSurfaceStyle));
        OnPropertyChanged(nameof(SelectedPanelIconSize));
        OnPropertyChanged(nameof(SelectedPanelViewMode));
        OnPropertyChanged(nameof(SelectedPanelSortMode));
        OnPropertyChanged(nameof(SelectedPanelSupportsManualOrder));
    }

    /// <summary>
    /// Unsubscribes from the current panel, if any (<see cref="SetPanel"/> with <see langword="null"/>).
    /// Called by <see cref="PanelCustomizeViewModel.Dispose"/> when its dialog closes, so this instance
    /// never outlives the dialog still listening to a panel nobody is editing any more.
    /// </summary>
    public void Dispose() => SetPanel(null);

    /// <summary>
    /// Re-raises the proxied property that changed on the underlying panel — see <see cref="SetPanel"/>'s
    /// remarks.
    /// </summary>
    private void OnPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ChildWindowModel.Title):
                OnPropertyChanged(nameof(SelectedPanelTitle));
                break;
            case nameof(ChildWindowModel.AccentColor):
                OnPropertyChanged(nameof(SelectedPanelAccentColor));
                break;
            case nameof(ChildWindowModel.TextColor):
                OnPropertyChanged(nameof(SelectedPanelTextColor));
                break;
            case nameof(ChildWindowModel.SurfaceOpacity):
                OnPropertyChanged(nameof(SelectedPanelSurfaceOpacity));
                break;
            case nameof(ChildWindowModel.SurfaceStyle):
                OnPropertyChanged(nameof(SelectedPanelSurfaceStyle));
                break;
            case nameof(ChildWindowModel.IconSize):
                OnPropertyChanged(nameof(SelectedPanelIconSize));
                break;
            case nameof(ChildWindowModel.ViewMode):
                OnPropertyChanged(nameof(SelectedPanelViewMode));
                break;
            case nameof(ChildWindowModel.SortMode):
                OnPropertyChanged(nameof(SelectedPanelSortMode));
                break;
            case nameof(ChildWindowModel.SourceFolderPath):
                OnPropertyChanged(nameof(SelectedPanelSupportsManualOrder));
                break;
        }
    }

    /// <summary>
    /// Applies a change to the selected panel and persists it. The model's own property setter guards
    /// equality, so re-applying an unchanged value is harmless. Does <b>not</b> raise
    /// <see cref="PropertyChanged"/> itself — every property <see cref="Apply"/> is used for has a
    /// same-named case in <see cref="OnPanelPropertyChanged"/>, which the panel's own (synchronous)
    /// <see cref="INotifyPropertyChanged.PropertyChanged"/> already triggers as a direct consequence of
    /// <paramref name="apply"/> mutating it — raising it again here would just be a redundant duplicate
    /// notification (FIX-B fix-cycle M-2 clean-up).
    /// </summary>
    /// <param name="apply">Mutates the current <see cref="ChildWindowModel"/>.</param>
    private void Apply(Action<ChildWindowModel> apply)
    {
        if (_panel is null) return;

        apply(_panel);
        _windowManager.SaveWindowsState();

        // FIX-B fix-cycle minor finding 6 audit: of this method's eight call sites, only SortMode's
        // underlying model change is also picked up by ChildWindowViewModel.OnModelPropertyChanged
        // (which raises FilesChanged, itself turned into a save by ChildWindow) — making this call
        // redundant-but-harmless for that one property alone. Title/AccentColor/TextColor/SurfaceOpacity/
        // SurfaceStyle/IconSize/ViewMode have no such reaction anywhere else (ViewMode's own
        // OnModelPropertyChanged branch only switches the view template — it never raises FilesChanged),
        // so for those seven this call is the only save that ever happens.
    }

    private void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
