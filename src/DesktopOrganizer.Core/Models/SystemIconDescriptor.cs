namespace DesktopOrganizer.Core;

/// <summary>
/// Maps a <see cref="SystemIconKind"/> to its known-folder identifier and default caption.
/// </summary>
/// <param name="Kind">The system item kind.</param>
/// <param name="KnownFolderId">
/// The Windows KNOWNFOLDERID GUID used by <c>SHGetKnownFolderIDList</c> to obtain a PIDL.
/// Note: these differ from the virtual-folder CLSIDs used in <c>::{CLSID}</c> shell paths.
/// </param>
/// <param name="DefaultCaption">
/// The English fallback caption. <c>Core</c> has no localization dependency (docs/PLANS/localization.md
/// §2.9/§3.2), so this is normally not the caption shown to the user — the displayed caption is looked
/// up by <c>DesktopOrganizer.UI.Services.SystemIconsPanelManager.ResolveIconLabel</c> from the RESX key
/// <c>SystemIcon_{Kind}</c>, falling back to this value only when that key is missing from the current
/// culture's RESX (i.e. a <see cref="Kind"/> the RESX files have not caught up with yet).
/// </param>
public sealed record SystemIconDescriptor(SystemIconKind Kind, Guid KnownFolderId, string DefaultCaption)
{
    /// <summary>
    /// Static table mapping every <see cref="SystemIconKind"/> to its descriptor.
    /// </summary>
    public static readonly IReadOnlyDictionary<SystemIconKind, SystemIconDescriptor> Table =
        new Dictionary<SystemIconKind, SystemIconDescriptor>
        {
            [SystemIconKind.ThisPc] = new(
                SystemIconKind.ThisPc,
                new Guid("0AC0837C-BBF8-452A-850D-79D08E667CA7"),
                "This PC"),
            [SystemIconKind.ControlPanel] = new(
                SystemIconKind.ControlPanel,
                new Guid("82A74AEB-AEB4-465C-A014-D097EE346D63"),
                "Control Panel"),
            [SystemIconKind.RecycleBin] = new(
                SystemIconKind.RecycleBin,
                new Guid("B7534046-3ECB-4C18-BE4E-64CD4CB7D6AC"),
                "Recycle Bin"),
            [SystemIconKind.Network] = new(
                SystemIconKind.Network,
                new Guid("D20BEEC4-5CA8-4905-AE3B-BF251EA09B53"),
                "Network"),
            [SystemIconKind.UserFolder] = new(
                SystemIconKind.UserFolder,
                new Guid("5E6C858F-0E22-4760-9AFE-EA3317B67173"),
                "User"),
        };
}
