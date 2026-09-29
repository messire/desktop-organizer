using System.ComponentModel;
using System.Runtime.CompilerServices;
using DesktopOrganizer.UI.Commands;

namespace DesktopOrganizer.UI.Services;

/// <summary>
/// Default <see cref="IStatusNotifier"/> — a DI singleton (docs/PLANS/design-system-1x.md §2.8, ADR-047
/// D-9) whose <see cref="Current"/>/<see cref="DismissCommand"/> <c>MainWindow.xaml</c>'s <c>InfoBar</c>
/// host binds against via <c>MainWindowViewModel.Status</c>.
/// </summary>
/// <remarks>
/// Auto-hide uses the same <see cref="IDebounceTimer"/> abstraction <c>SaveScheduler</c> already relies on
/// for deterministic testing — a real <see cref="Show"/> call restarts the window exactly like a debounced
/// save would, which is also the desired behavior here: showing a second Informational/Success status
/// before the first one auto-hid restarts the visible time instead of letting it disappear early.
/// </remarks>
public sealed class SettingsStatusNotifier : IStatusNotifier
{
    private static readonly TimeSpan AutoHideWindow = TimeSpan.FromSeconds(6);

    private readonly IDebounceTimer _autoHideTimer;
    private StatusItem? _current;

    /// <summary>
    /// Initializes a new instance of the <see cref="SettingsStatusNotifier"/> class.
    /// </summary>
    /// <param name="autoHideTimer">Drives the Informational/Success auto-hide delay.</param>
    public SettingsStatusNotifier(IDebounceTimer autoHideTimer)
    {
        _autoHideTimer = autoHideTimer;
        DismissCommand = new RelayCommand(Dismiss);
    }

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <inheritdoc/>
    public StatusItem? Current
    {
        get => _current;
        private set
        {
            if (_current == value) return;
            _current = value;
            OnPropertyChanged();
        }
    }

    /// <inheritdoc/>
    public ICommand DismissCommand { get; }

    /// <inheritdoc/>
    public void Show(StatusSeverity severity, string message, string? title = null)
    {
        // A fresh status always replaces whatever was showing, restarting (not merely leaving alone) any
        // pending auto-hide — see this class's own remarks.
        _autoHideTimer.Stop();
        Current = new StatusItem(severity, message, title);

        if (severity is StatusSeverity.Informational or StatusSeverity.Success)
            _autoHideTimer.Start(AutoHideWindow, Dismiss);
    }

    /// <inheritdoc/>
    public void Dismiss()
    {
        _autoHideTimer.Stop();
        Current = null;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
