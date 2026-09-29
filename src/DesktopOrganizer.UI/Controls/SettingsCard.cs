using System.Collections;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace DesktopOrganizer.UI.Controls;

/// <summary>
/// The "Settings card" pattern as a real templated control (design-system-1x.md §2.5, ADR-047 D-1, follow-up
/// DO-026) — <see cref="Header"/>/<see cref="Description"/>/<see cref="ActionContent"/> laid out in the
/// two-column shape <c>Resources/Styles/Settings.xaml</c>'s pre-6.2a hand-rolled
/// <c>Border</c>+<c>Grid</c>+<c>StackPanel</c> markup repeated in ~35 places across
/// <c>Views/Settings/*.xaml</c>/<c>Views/PanelCustomizeWindow.xaml</c>; <see cref="ContentControl.Content"/>
/// (inherited) is the optional body BELOW that row, for cards that do not fit the strict two-column shape
/// (a list+toolbar, a slider-with-readout, a RadioButton/segmented group).
/// </summary>
/// <remarks>
/// A lookless <see cref="ContentControl"/> (default style in
/// <c>Resources/Styles/Theme.Controls.xaml</c>) — not a <c>UserControl</c>, for the same reason
/// <see cref="PanelChrome"/> is one: a <c>UserControl</c> cannot be re-templated, and every one of this
/// control's four content slots (<see cref="Header"/>/<see cref="Description"/>/<see cref="ActionContent"/>/
/// <see cref="ContentControl.Content"/>) needs to reach the default style's <see cref="ContentPresenter"/>s
/// via <c>ContentSource</c>, exactly like <see cref="PanelChrome.TitleEditor"/>/<see cref="PanelChrome.HeaderActions"/>
/// already do.
/// </remarks>
public class SettingsCard : ContentControl
{
    /// <summary>Identifies the <see cref="Header"/> dependency property.</summary>
    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
        nameof(Header),
        typeof(object),
        typeof(SettingsCard),
        new PropertyMetadata(null, OnLogicalChildContentChanged));

    /// <summary>Identifies the <see cref="Description"/> dependency property.</summary>
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description),
        typeof(object),
        typeof(SettingsCard),
        new PropertyMetadata(null, OnLogicalChildContentChanged));

    /// <summary>Identifies the <see cref="ActionContent"/> dependency property.</summary>
    public static readonly DependencyProperty ActionContentProperty = DependencyProperty.Register(
        nameof(ActionContent),
        typeof(object),
        typeof(SettingsCard),
        new PropertyMetadata(null, OnLogicalChildContentChanged));

    static SettingsCard()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(SettingsCard), new FrameworkPropertyMetadata(typeof(SettingsCard)));
    }

    /// <summary>Gets or sets the card's title (typically a plain string, styled as <c>Settings.CardHeader</c> used to be).</summary>
    public object Header
    {
        get => GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    /// <summary>Gets or sets the card's description text under the header (typically a plain string, styled as <c>Settings.CardDescription</c> used to be).</summary>
    public object Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    /// <summary>Gets or sets the control docked to the header row's right column (a toggle, button, ComboBox, etc.) — automatically <see cref="AutomationProperties.LabeledByProperty"/>-linked to <see cref="Header"/> by the default style.</summary>
    public object ActionContent
    {
        get => GetValue(ActionContentProperty);
        set => SetValue(ActionContentProperty, value);
    }

    /// <summary>
    /// M-3 fix (docs/PLANS/design-system-1x.md phase 6.5 review round): <see cref="Header"/>/
    /// <see cref="Description"/>/<see cref="ActionContent"/> are now genuine logical children of this
    /// control — previously each reached the default style's <see cref="ContentPresenter"/>s only through
    /// its own <see cref="DependencyProperty"/> value, never via <see cref="LogicalTreeHelper"/>, which made
    /// tooling/tests that walk the logical tree (e.g. to find every <see cref="RadioButton"/>/
    /// <see cref="CheckBox"/> a host owns, or an automation-tree walker) blind to whatever a host put in
    /// <see cref="ActionContent"/> unless it knew to look inside that specific property too — the same M-G
    /// gap <see cref="PanelColorEditor.AdvancedExtraContent"/> already closed for itself, applied here to
    /// all three of this control's own extra content slots (<see cref="ContentControl.Content"/> — the
    /// fourth slot — already has base <see cref="ContentControl"/> logical-child handling of its own).
    /// </summary>
    protected override IEnumerator LogicalChildren
    {
        get
        {
            var baseChildren = base.LogicalChildren;
            if (Header is null && Description is null && ActionContent is null)
                return baseChildren;

            return EnumerateWithExtraContent(baseChildren, Header, Description, ActionContent);
        }
    }

    private static IEnumerator EnumerateWithExtraContent(IEnumerator baseChildren, object? header, object? description, object? actionContent)
    {
        while (baseChildren.MoveNext())
            yield return baseChildren.Current;

        if (header is not null) yield return header;
        if (description is not null) yield return description;
        if (actionContent is not null) yield return actionContent;
    }

    /// <summary>
    /// See <see cref="LogicalChildren"/>'s own remarks — keeps it (and therefore
    /// <see cref="LogicalTreeHelper"/>) in sync whenever a host swaps <see cref="Header"/>/
    /// <see cref="Description"/>/<see cref="ActionContent"/> in or out. Shared by all three properties'
    /// <see cref="PropertyMetadata"/> — each callback only ever touches the one old/new value WPF already
    /// isolated for that specific property.
    /// </summary>
    private static void OnLogicalChildContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (SettingsCard)d;

        if (e.OldValue is not null)
            card.RemoveLogicalChild(e.OldValue);

        if (e.NewValue is not null)
            card.AddLogicalChild(e.NewValue);
    }
}
