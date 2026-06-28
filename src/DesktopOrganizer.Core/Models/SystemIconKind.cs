namespace DesktopOrganizer.Core.Models;

/// <summary>
/// Identifies a well-known system shell item shown in the system icons panel.
/// </summary>
public enum SystemIconKind
{
    /// <summary>"This PC" (Computer) shell folder.</summary>
    ThisPc,

    /// <summary>Control Panel shell folder.</summary>
    ControlPanel,

    /// <summary>Recycle Bin shell folder.</summary>
    RecycleBin,

    /// <summary>Network shell folder.</summary>
    Network,

    /// <summary>Current user's profile folder.</summary>
    UserFolder,
}
