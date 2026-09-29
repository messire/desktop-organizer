namespace DesktopOrganizer.UI.Models;

/// <summary>
/// The value a page's name binding resolves to (ADR-043, docs/PLANS/page-display-names.md §3.1/§5.3):
/// <see cref="Id"/> plus the page's stored display <see cref="Name"/>, exactly as
/// <see cref="PageDescriptor"/> carries them. A distinct (lightweight, value-equal) type from
/// <see cref="PageDescriptor"/> only so <c>PageItemViewModel.Display</c> can hand out a fresh value every
/// time the underlying stored name changes without implying the immutable-snapshot semantics
/// <see cref="PageDescriptor"/> carries elsewhere (the authoritative
/// <see cref="DesktopOrganizer.UI.Services.IPageManager"/>-facing type). Consumed by
/// <c>PageDisplayNameConverter</c>/<c>PageDisplayNames.Resolve</c> — the same resolution rule
/// <see cref="PageDescriptor"/> gets.
/// </summary>
/// <param name="Id">The page's stable identifier.</param>
/// <param name="Name">The page's stored display name, or <see langword="null"/> for an unnamed <c>"default"</c> page.</param>
public readonly record struct PageDisplayInfo(string Id, string? Name);
