# ADR-036: Drag-Out of Panel Items

Status: Accepted (2026-09-27) — amends ADR-009, ADR-014

## Context

ADR-009 gave panels Move-semantics for drag&drop **in** (dropping a file/shortcut into a panel moves it
into managed storage) and ADR-014 gave panels drag-to-reorder **within** a panel's own file list. Neither
covered the reverse direction: dragging an item **out** of a panel — onto the desktop, into Explorer, or
into a different panel. The ROADMAP backlog carried this explicitly (Фаза 9 "Bug-tickets осознанно вне
объёма", Risk R7/ADR-014): "drag-reorder works only within one panel; drop of a foreign reorder payload is
safely rejected" — i.e. cross-panel/out-of-app drag was a known, deliberate gap, not an oversight. Stardock
Fences supports dragging an icon back out to the desktop or another fence; this package closes that gap.

## Decision

### One OLE drag gesture, three simultaneous formats

The existing `ListReorderGestureController.DoDragDrop` call now builds (via the new, pure
`PanelItemDragDataFactory.Create`) a single `DataObject` carrying **three** formats at once: the panel's
own private drag-to-reorder format (unchanged — a drop back onto the same panel's file list still reorders
exactly as ADR-014 built it), `DataFormats.FileDrop` (so a different panel, Explorer, or the desktop all
recognize the drag as an ordinary file drop), and the shell's `"Preferred DropEffect"` clipboard format set
to `DragDropEffects.Move` — Explorer's own default for a bare `FileDrop` payload is *copy*; this format is
what makes it move the file instead, matching Fences' own drag-out behavior. `allowedEffects = Move`.
Semantics is determined entirely by **the target**, not by a separate "did it leave the panel" check:
own panel → reorder (unchanged), a different panel in this app → transfer, Explorer/desktop → the shell
performs an ordinary file move itself.

### Coordination: `IPanelItemDragOutSession`

A process-wide, UI-thread-only singleton (`UI/Services/PanelItemDragOutSession.cs`): `Begin(sourcePanelId,
path)` right before `DoDragDrop`; `TryClaim(targetPanelId, path) → bool`, called by any panel that accepts a
drop whose `FileDrop` payload matches the active gesture's path; `End() → DragOutOutcome` resolves and
clears the gesture, called exactly once right after `DoDragDrop` returns, regardless of the effect it
reported or whether it threw. At most one gesture is ever in flight — `DoDragDrop` is synchronous/modal on
the UI thread, so a second `Begin` can never race a still-open one in practice.

`DragOutOutcome` is one of:

- **`Reordered`** — the drag never left its own panel (a same-panel drop landed outside the file list
  itself; a drop ON the file list is handled entirely inside the existing reorder gesture and never reaches
  the session at all).
- **`TransferredTo(targetPanelId)`** — a *different* panel claimed the drag. The source panel removes its
  own entry via `ChildWindowViewModel.RemoveTransferredItem` — **without** calling `Restore`: the managed
  copy is shared storage (ADR-013's `PanelItems\<guid>\`), only the reference moved between panels' own
  `FilePaths` lists.
- **`External`** — nothing claimed the drag (dropped on Explorer/the desktop, cancelled with Esc, or
  copied with Ctrl-drag). The returned OLE effect is **not trusted**: Explorer's own optimized move
  frequently reports `DragDropEffects.None` even on a successful move, and an async/deferred shell drop can
  complete after `DoDragDrop` has already returned.

### Verifying an external move: `MovedOutVerifier`

Re-checks path existence at 0 / +500 ms / +1500 ms after the drag completed (0/500/2000 ms in total,
`IDebounceTimer`-driven). The moment any check finds the path still present, verification stops and the
item stays in the panel — covering copy (Ctrl-drag), a cancelled drag (Esc), or a drop that doesn't
actually move the file. Only once **every** scheduled check finds the path gone does the source panel call
`RemoveMovedOutItem` → `IPanelItemStorageService.ForgetMovedOut(path)` (manifest-entry and empty-GUID-
directory cleanup only — never deletes a file itself, the reverse of `ReleaseToRecycleBin`'s job; a no-op
if the path is unmanaged or still exists on disk — the caller's own external-move confirmation is the only
gate this method trusts). Each `Schedule` call gets its own `IDebounceTimer` instance (not a single shared
one reused across overlapping gestures) so that dragging a second item out in quick succession can never
starve the first item's still-pending delayed check by re-arming a shared timer out from under it.

### Cross-panel transfer never re-invokes `MoveIn`

`AddDroppedFiles`, for a path that is already `IsManaged`, does not call `MoveIn` again (managed storage is
shared, ADR-013) — it adds a reference and calls `TryClaim`. A managed path arriving **without** an active
session (e.g. dragged directly from Explorer while browsing into `PanelItems\` itself) is rejected outright
— accepting it would create a duplicate reference to the same managed file from two panels' lists.

### Read-only sources are excluded

`PanelItemDragDataFactory.Create` returns `null` (aborting the drag before `DoDragDrop` is ever called) for
a Folder Portal item (ADR-036 DoD requirement: drag-out is impossible for a read-only/mirrored source) and
during inline rename (the existing rename gate, unchanged). Selection stays single-item — no multi-select
drag-out in this iteration.

## Consequences

Positive:
- Closes the Фаза 9 backlog gap: an item now really can leave its panel to the desktop, Explorer, or
  another panel, with the same Move semantics Fences uses.
- The three-format single-gesture design means reorder-within-a-panel required zero behavioral change —
  confirmed by regression tests on `ListReorderGestureController`.
- The unreliable OLE drop-effect problem (Explorer's optimized move reporting `None` on success) is closed
  by observation (existence re-check), not by trusting a value the shell itself does not guarantee.
- Persistence is untouched: the only write is the same debounced `FilesChanged` save path "Remove from
  panel" already used — no new schema, no new manifest shape.

Negative:
- Panels are `WS_EX_NOACTIVATE` children (embedded mode) or top-level (package F's mode T) — whether OLE
  drag genuinely works dropping onto `SHELLDLL_DefView`/another such window in every shell layout is only
  confirmed by manual verification, not by a unit test.
- A cross-volume drop onto Explorer/the desktop is the shell's own copy+delete, not a single atomic move —
  the existence-recheck window (2 seconds total) is a heuristic tuned against observed behavior, not a
  guarantee for an arbitrarily slow external drive.
- `ForgetMovedOut`/`MovedOutVerifier` add a second "is this item actually gone" code path alongside
  `ReleaseToRecycleBin`'s own existence recheck (ADR-035) — similar in shape, not shared, since one confirms
  a shell-initiated deletion and the other a shell-initiated move.

## Manual verify (Win10/Win11)

A file and a folder → desktop, → Explorer (same disk, different disk, network share), → a different panel,
→ the same panel (empty area / reorder); Esc mid-drag; Ctrl-drag (copy — item stays); a non-managed item
(a plain reference to the original); a Folder Portal item (drag-out refused).

## Related

- ADR-009 (Drag&Drop Move Semantics) — the managed-storage Move contract this ADR's cross-panel transfer
  and external-move confirmation both build on without changing.
- ADR-014 (Panel Item Sorting and Manual Order) — the drag-to-reorder gesture this ADR's three-format
  `DataObject` is layered onto unchanged; closes Risk R7 from that ADR's plan.
- ADR-035 (Panel Deletion → Recycle Bin) — the sibling "confirm the file is actually gone before touching
  the manifest" pattern, applied to a different trigger (drag-out vs. explicit removal).
