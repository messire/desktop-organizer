using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Abstractions.Storage;
using DesktopOrganizer.Services.Diagnostics;
using DesktopOrganizer.UI.Commands;
using DesktopOrganizer.UI.Services;

namespace DesktopOrganizer.UI.ViewModels;

/// <summary>
/// One row of the "Files without a panel" list (<see cref="ManagedStorageOrphansViewModel.Items"/>).
/// Immutable display data for a <see cref="ManagedStorageOrphan"/>.
/// </summary>
public sealed class ManagedStorageOrphanItem
{
    /// <summary>Initializes a new instance of the <see cref="ManagedStorageOrphanItem"/> class.</summary>
    /// <param name="orphan">The scanner's finding.</param>
    public ManagedStorageOrphanItem(ManagedStorageOrphan orphan)
    {
        ArgumentNullException.ThrowIfNull(orphan);

        ManagedPath = orphan.ManagedPath;
        OriginalPath = orphan.OriginalPath;
        Name = orphan.Name;
        IsDirectory = orphan.IsDirectory;
        HasStoredAt = orphan.StoredAt.Year > 1;
        StoredAt = HasStoredAt ? orphan.StoredAt.ToLocalTime() : DateTime.MinValue;
        OriginalFolderName = GetFolderName(orphan.OriginalPath);
    }

    /// <summary>Gets the file or folder name.</summary>
    public string Name { get; }

    /// <summary>Gets the path inside managed storage.</summary>
    public string ManagedPath { get; }

    /// <summary>Gets the path the manifest remembers — where "Return" puts the item back.</summary>
    public string OriginalPath { get; }

    /// <summary>
    /// Gets the name of the folder the item returns to (e.g. "Desktop"), shown in the "Original location"
    /// column - the full <see cref="OriginalPath"/> goes into the tooltip. Empty when the location is unknown.
    /// </summary>
    public string OriginalFolderName { get; }

    /// <summary>
    /// Gets a value indicating whether "Return" can work: the manifest remembers where the item came from.
    /// An item with an empty <see cref="OriginalPath"/> can only be opened in storage, never returned.
    /// </summary>
    public bool CanReturn => !string.IsNullOrWhiteSpace(OriginalPath);

    /// <summary>Gets a value indicating whether the item is a folder.</summary>
    public bool IsDirectory { get; }

    /// <summary>
    /// Gets a value indicating whether <see cref="StoredAt"/> is known. The scanner reports
    /// <see cref="DateTime.MinValue"/> when the timestamp could not be read; the UI then shows no date at all
    /// instead of "01.01.0001".
    /// </summary>
    public bool HasStoredAt { get; }

    /// <summary>Gets when the item was moved into storage, in local time (meaningful only when <see cref="HasStoredAt"/>).</summary>
    public DateTime StoredAt { get; }

    private static string GetFolderName(string originalPath)
    {
        if (string.IsNullOrWhiteSpace(originalPath)) return string.Empty;

        var folder = Path.GetDirectoryName(originalPath);
        if (string.IsNullOrEmpty(folder)) return string.Empty;

        // A drive root ("C:\") has no leaf name - show the root itself rather than nothing.
        var leaf = Path.GetFileName(folder);
        return leaf.Length > 0 ? leaf : folder;
    }
}

/// <summary>
/// ViewModel of the Backup section's "Files without a panel" card (ADR-009 amendment 2026-10-10, O2): on an
/// explicit command it looks — in the background — for managed-storage items no panel (on any page) and no
/// layout snapshot references, lists them, and lets the user return one to its original place
/// (<see cref="IPanelItemStorageService.Restore"/>) or open its storage folder. Nothing is ever deleted or
/// moved without that explicit per-item action, and nothing runs at startup.
/// </summary>
/// <remarks>
/// Refused in a read-only session (<see cref="IApplicationStateStore.IsWritable"/> is <see langword="false"/>):
/// such a session did not load the panels the newer/other instance owns, so every item would look orphaned —
/// returning one would pull a file out from under a real panel.
/// </remarks>
public sealed class ManagedStorageOrphansViewModel : INotifyPropertyChanged
{
    private readonly IManagedStorageOrphanScanner _scanner;
    private readonly IPanelItemStorageService _storage;
    private readonly IWindowManager _windowManager;
    private readonly ILayoutSnapshotService _snapshots;
    private readonly IApplicationStateStore _stateStore;
    private readonly IExternalLinkLauncher _linkLauncher;
    private readonly IStatusNotifier _statusNotifier;
    private readonly ILocalizationService _localization;
    private readonly IAppLog? _appLog;

