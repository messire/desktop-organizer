using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.UI.Services;

/// <summary>
/// Opens the per-panel "Customize panel…" dialog (docs/prompts/settings-window-redesign.md, D-3).
/// The dialog is modeless, one per application, and edits the given <see cref="ChildWindowModel"/>
/// live — it is the single source of truth shared with the desktop panel, so no OK/Cancel step exists.
/// </summary>
public interface IPanelCustomizeService
{
    /// <summary>
    /// Shows the dialog for <paramref name="model"/>. If the dialog is already open for another panel,
    /// it is re-targeted to <paramref name="model"/> and brought to the foreground.
    /// </summary>
    /// <param name="model">The panel to customize.</param>
    void Show(ChildWindowModel model);
}
