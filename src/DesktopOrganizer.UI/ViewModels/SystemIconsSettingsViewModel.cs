using System.Collections.ObjectModel;
using System.ComponentModel;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;

namespace DesktopOrganizer.UI.ViewModels;

/// <summary>
/// ViewModel for the "System Icons Panel" section in the settings window — wraps
/// <see cref="ISystemIconsPanelManager"/>, mirroring the shape of <see cref="LayoutSnapshotsViewModel"/>
/// (REFACTORING_PLAN.md §5.3). Owned and constructed internally by <see cref="MainWindowViewModel"/>,
/// which forwards its own identically-named properties to this instance and re-raises its
/// <see cref="PropertyChanged"/> notifications verbatim — so <c>MainWindow.xaml</c>'s existing
/// <c>{Binding SystemIconsPanelEnabled}</c>-style bindings (against the root
/// <see cref="MainWindowViewModel"/>) keep working unchanged.
/// </summary>
public class SystemIconsSettingsViewModel : INotifyPropertyChanged
{
    private readonly ISystemIconsPanelManager _systemIconsPanelManager;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Initializes a new instance of the <see cref="SystemIconsSettingsViewModel"/> class.
    /// </summary>
    public SystemIconsSettingsViewModel(ISystemIconsPanelManager systemIconsPanelManager)
    {
        _systemIconsPanelManager = systemIconsPanelManager;

        // Forwards the manager's own background-poll changes to VirtualDesktopFeatureIneffective (the
        // circuit breaker tripping/resetting) into a PropertyChanged for the read-only proxy below —
        // nothing else on this ViewModel ever sets that field, so without this subscription the UI
        // would never learn it changed (finalization MAJOR M-3). Never unsubscribed: this ViewModel is
        // owned for the lifetime of the (DI-singleton) MainWindowViewModel, and the manager (and its
        // Model) are themselves process-wide DI singletons, so there is no shorter-lived side to leak
        // against.
        _systemIconsPanelManager.Model.PropertyChanged += OnSystemIconsPanelModelPropertyChanged;
    }

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
    /// checkbox in <c>MainWindow.xaml</c> (finalization MAJOR M-3). Unlike the other System Icons Panel
    /// properties above, this one is never set by the user — it only ever changes from the manager's own
    /// background poll — so there is no setter, only the <see cref="SystemIconsPanelModel.PropertyChanged"/>
    /// subscription in the constructor forwarding the model's own change notification.
    /// </summary>
    public bool SystemIconsVirtualDesktopFeatureIneffective
        => _systemIconsPanelManager.Model.VirtualDesktopFeatureIneffective;

    private void OnSystemIconsPanelModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemIconsPanelModel.VirtualDesktopFeatureIneffective))
            OnPropertyChanged(nameof(SystemIconsVirtualDesktopFeatureIneffective));
    }

    private void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
