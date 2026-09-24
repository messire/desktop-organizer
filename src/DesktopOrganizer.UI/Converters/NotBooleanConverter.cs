using System.Globalization;
using System.Windows.Data;

namespace DesktopOrganizer.UI.Converters;

/// <summary>
/// Returns <see langword="true"/> when the bound value is boolean <see langword="false"/> — the NOT gate
/// <see cref="AllTrueMultiConverter"/> needs to express a "…AND NOT x" <c>IsEnabled</c> rule, which a
/// pure AND converter cannot do on its own.
/// </summary>
/// <remarks>
/// First consumer: <c>Views/ChildWindow.xaml</c>'s "Size" preset submenu (P04 fix-cycle 1, MV-21), which
/// must be disabled while the panel is rolled up — <c>SizePresetMenu_Click</c> has always refused that
/// state (a collapsed panel has no expanded height to apply a preset to), but silently, which reads as a
/// hang rather than as a rule.
/// <para>
/// Anything that is not boolean <see langword="true"/> converts to <see langword="true"/> (a
/// <see cref="Binding"/> that failed to resolve yields <c>DependencyProperty.UnsetValue</c>, and an
/// unknown state must not silently disable a command) — the mirror of
/// <see cref="AllTrueMultiConverter"/>'s own strict <c>is true</c> test.
/// </para>
/// </remarks>
public class NotBooleanConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
