namespace DesktopOrganizer.UI.Models;

/// <summary>
/// Immutable snapshot of one page (ADR-043, docs/PLANS/page-display-names.md §3.1): a stable
/// <see cref="Id"/> (never changes once the page is created — every <c>ChildWindowModel.PageId</c> and
/// <c>LayoutSnapshot</c> panel reference points at this) plus an optional stored display
/// <see cref="Name"/>. <see cref="Name"/> is <see langword="null"/> only for an unnamed
/// <c>"default"</c> page — every other page's effective display name is <see cref="Name"/> itself once
/// loaded (see <c>PagesStateMapper.FromDto</c>, which repairs a corrupted/hand-edited <c>null</c> name
/// on a non-default page to <see cref="Id"/> at load time, so this type's own consumers never have to
/// re-derive that fallback).
/// </summary>
/// <param name="Id">The page's stable identifier.</param>
/// <param name="Name">The page's stored display name, or <see langword="null"/> for an unnamed <c>"default"</c> page.</param>
public sealed record PageDescriptor(string Id, string? Name);
