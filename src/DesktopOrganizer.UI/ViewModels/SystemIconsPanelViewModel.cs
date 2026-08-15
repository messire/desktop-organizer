using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using DesktopOrganizer.Core.Models;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;

namespace DesktopOrganizer.UI.ViewModels;

/// <summary>
/// ViewModel for the system icons panel window.
/// Exposes the ordered, visible items and handles open/context-menu/drag-reorder/recycle-bin commands.
/// </summary>
public sealed class SystemIconsPanelViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ISystemShellService _shellService;
    private readonly SystemIconsPanelModel _model;
    private readonly IPanelSelectionCoordinator _selectionCoordinator;

    // Backing observable list that drives the ItemsControl.
    private readonly ObservableCollection<SystemIconItemModel> _visibleItems = [];

    private SystemIconItemModel? _selectedItem;

    /// <summary>
    /// Set for the duration of clearing <see cref="SelectedItem"/> from <see cref="OnSelectionClaimed"/>
    /// (a different panel just claimed the selection), so <see cref="SelectedItem"/>'s setter knows
    /// this particular change is an echo of a coordinator notification, not a fresh user selection to
    /// (re-)broadcast. Mirrors <c>ChildWindowViewModel._suppressCoordinatorEcho</c> (ui-polishing-1
    /// §2.4 Block A2, risk R3) — same reentrancy hazard, same fix, applied to the sidebar panel.
    /// </summary>
    private bool _suppressCoordinatorEcho;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Initializes a new instance of <see cref="SystemIconsPanelViewModel"/>.
    /// </summary>
    /// <param name="model">The panel's persisted state and item list.</param>
    /// <param name="shellService">Native shell integration (icons, context menu, Recycle Bin).</param>
    /// <param name="selectionCoordinator">
    /// The process-wide singleton (ui-polishing-1 §2.4 Block A2) that keeps this panel's selection and
    /// every fence panel's (<see cref="ChildWindowViewModel"/>) selection mutually exclusive, despite
    /// none of these <c>WS_EX_NOACTIVATE</c> windows ever receiving normal focus (ui-polishing-1 §5.4,
    /// Block D).
    /// </param>
    public SystemIconsPanelViewModel(
        SystemIconsPanelModel model,
        ISystemShellService shellService,
        IPanelSelectionCoordinator selectionCoordinator)
    {
        _model = model;
        _shellService = shellService;
        _selectionCoordinator = selectionCoordinator;

        _shellService.RecycleBinChanged += OnRecycleBinChanged;
        _selectionCoordinator.SelectionClaimed += OnSelectionClaimed;

        // Subscribe to model item changes so VisibleItems stays in sync.
        foreach (var item in _model.Items)
            item.PropertyChanged += OnItemPropertyChanged;

        _model.Items.CollectionChanged += OnItemsCollectionChanged;

        RebuildVisibleItems();

        OpenItemCommand = new RelayCommand<SystemIconKind>(kind =>
            _shellService.OpenSystemItem(kind, IntPtr.Zero));
    }

    // -------------------------------------------------------------------------
    // Bindable properties
    // -------------------------------------------------------------------------

    /// <summary>
    /// Visible items ordered by <see cref="SystemIconItemModel.Order"/>.
    /// Backed by an <see cref="ObservableCollection{T}"/> so the ItemsControl reacts to reorder.
    /// </summary>
    public IReadOnlyList<SystemIconItemModel> VisibleItems => _visibleItems;

    /// <summary>
    /// The icon size in pixels derived from the model setting.
    /// </summary>
    public int IconSizePx => (int)_model.IconSize;

    /// <summary>
    /// The single currently-selected item in the sidebar, or <see langword="null"/> when nothing is
    /// selected (ui-polishing-1 §5.4, Block D). Two-way bound to <c>ListBox.SelectedItem</c> in
    /// <c>SystemIconsPanelWindow.xaml</c>. Purely transient UI state — never persisted (not part of
    /// <c>SystemIconsPanelManager.PanelDto</c>/the settings JSON).
    /// </summary>
    /// <remarks>
    /// Claims the global cross-panel selection (<see cref="IPanelSelectionCoordinator.NotifySelected"/>)
    /// whenever the new value is non-null — mirrors <c>ChildWindowViewModel.OnSelectedFileChanged</c>
    /// exactly (ui-polishing-1 §2.4 Block A2): a panel clearing its own selection is not "this panel
    /// claiming the selection", so <see langword="null"/> assignments never notify, and the reentrancy
    /// guard <see cref="_suppressCoordinatorEcho"/> skips notifying while this exact clear is itself an
    /// echo of <see cref="OnSelectionClaimed"/> reacting to some other panel's claim.
    /// </remarks>
    public SystemIconItemModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (ReferenceEquals(_selectedItem, value)) return;

            _selectedItem = value;
            OnPropertyChanged();

            if (_suppressCoordinatorEcho) return;
            if (_selectedItem is not null) _selectionCoordinator.NotifySelected(this);
        }
    }

    // -------------------------------------------------------------------------
    // Commands
    // -------------------------------------------------------------------------

    /// <summary>
    /// Command to open a system item (double-click).
    /// </summary>
    public ICommand OpenItemCommand { get; }

    // -------------------------------------------------------------------------
    // Public methods (called from code-behind)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Shows the native shell context menu for <paramref name="kind"/> at the given screen coordinates.
    /// </summary>
    public void ShowContextMenu(SystemIconKind kind, IntPtr ownerHwnd, int screenX, int screenY)
    {
        try
        {
            _shellService.ShowContextMenu(kind, ownerHwnd, screenX, screenY);
        }
        catch
        {
            // Degrade silently — panel remains usable even if context menu fails.
        }
    }

    /// <summary>
    /// Loads icons from the shell service for all items.
    /// Should be called once the panel window handle is available.
    /// </summary>
    public void LoadIcons()
    {
        foreach (var item in _model.Items)
        {
            try
            {
                item.Icon = _shellService.GetSystemIcon(item.Kind, IconSizePx);
            }
            catch
            {
                // Graceful degrade: leave Icon as null (placeholder via empty Image).
            }
        }
    }

    /// <summary>
    /// Moves <paramref name="kind"/> to <paramref name="newOrder"/>, shifting other items
    /// to keep all Order values unique and contiguous. Rebuilds VisibleItems afterwards.
    /// </summary>
    public void Reorder(SystemIconKind kind, int newOrder)
    {
        var target = _model.Items.FirstOrDefault(i => i.Kind == kind);
        if (target is null) return;

        // Clamp to valid range.
        int count = _model.Items.Count;
        newOrder = Math.Max(0, Math.Min(newOrder, count - 1));

        var oldOrder = target.Order;
        if (oldOrder == newOrder) return;

        if (newOrder < oldOrder)
        {
            foreach (var item in _model.Items.Where(i => i.Kind != kind && i.Order >= newOrder && i.Order < oldOrder))
                item.Order++;
        }
        else
        {
            foreach (var item in _model.Items.Where(i => i.Kind != kind && i.Order > oldOrder && i.Order <= newOrder))
                item.Order--;
        }

        target.Order = newOrder;

        RebuildVisibleItems();
    }

    /// <summary>
    /// Sends <paramref name="paths"/> to the Recycle Bin and refreshes the bin icon.
    /// </summary>
    public void SendToRecycleBin(string[] paths, IntPtr ownerHwnd)
    {
        try
        {
            _shellService.SendToRecycleBin(paths, ownerHwnd);
        }
        catch
        {
            // Degrade silently.
        }

        // Explicit refresh: ISystemShellService.SendToRecycleBin may not always fire
        // RecycleBinChanged synchronously; one explicit refresh here is sufficient.
        // RefreshRecycleBin() is safe here because SendToRecycleBin is called from
        // the Drop handler which runs on the UI thread.
        RefreshRecycleBin();
    }

    /// <summary>
    /// Queries the current Recycle Bin state and refreshes the Recycle Bin item's icon.
    /// No-op if the Recycle Bin item is not present. Swallows shell failures gracefully.
    /// </summary>
    public void RefreshRecycleBin()
    {
        // Must be called on the UI thread (bindings update WPF properties).
        try
        {
            var binItem = _model.Items.FirstOrDefault(i => i.Kind == SystemIconKind.RecycleBin);
            if (binItem is null) return;

            binItem.Icon = _shellService.GetSystemIcon(SystemIconKind.RecycleBin, IconSizePx);
        }
        catch
        {
            // Degrade silently — panel remains functional even if icon cannot be refreshed.
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _shellService.RecycleBinChanged -= OnRecycleBinChanged;
        _selectionCoordinator.SelectionClaimed -= OnSelectionClaimed;

        // Releases this panel's claim on the coordinator's single, process-wide _currentOwner slot, if
        // it still holds it — see ChildWindowViewModel.Dispose()'s remarks (finalization MAJOR M-1) for
        // why this must be unconditional rather than gated on "did I hold the selection".
        _selectionCoordinator.Release(this);

        foreach (var item in _model.Items)
            item.PropertyChanged -= OnItemPropertyChanged;

        _model.Items.CollectionChanged -= OnItemsCollectionChanged;
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Rebuilds _visibleItems from the current model state (filter + sort). Must be called on the UI
    /// thread.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="SelectedItem"/> is tracked purely by the ViewModel's own field, not derived from
    /// <see cref="VisibleItems"/> or an index into it, so a reorder — the same
    /// <see cref="SystemIconItemModel"/> reference, just a different position — never disturbs it on
    /// its own (ui-polishing-1 §5.4). What this method must guard against instead is losing that
    /// selection to a side effect of the rebuild itself: <c>_visibleItems.Clear()</c> raises a
    /// <c>CollectionChanged</c> <c>Reset</c> immediately followed by one <c>Add</c> per item, and the
    /// sidebar's <c>ListBox.SelectedItem</c> is bound <c>TwoWay</c> — if WPF's <c>Selector</c> reacts to
    /// that transient <c>Reset</c> by clearing its own selection, the binding pushes that
    /// <see langword="null"/> straight back into <see cref="SelectedItem"/>'s setter before this method
    /// even finishes re-adding the items. Capturing the previous selection in a local variable before
    /// <c>Clear()</c> runs, then explicitly re-asserting it afterwards (or clearing it, if the item is
    /// genuinely gone) sidesteps that race entirely, regardless of how the view layer actually reacts
    /// to the <c>Reset</c>.
    /// </para>
    /// </remarks>
    private void RebuildVisibleItems()
    {
        var previousSelection = _selectedItem;

        _visibleItems.Clear();
        foreach (var item in _model.Items.Where(i => i.IsVisible).OrderBy(i => i.Order))
            _visibleItems.Add(item);

        if (previousSelection is null) return;

        // Re-assert (reorder) or clear (item hidden/removed) — see the remarks above for why this is
        // done explicitly rather than trusted to survive the Clear()+Add() sequence on its own.
        SelectedItem = _visibleItems.Contains(previousSelection) ? previousSelection : null;
    }

    /// <summary>
    /// Refreshes the Recycle Bin icon whenever the shell reports the bin's content changed.
    /// </summary>
    /// <remarks>
    /// The event may arrive on a timer/background thread, so the refresh is marshalled to the UI
    /// thread whenever there is one. <see cref="Application.Current"/> is <see langword="null"/> when
    /// this ViewModel runs without a WPF <see cref="Application"/> (unit tests, or after application
    /// shutdown): there is then no dispatcher to marshal to, and posting to a null dispatcher would
    /// drop the notification silently, so the refresh runs inline on the calling thread instead.
    /// That path only touches plain ViewModel/model state and <see cref="RefreshRecycleBin"/> already
    /// swallows shell failures, so it can neither throw nor block the shell's event source. Behaviour
    /// inside the running application (where <see cref="Application.Current"/> is always set) is
    /// unchanged.
    /// </remarks>
    private void OnRecycleBinChanged(object? sender, RecycleBinState state)
    {
        var dispatcher = Application.Current?.Dispatcher;

        if (dispatcher is null)
        {
            RefreshRecycleBin();
            return;
        }

        dispatcher.BeginInvoke(RefreshRecycleBin);
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Rebuild visible list whenever IsVisible or Order changes on any item.
        if (e.PropertyName is nameof(SystemIconItemModel.IsVisible) or nameof(SystemIconItemModel.Order))
            RebuildVisibleItems();
    }

    private void OnItemsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        RebuildVisibleItems();
    }

    /// <summary>
    /// Reacts to another owner claiming the global cross-panel selection
    /// (<see cref="IPanelSelectionCoordinator.SelectionClaimed"/>) by clearing this panel's own
    /// selection — mirrors <c>ChildWindowViewModel.OnSelectionClaimed</c> exactly (ui-polishing-1 §5.4,
    /// §2.4 Block A2 DoD 4: "selecting here deselects there", now across both panel types). A no-op
    /// when <paramref name="owner"/> is this instance itself, or when this panel has no selection to
    /// clear in the first place. Wraps the actual clear in <see cref="_suppressCoordinatorEcho"/> so
    /// <see cref="SelectedItem"/>'s setter doesn't treat this echo as a fresh claim to re-broadcast.
    /// </summary>
    private void OnSelectionClaimed(object? sender, object owner)
    {
        if (ReferenceEquals(owner, this)) return;
        if (_selectedItem is null) return;

        _suppressCoordinatorEcho = true;
        try
        {
            SelectedItem = null;
        }
        finally
        {
            _suppressCoordinatorEcho = false;
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // -------------------------------------------------------------------------
    // Minimal relay command implementation (internal to this ViewModel)
    // -------------------------------------------------------------------------

    private sealed class RelayCommand<T>(Action<T> execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter)
        {
            if (parameter is T value)
                execute(value);
        }
    }
}
