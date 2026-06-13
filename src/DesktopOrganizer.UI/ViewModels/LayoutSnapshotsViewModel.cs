using System.Collections.ObjectModel;
using System.ComponentModel;
using DesktopOrganizer.Core.Models;
using DesktopOrganizer.UI.Commands;
using DesktopOrganizer.UI.Services;

namespace DesktopOrganizer.UI.ViewModels;

/// <summary>
/// ViewModel for the "Снапшоты раскладки" section in the settings window.
/// Exposes commands and an observable snapshot list to the view.
/// </summary>
public class LayoutSnapshotsViewModel : INotifyPropertyChanged
{
    private readonly ILayoutSnapshotService _service;
    private readonly IConfirmationService _confirmation;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Observable list of snapshots, kept in sync with the service.
    /// Ordered newest-first for display.
    /// </summary>
    public ObservableCollection<LayoutSnapshot> Snapshots { get; } = [];

    /// <summary>
    /// Command: capture the current layout under an auto-generated name or user-provided name.
    /// </summary>
    public ICommand CaptureSnapshotCommand { get; }

    /// <summary>
    /// Command: restore the layout from a snapshot. Parameter = snapshot Id (string).
    /// </summary>
    public ICommand RestoreSnapshotCommand { get; }

    /// <summary>
    /// Command: delete a snapshot. Parameter = snapshot Id (string).
    /// </summary>
    public ICommand DeleteSnapshotCommand { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="LayoutSnapshotsViewModel"/> class.
    /// </summary>
    public LayoutSnapshotsViewModel(ILayoutSnapshotService service, IConfirmationService confirmation)
    {
        _service = service;
        _confirmation = confirmation;

        CaptureSnapshotCommand = new RelayCommand(CaptureSnapshot);
        RestoreSnapshotCommand = new RelayCommand<string>(RestoreSnapshot);
        DeleteSnapshotCommand  = new RelayCommand<string>(DeleteSnapshot);

        _service.Load();
        RefreshList();
    }

    private void CaptureSnapshot()
    {
        var name = $"Раскладка {DateTime.Now:dd.MM HH:mm}";
        _service.Capture(name);
        RefreshList();
    }

    private void RestoreSnapshot(string? snapshotId)
    {
        if (string.IsNullOrWhiteSpace(snapshotId)) return;

        if (!_confirmation.Confirm(
                "Текущая раскладка будет заменена снапшотом. Продолжить?",
                "Восстановить раскладку")) return;

        _service.Restore(snapshotId);
    }

    private void DeleteSnapshot(string? snapshotId)
    {
        if (string.IsNullOrWhiteSpace(snapshotId)) return;
        _service.Delete(snapshotId);
        RefreshList();
    }

    private void RefreshList()
    {
        Snapshots.Clear();

        // Display newest first.
        var sorted = new System.Collections.Generic.List<LayoutSnapshot>(_service.Snapshots);
        sorted.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt));

        foreach (var s in sorted)
            Snapshots.Add(s);

        OnPropertyChanged(nameof(Snapshots));
    }

    private void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
