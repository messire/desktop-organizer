namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// One page's snapshot for the tray context menu's "Pages" submenu (backlog-1x-master-plan.md §2,
/// ADR-044). This layer has no visibility into <c>IPageManager</c>/<c>PageDescriptor</c> (they live in
/// DesktopOrganizer.UI, above this one in the layering — see
/// <see cref="ITrayIconManager.OnNextPageRequested"/>'s remarks) — the caller (<c>TrayPagesAdapter</c>)
/// is expected to translate its own page model into this record, already resolved to the effective,
/// localized display name (never a raw id/GUID — <c>PageDisplayNames.Resolve</c>) before handing it to
/// <see cref="ITrayIconManager.SetPages"/>.
/// </summary>
/// <param name="Id">The page's stable identifier — never shown to the user, only used to round-trip <see cref="ITrayIconManager.OnPageSelected"/>.</param>
/// <param name="DisplayName">The page's effective, already-localized display name.</param>
/// <param name="IsCurrent"><see langword="true"/> if this is the currently active page (rendered with a checkmark).</param>
public sealed record TrayPageEntry(string Id, string DisplayName, bool IsCurrent);
