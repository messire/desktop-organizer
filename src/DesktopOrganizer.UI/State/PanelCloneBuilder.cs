using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.UI.State;

/// <summary>
/// Builds a fully independent clone of a <see cref="ChildWindowModel"/> (ADR-045, «Panel clone to page»)
/// through <see cref="PanelStateMapper.ToDto"/> → mutate the DTO → <see cref="PanelStateMapper.FromDto"/>,
/// instead of copying each property by hand — every field <see cref="PanelStateMapper"/> already knows how
/// to persist is copied automatically, today's and any future one, with no second "which fields does a
/// clone need" list to keep in sync with the mapper. <see cref="ChildWindowModel.FileItems"/> is the one
/// property this deliberately never touches: <see cref="PanelStateMapper"/> never maps it either (it is the
/// source's own runtime-only projection of <see cref="ChildWindowModel.FilePaths"/>), so the clone comes
/// back with a fresh, empty collection of its own — the panel populates it from its own
/// <see cref="ChildWindowModel.FilePaths"/> once created, exactly like any panel loaded from disk.
/// </summary>
internal static class PanelCloneBuilder
{
    /// <summary>
    /// Builds the clone. <see cref="ChildWindowModel.Id"/> is always a fresh <see cref="Guid"/>;
    /// <see cref="ChildWindowModel.PageId"/>, <see cref="ChildWindowModel.Title"/> and
    /// <see cref="ChildWindowModel.FilePaths"/> are set to the given values; every other persisted field
    /// (appearance, geometry, view/sort/filter, <see cref="ChildWindowModel.SourceFolderPath"/>, …) is
    /// copied verbatim from <paramref name="source"/>. <see cref="ChildWindowModel.IsVisible"/> and — for a
    /// same-page clone — <see cref="ChildWindowModel.Position"/> are the CALLER's responsibility to adjust
    /// afterwards (<c>WindowManager.ClonePanelToPage</c>): this method only ever produces a byte-for-byte
    /// independent copy of what <paramref name="source"/> looked like at the moment it is called.
    /// </summary>
    /// <param name="source">The panel to clone. Never mutated.</param>
    /// <param name="pageId">The clone's destination page — see <see cref="ChildWindowModel.PageId"/>.</param>
    /// <param name="title">The clone's resolved title — see <c>PanelCloneTitles.Resolve</c>.</param>
    /// <param name="filePaths">
    /// The clone's own, already-duplicated file path list (managed items copied into new per-item
    /// directories, everything else re-referenced as-is) — never the SAME list instance as
    /// <paramref name="source"/>'s own <see cref="ChildWindowModel.FilePaths"/>.
    /// </param>
    /// <returns>A brand-new <see cref="ChildWindowModel"/>, sharing no mutable reference with <paramref name="source"/>.</returns>
    public static ChildWindowModel Build(ChildWindowModel source, string pageId, string title, List<string> filePaths)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(filePaths);

        var dto = PanelStateMapper.ToDto(source);
        dto.Id = Guid.NewGuid();
        dto.PageId = pageId;
        dto.Title = title;
        dto.Content.FilePaths = filePaths;

        return PanelStateMapper.FromDto(dto);
    }
}