    private SummaryKind _summary;
    private bool _isBusy;

    /// <summary>Initializes a new instance of the <see cref="ManagedStorageOrphansViewModel"/> class.</summary>
    /// <param name="scanner">Finds the unreferenced items.</param>
    /// <param name="storage">Returns an item to its original location.</param>
    /// <param name="windowManager">Source of every live panel (all pages).</param>
    /// <param name="snapshots">Source of the panels stored in layout snapshots (their items count as in use).</param>
    /// <param name="stateStore">Tells whether this session is read-only.</param>
    /// <param name="linkLauncher">Opens an item's storage folder.</param>
    /// <param name="statusNotifier">Non-blocking result messages (no <c>MessageBox</c>).</param>
    /// <param name="localization">Localizes the texts and follows a hot language switch.</param>
    /// <param name="appLog">Failure log; <see langword="null"/> falls back to the ambient one.</param>
    public ManagedStorageOrphansViewModel(
        IManagedStorageOrphanScanner scanner,
        IPanelItemStorageService storage,
        IWindowManager windowManager,
        ILayoutSnapshotService snapshots,
        IApplicationStateStore stateStore,
        IExternalLinkLauncher linkLauncher,
        IStatusNotifier statusNotifier,
        ILocalizationService localization,
        IAppLog? appLog = null)
    {
        _scanner = scanner;
        _storage = storage;
        _windowManager = windowManager;
        _snapshots = snapshots;
        _stateStore = stateStore;
        _linkLauncher = linkLauncher;
        _statusNotifier = statusNotifier;
        _localization = localization;
        _appLog = appLog;

        ScanCommand = new RelayCommand(() => _ = ScanAsync(), () => !IsBusy && !IsReadOnlySession);
        ReturnCommand = new RelayCommand<ManagedStorageOrphanItem>(item => _ = ReturnAsync(item), item => item is { CanReturn: true } && !IsBusy && !IsReadOnlySession);
        OpenFolderCommand = new RelayCommand<ManagedStorageOrphanItem>(OpenFolder, item => item is not null);

        // Never unsubscribed: DI singleton for the app's lifetime (same rationale as AboutViewModel).
        _localization.CultureChanged += (_, _) => OnPropertyChanged(nameof(StatusText));
        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasItems));
    }

    private enum SummaryKind
    {
        None,
        Scanning,
        NothingFound,
        Found,
        ReadOnly,
        Failed,
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Gets the orphans found by the last scan.</summary>
    public ObservableCollection<ManagedStorageOrphanItem> Items { get; } = [];

    /// <summary>Gets the command that scans (parameterless).</summary>
    public ICommand ScanCommand { get; }

    /// <summary>Gets the command that returns an item to its original place (parameter: <see cref="ManagedStorageOrphanItem"/>).</summary>
    public ICommand ReturnCommand { get; }

    /// <summary>Gets the command that opens an item's storage folder (parameter: <see cref="ManagedStorageOrphanItem"/>).</summary>
    public ICommand OpenFolderCommand { get; }

    /// <summary>Gets a value indicating whether the session is read-only (the card is then informational only).</summary>
    public bool IsReadOnlySession => !_stateStore.IsWritable;

    /// <summary>Gets a value indicating whether a scan or a return is in flight.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (_isBusy == value) return;
            _isBusy = value;
            OnPropertyChanged(nameof(IsBusy));
        }
    }

    /// <summary>Gets a value indicating whether <see cref="Items"/> has any row (the list is only shown then).</summary>
    public bool HasItems => Items.Count > 0;

    /// <summary>Gets a value indicating whether a scan has completed at least once (the list is meaningful).</summary>
    public bool HasScanned { get; private set; }

    /// <summary>Gets the line under the scan button: progress, count or the reason nothing is listed. Localized live.</summary>
    public string StatusText => _summary switch
    {
        SummaryKind.Scanning => _localization.GetString("Settings_Backup_Orphans_Scanning"),
        SummaryKind.NothingFound => _localization.GetString("Settings_Backup_Orphans_None"),
        SummaryKind.Found => _localization.Format("Settings_Backup_Orphans_Found_Format", Items.Count),
        SummaryKind.ReadOnly => _localization.GetString("Settings_Backup_Orphans_ReadOnly"),
        SummaryKind.Failed => _localization.GetString("Settings_Backup_Orphans_ScanFailed"),
        SummaryKind.None when IsReadOnlySession => _localization.GetString("Settings_Backup_Orphans_ReadOnly"),
        _ => string.Empty,
    };

    private IAppLog Log => _appLog ?? AppLog.Current;

    /// <summary>
    /// The scan, run fire-and-forget by <see cref="ScanCommand"/>. <c>internal</c> as a test seam
    /// (<see cref="AboutViewModel.CheckForUpdatesAsync"/> precedent). The references are gathered on the
    /// calling (UI) thread — they read live panel models — and only the disk work runs in
    /// <see cref="Task.Run(Func{Task})"/>; the result lands back on the UI thread.
    /// </summary>
    /// <returns>A task completing when the scan (and the list update) is done.</returns>
    internal async Task ScanAsync()
    {
        if (IsBusy) return;

        if (IsReadOnlySession)
        {
            SetSummary(SummaryKind.ReadOnly);
            return;
        }

        IsBusy = true;
        SetSummary(SummaryKind.Scanning);

        try
        {
            var references = CollectReferencedPaths();
            var orphans = await Task.Run(() => _scanner.Scan(references)).ConfigureAwait(true);

            Items.Clear();
            foreach (var orphan in orphans) Items.Add(new ManagedStorageOrphanItem(orphan));

            HasScanned = true;
            OnPropertyChanged(nameof(HasScanned));
            SetSummary(Items.Count > 0 ? SummaryKind.Found : SummaryKind.NothingFound);
        }
        catch (Exception ex)
        {
            // The scan is a convenience; whatever went wrong (unreadable storage, a vanished folder) is
            // logged and shown as one line - it must never take the settings window down.
            Log.Error("ManagedStorageOrphansViewModel", "ScanFailed", ex);
            SetSummary(SummaryKind.Failed);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Returns <paramref name="item"/> to its original location via <see cref="IPanelItemStorageService.Restore"/>,
    /// after re-checking (in the background, against freshly gathered references) that no panel started using
    /// it since the scan. <c>internal</c> as a test seam.
    /// </summary>
    /// <param name="item">The row to return.</param>
    /// <returns>A task completing when the item was handled.</returns>
    internal async Task ReturnAsync(ManagedStorageOrphanItem? item)
    {
        if (item is null || !item.CanReturn || IsBusy || IsReadOnlySession) return;

        IsBusy = true;
        var title = _localization.GetString("Settings_Backup_Orphans_Header");

        try
        {
            var references = CollectReferencedPaths();
            var outcome = await Task.Run(() => TryReturn(item, references)).ConfigureAwait(true);

            switch (outcome)
            {
                case ReturnOutcome.Returned:
                    Items.Remove(item);
                    _statusNotifier.Show(StatusSeverity.Success, _localization.Format("Settings_Backup_Orphans_Returned_Format", item.Name, item.OriginalFolderName), title);
                    break;

                case ReturnOutcome.NowReferenced:
                    Items.Remove(item);
                    _statusNotifier.Show(StatusSeverity.Caution, _localization.Format("Settings_Backup_Orphans_NowUsed_Format", item.Name), title);
                    break;

                case ReturnOutcome.OriginalUnknown:
                    _statusNotifier.Show(StatusSeverity.Caution, _localization.Format("Settings_Backup_Orphans_ReturnUnknown_Format", item.Name), title);
                    break;

                default:
                    _statusNotifier.Show(StatusSeverity.Caution, _localization.Format("Settings_Backup_Orphans_ReturnFailed_Format", item.Name), title);
                    break;
            }

            if (HasScanned) SetSummary(Items.Count > 0 ? SummaryKind.Found : SummaryKind.NothingFound);
        }
        catch (Exception ex)
        {
            Log.Error("ManagedStorageOrphansViewModel", "ReturnFailed", ex);
            _statusNotifier.Show(StatusSeverity.Caution, _localization.Format("Settings_Backup_Orphans_ReturnFailed_Format", item.Name), title);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static IEnumerable<string> ReadReferencedPaths(string json)
    {
        List<string> result = [];
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return result;

            foreach (var property in document.RootElement.EnumerateObject())
            {
                // A Folder Portal's source folder counts as in use too: a portal may point into
                // PanelItems\{guid}\..., and returning that item would pull the folder out from under it.
                if (property.NameEquals("SourceFolderPath")
                    && property.Value.ValueKind == JsonValueKind.String
                    && property.Value.GetString() is { Length: > 0 } source)
                {
                    result.Add(source);
                    continue;
                }

                if (!property.NameEquals("FilePaths") || property.Value.ValueKind != JsonValueKind.Array) continue;

                foreach (var element in property.Value.EnumerateArray())
                {
                    if (element.ValueKind == JsonValueKind.String && element.GetString() is { Length: > 0 } path)
                        result.Add(path);
                }
            }
        }
        catch (JsonException)
        {
            // A corrupt snapshot entry references nothing we can read - skip it, as Restore does.
        }

        return result;
    }

    private ReturnOutcome TryReturn(ManagedStorageOrphanItem item, IReadOnlyCollection<string> references)
    {
        var stillOrphan = _scanner.Scan(references)
            .Any(o => string.Equals(o.ManagedPath, item.ManagedPath, StringComparison.OrdinalIgnoreCase));
        if (!stillOrphan) return ReturnOutcome.NowReferenced;

        return _storage.Restore(item.ManagedPath) switch
        {
            RestoreOutcome.Restored => ReturnOutcome.Returned,
            RestoreOutcome.OriginalPathUnknown => ReturnOutcome.OriginalUnknown,
            _ => ReturnOutcome.Failed,
        };
    }

    private void OpenFolder(ManagedStorageOrphanItem? item)
    {
        if (item is null) return;

        // The per-item GUID folder holds exactly this item, so the user lands on it directly.
        var folder = Path.GetDirectoryName(item.ManagedPath);
        if (!string.IsNullOrEmpty(folder)) _linkLauncher.OpenFolder(folder);
    }

    /// <summary>
    /// Every path the live panels (all pages) and every layout snapshot list. Folder Portal panels count
    /// too: they keep their pre-portal <c>FilePaths</c> and restoring those would bring the items back.
    /// </summary>
    private List<string> CollectReferencedPaths()
    {
        // Make the models current first (a debounced save may still be pending), exactly like a snapshot capture.
        _windowManager.SyncAllWindowsToModels();

        var paths = new List<string>();
        foreach (var model in _windowManager.GetAllChildWindows())
        {
            paths.AddRange(model.FilePaths);
            if (!string.IsNullOrWhiteSpace(model.SourceFolderPath)) paths.Add(model.SourceFolderPath);
        }

        foreach (var snapshot in _snapshots.Snapshots)
        {
            foreach (var json in snapshot.PanelJsonItems)
                paths.AddRange(ReadReferencedPaths(json));
        }

        return paths;
    }

    private void SetSummary(SummaryKind kind)
    {
        _summary = kind;
        OnPropertyChanged(nameof(StatusText));
    }

    private void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private enum ReturnOutcome
    {
        Returned,
        NowReferenced,
        OriginalUnknown,
        Failed,
    }
}
