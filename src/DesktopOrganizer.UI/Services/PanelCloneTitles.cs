using DesktopOrganizer.Services.Abstractions;

namespace DesktopOrganizer.UI.Services;

/// <summary>
/// Resolves the title a panel clone gets (ADR-045, «Panel clone to page»): the SAME title as the source
/// when nothing on the destination page already uses it, otherwise the Explorer-style
/// <c>"{0} — copy"</c>/<c>"{0} — copy (2)"</c> chain — localized once, at the moment the clone is created
/// (ADR-032 D-5: a resolved title is then just data, never re-localized later).
/// </summary>
internal static class PanelCloneTitles
{
    /// <summary>
    /// Resolves the clone's title. Comparison against <paramref name="titlesOnTargetPage"/> is trimmed and
    /// case-insensitive (<see cref="StringComparer.OrdinalIgnoreCase"/>), so <c>"Notes"</c> and
    /// <c>"notes "</c> are treated as the same title. For a clone onto the SAME page as the source, the
    /// source's own title is always among <paramref name="titlesOnTargetPage"/>, so a collision (and the
    /// resulting <c>"— copy"</c> suffix) is guaranteed there.
    /// </summary>
    /// <param name="sourceTitle">The source panel's title.</param>
    /// <param name="titlesOnTargetPage">Every panel title already on the clone's destination page.</param>
    /// <param name="localization">Formats <c>Panel_CopyTitle_Format</c>/<c>Panel_CopyTitle_Numbered_Format</c> in the current UI culture.</param>
    /// <returns>The resolved title.</returns>
    public static string Resolve(string sourceTitle, IReadOnlyCollection<string> titlesOnTargetPage, ILocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(sourceTitle);
        ArgumentNullException.ThrowIfNull(titlesOnTargetPage);
        ArgumentNullException.ThrowIfNull(localization);

        var existing = new HashSet<string>(
            titlesOnTargetPage.Select(title => (title ?? string.Empty).Trim()),
            StringComparer.OrdinalIgnoreCase);

        if (!existing.Contains(sourceTitle.Trim())) return sourceTitle;

        var copyTitle = localization.Format("Panel_CopyTitle_Format", sourceTitle);
        if (!existing.Contains(copyTitle.Trim())) return copyTitle;

        for (var n = 2; ; n++)
        {
            var numberedTitle = localization.Format("Panel_CopyTitle_Numbered_Format", sourceTitle, n);
            if (!existing.Contains(numberedTitle.Trim())) return numberedTitle;
        }
    }
}
