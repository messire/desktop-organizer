namespace DesktopOrganizer.Services.Enums;

/// <summary>
/// File access flags and attributes, used for file handling operations.
/// </summary>
[Flags]
[CLSCompliant(false)]
internal enum FileAttribute : uint
{
    /// <summary>
    /// File is read only. Applications can read file but cannot write to or delete it.
    /// </summary>
    ReadOnly = 0x00000001,

    /// <summary>
    /// File is hidden and not included in an ordinary directory listing.
    /// </summary>
    Hidden = 0x00000002,

    /// <summary>
    /// File is part of or used exclusively by an operating system.
    /// </summary>
    System = 0x00000004,

    /// <summary>
    /// A file or directory that the operating system uses directory.
    /// </summary>
    Directory = 0x00000010,

    /// <summary>
    /// A file that is an archive file. Applications typically use this attribute to mark files for backup or removal.
    /// </summary>
    Archive = 0x00000020,

    /// <summary>
    /// A file that does not have other attributes set. This attribute is valid only when used alone.
    /// </summary>
    Normal = 0x00000080,

    /// <summary>
    /// A file that is temporary file. File systems attempt to keep all the data in memory for quick access.
    /// </summary>
    Temporary = 0x00000100,

    /// <summary>
    /// A file that is sparse file.
    /// </summary>
    SparseFile = 0x00000200,

    /// <summary>
    /// A file or directory that has an associated reparse point, or file that is symbolic link.
    /// </summary>
    ReparsePoint = 0x00000400,

    /// <summary>
    /// A file or directory that is compressed.
    /// </summary>
    Compressed = 0x00000800,

    /// <summary>
    /// The data of file is not immediately available. Applications can use this attribute to skip offline files.
    /// </summary>
    Offline = 0x00001000,

    /// <summary>
    /// File or directory is not indexed by the content indexing service.
    /// </summary>
    NotContentIndexed = 0x00002000,

    /// <summary>
    /// A file or directory that is encrypted.
    /// </summary>
    Encrypted = 0x00004000,

    /// <summary>
    /// File is being opened or created for backup or restore operation.
    /// </summary>
    BackupSemantics = 0x02000000,

    /// <summary>
    /// Access will occur according to sequential access patterns.
    /// </summary>
    SequentialScan = 0x08000000,

    /// <summary>
    /// Access is intended to be random.
    /// </summary>
    RandomAccess = 0x10000000,

    /// <summary>
    /// File is automatically deleted when it is no longer in use.
    /// </summary>
    DeleteOnClose = 0x04000000,

    /// <summary>
    /// File is being opened with no system caching for data reads and writes.
    /// </summary>
    NoBuffering = 0x20000000,
}
