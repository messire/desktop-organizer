# ADR-024: Versioned JSON Persistence for Structural Application State

Status: Accepted

Branch `refactor/versioned-json-store` (from `rc/0.4.0`). Implementation master plan:
`docs/PLANS/phase-4-versioned-json-store.md` (D-0..D-13, risks R-1..R-14). Commits `131f009`(plan)
`a140988`(C1 contracts) `1f486c6`(C4 legacy importer) `47f27a6`(C2+C3 combined: atomic write +
recovery ladder + migration chain) `54f1d7b`(C5 advisory lock) `e31d247`(C6 — UI wiring)
`d16f313`(C7 recovery switch + docs) → post-review fixes `dec61fe`(M-3/M-4 + part of M-6)
`fa38f68`(M-6 + mixed C-1 tests) `e3c845d`(C-1 partial-load quarantine) `a15df11`(M-1
consumer-ready write barrier) `b015849`(M-2 `LayoutSnapshotService` idempotence). 1551/1551 tests
green, `dotnet build` clean. Passed `code-quality-reviewer` (1 CRITICAL + 6 MAJOR found and fixed,
all with regression tests) and `fences-ux-parity-reviewer` (no divergence from reference behavior;
all 17 persisted `ChildWindowModel` fields confirmed round-trip).

## Context

`ApplicationSettingsBase` (`user.config`) is convenient for scalar preferences but was also carrying
the entire structural application state — panels, pages, layout snapshots, the system icons panel —
across **four independent writers**, each with its own serialization dialect (two incompatible enum
conventions, JSON-strings-inside-JSON for layout snapshots), no schema version, no atomicity, and no
backup. A single `Settings.Default.Save()` rewrote the whole XML container regardless of which slice
actually changed. See ADR-024's master plan §0 for the full inventory of the four writers and their
individual quirks.

## Decision

Use a versioned JSON state file for panels/layout/pages/content metadata, replacing the four ad hoc
`user.config` writers with one document, one atomic write protocol, and one recovery ladder on read.
`ApplicationSettingsBase` is preserved for the 9 remaining simple scalar preferences (grid size,
snap distance, default appearance, etc. — see the master plan §0.2) where it remains convenient.

### D-1. `JsonObject`, never a CLR type — migrations operate on JSON, not on today's C# types

The plan's original draft contract (`docs/Refactoring/Storage/IApplicationStateStore.cs`) sketched
`record ApplicationStateEnvelope(int SchemaVersion, DateTimeOffset WrittenAtUtc, string AppVersion,
object State)`. `object State` is unusable: a v1→v2 migration must read a document written by code
that may no longer exist in the current build — deserializing it straight into *today's* CLR types
before migrating is exactly the "deserialize-and-pray upgrade" the plan forbids (a renamed or removed
property silently drops data before a migration ever sees it).

**Decision:** the store and the entire migration chain operate on `System.Text.Json.Nodes.JsonObject`
exclusively. Typing happens exactly once, at the UI-layer boundary, when an already-migrated,
current-schema-version document is deserialized into UI DTOs (`ApplicationStateDocumentDto` and
friends, `DesktopOrganizer.UI/State/Dto/`).

**Cost:** schema field names now exist in two places — the UI-layer DTOs and the string literals
inside `V1ToV2Migration`/`V2ToV3Migration`. This is compensated by a dedicated contract test,
`SchemaContractTests`, which asserts the keys produced by the last migration step match byte-for-byte
the keys produced by serializing the DTOs, and that no migration ever emits a reserved envelope key.

### D-2. Synchronous API, not `Task`-based

The draft contract used `Task<ApplicationStateEnvelope> LoadAsync(CancellationToken)`. Rejected:

- `App.OnExit` must finish writing **before** `_serviceProvider.Dispose()` runs; an async store API on
  that path degenerates into `.GetAwaiter().GetResult()` — sync-over-async on exactly the path where
  losing data matters most.
- Document size is tens-to-hundreds of KB; "don't freeze the UI" is already solved by the existing
  500 ms debounce (`SaveScheduler`, ADR-012), not by making the I/O itself asynchronous.
