# ADR-035: Panel/Page Deletion Goes to the Recycle Bin

Status: Accepted (2026-09-27) — amends ADR-010; amended 2026-09-27 by ADR-040

## Context

ADR-010 gave "close a panel" (keep managed files) and "delete a panel" (destroy managed files) separate,
explicit semantics, with a confirm dialog mitigating the surprise of the latter. That confirm dialog
mitigated *surprise*, not the underlying divergence from Stardock Fences' own behavior: in Fences, removing
a fence is purely a visual grouping operation — the icons it contained remain on the desktop, never
destroyed. `docs/ROADMAP.md`'s "Сквозные задачи" section carried this as an explicit, unresolved backlog
item since ADR-010 shipped: *"удалить панель" стирает managed-файлы мимо Корзины* — deleting a panel with
managed files was the one scenario in the whole application where user files were destroyed outside the
Recycle Bin, with no `Ctrl+Z`/"Restore" safety net.

The release-hardening 1.0 plan's package D closes exactly that backlog item, without touching Fences-style
"return files to the desktop" semantics (rejected direction, see Alternatives) — the chosen fix is narrower
and unconditional: never destroy content outside the Recycle Bin, full stop, for either a single panel or
an entire page.

## Alternatives Considered

- **"Delete panel" restores managed files to the desktop instead of recycling them** (the literal
  Fences-parity direction the ROADMAP backlog item floated as a "possible future direction"). Rejected for
  this package: a much larger behavior change (reintroducing files at an unpredictable desktop location,
  re-triggering desktop-icon layout) than "never destroy data outside the Recycle Bin" requires, and the
  ROADMAP item itself flagged it as needing separate discussion before implementation.
- **Keep the confirm dialog as the only safety net, add nothing else.** Rejected: a confirm dialog mitigates
  *surprise*, not *data loss* — a user who clicks through it (or a script/automation that skips it) still
  had files permanently destroyed with zero recovery path.

## Decision

**Never hard-delete user data.** There is no fallback to `File.Delete`/`Directory.Delete(recursive: true)`
of managed content anywhere in the removal path — the only thing ever deleted directly is an emptied,
now-orphaned per-item GUID wrapper directory (structure cleanup, never content). On any failure, the
removal **fails visibly**: the panel (or page) is **not** removed.

### `INativeApi.RecycleItems(string[] paths, IntPtr owner) → bool`

`SHFileOperation`/`FO_DELETE` with `FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING |
FOF_NOERRORUI`. `FOF_WANTNUKEWARNING` is deliberately kept: without it, an item too large for the Recycle
Bin would be silently, permanently deleted instead of asking — the shell's own warning dialog is the one
acceptable shell-owned UI surface this feature introduces. `owner = IntPtr.Zero`; must be called on the UI
(STA) thread, since the shell may show that dialog. The pre-existing `SendToRecycleBin` (used by
`SystemIconsPanel`) is untouched — different call site, different semantics, not shared.

### `IPanelItemStorageService.ReleaseToRecycleBin(IReadOnlyList<string> storedPaths) → RecycleOutcome`

