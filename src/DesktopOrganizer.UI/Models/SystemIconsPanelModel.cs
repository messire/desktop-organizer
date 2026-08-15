using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using DesktopOrganizer.Core.Models;

namespace DesktopOrganizer.UI.Models;

/// <summary>
/// Top-level model for the system icons panel.
/// Implements <see cref="INotifyPropertyChanged"/> so the settings UI and panel stay in sync.
/// </summary>
public sealed class SystemIconsPanelModel : INotifyPropertyChanged
{
    private bool _isEnabled;
    private bool _showOnAllVirtualDesktops;
    private bool _virtualDesktopFeatureIneffective;
    private SystemIconSize _iconSize = SystemIconSize.Medium;
    private Guid? _anchorVirtualDesktopId;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// When <see langword="false"/> the panel window is closed; when <see langword="true"/> it is shown.
    /// </summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetField(ref _isEnabled, value);
    }

    /// <summary>
    /// When <see langword="true"/> the panel follows the user across Windows virtual desktops
    /// (Task View): the manager polls the active virtual desktop and pulls the panel window over
    /// when it is left behind. Defaults to <see langword="false"/>; has no effect on systems where
    /// the virtual desktop manager is unavailable.
    /// </summary>
    public bool ShowOnAllVirtualDesktops
    {
        get => _showOnAllVirtualDesktops;
        set => SetField(ref _showOnAllVirtualDesktops, value);
    }

    /// <summary>
    /// Runtime-only flag (never persisted): the poll loop repeatedly asked the shell to move the
    /// panel onto the active virtual desktop, the call reported success every time, and the panel
    /// still was not reported as being on the active desktop. On such a system the documented
    /// <c>MoveWindowToDesktop</c> is a silent no-op for this window (see ADR-016), so the manager
    /// gives up and stops polling instead of hammering Explorer once a second forever.
    /// Reset whenever the user re-applies the setting.
    /// </summary>
    public bool VirtualDesktopFeatureIneffective
    {
        get => _virtualDesktopFeatureIneffective;
        set => SetField(ref _virtualDesktopFeatureIneffective, value);
    }

    /// <summary>
    /// Display size used for all system icon items.
    /// </summary>
    public SystemIconSize IconSize
    {
        get => _iconSize;
        set => SetField(ref _iconSize, value);
    }

    /// <summary>
    /// Id of the virtual desktop the panel is pinned to while <see cref="ShowOnAllVirtualDesktops"/>
    /// is <see langword="false"/> ("Desktop 1" in ADR-019 terms). <see langword="null"/> means no
    /// anchor has been captured yet (fresh install, or the previous capture attempt failed) — it is
    /// captured on the next opportunity by <c>SystemIconsPanelManager.EnforceAnchorDesktopOnce</c>,
    /// never guessed.
    /// </summary>
    /// <remarks>
    /// Purely internal manager state — deliberately <b>not</b> bound to anything in the settings UI.
    /// </remarks>
    public Guid? AnchorVirtualDesktopId
    {
        get => _anchorVirtualDesktopId;
        set => SetField(ref _anchorVirtualDesktopId, value);
    }

    /// <summary>
    /// Ordered list of system icon items. Each entry corresponds to one well-known shell item.
    /// </summary>
    public ObservableCollection<SystemIconItemModel> Items { get; init; } = BuildDefaultItems();

    /// <summary>
    /// Creates the default five-item collection in canonical order with all items visible.
    /// </summary>
    public static ObservableCollection<SystemIconItemModel> BuildDefaultItems()
    {
        var items = new ObservableCollection<SystemIconItemModel>();
        int order = 0;
        foreach (var (kind, descriptor) in SystemIconDescriptor.Table)
        {
            items.Add(new SystemIconItemModel
            {
                Kind = kind,
                IsVisible = true,
                Order = order++,
                Label = descriptor.DefaultCaption,
            });
        }

        return items;
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