- If a background writer is ever needed, the project already has a template
  (`JsonLinesDiagnosticsLog`'s dedicated writer thread + bounded-wait `Dispose`). Introducing that
  complexity on the shutdown-critical state-save path now, pre-emptively, was judged not worth it.

`IApplicationStateStore.Load()`/`Save(JsonObject)` are both synchronous; both never throw (every
failure is represented as a `StateLoadStatus`/`StateSaveResult` value instead).

### D-3. Layering

```
Services.Abstractions/Storage/   contracts, no knowledge of panels, no WPF
Services/Storage/                atomic write, recovery ladder, migration chain, advisory lock
UI/State/                        DTO schema, ChildWindowModel<->DTO mapping, single load point
```

`ChildWindowModel` is a UI-layer type (`System.Windows.WindowState`, `System.Drawing.Point`/`Size`),
so the store cannot and must not know about it. `JsonObject` (D-1) is the boundary that cuts this.

### D-4. Files on disk

Root: `%LOCALAPPDATA%\DesktopOrganizer\State\`.

| File | Purpose |
|---|---|
| `application-state.json` | primary |
| `application-state.json.bak` | previous known-good (created by `File.Replace` itself) |
| `application-state.json.tmp-{guid}` | write-in-progress temp; swept at every startup |
| `application-state.corrupt-{yyyyMMdd-HHmmss}.json` | quarantine (inherits the `WindowManager.cs`
  legacy corrupt-payload naming convention) |
| `%LOCALAPPDATA%\DesktopOrganizer\.storage-lock` | advisory lock, D-8 |

Quarantine retention: at most 5 files, oldest deleted at startup (same discipline as
`JsonLinesDiagnosticsLog.EnsureDirectoryAndRetention`).

### D-5. Format — flat envelope, three reserved keys

```json
{ "schemaVersion": 3, "writtenAtUtc": "2026-09-19T08:00:00.0000000Z", "appVersion": "0.4.0",
  "panels": [], "pages": {}, "layoutSnapshots": [], "systemIconsPanel": {} }
```

`schemaVersion`/`writtenAtUtc`/`appVersion` are the only reserved top-level keys; every other
top-level key belongs to the payload. No migration is allowed to create a payload key with a reserved
name (enforced by `SchemaContractTests`). `appVersion` is informational
(`AssemblyInformationalVersion`, e.g. `0.4.0`) and **never** drives a decision on read — only
`schemaVersion` does; branching on `appVersion` was explicitly rejected as a future temptation to
avoid.

### D-6. Write protocol (5 stages)

1. Serialize payload → envelope (`JsonObject`) → UTF-8, no BOM, into `…tmp-{guid}` in the **same
   directory** as the primary (otherwise `File.Replace` is not atomic).
2. `FileStream.Flush(flushToDisk: true)` — a real fsync — then `Dispose`.
3. **Validate parse:** reopen the temp file, `JsonNode.Parse`, confirm `schemaVersion` is present and
   typed, confirm payload keys survived. Failure → temp file deleted, primary untouched.
4. Atomic replace: `File.Replace(tmp, primary, backup: primary + ".bak", ignoreMetadataErrors: true)`
   — the single call that gives both atomicity and `.bak` together. If no primary exists yet (first
   run), `File.Move(tmp, primary)` instead (`File.Replace` requires an existing destination).
   `IOException`/`UnauthorizedAccessException` (AV/indexer holding a handle, Controlled Folder
   Access) → 3 retries with 50/100/200 ms backoff, then fail.
5. Only after a confirmed success does the store's in-memory committed state move (the no-op-guard
   snapshot and the last-committed payload). **A failed write must never move committed state** — the
   same class of bug previously seen in `PanelItemStorageService`'s manifest cache
   (`EnsureManifestCacheUnlocked`/`WriteManifestUnlocked`).

Test seam for crash injection: an `internal` constructor accepting `Func<StateWriteStage, bool>?`
(`AfterTempWrite`/`AfterFlush`/`AfterValidate`/`BeforeReplace`/`AfterReplace`), mirroring
`JsonLinesDiagnosticsLog._testForceReopenFailure` and `PanelItemStorageService.forceCrossVolume`.

### D-7. Read protocol — the 7-rung recovery ladder

| # | Situation | Action | Status |
|---|---|---|---|
| 1 | primary parses, `schemaVersion ≤ Current`, migrations succeed | load | `Loaded` |
| 2 | primary missing, `.bak` valid | load from `.bak`, immediately rewrite primary | `RecoveredFromBackup` |
| 3 | primary corrupt, `.bak` valid | primary → quarantine, load `.bak`, rewrite primary | `RecoveredFromBackup` |
| 4 | primary corrupt, `.bak` corrupt/absent | primary and `.bak` → quarantine, empty structural state, writes **allowed** | `Quarantined` |
| 5 | neither primary nor `.bak` exist, legacy `user.config` has non-empty structural state | import legacy → v1 → migrate to Current → write | `ImportedFromLegacy` |
| 6 | neither of the above, no legacy either | empty default state (`pages: ["default"]`) | `FirstRun` |
| 7 | primary parses but `schemaVersion > Current` | **never migrate downward, never overwrite**; empty structural state, writes **forbidden** for the rest of the session | `NewerSchema` |

**Rung 4 (deliberate trade-off).** The tempting fallback — "fall back to legacy import" — is rejected:
if a JSON document ever existed, the legacy blobs are frozen as of the migration moment (D-10), and
resurrecting them would silently replace current state with a stale snapshot (panels deleted a month
ago would reappear; panels created since would vanish). Instead: quarantine + a loud log +
diagnostics event + manual recovery via `--restore-state-backup` (C7, §D-11-adjacent).

**Rung 7 is half of the rollback story (see Consequences).** "A document that's newer wins, hands off
untouched" must be present in the reader from the very first release that ships this store, or a
rollback from a build that writes schema v3 to a build that only understands v2 would clobber the
user's state. This is far cheaper to build in from day one than to retrofit.

### D-8. Advisory file lock — done in this phase (closes ADR-023 R-15)

`ADR-023 §Not done in this phase` explicitly deferred a storage-root file lock to this phase as R-15
(the residual FUS/concurrent-write risk the pipe tie-breaker does not close). **Done here**, because:
the cost is small (~60 lines + DI + one call site in `Program.Main`); it is fully testable in this
environment (`FileShare.None` is per-handle, not per-process — two `FileStream`s in one test process
reproduce the exact conflict semantics of two real processes); and the whole phase is already about
the write protocol, so the lock belongs in the same reviewable change set.

`StorageFileLock` opens `%LOCALAPPDATA%\DesktopOrganizer\.storage-lock`
(`FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None`), held for the process lifetime, taken
in `Program.Main` right after `RunAsPrimary`, before `ApplicationStateService.Load()` and before
`new App(...)`. Schedule: 3 attempts × 150 ms (≤450 ms, matching `SingleInstanceHandshakePolicy`'s
spirit). The lock lives at the storage **root**, not under `State\`, because it conceptually also
covers `PanelItems\.manifest.json` — the second write-shared surface named in R-15.

**Failure behavior is degraded read-only, not exit.** The process already passed the Phase 3
handshake and was recognized as Primary; killing it over a stuck lock file is worse than running
without write capability. On failure: load still proceeds, `IApplicationStateStore.IsWritable`
becomes (and stays) `false`, every `Save` call returns `SkippedReadOnly` without touching disk, and
`StateStoreLockUnavailable` is written once to the diagnostics log and once to `Console.Error`.

**Residual risk knowingly carried forward:** `PanelItemStorageService` does **not** consult this lock
in this phase — its own writes are on drag&drop paths not exercisable in this environment, and it
already has its own defense (`EnsureManifestCacheUnlocked` → untrusted → abort). Recorded as backlog
item `DO-011`, owned by Phase 9.

### D-9. Three schema versions — all real, none synthetic

Each version removes one concrete pre-existing defect (see master plan §0.1), not a synthetic bump to
satisfy the "golden tests for ≥3 versions" DoD line:

- **v1 — literal transcription of legacy.** The exact same four serializations as today, placed
  side-by-side inside the envelope. No shape changes. This makes the legacy→JSON step trivially
  testable/reversible in isolation, and moves all substantive work into v1→v2, whose input is already
  JSON, not an XML container.
- **v2 — fixes two real defects.** (1) `layoutSnapshots[].PanelJsonItems: string[]` (JSON strings
  inside JSON — a workaround for `Core` not being able to reference UI types) becomes
  `layoutSnapshots[].panels: object[]` (real objects — no such constraint exists at the JSON level).
  (2) One dialect for the whole document: camelCase + **string** enums everywhere. Numeric enums in
  `panels` were a live landmine — inserting a member in the middle of `PanelViewMode`/
  `PanelContentFilter`/`PanelSortMode` between releases would silently reinterpret every saved panel.
- **v3 — the grouping PHASE-4's target explicitly asks for** (`geometry`/`appearance`/`content` per
  panel). Virtual-desktop bindings remain only on the system icons panel (`anchorVirtualDesktopId`/
  `showOnAllVirtualDesktops`) — a per-panel block is **not** invented in this phase; reserved for
  Phase 7.

`CurrentSchemaVersion = 3`. The writer always writes 3; v1/v2 exist purely as migration inputs and
permanent golden fixtures (`tests/DesktopOrganizer.Tests/Resources/Storage/golden/`, immutable after
merge).

### D-10. Legacy is frozen — not deleted, not dual-written

- **Not deleted** (minimum one release after JSON becomes primary; actual removal is Phase 9).
- **Not dual-written either.** The alternative considered — writing structural state to both JSON and
  `user.config` for a whole deprecation release, for maximum rollback fidelity — was rejected: it
  would put `Settings.Default.Save()` (a full `user.config` rewrite) back on hot paths that the
  Phase 7 debounce (ADR-012) exists specifically to keep off, and it would recreate two sources of
  truth, exactly the defect this phase removes.
- Consequence: a rollback to an old, `user.config`-only build sees state **as of the migration
  moment**. This limitation is documented explicitly (`docs/Refactoring/Storage/rollback.md`
  scenario A), not silently discovered by a user.

### D-11. Single load point + executable write barrier

Structural state used to be read in four different places at four different startup moments
(`PageManagerService`'s constructor, `WindowManager.LoadWindowsState()` from `App.LoadPanels()`,
`SystemIconsPanel.Load()` afterward, `LayoutSnapshotService.Load()` on demand), while
`MonitorService.Start()` runs *before* `LoadPanels()` and can trigger `OnMonitorsChanged →
SaveWindowsState()`. With one shared document, "save" means "rewrite the whole document," and a save
that races ahead of load completion would clobber a just-migrated document — the single biggest risk
of this phase (plan R-3).

**Two-part solution:**

1. `ApplicationStateService.Load()` runs exactly **once**, in `Program.Main`, before `new App(...)` —
   i.e. before any component capable of calling `Save` exists. All four consumers read from the
   already-loaded in-memory document, never from disk directly.
2. `IApplicationStateStore.Save(...)` refuses (and logs `StateWriteBarrierViolation`) until the first
   successful `Load()` in the process. This is an executable invariant, closed by a test, not a
   comment — it also guards against any future refactor of startup ordering.

Startup order in `Program.Main`:

```
handshake (ADR-023) → RunAsPrimary
  → storageLock.TryAcquire()                (D-8; failure -> degraded read-only)
  → applicationState.Load()                 (recovery ladder D-7 + migrations D-9)
  → new App(provider, coordinator, role)    (StartApp reads from memory from here on)
```

**Post-review finding — the barrier above was necessary but not sufficient (M-1).** The store-level
barrier only covers "before `Load()` completed." It does not cover the gap between `Load()` returning
and each of the four consumers (`WindowManager`, `PageManagerService`, `LayoutSnapshotService`,
`SystemIconsPanelManager`) actually having read its own slice of the just-loaded document — a
`RequestSave()`/`SaveNow()` in that window would pull an empty/default section from whichever
consumer hasn't loaded yet and persist it, in the same atomic document write, over real on-disk data
for the other sections. Fixed by adding a **second, symmetric barrier**:
`IApplicationStateService.ConsumersReady` (`bool`, `false` from construction) and
`MarkConsumersReady()`, called exactly once at the very end of `App.StartApp`, after all four
consumers have definitely loaded (`SystemIconsPanel.Load()` is the last of the four to run).
`ApplicationStateService.PersistNow()` — the method both `RequestSave()`'s debounce and `SaveNow()`
ultimately call — is a no-op and writes `StateWriteBarrierViolation` with `caller:
"PersistNow/ConsumersNotReady"` until `ConsumersReady` is `true`. `caller` on this event is therefore
a closed two-value set: `"Save"` (the original store-level barrier, before `Load()`) and
`"PersistNow/ConsumersNotReady"` (this one, after `Load()` but before every consumer has read its
slice).

### D-12. Write discipline is preserved; `SaveScheduler` moves

`SaveScheduler` used to belong to `WindowManager`. With one shared document, two writers (panels
through the scheduler, pages/snapshots/system-icons-panel synchronously) would race on the same file.
So the single `SaveScheduler` instance moves into `ApplicationStateService`:

- `RequestSave()` — 500 ms debounce: drag, resize, roll-up, filter/view/sort change,
  `OnMonitorsChanged`.
- `SaveNow()` — synchronous: panel create/delete, page add/remove/switch, snapshot save/delete/
  restore, `SystemIconsPanel.Save()`, `App.OnExit`.

The public contract `IWindowManager.SaveWindowsState()`/`SaveWindowsStateNow()` is unchanged — both
become thin delegates onto `ApplicationStateService`, deliberately limiting the blast radius of this
refactor on a method with many, already-documented call sites.

### D-13. Import/export — not in this phase, boundary fixed now

No import/export feature exists anywhere in the codebase today (verified — neither UI nor services).
Building a second on-the-wire JSON format now would be building a format with no consumer.
**Decision:** only the architectural half of the requirement ("import/export JSON must not be 1:1
with the internal store contract") is done in this phase:

1. The DTO schema and envelope types are `internal` (using the repo-wide `InternalsVisibleTo
   DesktopOrganizer.Tests`), so the internal contract cannot accidentally become a public wire format.
2. This ADR records the rule for the future: any export/import feature must define its own DTO and
   its own version; `ApplicationStateDocumentDto`/the envelope are never handed to an external format
   directly.

The feature itself is deferred to Phase 7; recorded as backlog item `DO-012`.

## Bugs found and fixed during review (important lessons)

`code-quality-reviewer` found 1 CRITICAL + 6 MAJOR issues on this branch, all fixed with dedicated
regression tests before merge:

- **CRITICAL (C-1) — the JSON-store transition silently dropped the partial-panel-load safety net.**
  Before this phase, `WindowManager` had `BackupPendingCorruptStateIfAny`
  (`WindowManager.cs:426-447`, pre-refactor): if some saved panels failed to construct and were
  skipped, it quarantined the **full** `user.config` payload before the next save could overwrite it
  with the reduced set. The store-level quarantine this phase introduced
  (`JsonApplicationStateStore`'s `QuarantineFile`, part of the D-7 recovery ladder) does **not**
  replicate this — it only fires when the on-disk document fails to *parse*, not when a caller
  successfully parses/migrates the document and then, at the UI layer, silently drops part of it (a
  panel whose construction threw in `WindowManager.LoadWindowsState`). Without a fix, the very first
  `Save()` after such a load would silently overwrite the primary with fewer panels, and the next
  `Save()` after that would overwrite `.bak` too — permanent, silent data loss.
  **Fix:** a new method, `IApplicationStateStore.QuarantineLoadedDocument(string reason)`, copies
  (not moves — the primary stays the committed state) the current primary into a timestamped
  quarantine file before it can be overwritten. Idempotent per `Load()` call (only the first call
  after a given `Load()` actually copies anything). Wired through
  `IApplicationStateService.ReportPartialLoad(string reason)`, called from
  `WindowManager.LoadWindowsState()` whenever `CreateEachIsolated` reports `skipped > 0`, with
  `reason: "PartialPanelLoad"`.
- **MAJOR (M-1) — write barrier didn't account for "consumers haven't loaded yet."** See D-11 above:
  `IApplicationStateService.ConsumersReady`/`MarkConsumersReady()`, the second, symmetric barrier.
- **MAJOR (M-2) — `LayoutSnapshotService.Load()` was not idempotent.** A second call would `Clear()`
  and re-add every entry from the just-loaded document, wiping out any snapshot created earlier in
  the same session (e.g. the user saves a snapshot, then something downstream calls `Load()` again).
  Fixed with a `_loaded` guard: only the first call in the process's lifetime actually seeds
  `_snapshots`.
- **MAJOR (M-3) — `AtomicJsonFileWriter` only retried `IOException`.** Controlled Folder Access, a
  restrictive AV product, or a momentarily-denying ACL raise `UnauthorizedAccessException` on
  Windows, not `IOException`, for what is functionally the same transient "something else has this
  path locked/blocked right now" condition. `AtomicJsonFileWriter`'s replace-stage retry now catches
  `IOException or UnauthorizedAccessException` uniformly (3 retries, same 50/100/200 ms backoff).
- **MAJOR (M-4) — `StateRestoreCommand` had an inverted, data-losing failure order.** The original
  order quarantined (destructively `File.Move`d) the existing primary **before** attempting to write
  the restored candidate; a write failure after that move left the user with **no primary file at
  all**. Fixed: the new primary is written **first**; only after that write succeeds is the
  previous-primary content (already durably rotated into `.bak` by `AtomicJsonFileWriter`'s own
  `File.Replace`) additionally copied into a dedicated quarantine file for history. A failed write now
  leaves the previous primary exactly where it was.
- **MAJOR (M-6) — an uncaught exception inside a migration step permanently bricked startup.** Before
  the fix, a bug inside `ISettingsMigration.Migrate` (or a gap/downgrade the chain only discovers at a
  specific document's version) propagated straight out of `Load()`, past the entire recovery ladder,
  crashing the process — and, because a read that merely throws changes nothing on disk, identically
  on every subsequent launch. Fixed: `JsonApplicationStateStore.TryMigrate` wraps every migration
  attempt; a failure now quarantines the document (best-effort, same `QuarantineFile` the corrupt-file
  rungs use) and degrades to an empty, writable structural state — the same outcome every other
  unreadable-document case on the ladder already produces — instead of crashing. Reported via a new
  diagnostics event, `StateMigrationFailed` (`fromVersion`, `toVersion`, `errorType` — never
  `ex.Message`).
- One further MAJOR finding — `StateRestoreCommand`'s misleading use of `StateRecoveredFromBackup` for
  a *failed* restore attempt — was folded into the M-4 fix: a failed `Restore()` now writes a
  dedicated `StateRestoreFailed` event (`errorType` only) instead of reusing the success event's name.

`fences-ux-parity-reviewer` found no divergence from Fences' reference behavior: all 17 persisted
`ChildWindowModel` fields were confirmed round-trip (`PanelStateMapperTests`), and ordinary panel
load/save/drag/resize/page-switch/snapshot behavior is unchanged from the user's perspective.

## Invariants

- migrations only ever operate on `JsonObject`, never deserialize an older document straight into
  today's CLR types (D-1);
- `Save` is a no-op (never a crash, never a partial write) before `Load()` has completed in this
  process, **and** before every one of the four state-owning consumers has read its own slice of what
  `Load()` returned (D-11, M-1);
- a failed write never moves the store's committed-state snapshot (D-6 step 5);
- a document written by a newer schema version is never migrated downward and never overwritten —
  rung 7 of the read ladder is unconditional (D-7);
- the advisory storage lock, once failed to acquire, makes every `Save` a no-op for the rest of the
  session rather than crashing a legitimately-primary process (D-8);
- legacy `user.config` structural keys are read at most once (import) and never written to again
  after the JSON store becomes primary (D-10);
- no diagnostics event in the `state` category ever carries `Exception.Message` or a file path — only
  `Exception.GetType().Name`, closed-set enum names, and numeric counters.

## Consequences

- Four independent `user.config` writers collapse into one document, one write protocol, one recovery
  ladder — eliminates the "four JSON dialects in one XML container" defect class entirely.
- `IWindowManager`'s public save contract is unchanged (D-12); callers outside `WindowManager` itself
  needed no changes.
- Schema field names now exist in two places (UI DTOs and migration string literals) — the cost of
  D-1's `JsonObject`-only migration layer; mitigated by `SchemaContractTests`.
- **R-15 (ADR-023) is now closed.** The advisory storage lock (D-8) provides the file-level protection
  against concurrent Fast-User-Switching writes that ADR-023 explicitly deferred to this phase.
  `PanelItemStorageService` still does not consult this lock — tracked separately as `DO-011`,
  Phase 9.
- Rollback to a pre-JSON release (`docs/Refactoring/Storage/rollback.md` scenario A) restores state
  **as of the migration moment**, not the very latest state — a direct, documented consequence of
  D-10's "no dual-write."
- Rollback to a JSON-aware but older-schema release (scenario B) is handled automatically by rung 7 of
  the read ladder (`NewerSchema`) — no user action needed, no data loss, but that session starts with
  empty structural state by design.
- Manual verification on live Windows (real-install upgrade, corrupt-primary recovery, FUS lock
  contention, Controlled-Folder-Access/AV interaction, `--restore-state-backup`, startup performance)
  is still outstanding — see `docs/Refactoring/Testing/storage-manual-matrix.md` (S-1..S-11).

## Not done in this phase (deliberately)

- Import/export as a user-facing feature (D-13) — architecture boundary only; feature itself is
  Phase 7, backlog `DO-012`.
- Deletion of legacy `Settings.settings` structural keys (D-10) — Phase 9.
- `PanelItemStorageService` consulting the new storage lock (D-8) — Phase 9, backlog `DO-011`.
- Background/asynchronous state writes (D-2).
- Per-panel virtual-desktop bindings (D-9) — reserved, not created; Phase 7.
- State-file encryption/signing — no threat model for it (per-user `%LOCALAPPDATA%`,
  `PrivilegesRequired=lowest`).
- `ShutdownMode`, autostart, single-instance protocol — unchanged (Phase 3 already closed).
- Full per-shape `StateMigrated`/complete `StateLoaded`/`StateSaved` diagnostics payload (`panelCount`,
  `pageCount`, `snapshotCount`, `durationMs`, per-step migration events) — partially implemented; see
  `docs/Refactoring/Testing/diagnostics-log-events.md` `state` category for the exact, honest
  as-shipped status versus the master plan's §4 aspiration.

---

## Amendment 2026-09-22 (ADR-028)

*(Раздел на русском по требованию текущего проходного задания; остальной документ ADR-024 сохранён на
языке оригинала.)*

[ADR-028](ADR-028-workspace-grid.md) (совместно с параллельно готовящимся ADR-029) вводит
**v4→v5**-миграцию — первую версию схемы после v4 (ADR-025, `accentColor` lossy-миграция), сверенную по
коду (`StateEnvelope.CurrentSchemaVersion = 4` на момент написания этого amendment). Полный план —
[`docs/PLANS/workspace-grid-migration.md`](../docs/PLANS/workspace-grid-migration.md); здесь фиксируется
только соответствие уже принятым инвариантам этого ADR:

- **Только аддитивные поля** в уже существующем v3-объекте `panels[].geometry`
  (`geometryMode`/`widthSpanUnits`/`heightSpanUnits`/`sizingMode`) — не новый top-level ключ, не удаление
  и не переименование существующих `X/Y/Width/Height` (D-9 v3 группировка не пересматривается).
- **Версия отражает реальный, а не синтетический шаг** (D-9 принцип «каждая версия чинит конкретный
  дефект») — v5 добавляет отсутствовавшую ранее возможность выразить `GridAligned`-геометрию и
  `sizingMode`/`iconLayoutPolicy` (ADR-029), а не бампает номер без содержательного изменения контракта.
- **`JsonObject`-only миграция (D-1)** — `V4ToV5Migration` следует тому же контракту, что
  `V1ToV2Migration`/`V2ToV3Migration`/`V3ToV4Migration`, никакой десериализации в текущие CLR-типы до
  миграции.
- **Rung 7 (D-7, «`NewerSchema`») подтверждён построчно** (`docs/PLANS/workspace-grid-migration.md`
  §4) как единственный необходимый механизм безопасного отката к сборке, не знающей о `geometryMode`:
  `IsWritable` выставляется в `false` на весь остаток сессии (`_newerSchemaLockout`,
  `JsonApplicationStateStore.cs:104,115`), `Save()` не пишет поверх v5-документа — никакой
  дополнительной защиты в `V4ToV5Migration` не требуется.
- **`LayoutSnapshotService` (ADR-004) принимает новые поля аддитивно** без изменений собственного
  Capture/Restore контракта — то же свойство устойчивости к добавлению полей, что уже используется для
  `PageId`/`MonitorDeviceId` (ADR-004 Решение 2).

---

## Amendment 2026-09-25: раздельные корни хранилища для Debug и Release

**Контекст.** До этой поправки все сборки писали в один корень `%LOCALAPPDATA%\DesktopOrganizer`:
`StateStorePaths`, `PanelItemStorageService` и каталог диагностики в `DiConfig` каждый по отдельности
собирали этот путь. В результате установленное приложение (инсталлер всегда собирает Release) при
первом запуске показывало панели, страницы и раскладки, созданные во время разработки из IDE (Debug), а
dev-запуск, в свою очередь, ротировал рабочий `.bak` и мог переписать значение автозапуска в реестре
путём к `bin\Debug`.

**Решение.**

- Единая точка правды корня — `DesktopOrganizer.Services.Storage.AppStorageRoot`. Выбор папки —
  **по конфигурации сборки**, компилируемой константой: Release → `%LOCALAPPDATA%\DesktopOrganizer`
  (исторический корень, установленное приложение продолжает читать то, что писали прежние версии);
  Debug → `%LOCALAPPDATA%\DesktopOrganizer.Dev`. Никакого флага командной строки или переменной
  окружения: рабочий корень недостижим из dev-сборки без каких-либо действий разработчика.
- Все три поверхности (`State\`, `PanelItems\`, `Logs\`) и advisory-lock `.storage-lock` (D-8)
  выводятся из `AppStorageRoot.Default`; internal-конструкторы с явным корнем для тестов не изменились.
- Значение автозапуска `HKCU\...\Run` тоже раздваивается: Release → `DesktopOrganizer` (контракт с
  инсталлером, `AutostartService.InstallerValueName`), Debug → `DesktopOrganizer.Dev`.
- Защита от второго экземпляра (ADR-023) **не** делится по корню намеренно: один экземпляр на
  пользователя независимо от конфигурации. Dev-сборка, запущенная при работающем установленном
  приложении, активирует его и завершается — две сборки никогда не борются за рабочий стол.

**Последствия.** Данные, накопленные до поправки в `%LOCALAPPDATA%\DesktopOrganizer`, остаются на
месте и принадлежат Release; для продолжения разработки их копируют в `DesktopOrganizer.Dev`
вручную (одноразово). Инварианты D-1…D-11 не затронуты. Закреплено `AppStorageRootTests` и
`InstallerAutostartContractTests`.

