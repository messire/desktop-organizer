using System.Windows;
using DesktopOrganizer.UI.Services;
using DesktopOrganizer.UI.Theming;

namespace DesktopOrganizer.UI.Behaviors;

/// <summary>
/// Bridges <see cref="ILayoutEditModeService.ModeChanged"/> into
/// <see cref="PanelTheme.IsLayoutEditingProperty"/> (docs/PLANS/phase-6-edit-layout-ux.md §1 D-6) — the
/// direct sibling of <see cref="PanelVisualStateBehavior"/>, which does the identical job for
/// <see cref="PanelTheme.VisualStateProperty"/>: subscribe once in the constructor, republish via a
/// single <see cref="DependencyObject.SetValue(DependencyProperty, object)"/> call on every change,
/// unsubscribe in <see cref="Dispose"/>.
/// </summary>
/// <remarks>
/// Pure WPF glue: all the actual mode-resolution logic already lives in the process-wide
/// <see cref="ILayoutEditModeService"/> singleton (P6-1) — this class adds no mapping logic of its own to
/// extract/test. Written to <c>PanelRoot</c> — the same element <see cref="PanelVisualStateBehavior"/>
/// writes <see cref="PanelTheme.VisualStateProperty"/> to — so every descendant (including
/// <see cref="Controls.PanelChrome"/> and, directly via inheritance, <c>ChildWindow.xaml</c>'s
/// <c>ResizeGripVisuals</c> borders) sees the resolved value without its own subscription.
/// </remarks>
public sealed class LayoutEditModeBehavior : IDisposable
{
    private readonly FrameworkElement _root;
    private readonly ILayoutEditModeService _layoutEditMode;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="LayoutEditModeBehavior"/> class and immediately
    /// publishes the current mode.
    /// </summary>
    /// <param name="root">The panel's root element — the same <c>PanelRoot</c> <see cref="PanelVisualStateBehavior"/> targets.</param>
    /// <param name="layoutEditMode">The process-wide <see cref="ILayoutEditModeService"/> singleton (P6-1).</param>
    public LayoutEditModeBehavior(FrameworkElement root, ILayoutEditModeService layoutEditMode)
    {
        _root = root;
        _layoutEditMode = layoutEditMode;

        _layoutEditMode.ModeChanged += OnModeChanged;

        Refresh();
    }

    /// <summary>Re-publishes <see cref="ILayoutEditModeService.IsEditing"/> via <see cref="PanelTheme.SetIsLayoutEditing"/>.</summary>
    public void Refresh() => PanelTheme.SetIsLayoutEditing(_root, _layoutEditMode.IsEditing);

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _layoutEditMode.ModeChanged -= OnModeChanged;
    }

    private void OnModeChanged(object? sender, LayoutModeChangedEventArgs e) => Refresh();
}
