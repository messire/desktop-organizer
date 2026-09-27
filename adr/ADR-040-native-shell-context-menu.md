# ADR-040: Native Shell Context Menu for Panel Items

Status: Accepted (2026-09-27) — amends ADR-009, ADR-010

## Context

A panel item's right-click menu was a bespoke WPF `ContextMenu` (`FileContextMenu`,
`Resources/Templates.xaml`): Open · Open containing folder · Properties · Remove from panel. It could not
show "Open with", "Send to", third-party shell extensions (7-Zip, antivirus scan, cloud-storage context
items), or any of the other entries Explorer's own menu offers — the exact opposite of Fences, which shows
the real shell menu for every icon. `SystemIconsPanel` already had a working native `IContextMenu` COM
pipeline (ADR-007/ADR-008); panel items had never been wired to it.

Separately, ADR-036 (drag-out) had already made "remove an item from a panel" a **drag** gesture — matching
Fences, where a fence's own context menu never listed an item-removal command either. Adopting the real
shell menu removes the last WPF-side context-menu command (`Panel_FileMenu_RemoveFromPanel_Tooltip`)
entirely; it was already redundant with drag-out and is not reintroduced as a shell verb.

## Decision

### Hidden helper window, foreground pattern, message forwarding — shared with `SystemIconsPanel`

`NativeApi.ShowShellContextMenuForPath(path, ownerHwnd, screenX, screenY, interceptDelete)` shares its
tracking/invocation core (`ShowShellContextMenuCore`) with the pre-existing `ShowShellContextMenu(pidl, ...)`
used by `SystemIconsPanel`: both route through a single hidden helper window (`EnsureShellMenuHost`), made
foreground before `TrackPopupMenuEx` and nudged with a `WM_NULL` afterward — the documented KB 135788
pattern, without which a menu owned by a non-foreground window never dismisses on an outside click. The
panel window itself is **never** made foreground or activated (it keeps `WS_EX_NOACTIVATE` and its Z-order
untouched) — only the invisible helper window is. While tracking, `ShellMenuHostWndProc` forwards the
owner-draw/popup-init messages to the menu's `IContextMenu3` (preferred) or `IContextMenu2` — whichever the
handler implements — which is what fills lazily-populated submenus and lets owner-drawn shell-extension
items render. A re-entrancy guard (`_shellMenuActive`) refuses to nest a second shell menu into the first
one's still-open modal loop (a queued dispatcher operation could otherwise race a live `TrackPopupMenuEx`
call). `NativeApi` is now `IDisposable`: `Dispose()` destroys the helper window and unregisters its window
class — the same hidden-window lifetime discipline the rest of the app already applies to its other native
resources.

The invoked command goes through `CMINVOKECOMMANDINFOEX` (`InvokeShellCommand`) rather than the older,
narrower `CMINVOKECOMMANDINFO`, giving Unicode verb/parameter fields and the menu's own screen position to
shell extensions that need it (e.g. a "Send to" submenu item that shows its own follow-up dialog anchored to
the click point).

### `IPanelItemDragOutSession`'s helper-window exclusion extends to virtual-desktop foreground lookup

`VirtualDesktopService`'s "which desktop does the foreground window belong to" probe now excludes the shell
menu's own hidden helper window (`INativeApi.IsOwnHelperWindow` — a pure handle comparison, no Win32 call):
right after a native shell context menu closes, the OS foreground window can transiently be this process's
own never-shown helper window, whose virtual-desktop id (if `GetWindowDesktopId` even answers for it) can be
stale — a window never surfaced to the user has no meaningful desktop of its own to report.

### Rename verb → inline F2 editor; Delete verb on a managed item → restore-then-recycle

`ShowShellContextMenuForPath` never invokes the shell's own "Rename" or (for a managed item) "Delete" verb —
both are *intercepted*: the canonical verb string is read via `GetCommandString` before dispatch, and a
match on `"rename"`/`"delete"` short-circuits to `ShellContextMenuOutcome.RenameRequested`/`DeleteRequested`
instead of calling `InvokeCommand`.

