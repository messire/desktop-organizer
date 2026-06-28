using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using DesktopOrganizer.Core.Models;

namespace DesktopOrganizer.UI.Models;

/// <summary>
/// Represents a single system icon entry in the system icons panel.
/// Implements <see cref="INotifyPropertyChanged"/> so UI bindings react to runtime updates.
/// </summary>
public sealed class SystemIconItemModel : INotifyPropertyChanged
{
    private bool _isVisible = true;
    private int _order;
    private string _label = string.Empty;
    private ImageSource? _icon;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Identifies which well-known shell item this entry represents.
    /// </summary>
    public SystemIconKind Kind { get; init; }

    /// <summary>
    /// Whether this item is shown in the panel.
    /// </summary>
    public bool IsVisible
    {
        get => _isVisible;
        set => SetField(ref _isVisible, value);
    }

    /// <summary>
    /// Display order within the panel (ascending, zero-based).
    /// </summary>
    public int Order
    {
        get => _order;
        set => SetField(ref _order, value);
    }

    /// <summary>
    /// Display label shown under the icon. Defaults to the descriptor's <c>DefaultCaption</c>.
    /// </summary>
    public string Label
    {
        get => _label;
        set => SetField(ref _label, value);
    }

    /// <summary>
    /// Runtime icon image loaded from the native shell. Not persisted.
    /// <see langword="null"/> while loading or when native retrieval fails.
    /// </summary>
    public ImageSource? Icon
    {
        get => _icon;
        set => SetField(ref _icon, value);
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
