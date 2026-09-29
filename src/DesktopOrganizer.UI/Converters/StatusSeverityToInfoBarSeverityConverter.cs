using System.Globalization;
using System.Windows.Data;
using DesktopOrganizer.UI.Controls;
using DesktopOrganizer.UI.Services;

namespace DesktopOrganizer.UI.Converters;

/// <summary>
/// Maps <see cref="StatusSeverity"/> (the service-facing <see cref="IStatusNotifier"/> contract) onto
/// <see cref="InfoBarSeverity"/> (the <see cref="InfoBar"/> control's own dependency property type) — the
/// two are deliberately distinct types (<see cref="StatusSeverity"/>'s own remarks) with identically-named
/// cases, so <c>MainWindow.xaml</c>'s <c>InfoBar.Severity="{Binding Status.Current.Severity, Converter=…}"</c>
/// binding needs this one-line bridge rather than either type depending on the other.
/// </summary>
public sealed class StatusSeverityToInfoBarSeverityConverter : IValueConverter
{
    /// <inheritdoc/>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        StatusSeverity.Success => InfoBarSeverity.Success,
        StatusSeverity.Caution => InfoBarSeverity.Caution,
        StatusSeverity.Critical => InfoBarSeverity.Critical,
        _ => InfoBarSeverity.Informational,
    };

    /// <inheritdoc/>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
