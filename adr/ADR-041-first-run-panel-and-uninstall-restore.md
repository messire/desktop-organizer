# ADR-041: First-Run Panel and Uninstall Restore of Panel Items

**Статус:** Accepted (2026-09-27) — Amended 2026-10-09 (ADR-audit WP-A: a failed directory enumeration counts as an unrestored item — the storage root is kept, exit code 7). **Current consolidated release rules for managed items (incl. which restore policy applies where): [ADR-048](ADR-048-panel-item-lifecycle-release-rules.md).**

## Context

Two related gaps in the new-user/leaving-user experience, both about what happens at the edges of the
app's lifetime rather than during normal use:

1. **First run showed a blank desktop with nothing to click.** `README.md` had already promised "on first
   run it creates one empty panel", but nothing implemented it — a fresh install left the user staring at
   an unchanged desktop with no visible entry point into the app at all (the tray icon is the only other
   affordance).
2. **Uninstalling destroyed every file a user had ever dragged into a panel.** ADR-009's Move-semantics
   physically relocates dropped files into `%LocalAppData%\DesktopOrganizer\PanelItems\{guid}\`; uninstalling
   the app removes `%LocalAppData%\DesktopOrganizer` entirely (`[UninstallDelete]`/normal app-data cleanup)
   with no step that ever moved those files back out first. A user uninstalling the app to "clean up" would
   permanently lose the exact files they had dragged into a panel to *keep*.

Both are addressed independently (they touch install-time and uninstall-time respectively) but share the
same underlying principle: the app should never surprise a user by *inventing* state that was not really
there (first-run panel) nor by *silently destroying* state that was (uninstall restore) — the frozen
persistence contour (ADR-024, `CLAUDE.md`) is not touched by either.

## Decision

### First-run panel: gated on `StateLoadStatus.FirstRun` AND zero panels, not merely zero panels

`WindowManager.LoadWindowsState` calls `CreateFirstRunPanelIfApplicable` whenever the just-completed load
produced zero panels; that method only actually creates one when
`ShouldCreateFirstRunPanel(existingPanelCount, loadStatus)` — `existingPanelCount == 0 && loadStatus ==
StateLoadStatus.FirstRun` — holds. `StateLoadStatus.FirstRun` means no JSON store existed yet **and** legacy
`user.config` was empty/default: a genuine brand-new install, not any of the other ways a load can also
report zero panels:

| `StateLoadStatus` | Zero panels means | First-run panel? |
|---|---|---|
| `FirstRun` | Nothing configured yet — a real first launch | **Yes** |
| `Loaded` | The user deliberately deleted every panel from an existing document | No |
| `Quarantined` / `RecoveredFromBackup` | A corrupt or recovered document, not "nothing configured" | No |
| `ImportedFromLegacy` | A legacy `user.config` install, handled by its own import path | No |
| `NewerSchema` | A read-only session against a schema this build cannot write | No |

Inventing a panel on any status other than `FirstRun` would either silently override a user's deliberate
"remove every panel" choice, or add a fabricated panel on top of a document the app is not even able to
interpret/write back correctly. `ShouldCreateFirstRunPanel` is `internal static` and directly unit-tested,
mirroring `CreateEachIsolated`/`SyncEachIsolated`'s existing testability pattern — no real WPF `ChildWindow`
or DI graph needed to verify the gate itself.

**Mechanism**: goes through the exact same creation path as the "Add panel" toolbar/tray action —
`PanelDefaults.CreateNew` (localized default title, current page, appearance defaults) →
`PanelDefaults.ReconcileGridAlignedSpans` (self-consistent size) → `WindowManager.PlaceNewPanelOnFreeSlot`
(snapped, non-overlapping position on the primary monitor's work area — with no other panels yet, this is
always the top-left-most slot) → `CreateChildWindow`. No bespoke geometry or persistence logic. The
resulting panel is persisted through the ordinary debounced `SaveWindowsState` path, the same as every other
structural adjustment `LoadWindowsState` itself makes (e.g. grid-size normalization) — no synchronous flush
is needed, since the debounce timer only fires once the dispatcher starts pumping, by which point
`IApplicationStateService.ConsumersReady` is already `true`.

### `--restore-panel-items`: a one-shot, headless command-line switch, not a background/interactive feature

`Program.Main` checks for `--restore-panel-items` **before** the WinForms exception bridge, the culture
bootstrap, and the single-instance handshake — it needs none of them, and `RunRestorePanelItems`'s own
`try`/`catch` guarantees it never reaches `Main`'s outer, `MessageBox`-showing catch, which would otherwise
hang a headless `runhidden waituntilterminated` uninstall step with nobody present to dismiss a dialog. No
WPF `Application` is ever constructed on this path. It is wired into `installer/DesktopOrganizer.iss`'s
`[UninstallRun]` (`Filename: "{app}\DesktopOrganizer.UI.exe"; Parameters: "--restore-panel-items"; Flags:
runhidden waituntilterminated skipifdoesntexist; RunOnceId: "RestorePanelItems"`), which Inno Setup runs
*before* `[UninstallDelete]`/removal of installed files — and after `InitializeUninstall`'s `EnsureAppClosed`
(ADR-023 Addendum) has already made sure no instance of the app is still running, so the exe is free to
launch and exit again here.

**Exit codes** (`Program.Main`'s own remarks, alongside the pre-existing single-instance/
`--restore-state-backup` codes): `0` — completed, everything that could be moved was moved (including the
degenerate "nothing to restore" case); `6` — the storage lock could not be acquired (another running
instance holds it) — nothing was moved, a safe no-op; `7` — one or more items could not be moved and remain
in managed storage (every item that *could* be moved still was); `8` — an unexpected exception was caught
entirely inside `RunRestorePanelItems`'s own `try`/`catch` (never reaching `Main`'s outer handler).

### `PanelItemsUninstallRestorer.RestoreAll()` semantics

For every managed item under `PanelItems` (both the current per-item-GUID-directory layout and a legacy flat
file directly under the storage root, from a pre-ADR-013 install):

1. **Target resolution** (`ResolveTarget`): the item's recorded original location (from the manifest,
   ADR-009) **if** that directory still exists, is on a fixed local drive (`DriveType.Fixed` — never a
   network share, removable/optical drive, or anything `DriveInfo` cannot parse), and nothing already
   occupies the exact target path; otherwise the desktop, under an Explorer-style collision-free name
   (`name (2).ext`). An **orphan** (present on disk, absent from the manifest — no recorded original at all)
   always resolves to the desktop, the same as a target whose original location fails any of the checks
   above.
2. **Move**: same-volume is an atomic `File.Move`/`Directory.Move`; cross-volume is copy-then-verify-then-
   delete-source, the same discipline `PanelItemStorageService.MoveIn`/`Restore` already use. A read-only
   source that cannot be deleted after its copy has already been verified byte-for-byte is **not** counted
   as a failure — the copy is complete and correct, a warning is logged
   (`VerifiedCopySourceDeleteFailed`), and the item counts as **restored** (leaving a leftover, but no
   longer counted, copy under managed storage) rather than being duplicated on a re-run.
3. **Manifest temp leftovers** (`.manifest.json.tmp<guid>`, left behind by a crashed atomic manifest write —
   `PanelItemStorageService`'s own atomic-write temp suffix) are deleted outright, never treated as a legacy
   flat-layout panel item that would otherwise land on the desktop as meaningless clutter.
4. **Cleanup**: now-empty per-item GUID directories are deleted (structure only, never content) regardless
   of outcome; the entire `PanelItems` storage root itself is deleted **only when every single item was
   successfully restored** (`failed == 0`) — a partial failure leaves the root (and its still-unmoved
   items) in place rather than losing track of them. A directory whose content could not even be listed
   counts as a failure too (Amendment 2026-10-09 at the end of this document).
5. **Early exit, no folder creation on a clean install**: if the `PanelItems` folder does not exist at all
   (an install that never had a managed item), `RestoreAll` returns immediately with a "nothing to restore"
   result — it never creates the folder, the manifest, or the desktop directory as a side effect of running.

**What this restorer never touches**: `State`/`Logs` under `%LocalAppData%\DesktopOrganizer` (the frozen
persistence contour, ADR-024) — this is a `PanelItems`-only operation; the manifest file format itself is
unchanged (read tolerantly — a missing or corrupt manifest degrades every item on disk to an orphan rather
than aborting the whole restore); `application-state.json` is not read, written, or migrated by this path at
all.

## Consequences

Positive:
- A fresh install has a visible, working panel to interact with immediately, without inventing state on top
  of an existing (even if now-empty) configuration.
- Uninstalling the app no longer permanently destroys files a user moved into a panel — they reappear either
  at their original location or on the desktop, matching the one guarantee the app already makes for
  "removing a single item" (ADR-009 `Restore`) extended to "removing the whole app".
- The restore step is silent and headless for the common case (nothing to restore, or everything restored
  cleanly) — it does not add an interactive dialog to uninstall.

Negative / known limitations:
- An item whose original folder was itself deleted, is on a removable/network drive, or now collides with
  something else at the exact original path lands on the desktop instead — not necessarily where the user
  would expect it, though never lost.
- A partial failure (exit code 7) leaves `PanelItems` (and its manifest) in place with no further automatic
  retry — a user must be pointed at the diagnostics log to find out what remains and why.
- The first-run panel appears with the app's own localized default title/appearance, not tailored to
  anything about the specific machine — a cosmetic, not a functional, gap (see ROADMAP backlog: friendlier
  first-run title).

## Manual verify (Win10/Win11)

Fresh install (no prior `%LocalAppData%\DesktopOrganizer`) shows exactly one panel on first launch; deleting
every panel and restarting does **not** recreate one; drag a file and a folder into a panel, then uninstall
— both reappear at their original location; delete the original folder first, then uninstall — the item
lands on the desktop instead, with a collision-free name if something of the same name is already there; run
the uninstaller while the app is still running — `EnsureAppClosed` closes it first, then panel items are
restored, then files are removed; run `--restore-panel-items` twice in a row — the second run reports
"nothing to restore" and does not error.

## Related

- ADR-009 (Drag&Drop Move Semantics) — the managed-storage layout and manifest (`original → managed` path)
  this restorer reads in reverse and the collision-free naming pattern it reuses.
- ADR-023 Addendum (Single Instance and IPC — installer graceful close) — `EnsureAppClosed`, which the
  `[UninstallRun]` restore step relies on having already run.
- ADR-024 (Versioned JSON Persistence) — the frozen persistence contour (`State`/`Logs`,
  `application-state.json`) this ADR's restorer and first-run panel logic both leave untouched.
- ADR-033 (Crash Safety and an Always-On Application Log) — `Program.Main`'s existing exit-code convention,
  extended by this ADR with `6`/`7`/`8`.

## Amendment (2026-10-09, ADR-audit WP-A): a failed enumeration is an unrestored item

**Defect (audit finding).** A listing failure during enumeration (access denied, a broken reparse point, a path
error) was logged but not counted: a directory whose content could not be listed contributed nothing to `failed`, so
with the remaining items restored the run could report `failed == 0` and delete the whole `PanelItems` storage root
(`TryDeleteStorageRoot`) over files it had never seen — the opposite of this ADR's "never lose track of them" rule
(item 4 above).

**Decision.** `EnumerateManagedItems()` now returns the items **and** a failure count, and `RestoreAll` seeds
`failed` with it. Every unreadable location counts as one failed item: the storage root itself
(`EnumerateStorageRootFailed`), a top-level entry whose path is invalid (`EnumerateEntryPathInvalid`) and a
per-item GUID directory that cannot be listed (`EnumerateItemDirectoryFailed`), each logged at `Error` with the path.
Consequences, all following from the unchanged `failed == 0` rule: the storage root and the manifest are **kept**,
every item that *could* be listed is still restored, `PanelItemsRestoreResult.Success` is `false`, and
`--restore-panel-items` exits with code **7** (partial failure) instead of 0.

**Message.** On a partial failure the one-line CLI message (stderr; the diagnostics log has the details) reads
`Restored {restored} item(s); {failed} item(s) or location(s) could not be restored and remain under '{storageRoot}' —
see the diagnostics log.` — "or location(s)" because a failed enumeration is a location, not an item known to exist.

Unchanged: the manifest format, the persistence contour (`State`/`Logs`), `ResolveTarget` (policy U, ADR-048), the exit
codes `0/6/7/8` and the "no early deletion of the root" guard.
