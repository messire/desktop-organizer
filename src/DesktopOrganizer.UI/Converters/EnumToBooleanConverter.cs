using System.Globalization;
using System.Windows.Data;

namespace DesktopOrganizer.UI.Converters;

/// <summary>
/// Two-way enum&lt;-&gt;bool converter for a <see cref="System.Windows.Controls.RadioButton"/> bound
/// directly to an enum-typed property (docs/prompts/settings-window-redesign.md, "Move/resize outside
/// Edit layout" group). <c>IsChecked</c> is <see langword="true"/> when the bound value equals the
/// converter <c>parameter</c>.
/// </summary>
/// <remarks>
/// <see cref="ConvertBack"/> only ever applies the checked (<see langword="true"/>) direction: within a
/// mutually-exclusive RadioButton group, the sibling that becomes checked already pushes its own value
/// back through this same path, so there is no scenario where "this option was just unchecked" should
/// also write to the source — doing so would race with (and could overwrite) the sibling's own write.
/// <see cref="Binding.DoNothing"/> leaves the source untouched in that case.
/// </remarks>
public sealed class EnumToBooleanConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not null && parameter is not null && value.Equals(parameter);

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true && parameter is not null ? parameter : Binding.DoNothing;
}
