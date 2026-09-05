using System.ComponentModel;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;

namespace DesktopOrganizer.UI.ViewModels;

/// <summary>
/// Property proxy for the currently selected panel's editable settings (title/background/text/
/// opacity/icon-size/view-mode/sort), extracted from <see cref="MainWindowViewModel"/>
/// (REFACTORING_PLAN.md §5.3). Owned and constructed internally by <see cref="MainWindowViewModel"/>,
/// which forwards its own identically-named properties to this instance and re-raises its
/// <see cref="PropertyChanged"/> notifications verbatim — so <c>MainWindow.xaml</c>'s existing
/// <c>{Binding SelectedPanelTitle}</c>-style bindings (against the root <see cref="MainWindowViewModel"/>)
/// keep working unchanged.
/// </summary>
public class SelectedPanelSettingsViewModel : INotifyPropertyChanged
{
    private readonly IWindowManager _windowManager;
    private ChildWindowModel? _panel;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Initializes a new instance of the <see cref="SelectedPanelSettingsViewModel"/> class.
    /// </summary>
    public SelectedPanelSettingsViewModel(IWindowManager windowManager)
    {
        _windowManager = windowManager;
    }

    /// <summary>Whether a panel is selected (drives enabled state of the editor section).</summary>
    public bool HasSelectedPanel => _panel is not null;

    /// <summary>Title of the selected panel.</summary>
    public string SelectedPanelTitle
    {
        get => _panel?.Title ?? string.Empty;
        set => Apply(p => p.Title = value);
    }

    /// <summary>Background colour (hex) of the selected panel.</summary>
    public string SelectedPanelBackgroundColor
    {
        get => _panel?.BackgroundColor ?? string.Empty;
        set => Apply(p => p.BackgroundColor = value);
    }

    /// <summary>Text colour (hex) of the selected panel.</summary>
    public string SelectedPanelTextColor
    {
        get => _panel?.TextColor ?? string.Empty;
        set => Apply(p => p.TextColor = value);
    }

    /// <summary>Background opacity (0–1) of the selected panel.</summary>
    public double SelectedPanelOpacity
    {
        get => _panel?.BackgroundOpacity ?? 1.0;
        set => Apply(p => p.BackgroundOpacity = value);
    }

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

    /// <summary>Sort order of the selected panel.</summary>
    public PanelSortMode SelectedPanelSortMode
    {
        get => _panel?.SortMode ?? PanelSortMode.Manual;
        set => Apply(p => p.SortMode = value);
    }

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
    /// </summary>
    /// <param name="panel">The newly selected panel, or <see langword="null"/> when nothing is selected.</param>
    public void SetPanel(ChildWindowModel? panel)
    {
        _panel = panel;

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
    /// Applies a change to the selected panel and persists it. The model's own property setter guards
    /// equality, so re-applying an unchanged value is harmless.
    /// </summary>
    private void Apply(Action<ChildWindowModel> apply, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (_panel is null) return;

        apply(_panel);
        _windowManager.SaveWindowsState();
        OnPropertyChanged(propertyName!);
    }

    private void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
