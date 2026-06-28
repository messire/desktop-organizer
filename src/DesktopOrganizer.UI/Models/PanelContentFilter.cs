namespace DesktopOrganizer.UI.Models;

/// <summary>
/// Controls which file items a panel displays.
/// </summary>
public enum PanelContentFilter
{
    /// <summary>
    /// Show every item (default).
    /// </summary>
    ShowAll,

    /// <summary>
    /// Hide every item — panel appears empty.
    /// </summary>
    HideAll,

    /// <summary>
    /// Hide <c>.lnk</c> shortcut files; show everything else.
    /// </summary>
    HideShortcuts,

    /// <summary>
    /// Show only directories.
    /// </summary>
    FoldersOnly,
}
