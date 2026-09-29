# Versioned JSON Store — Rollback (ADR-024 §7)

Canonical artifact for the Phase 4 (`docs/PLANS/phase-4-versioned-json-store.md`) DoD item "rollback
to a previous app release is documented". Covers what happens when an older DesktopOrganizer build
runs against state written by a newer one — whether that is a real installer rollback or just running
two builds side by side during development.

## Why rollback is possible at all

Three facts about the install/uninstall footprint, all true before this phase and unchanged by it:

1. `installer/DesktopOrganizer.iss:88-91` deletes only `{app}` (the program files directory) on
   uninstall. `%LOCALAPPDATA%\DesktopOrganizer\` — which holds `State\` (this phase's JSON store),
   `PanelItems\`, and `Logs\` — is never touched by the installer, on either upgrade or uninstall.
2. `user.config` (the legacy `.NET` user-scope settings file the four old structural writers used,
   ADR-024 §0.1) lives at a path keyed by `Directory.Build.props:15`'s `<AssemblyVersion>1.0.0.0</AssemblyVersion>`,
   which is pinned and does not change between releases. The path is therefore stable across every
   install/uninstall/reinstall cycle, and legacy data is never orphaned by a version bump.
3. Legacy structural data is frozen, not deleted, once the JSON store exists (ADR-024 D-10) — the four
   legacy `user.config` keys (`ChildWindowsState`, `Pages`, `CurrentPage`, `LayoutSnapshots`,
   `SystemIconsPanel`) are read once, at the moment of first migration, and never written to again by
   any build that knows about the JSON store.

Put together: every artifact a rollback could need — the old `user.config`, and the versioned JSON
document itself — survives an uninstall/reinstall of any release. The three scenarios below are the
only ways an older build can end up running against this phase's on-disk state.

## Scenario A — roll back to a pre-JSON release (0.3.x)

**What happens.** Install an old (pre-Phase-4) build over/after a newer one. The old build has no
concept of `State\application-state.json` at all — it reads `user.config` directly, exactly as it
always did. Since the JSON store was written *from* a JSON-ified copy of `user.config`'s last state at
migration time (and `user.config` itself was never touched afterward, per D-10 above), the old build
sees the layout exactly as it was **at the moment of the original migration**.

**What is/isn't lost.** Nothing is deleted. Any panel/page/snapshot/system-icons-panel change made
*after* migration — i.e. every session that ran on a JSON-store build — lives only in
`State\application-state.json` and is invisible to the old build. It is not gone: reinstalling a
JSON-store-aware release picks it straight back up, because that file was never touched by the old
build either (it has no code path that writes to `State\`). This is a rollback **in time**, not a data
loss — but it must be named explicitly in release notes, since a user restoring an old version and
then finding their panels "reset" to an earlier state is exactly the kind of thing that looks like a
bug if it isn't explained.

**Why not dual-write to close this gap?** Considered and rejected (ADR-024 D-10). Keeping `user.config`
current for the length of a whole deprecation release would put `Settings.Default.Save()` (which
rewrites the *entire* XML file) back on every hot save path — precisely the cost the debounce
discipline (`SaveScheduler`, ADR-012) exists to avoid — and would reintroduce two independent sources
of truth, which is the defect this phase removes in the first place.

## Scenario B — roll back to a release that knows JSON, but an older schema

**What happens.** Install a release whose build understands the JSON store format but only up to some
schema version below `StateEnvelope.CurrentSchemaVersion` (currently 3). This is the case the
read/recovery ladder (ADR-024 D-7) was built to make safe without any user action:

1. The older build's reader parses `State\application-state.json`, reads `schemaVersion`, and finds it
   is *greater* than the schema version that build understands.
2. Per D-7 rung 7 (`StateLoadStatus.NewerSchema`), it **never** migrates the document downward and
   **never** writes to it. `IApplicationStateStore.IsWritable` is forced `false` for the remainder of
   that process — every subsequent `Save()` call returns `StateSaveResult.SkippedReadOnly` without
   touching disk.
3. That session runs with an empty in-memory structural state (no panels/pages/snapshots) — safe, but
   not useful for actually using the app on that particular build.

**What is/isn't lost.** Nothing. The on-disk file is byte-for-byte untouched for the entire session.
Reinstalling the newer release picks the file straight back up, unmigrated and unmodified, and
everything the user had is exactly where it was left.

**Why this trade-off (blank session, not "try to read what we can")?** Guessing at a newer schema's
shape from an older build is exactly the "deserialize-and-pray" failure mode ADR-024 D-1 exists to
rule out — a renamed or restructured field could be silently misread rather than cleanly rejected.
"Don't touch it, don't guess" is the only choice that can never corrupt state.

**This rule ships from the very first release that has schema versioning at all** — it cannot be
added retroactively once an older build without it is already out in the field, because that build's
reader has no way to learn the new rule after the fact. It is baked into `JsonApplicationStateStore.Load`
from commit C2 onward, before any schema beyond v1 exists.

**Concrete instance: v6 → v5 (ADR-043, page display names).** The first release with schema v6 (pages
gain a separate display `name` next to their stable `id`) is the first concrete instance of this scenario
in the field. Nothing above is special-cased for it — the same rung-7 read-only session applies verbatim.
Two things specific to this particular step are worth naming for anyone reading this file to recover from
it: (1) the pre-upgrade snapshot this step writes is named `application-state.premigration-v5.json` — by
the *source* schema version (5), not the target (6), matching every other step's naming (see
`StateStorePaths.PreMigrationBackupPath`); (2) if a stale file with that exact name is already present
from an earlier, unrelated incident, `WritePreMigrationBackupIfMissing`'s existing `File.Exists` guard
skips writing a fresh one — the recovery snapshot on disk would then be older than the actual state right
before this upgrade. ADR-043 documents this as a known, accepted risk (R-2), closed by a manual step
(rename or delete the stale file before upgrading), not by a code change to this mechanism.

## Scenario C — manual recovery: `--restore-state-backup`

Scenarios A and B are both automatic — no user action is required, and both are safe by construction.
Scenario C is the deliberate, manual escape hatch for the case neither of those covers: the **current**
primary document itself is corrupt (or otherwise undesirable — e.g. it was quarantined by the normal
recovery ladder, D-7 rung 4, and the user wants to try a specific quarantined snapshot back) and the
automatic ladder inside a normal run already gave up.

**Usage.** `DesktopOrganizer.exe --restore-state-backup`, run from a terminal (the outcome is also
shown in a message box, in case it wasn't). Handled by `Program.Main` entirely on its own, before the
single-instance handshake and before the normal `ApplicationStateService.Load()` startup path ever run
— no `Application`/window is ever created on this path.

**What it does** (`IStateRestoreCommand`/`StateRestoreCommand`,
`src/DesktopOrganizer.Services/Storage/StateRestoreCommand.cs`):

1. Acquires the same advisory storage lock (ADR-024 D-8) the normal startup path uses. If another
   DesktopOrganizer process currently holds it, the command refuses to run rather than racing a live
   writer — exits non-zero with a message, touches nothing.
2. Picks exactly one candidate: `application-state.json.bak` if it exists, otherwise the newest
   `application-state.corrupt-{yyyyMMdd-HHmmss}.json` quarantine file (by last-write time). No ladder
   of further fallbacks within a single invocation.
3. **Validates before touching anything**: parses the candidate as JSON, confirms it carries a valid
   `schemaVersion`, and runs it through the full migration chain (`StateMigrationRunner`) up to the
   current schema version. A candidate newer than the current schema is also rejected here — restoring
   from a document this build cannot fully understand is refused for the same reason as scenario B.
4. Only if validation succeeds: the existing primary file (if any — it is expected to be corrupt, or
   the whole exercise would be pointless, but the command does not require that) is moved to
   quarantine, **never deleted**. The validated, migrated candidate is then atomically written as the
   new primary (same atomic-write protocol as every other write to this store, ADR-024 D-6).
5. If validation fails for any reason, the command changes nothing on disk — the bad primary (if any)
   is left exactly as it was, and the command exits non-zero with a message explaining why.

**Exit codes.** `0` — restored successfully. `2` — nothing was restored (no candidate found, the
candidate failed validation, the storage lock could not be acquired, or the write itself failed).

**Diagnostics.** A successful restore writes a `state`/`StateRecoveredFromBackup` event (same event
name and category the normal read ladder's own backup-recovery rungs use); quarantining the previous
primary writes `state`/`StateQuarantined`. See `docs/Refactoring/Testing/diagnostics-log-events.md`.
