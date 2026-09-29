using System.Collections;
using System.Windows;
using System.Windows.Controls;

namespace DesktopOrganizer.UI.Controls;

/// <summary>
/// Shared accent/text colour editor (ADR-042, backlog 1.x item 5 "simplified colour editor") — a single
/// "Colour" card (preset swatch grid, backed by <see cref="Controls.PanelColorEditor"/>'s own
/// <c>PanelColorEditorViewModel.SwatchItems</c>, plus a compact secondary Light/Dark toggle underneath it)
/// and a collapsed-by-default "Advanced" <c>Settings.CardExpander</c> (raw hex <see cref="TextBox"/>es,
/// "Custom…" text picker, <see cref="AdvancedExtraContent"/>, and "Reset to defaults") — used by both
/// <c>PanelCustomizeWindow</c>'s Appearance section and Settings' "New panel defaults" section.
/// <see cref="FrameworkElement.DataContext"/> is a <c>PanelColorEditorViewModel</c>, set by the consuming
/// window/section.
/// </summary>
public partial class PanelColorEditor : UserControl
{
    /// <summary>
    /// Backlog 1.x item 5 (§5.2 "Целевая структура"): lets each host inject its OWN surface-style/opacity
    /// controls (today, a single shared <see cref="SurfaceOptionsEditor"/> instance — backlog 1.x wave 2
    /// review M-G) into this control's Advanced card, between the raw text-colour row and "Reset to
    /// defaults" — the shape both <c>PanelCustomizeWindow.xaml</c>'s Appearance section and
    /// <c>NewPanelDefaultsSection.xaml</c> used to duplicate as a separate "Surface" card of their own.
    /// This control's <c>PanelColorEditorViewModel</c> DataContext (<c>Colors</c>/<c>DefaultColors</c>) owns
    /// no surface/opacity state of its own (ADR-042 "thin proxy") — the content assigned here therefore
    /// binds back to the HOST's own DataContext via <c>RelativeSource AncestorType</c> (Window for
    /// <c>PanelCustomizeWindow</c>, the section type itself for <c>NewPanelDefaultsSection</c> — plain
    /// <c>AncestorType=UserControl</c> would incorrectly resolve to THIS control, itself a
    /// <see cref="UserControl"/>, sitting in between), not <see cref="FrameworkElement.DataContext"/>
    /// inheritance (which would resolve to this control's own <c>Colors</c> proxy instead).
    /// </summary>
    public static readonly DependencyProperty AdvancedExtraContentProperty = DependencyProperty.Register(
        nameof(AdvancedExtraContent),
        typeof(object),
        typeof(PanelColorEditor),
        new PropertyMetadata(null, OnAdvancedExtraContentChanged));

    /// <summary>
    /// Initializes a new instance of the <see cref="PanelColorEditor"/> class.
    /// </summary>
    public PanelColorEditor() => InitializeComponent();

    /// <summary>The host-supplied extra content shown inside the Advanced card — see this property's own <see cref="DependencyProperty"/> remarks.</summary>
    public object? AdvancedExtraContent
    {
        get => GetValue(AdvancedExtraContentProperty);
        set => SetValue(AdvancedExtraContentProperty, value);
    }

    /// <summary>
    /// Forces both hex <see cref="TextBox"/>es' pending (uncommitted, <c>LostFocus</c>-bound) edits into
    /// the bound <c>PanelColorEditorViewModel</c> — same guard as the pre-WP-B code this replaces
    /// (<c>PanelCustomizeWindow.OnClosing</c>): only a genuinely dirty binding is pushed, so closing the
    /// window without ever touching either box is a no-op.
    /// </summary>
    public void CommitPendingEdits()
    {
        if (AccentHexBox.GetBindingExpression(TextBox.TextProperty) is { IsDirty: true } accentBinding)
            accentBinding.UpdateSource();

        if (TextHexBox.GetBindingExpression(TextBox.TextProperty) is { IsDirty: true } textBinding)
            textBinding.UpdateSource();
    }

    /// <summary>
    /// M-G (backlog 1.x wave 2 review): <see cref="AdvancedExtraContent"/> is now a genuine logical child
    /// of this control (see <see cref="OnAdvancedExtraContentChanged"/>) — previously it was reachable only
    /// through this <see cref="DependencyProperty"/>'s own value, never via <see cref="LogicalTreeHelper"/>,
    /// which made tooling/tests that walk the logical tree (e.g. to find every
    /// <see cref="System.Windows.Controls.RadioButton"/> a host owns) blind to it unless they knew to look
    /// inside this specific property too.
    /// </summary>
    protected override IEnumerator LogicalChildren =>
        AdvancedExtraContent is null ? base.LogicalChildren : EnumerateWithExtraContent(base.LogicalChildren, AdvancedExtraContent);

    private static IEnumerator EnumerateWithExtraContent(IEnumerator baseChildren, object extraContent)
    {
        while (baseChildren.MoveNext())
            yield return baseChildren.Current;

        yield return extraContent;
    }

    /// <summary>
    /// See <see cref="AdvancedExtraContentProperty"/>'s own remarks — keeps <see cref="LogicalChildren"/>
    /// (and therefore <see cref="LogicalTreeHelper"/>) in sync whenever a host swaps the content in or out.
    /// </summary>
    private static void OnAdvancedExtraContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var editor = (PanelColorEditor)d;

        if (e.OldValue is not null)
            editor.RemoveLogicalChild(e.OldValue);

        if (e.NewValue is not null)
            editor.AddLogicalChild(e.NewValue);
    }
}