- **Rename** always starts this panel's own inline editor (`ChildWindowViewModel.BeginRename` — the same one
  `F2` starts, ADR-013) instead of letting the shell show its own in-place edit box inside a WPF `ListBox`
  item, which does not host a native rename edit control cleanly.
- **Delete**, for an **app-managed** item only (`interceptDelete: true`, i.e.
  `!IsPortal && IPanelItemStorageService.IsManaged(item.Path)`), calls the new
  `IPanelItemStorageService.RecycleManagedItem(storedPath)`: the item is first **restored** to its original
  location (the exact mechanism `Restore` already used, ADR-009) and only then recycled **from there**
  (`INativeApi.RecycleItems`, ADR-035's `FOF_ALLOWUNDO | FOF_WANTNUKEWARNING`) — never recycled from inside
  the hidden `PanelItems\{guid}\` folder. This is why Explorer's "Restore" on a deleted managed item lands it
  back somewhere visible (its original folder, or the desktop if that folder is gone) instead of inside the
  app's own hidden storage, closing the exact residual ADR-035 documented as a known limitation for this
  specific path. A cancelled/declined recycle (the shell's nuke-warning dialog answered "No", a locked file)
  re-adopts the restored file back into managed storage (`ManagedRecycleKind.Kept`) and the item simply
  follows it to its (possibly renamed, collision-avoided) new managed path — nothing is lost either way. A
  non-managed item's Delete is **not** intercepted: the shell deletes it exactly as it would in Explorer.
  This is also why "Убрать из панели"/"Remove from panel" (ADR-009 §Решение 4, ADR-010) as a **menu command**
  no longer exists in the app at all: item removal is now either a drag-out (ADR-036) or, for a managed item,
  the shell's own Delete verb via this interception.

### Reconcile after any other invoked command: pre-invoke snapshot, rename/delete detection, sibling adoption

Any command other than the two intercepted verbs (Cut, Copy, "Create shortcut", "Send to > Compressed
folder", a third-party "Scan with...", an archiver) acts directly on the file — for a managed item, on its
copy under `PanelItems\{guid}\`. Before the menu is shown, the item's per-item directory is snapshotted
(`IPanelItemStorageService.GetItemDirectoryEntries`) when it is a managed item; after the menu returns with
`ShellContextMenuOutcome.Invoked`, `ChildWindowViewModel.ReconcileAfterShellCommand(target, entriesBefore)`
compares the live state to that snapshot:

- The target still exists, with one or more new entries next to it → the new entries are **adopted** as new
  panel items (`IPanelItemStorageService.ReconcileExternalChange`) — e.g. "Create shortcut" or an archiver
  writing a `.zip` beside the original.
- The target vanished, with exactly one new entry next to it → treated as a **rename** and relocated in
  place via the same `BaseViewModel.TryRenameItem` path the inline editor uses.
- The target vanished with nothing new → treated as **deleted or moved away** and leaves the panel.
- Every *other* item in the panel whose path no longer exists gets the same snapshot-less reconcile (only
  "completely gone" is acted on) — a Folder Portal skips this whole reconcile (its watcher mirrors the real
  folder) but still gets the plain existence refresh.

**Sibling adoption keeps looking for up to 40 liveness ticks (`SiblingAdoptionWatchTicks`, roughly the
existing ~2-minute liveness poll horizon) after the command returns** (`ProcessPendingSiblingAdoption`),
because an archiver or a background shell extension can still be writing the new entry after
`InvokeCommand` itself has already returned — a directory or a file still being written is not adopted until
it settles. The watch ends when its tick budget runs out, a new shell command supersedes it, or the tracked
item itself left the panel or vanished (checked *without* the stale snapshot, so an entry created afterward
is never mistaken for the item that disappeared). **Cut→Paste** across two of the app's own dialogs, or
between the panel and Explorer, is picked up the same way: the same liveness-tick reconcile that detects a
rename also detects "vanished, no sibling created" for a Cut source, and — for a Paste target reached some
other way — the ordinary existence refresh.

## Known Limitation

An entry still being written when the sibling-adoption watch's tick budget expires (40 ticks after the
triggering command) stays inside the hidden per-item directory, invisible to the panel, until the *next*
shell command run on the same item happens to pick it up via a fresh snapshot comparison. Right-clicking an
item whose file has already vanished (e.g. deleted from Explorer behind the panel's back, before the next
liveness tick catches up) shows no menu at all — `IFileLauncher.ShowContextMenu` degrades to
`ShellContextMenuOutcome.NotShown` for a path that no longer exists, rather than the empty/errored menu
Explorer itself would show for the same race.

## Consequences

Positive:
- Panel items get the exact same shell menu Explorer shows for the same file — "Open with", "Send to",
  every installed shell extension — instead of four bespoke, hand-maintained commands.
- Fences parity on removal semantics: neither a fence context menu nor this app's own menu lists an
  item-removal command any more; removal is a drag gesture (ADR-036) or, for anything the shell itself
  deletes, the shell's own Delete (fail-safe to the Recycle Bin for a managed item, per ADR-035).
- A managed item recycled via the shell menu restores to a **visible** location on Explorer "Restore",
  closing the ADR-035 known limitation for this specific path (it remains open for panel-/page-level
  deletion, which still recycles from inside `PanelItems\{guid}\`).

Negative:
- The known limitation above (a slow-writing sibling after the watch expires; no menu on an already-vanished
  item) is a real, if narrow, UX gap versus Explorer's own always-available menu.
- Every stage of the native pipeline (`BindToParent`, `GetUIObjectOf`, `QueryContextMenu`,
  `TrackPopupMenu`, `GetCommandString`, `InvokeCommand`) can fail against a broken/virtual shell item or a
  faulty third-party shell extension; every failure is logged as `ShellContextMenuFailed` with a closed-set
  `stage` token, an extension, and an HRESULT (never a path) and degrades to
  `ShellContextMenuOutcome.NotShown` rather than surfacing to the user or crashing the panel.

## Manual verify (Win10/Win11)

Right-click a shortcut/file/folder in a panel shows the real shell menu including "Open with"/"Send to" and
any installed third-party extensions; Shift+right-click shows extended verbs; choosing "Rename" starts the
panel's own inline editor, not a native in-place edit box; "Delete" on a managed item recycles it and
Explorer's Recycle Bin "Restore" puts it back at a visible location; declining the Recycle Bin's oversized-
item warning keeps the item in the panel; "Create shortcut"/an archiver run from the menu shows the new
sibling as a panel item once it settles; Cut an item then Paste it elsewhere removes it from the panel; the
panel never gains keyboard focus or comes to the foreground while the menu is open or after it closes;
"Show desktop icons"/virtual-desktop tracking is unaffected right after closing a panel-item context menu.

## Related

- ADR-007/ADR-008 (System Icons Panel / Shell COM STA threading) — the pre-existing native `IContextMenu`
  pipeline this ADR extends to panel items rather than duplicating.
- ADR-009 (Drag&Drop Move Semantics) — "Убрать из панели"/"Remove from panel" as a menu command, superseded
  by this ADR (removal is now drag-out or the shell's own Delete); `Restore`, which `RecycleManagedItem`
  reuses to bring a managed item back to a visible location before recycling it.
- ADR-010 (Managed Storage Cleanup on Panel Removal) — its "Убрать из панели" references describe the same
  now-removed menu command; panel-/page-level deletion semantics themselves are unchanged.
- ADR-013 (Panel Item Selection and Rename) — `BeginRename`/inline editor, reused verbatim by the
  intercepted shell "Rename" verb.
- ADR-035 (Panel/Page Deletion → Recycle Bin) — `INativeApi.RecycleItems`, reused by
  `RecycleManagedItem`; this ADR closes its "restored items are orphaned" limitation for the single-item
  shell-menu Delete path specifically.
- ADR-036 (Drag-Out of Panel Items) — now the only way to remove an item from a panel without deleting it.