Filters to `IsManaged` paths only (an unmanaged path is silently ignored, never touched), then makes
**one** batch shell call for all of them. What was actually recycled is decided **afterwards**, by checking
which paths still exist on disk — robust to a partially-completed shell operation (locked file, the user
declining the nuke-warning prompt, a path too long for `SHFileOperation`). Manifest entries and their
now-empty per-item GUID directories are cleaned up only for paths reported `Recycled`; every path reported
`Remaining` is left completely untouched, manifest included. `RecycleOutcome` is a `readonly record struct`
with `Recycled`/`Remaining`/`Cancelled`/`IsComplete`, and `default(RecycleOutcome)` is itself a valid empty,
non-cancelled outcome (a loose test double returning the default can never NRE a caller). A storage-root
guard (`IsStrictlyManagedItem`) rejects the storage root itself or a bare per-item GUID wrapper directory —
only ever recycles an actual item strictly below one — while still accepting a legacy flat-layout file
(pre-ADR-013 installs) directly under the storage root, so those remain recyclable rather than silently
excluded. The old `Release(string)` member is removed from the interface; its one remaining caller
(`ChildWindowViewModel.RemoveFileItem`'s `OriginalPathUnknown` legacy branch) now calls
`ReleaseToRecycleBin([path])` instead.

### All-or-kept semantics at the panel/page level

`IWindowManager.RemoveChildWindow(..., releaseManagedItems: true, ...)` now returns a `PanelRemovalResult`
(`Removed` | `Blocked(remaining)`): on `Blocked`, only the paths that genuinely left disk are removed from
the model/ViewModel, the window is **not** closed, and the debounced save behaves exactly as an ordinary
item removal would. `PageManagerService.RemovePage` collects every managed path across every panel on the
page, issues **one** recycle call for the whole page, and if anything is left over, **the entire page and
all its panels are kept** (only the genuinely-gone items are removed) with a message shown; only once
everything is confirmed gone does it proceed to remove the panels (`releaseManagedItems: false` — already
recycled) and then the page itself.

`IConfirmationService.Notify(string message, string title, MessageBoxImage icon)` (new member) surfaces the
failure: `Recycle_Failed_Message_Format` ("{0}" = count of items still on disk). Existing confirm-dialog
text (`Settings_Confirm_RemovePanel_ManagedFiles_Message`, `Page_Confirm_Remove_ManagedFiles_Message`) was
reworded to say files move to the Recycle Bin rather than being destroyed.

### Known limitation: restored items land under a new GUID, not their panel's original slot

Restoring a recycled item from the Recycle Bin (Explorer's own "Restore") puts it back at
`%LocalAppData%\DesktopOrganizer\PanelItems\<guid>\` — the exact per-item managed path it was recycled
from — not back onto any panel (the panel/page it belonged to is, by definition, already gone by the time
a restore happens). This is an accepted, documented residual: the file is not lost, but it is orphaned
relative to any panel UI until the user manually relocates it.

## Consequences

Positive:
- Closes the one remaining scenario in the application where user files were destroyed outside the
  Recycle Bin (the ROADMAP backlog item this ADR resolves).
- Fail-visibly is mechanically enforced, not just documented: a grep-style test asserts no
  `File.Delete`/`Directory.Delete(…, true)` call exists anywhere on a `Release*` removal path.
- Page-level removal is atomic in the sense that matters: either every managed file across the whole page
  is confirmed gone and the page disappears, or nothing does and the user sees why.

Negative:
- `FOF_WANTNUKEWARNING` can surface a shell dialog the user must dismiss (Yes/No) before a large item is
  ever removed from a panel — a small UX cost in exchange for never silently permanently deleting it.
- Paths beyond `MAX_PATH` are not supported by `SHFileOperation`'s underlying `\\?\`-unaware Recycle Bin
  operation and simply end up in `Remaining` — a visible, non-destructive failure, but still a failure a
  user must work around manually (documented in README "Known limitations").
- Restored items are orphaned relative to their original panel (see the known limitation above) — no UI
  currently re-associates them with anything.

## Manual verify (Win10/Win11)

Delete a panel with files and a folder → items land in the Recycle Bin → "Restore" returns them to
`PanelItems\<guid>\` (orphaned — expected, documented above); an item larger than the Recycle Bin's
configured size → shell dialog, "No" → panel stays; delete a page with 3 panels; a locked file → `Blocked`
outcome, message shown, panel/page kept.

## Related

- ADR-010 (Managed Storage Cleanup on Panel Removal) — amended: "delete" no longer means "destroy
  outside the Recycle Bin" under any circumstance; the close/delete split itself is unchanged.
- ADR-009 (Drag&Drop Move Semantics) — the managed-storage layout (`PanelItems\<guid>\`) this ADR recycles
  from and restores into is unchanged.
- `docs/ROADMAP.md` "Сквозные задачи" — the backlog item this ADR closes.

## Amendment (2026-09-27, release polish 1.0 — ADR-040)

[ADR-040](ADR-040-native-shell-context-menu.md) (same day, later in this release round) adds a **second**,
narrower Recycle Bin path: a managed item's own "Delete" verb in its native shell context menu, intercepted
and routed through the new `IPanelItemStorageService.RecycleManagedItem` — which restores the item to its
original location (or the desktop) **first**, via the same mechanism `Restore` already used (ADR-009), and
only recycles it **from there**. This closes the "Known limitation" section above (restored items land under
a new GUID, not their original slot) for that one specific path: an item deleted through its own shell menu
restores to a **visible** location on Explorer "Restore", not into `PanelItems\<guid>\`. The limitation as
originally documented — panel-/page-level deletion (`ReleaseToRecycleBin`, `PageManagerService.RemovePage`)
still recycles content from inside managed storage — remains open and is not addressed by this amendment;
`ReleaseToRecycleBin`/`RecycleOutcome`/the all-or-kept page semantics are otherwise unchanged.
