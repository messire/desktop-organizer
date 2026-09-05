namespace DesktopOrganizer.Core;

/// <summary>
/// Total-order comparer for panel items, used by the UI layer's <c>PanelSortMode.Name</c> to sort
/// the items a panel shows. See ADR-014 Decision 4.
/// </summary>
/// <remarks>
/// Three-level ordering, each level only breaking ties left unresolved by the previous one:
/// <list type="number">
/// <item>directories before files (<see cref="FileItem.IsDirectory"/>) — matches Explorer/desktop
/// "sort by name" behavior;</item>
/// <item><see cref="FileItem.DisplayName"/> (not <see cref="FileItem.Name"/>: a legacy
/// managed-storage item's <see cref="FileItem.Name"/> carries a GUID directory segment the user
/// never sees — see ADR-013 Decision 3 — and the user sorts what they see) via
/// <see cref="NaturalStringComparer.Instance"/>;</item>
/// <item><see cref="FileItem.Path"/>, <see cref="StringComparison.OrdinalIgnoreCase"/> — two panel
/// items can legally share the same <see cref="FileItem.DisplayName"/> (ADR-013 Risk R11: distinct
/// managed-storage GUID directories can each hold a same-named file), and without this tie-break
/// their relative order would be undefined and could visibly "jitter" between rebuilds of the same
/// set.</item>
/// </list>
/// </remarks>
public sealed class PanelItemSortComparer : IComparer<FileItem>
{
    /// <summary>
    /// The "sort by name" panel item order: see the type-level remarks for the exact three-level
    /// rule. Shared stateless instance — no per-call state, no per-sort allocation.
    /// </summary>
    public static readonly PanelItemSortComparer ByName = new();

    private PanelItemSortComparer()
    {
    }

    /// <inheritdoc />
    public int Compare(FileItem? x, FileItem? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        // Directories first: IsDirectory == true must sort BEFORE IsDirectory == false, which is
        // the opposite of the natural bool ordering (false < true) — so compare y.IsDirectory
        // against x.IsDirectory, not the other way around.
        var directoryCmp = y.IsDirectory.CompareTo(x.IsDirectory);
        if (directoryCmp != 0) return directoryCmp;

        var nameCmp = NaturalStringComparer.Instance.Compare(x.DisplayName, y.DisplayName);
        if (nameCmp != 0) return nameCmp;

        return string.Compare(x.Path, y.Path, StringComparison.OrdinalIgnoreCase);
    }
}
