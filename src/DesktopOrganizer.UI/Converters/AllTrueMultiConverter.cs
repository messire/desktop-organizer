using System.Globalization;
using System.Windows.Data;

namespace DesktopOrganizer.UI.Converters;

/// <summary>
/// Returns <see langword="true"/> only when every bound value is boolean <see langword="true"/> — a
/// generic AND gate for <c>IsEnabled</c>/<c>Visibility</c> bindings that need to combine two independent
/// conditions coming from different binding sources (e.g. a <c>StaticResource</c> singleton service
/// property and a per-panel <c>DataContext</c> property), which a single-value
/// <see cref="System.Windows.Data.IValueConverter"/> cannot do.
/// </summary>
/// <remarks>
/// First consumer: <c>Views/ChildWindow.xaml</c>'s "Auto-size to content"/"Fit panel to content"
/// context-menu items (P06, <c>docs/fix-prompts/06-fit-content.md</c>) — both must be disabled when
/// EITHER <c>ILayoutEditModeService.CanEnterEditLayout</c> is <see langword="false"/> (ADR-026 gating,
/// same as the existing "Size"/"Align to workspace grid" items) OR the panel's
/// <c>ChildWindowModel.ViewMode</c> is not <c>Grid</c> (ADR-029 Открытые вопросы Q3 = B: the fit formula
/// has no List-mode equivalent).
/// </remarks>
public sealed class AllTrueMultiConverter : IMultiValueConverter
{
    /// <inheritdoc />
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
        => values.Length > 0 && values.All(v => v is true);

    /// <inheritdoc />
    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
