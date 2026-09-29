using System.Windows;
using System.Windows.Controls;

namespace DesktopOrganizer.UI.Behaviors;

/// <summary>
/// M-1 fix (docs/PLANS/design-system-1x.md phase 6.5 review round): re-pulls a checkable radio-group
/// <see cref="MenuItem"/>'s <c>IsChecked</c> binding right after every click.
/// </summary>
/// <remarks>
/// WPF's own <c>MenuItem.OnClickImpl</c> toggles <c>IsChecked</c> locally through <c>SetCurrentValue</c>
/// SYNCHRONOUSLY, on the same call that handles the click — deliberately, so a plain click never tears
/// down a <c>OneWay</c>/<c>TwoWay</c> binding outright. Both the <see cref="MenuItem.Click"/> event AND the
/// bound <c>Command</c>'s execution are themselves deferred one dispatcher frame later (a
/// <see cref="System.Windows.Threading.DispatcherPriority.Render"/>-priority <c>BeginInvoke</c> WPF queues
/// from inside <c>OnClickImpl</c>), with <c>Click</c> raised strictly BEFORE the <c>Command</c> runs inside
/// that same later frame — so by the time this behavior's own <see cref="OnClick"/> handler runs, the
/// SYNCHRONOUS toggle has already happened but the <c>Command</c> has NOT yet. That ordering does not
/// matter for correctness here: for a genuine two-state toggle, the command always changes the bound
/// source, and the resulting <c>PropertyChanged</c> — raised once <c>Command</c> execution actually reaches
/// it, moments after this handler returns — re-pulls the real value on its own regardless of what this
/// handler already did. For a RADIO-GROUP item whose bound value is a comparison against an
/// already-selected value (<c>EnumEquals</c>/<c>EqualityToBool</c> converters elsewhere in
/// <c>ChildWindow.xaml</c>), re-clicking the item that is ALREADY selected is a no-op for the source —
/// setting an enum property to the value it already holds raises no <c>PropertyChanged</c> at all — so
/// nothing else ever corrects the local <c>SetCurrentValue(false)</c> WPF applied synchronously on click:
/// the check glyph silently disappears even though the item is still logically selected (the panel's own
/// state never actually changed). Re-pulling the binding value from THIS handler is what closes that one
/// gap, precisely because the source's value at that moment (before or after the no-op command runs, it is
/// identical either way) is still the real, correct one.
/// <para>
/// Attaching <see cref="IsRadioItemProperty"/>="True" hooks the routed <see cref="MenuItem.Click"/> event
/// and forces <see cref="System.Windows.Data.BindingExpressionBase.UpdateTarget"/> on the bound
/// <see cref="MenuItem.IsCheckedProperty"/>, re-applying the real (still <see langword="true"/>) value.
/// This keeps the existing <c>OneWay</c> binding — and the UIA <c>TogglePattern</c> it drives — completely
/// intact (variant B of the fix), rather than replacing it with a converter that has to track previous
/// selection state itself.
/// </para>
/// </remarks>
public static class RadioMenuItemRefreshBehavior
{
    /// <summary>
    /// When set to <see langword="true"/> on a checkable radio-group <see cref="MenuItem"/>, re-applies its
    /// bound <see cref="MenuItem.IsCheckedProperty"/> value after every click (see remarks on
    /// <see cref="RadioMenuItemRefreshBehavior"/>).
    /// </summary>
    public static readonly DependencyProperty IsRadioItemProperty =
        DependencyProperty.RegisterAttached(
            "IsRadioItem",
            typeof(bool),
            typeof(RadioMenuItemRefreshBehavior),
            new PropertyMetadata(false, OnIsRadioItemChanged));

    /// <summary>Gets the value of the <see cref="IsRadioItemProperty"/> attached property.</summary>
    /// <param name="obj">The <see cref="MenuItem"/> to read the property from.</param>
    public static bool GetIsRadioItem(DependencyObject obj) => (bool)obj.GetValue(IsRadioItemProperty);

    /// <summary>Sets the value of the <see cref="IsRadioItemProperty"/> attached property.</summary>
    /// <param name="obj">The <see cref="MenuItem"/> to wire up (or unwire) with the post-click refresh.</param>
    /// <param name="value"><see langword="true"/> to wire this <see cref="MenuItem"/> up; <see langword="false"/> to remove the wiring.</param>
    public static void SetIsRadioItem(DependencyObject obj, bool value) => obj.SetValue(IsRadioItemProperty, value);

    private static void OnIsRadioItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not MenuItem menuItem) return;

        if ((bool)e.NewValue)
            menuItem.Click += OnClick;
        else
            menuItem.Click -= OnClick;
    }

    /// <summary>
    /// Runs once <see cref="MenuItem.Click"/> is actually raised — after WPF's own SYNCHRONOUS
    /// <c>IsChecked</c> toggle, but BEFORE the bound <c>Command</c> has executed (see this class's own
    /// remarks for the exact, dispatcher-deferred order). Re-pulling the binding here always ends up
    /// correct regardless: re-selecting the same item corrects WPF's own local
    /// <see langword="false"/> back to the real, still-selected <see langword="true"/> immediately; a
    /// genuine new selection instead relies on the ordinary <c>PropertyChanged</c> WPF's binding engine
    /// reacts to on its own once the command actually runs moments later — this handler having already
    /// run first does not change that outcome.
    /// </summary>
    private static void OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem) return;
        menuItem.GetBindingExpression(MenuItem.IsCheckedProperty)?.UpdateTarget();
    }
}
