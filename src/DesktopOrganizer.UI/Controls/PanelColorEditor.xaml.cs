using System.Windows;
using System.Windows.Controls;

namespace DesktopOrganizer.UI.Controls;

/// <summary>
/// Shared accent/text colour editor (ADR-042) — a Mode (Light/Dark) segmented control, 12 circular preset
/// swatches plus a "Custom…" swatch, and a collapsed-by-default Advanced card (raw hex <see cref="TextBox"/>es
/// + "Reset to defaults") — used by both <c>PanelCustomizeWindow</c>'s Appearance section and Settings'
/// "New panel defaults" section. <see cref="FrameworkElement.DataContext"/> is a
/// <c>PanelColorEditorViewModel</c>, set by the consuming window/section.
/// </summary>
public partial class PanelColorEditor : UserControl
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PanelColorEditor"/> class.
    /// </summary>
    public PanelColorEditor() => InitializeComponent();

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
}
