using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using DesktopOrganizer.Core;
using DesktopOrganizer.UI.Commands;
using DesktopOrganizer.UI.Services;

namespace DesktopOrganizer.UI.ViewModels;

/// <summary>
/// ViewModel for the "Layout snapshots" section in the settings window.
/// Exposes commands and an observable snapshot list to the view.
/// </summary>
public class LayoutSnapshotsViewModel : INotifyPropertyChanged
{
    private readonly ILayoutSnapshotService _service;
    private readonly IConfirmationService _confirmation;
    private readonly IInputDialogService _inputDialog;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Observable list of snapshots, kept in sync with the service.
    /// Ordered newest-first for display.
    /// </summary>
    public ObservableCollection<LayoutSnapshot> Snapshots { get; } = [];

    /// <summary>
    /// Command: prompts for a snapshot name (pre-filled with an auto-generated default, editable or
    /// acceptable as-is) and, unless the user cancels the prompt, captures the current layout under it.
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
    /// <param name="service">Persists and restores layout snapshots.</param>
    /// <param name="confirmation">Gates the restore/delete commands behind a user confirmation prompt.</param>
    /// <param name="inputDialog">Prompts for the snapshot name when capturing.</param>
    public LayoutSnapshotsViewModel(ILayoutSnapshotService service, IConfirmationService confirmation, IInputDialogService inputDialog)
    {
        _service = service;
        _confirmation = confirmation;
        _inputDialog = inputDialog;

        CaptureSnapshotCommand = new RelayCommand(CaptureSnapshot);
        RestoreSnapshotCommand = new RelayCommand<string>(RestoreSnapshot);
        DeleteSnapshotCommand = new RelayCommand<string>(DeleteSnapshot);

        _service.Load();
        RefreshList();
    }

    private void CaptureSnapshot()
    {
        var defaultName = $"Layout {DateTime.Now:dd.MM HH:mm}";
        var name = _inputDialog.PromptText("Save snapshot", "Snapshot name:", defaultName);

        // Cancel returns null by contract; a blank/whitespace-only name is treated the same way as a
        // defense-in-depth guard (matching RestoreSnapshot/DeleteSnapshot's own IsNullOrWhiteSpace
        // checks below) even though the real InputDialog's OK button is already disabled for blank input.
        if (string.IsNullOrWhiteSpace(name)) return;

        _service.Capture(name);
        RefreshList();
    }

    private void RestoreSnapshot(string? snapshotId)
    {
        if (string.IsNullOrWhiteSpace(snapshotId)) return;

        if (!_confirmation.Confirm(
                "The current layout will be replaced by this snapshot. Continue?",
                "Restore Layout")) return;

        _service.Restore(snapshotId);
    }

    private void DeleteSnapshot(string? snapshotId)
    {
        if (string.IsNullOrWhiteSpace(snapshotId)) return;

        var snapshot = _service.Snapshots.FirstOrDefault(s => s.Id == snapshotId);
        var name = snapshot?.Name ?? "this snapshot";
        if (!_confirmation.Confirm(
                $"Delete snapshot “{name}”? This cannot be undone.",
                "Delete snapshot",
                MessageBoxImage.Warning)) return;

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
