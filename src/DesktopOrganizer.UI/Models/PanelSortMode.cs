namespace DesktopOrganizer.UI.Models;

/// <summary>
/// How a panel orders the items it shows. See ADR-014 Decision 1.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why <see cref="Manual"/> is <c>0</c>:</b> <see cref="ChildWindowModel"/> is serialized with
/// <c>System.Text.Json</c>, which encodes enums as their underlying numeric value by default (there
/// is no <c>JsonStringEnumConverter</c> registered for this type, unlike some other enums in this
/// codebase). A JSON document saved before this feature existed simply has no <c>SortMode</c>
/// property at all, and a missing property deserializes to the type's <see langword="default"/> —
/// <c>0</c>. Making <c>Manual = 0</c> therefore means every panel persisted before this feature keeps
/// showing its items in exactly the same order it always did, with zero migration code: the old,
/// undeclared "whatever order they were added in" behavior *is* <see cref="Manual"/>. Had
/// <see cref="Name"/> been the zero value instead, every existing panel would have silently
/// re-sorted itself the first time it loaded after an update — a surprising, unrequested behavior
/// change for users who never touched the new setting. This also matches Stardock Fences, where a
/// fence's default arrangement is the user's own manual layout, and sorting is an explicit opt-in.
/// </para>
/// </remarks>
public enum PanelSortMode
{
    /// <summary>
    /// Items keep whatever order they're stored in (<see cref="ChildWindowModel.FilePaths"/> /
    /// the underlying <c>_allItems</c> order) — i.e. the order the user last left them in, including
    /// via drag-to-reorder. The default so old panels are unaffected; see the type-level remarks.
    /// </summary>
    Manual = 0,

    /// <summary>
    /// Items are sorted alphabetically by <see cref="DesktopOrganizer.Core.Models.FileItem.DisplayName"/>,
    /// folders before files (<see cref="DesktopOrganizer.Core.Models.PanelItemSortComparer.ByName"/>). A non-mutating
    /// projection: switching back to <see cref="Manual"/> restores the prior manual order exactly.
    /// </summary>
    Name = 1,
}
