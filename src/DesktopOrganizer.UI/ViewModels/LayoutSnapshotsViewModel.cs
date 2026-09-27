using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using DesktopOrganizer.Core;
using DesktopOrganizer.Services.Abstractions;
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
    private readonly ILocalizationService _localization;

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
    /// <param name="localization">Localizes the auto-generated snapshot name and this ViewModel's dialogs.</param>
    public LayoutSnapshotsViewModel(
        ILayoutSnapshotService service,
        IConfirmationService confirmation,
        IInputDialogService inputDialog,
        ILocalizationService localization)
    {
        _service = service;
        _confirmation = confirmation;
        _inputDialog = inputDialog;
        _localization = localization;

        CaptureSnapshotCommand = new RelayCommand(CaptureSnapshot);
        RestoreSnapshotCommand = new RelayCommand<string>(RestoreSnapshot);
        DeleteSnapshotCommand = new RelayCommand<string>(DeleteSnapshot);

        _service.Load();
        RefreshList();
    }

    private void CaptureSnapshot()
    {
        // D-5 (docs/PLANS/localization.md): the default name is resolved for the CURRENT language at
        // creation time only — like every other piece of user data, it is never re-translated on a later
        // language switch.
        var defaultName = _localization.Format("Snapshot_DefaultName_Format", DateTime.Now);
        var name = _inputDialog.PromptText(
            _localization.GetString("Snapshot_Capture_Title"),
            _localization.GetString("Snapshot_Capture_Label"),
            defaultName);

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
                _localization.GetString("Snapshot_Restore_Message"),
                _localization.GetString("Snapshot_Restore_Title"))) return;

        _service.Restore(snapshotId);
    }

    private void DeleteSnapshot(string? snapshotId)
    {
        if (string.IsNullOrWhiteSpace(snapshotId)) return;

        var snapshot = _service.Snapshots.FirstOrDefault(s => s.Id == snapshotId);
        var name = snapshot?.Name ?? _localization.GetString("Snapshot_Delete_UnnamedFallback");
        if (!_confirmation.Confirm(
                _localization.Format("Snapshot_Delete_Message_Format", name),
                _localization.GetString("Snapshot_Delete_Title"),
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
