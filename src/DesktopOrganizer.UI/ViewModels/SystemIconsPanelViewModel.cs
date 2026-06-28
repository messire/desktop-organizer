using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using DesktopOrganizer.Core.Models;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.UI.ViewModels;

/// <summary>
/// ViewModel for the system icons panel window.
/// Exposes the ordered, visible items and handles open/context-menu/drag-reorder/recycle-bin commands.
/// </summary>
public sealed class SystemIconsPanelViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ISystemShellService _shellService;
    private readonly SystemIconsPanelModel _model;

    // Backing observable list that drives the ItemsControl.
    private readonly ObservableCollection<SystemIconItemModel> _visibleItems = [];

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Initializes a new instance of <see cref="SystemIconsPanelViewModel"/>.
    /// </summary>
    public SystemIconsPanelViewModel(
        SystemIconsPanelModel model,
        ISystemShellService shellService)
    {
        _model = model;
        _shellService = shellService;

        _shellService.RecycleBinChanged += OnRecycleBinChanged;

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

        foreach (var item in _model.Items)
            item.PropertyChanged -= OnItemPropertyChanged;

        _model.Items.CollectionChanged -= OnItemsCollectionChanged;
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Rebuilds _visibleItems from the current model state (filter + sort).
    /// Must be called on the UI thread.
    /// </summary>
    private void RebuildVisibleItems()
    {
        _visibleItems.Clear();
        foreach (var item in _model.Items.Where(i => i.IsVisible).OrderBy(i => i.Order))
            _visibleItems.Add(item);
    }

    private void OnRecycleBinChanged(object? sender, RecycleBinState state)
    {
        // Event may arrive on a timer/background thread — marshal to UI thread.
        Application.Current?.Dispatcher.BeginInvoke(RefreshRecycleBin);
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
