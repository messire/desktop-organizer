using DesktopOrganizer.Attributes;

namespace DesktopOrganizer.Services.Enums;

/// <summary>
/// Flags used with SHGetFileInfo function to specify file information to retrieve.
/// </summary>
[Flags]
public enum FileFlag
{
    /// <summary>
    /// Retrieve the handle to the icon that represents file.
    /// </summary>
    [NativeName("SHGFI_ICON")]
    Icon = 0x000000100,

    /// <summary>
    /// Retrieve the display name for file.
    /// </summary>
    [NativeName("SHGFI_DISPLAYNAME")]
    DisplayName = 0x000000200,

    /// <summary>
    /// Retrieve the type name of file.
    /// </summary>
    [NativeName("SHGFI_TYPENAME")]
    TypeName = 0x000000400,

    /// <summary>
    /// Retrieve file attributes.
    /// </summary>
    [NativeName("SHGFI_ATTRIBUTES")]
    Attributes = 0x000000800,

    /// <summary>
    /// Retrieve the name of file containing the icon and the index of the icon within that file.
    /// </summary>
    [NativeName("SHGFI_ICONLOCATION")]
    IconLocation = 0x000001000,

    /// <summary>
    /// Retrieve the type of the executable file if applicable.
    /// </summary>
    [NativeName("SHGFI_EXETYPE")]
    ExeType = 0x000002000,

    /// <summary>
    /// Retrieve the index of the icon in the system image list.
    /// </summary>
    [NativeName("SHGFI_SYSICONINDEX")]
    SysIconIndex = 0x000004000,

    /// <summary>
    /// Add the link overlay to file's icon.
    /// </summary>
    [NativeName("SHGFI_LINKOVERLAY")]
    LinkOverlay = 0x000008000,

    /// <summary>
    /// Add the selected state to file's icon.
    /// </summary>
    [NativeName("SHGFI_SELECTED")]
    Selected = 0x000010000,

    /// <summary>
    /// Retrieve only the specified attributes.
    /// </summary>
    [NativeName("SHGFI_ATTR_SPECIFIED")]
    AttrSpecified = 0x000020000,

    /// <summary>
    /// Retrieve large icons.
    /// </summary>
    [NativeName("SHGFI_LARGEICON")]
    LargeIcon = 0x000000000,

    /// <summary>
    /// Retrieve small icons.
    /// </summary>
    [NativeName("SHGFI_SMALLICON")]
    SmallIcon = 0x000000001,

    /// <summary>
    /// Retrieve file's open icon.
    /// </summary>
    [NativeName("SHGFI_OPENICON")]
    OpenIcon = 0x000000002,

    /// <summary>
    /// Retrieve icons in the system-defined size.
    /// </summary>
    [NativeName("SHGFI_SHELLICONSIZE")]
    ShellIconSize = 0x000000004,

    /// <summary>
    /// Retrieve the item attributes without accessing file.
    /// </summary>
    [NativeName("SHGFI_USEFILEATTRIBUTES")]
    UseFileAttributes = 0x000000010,

    /// <summary>
    /// Indicates that pszPath is the address of an ITEMIDLIST structure rather than file path.
    /// </summary>
    [NativeName("SHGFI_PIDL")]
    Pidl = 0x000000008,
}
