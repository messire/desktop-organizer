using System.ComponentModel;
using DesktopOrganizer.UI.Services;

namespace DesktopOrganizer.UI.ViewModels;

/// <summary>
/// Global default settings for new panels and grid/snap behavior, extracted from
/// <see cref="MainWindowViewModel"/> (REFACTORING_PLAN.md §5.3). Owned and constructed internally by
/// <see cref="MainWindowViewModel"/>, which forwards its own identically-named properties to this
/// instance and re-raises its <see cref="PropertyChanged"/> notifications verbatim — so
/// <c>MainWindow.xaml</c>'s existing <c>{Binding GridSize}</c>-style bindings (against the root
/// <see cref="MainWindowViewModel"/>) keep working unchanged.
/// </summary>
public class GlobalDefaultsViewModel : INotifyPropertyChanged
{
    private readonly IAppSettings _appSettings;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Initializes a new instance of the <see cref="GlobalDefaultsViewModel"/> class.
    /// </summary>
    public GlobalDefaultsViewModel(IAppSettings appSettings)
    {
        _appSettings = appSettings;
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

    private void SetGlobal<T>(Action<T> apply, T newValue, T current, string propertyName)
    {
        if (EqualityComparer<T>.Default.Equals(current, newValue)) return;
        apply(newValue);
        _appSettings.Save();
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
