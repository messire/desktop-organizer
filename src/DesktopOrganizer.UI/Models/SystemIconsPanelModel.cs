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
    private SystemIconSize _iconSize = SystemIconSize.Medium;

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
    /// Display size used for all system icon items.
    /// </summary>
    public SystemIconSize IconSize
    {
        get => _iconSize;
        set => SetField(ref _iconSize, value);
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
