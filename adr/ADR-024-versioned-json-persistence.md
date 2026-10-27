# ADR-024: Versioned JSON Persistence for Structural Application State

**Статус:** Accepted — Amended 2026-09-22 (ADR-025: схема v3→v4, `accentColor`, лоссивная миграция `#1E1E1E` → `#6B7280`); Amended 2026-09-22 (ADR-028: схема v4→v5, аддитивные `geometryMode`/`widthSpanUnits`/`heightSpanUnits`/`iconLayoutPolicy`/`sizingMode`); Amended 2026-09-25 (раздельные корни хранилища Debug/Release); Amended 2026-09-27 (ADR-032: аддитивный скаляр `IAppSettings.Language`, единственное расширение `user.config` с явного разрешения пользователя); Amended 2026-09-29 (ADR-043: схема v5→v6, `pages.ids[]` → `pages.items[{id,name?}]`, с явного разрешения пользователя); Amended 2026-10-09 (ADR-audit WP-B: read-only сессия видна пользователю — UI читает `IApplicationStateStore.IsWritable`, drop не двигает файлы, `LayoutSnapshotService.Restore` в read-only не возвращает файлы; формат и семантика записи не менялись); Amended 2026-10-10 (решение пользователя «Аддитивная ревизия схемы»: схема v7 + envelope-ключ `schemaRevision`, round-trip неизвестных полей, толерантные enum, `V6ToV7Migration` — тождественная; читаются файлы того же major с более высокой ревизией; D-5 — четыре reserved-ключа, D-7 rung 1 — см. раздел «Amendment 2026-10-10»; после перехода на v7 каждый запуск ≤0.8.0 остаётся read-only, пока файл не восстановлен из premigration-бэкапа или не возвращена сборка ≥v7 — однократно лишь событие major-bump); Amended 2026-10-10 (ADR-050: путь корня хранилища зависит от канала — под package identity (Store) корень `%USERPROFILE%\DesktopOrganizer`, с явного разрешения владельца; схема, DTO, миграции и `IAppSettings` не менялись)

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

### D-5. Format — flat envelope, three reserved keys (four since v7 — Amendment 2026-10-10)

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

**Amendment 2026-10-10 (v7):** the set of reserved top-level keys is now **four** — `schemaVersion`,
`writtenAtUtc`, `appVersion` and `schemaRevision` (`StateEnvelope.ReservedKeys`/`IsReservedKey`/`Wrap(…,
schemaRevision)`/`TryGetSchemaRevision`). The sentence above ("three … only reserved keys") describes the v3–v6
envelope. The reserved set is **frozen within a major**: a new envelope key is only possible together with a major bump
(otherwise a build of the same major would take it for a payload key). `SchemaContractTests` now enforces the
"no migration emits a reserved key" rule against all four.

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
| 1 | primary parses, `schemaVersion ≤ Current` (since v7: the same major with **any** `schemaRevision` is also rung 1 — Amendment 2026-10-10), migrations succeed | load | `Loaded` |
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

**Amendment 2026-10-10:** rung 7 still compares the **major** only. Since v7 a document with the same `schemaVersion`
but a higher `schemaRevision` is *not* rung 7 — it is rung 1 (`Loaded`, writable, unknown fields preserved on save); see
the read table in the Amendment 2026-10-10 section below. "Never overwritten" in rung 7 and in the Invariants applies to
a higher **major**.

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
  contention, Controlled-Folder-Access/AV interaction, `--restore-state-backup`, startup performance):
  **performed by the user on Windows 11 24H2, 2026-09-27, accepted as sufficient** (Windows 10 is not
  checked separately) — see [docs/ROADMAP.md](../docs/ROADMAP.md) (lines 6–8, manual verify for Phases 0–10). The
  historical checklist `docs/Refactoring/Testing/storage-manual-matrix.md` (S-1..S-11) is kept as a record.

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

## Amendment 2026-09-27 (ADR-032): аддитивный скаляр `Language`

*(Раздел на русском, как и предыдущий amendment; остальной документ ADR-024 — на языке оригинала.)*

[ADR-032](ADR-032-ui-localization.md) вводит `IAppSettings.Language` (`""`/`"en"`/`"ru"`, дефолт `""`) —
единственное расширение контура `user.config`, сделанное с явного разрешения пользователя
(`docs/prompts/localization.md` D-1) поверх заморозки, которую CLAUDE.md формализовало из контекста этого
ADR (`ApplicationSettingsBase` сохранён именно для «оставшихся простых скалярных настроек», см. раздел
Context выше). `Language` пополняет прецедентный список аддитивных `IAppSettings`-скаляров, введённых
после этого ADR без изменения `schemaVersion`/JSON-схемы: `DefaultSurfaceStyle`, `IsLayoutLocked`,
`NormalModeLayoutGestures`, `DefaultPanelGeometryMode`, `DefaultIconLayoutPolicy`. Diff контура persistence
ограничен четырьмя файлами (`Properties/Settings.settings`, `Properties/Settings.Designer.cs`,
`Services/IAppSettings.cs`, `Services/AppSettings.cs`) — JSON-документ (`schemaVersion`, миграции, DTO)
не тронут; инварианты D-1..D-13 не затронуты. Это не открывает общее разрешение на будущие скаляры —
каждый новый скаляр продолжает требовать отдельного явного «да» пользователя в самой задаче.

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
- Значение автозапуска `HKCU\...\Run` пишет только Release → `DesktopOrganizer` (контракт с
  инсталлером, `AutostartService.InstallerValueName`). Debug в реестр не пишет вовсе: переключатель —
  флаг на время сессии (`AutostartService.PersistsToRegistry`). *Поправка 2026-10-27:* до 1.0.1 Debug
  регистрировал собственное значение `DesktopOrganizer.Dev`; при логоне оно стартовало `bin\Debug`-exe,
  тот выигрывал single-instance (ADR-023), а установленное приложение завершалось как второй экземпляр.
  Остаток `DesktopOrganizer.Dev` удаляется однократно (`AutostartService.LegacyDebugValueName`).
  Та же поправка учитывает флаг Task Manager / Параметры → Автозагрузка
  (`HKCU\...\Explorer\StartupApproved\Run`, бит 0 первого байта = выключено): значение `Run`,
  выключенное там, Windows не запускает, поэтому «включено» = значение есть **и** флаг не выставлен,
  а включение сбрасывает флаг; инсталлер делает то же (`AutostartApprovedKey`).
- Защита от второго экземпляра (ADR-023) **не** делится по корню намеренно: один экземпляр на
  пользователя независимо от конфигурации. Dev-сборка, запущенная при работающем установленном
  приложении, активирует его и завершается — две сборки никогда не борются за рабочий стол.

**Последствия.** Данные, накопленные до поправки в `%LOCALAPPDATA%\DesktopOrganizer`, остаются на
месте и принадлежат Release; для продолжения разработки их копируют в `DesktopOrganizer.Dev`
вручную (одноразово). Инварианты D-1…D-11 не затронуты. Закреплено `AppStorageRootTests` и
`InstallerAutostartContractTests`.

---

## Amendment 2026-09-29 (ADR-043): schema v6, страницы как `items[{id,name?}]`

[ADR-043](ADR-043-page-display-names.md) вводит **v5→v6** — пятую ступень лестницы миграций
(`V5ToV6Migration`, `JsonObject`-only, D-1) с явного разрешения пользователя, в объёме, ограниченном
одной секцией payload: `pages.ids[]` заменяется на `pages.items[{id, name?}]` (стабильный `id` +
опциональное отображаемое `name`, та же форма, что уже несёт `panels[]` — `Id` + `Title`). Все прочие
top-level ключи (`panels`, `layoutSnapshots`, `systemIconsPanel`) копируются миграцией побайтно —
`panels[].pageId`/`layoutSnapshots[].panels[].pageId` не трогаются ни этой, ни какой-либо ещё миграцией.

- **Rung 7 («`NewerSchema`», D-7) подтверждён без изменений** — сборка ≤0.7.0, открыв v6-документ, уходит
  в read-only режим до чтения любого ключа `pages`; файл не перезаписывается ни при каком последующем
  `Save()` в рамках той сессии.
- **Premigration-бэкап именуется по исходной версии, не по целевой** — `StateStorePaths.PreMigrationBackupPath`
  не менялся, миграция v5→v6 пишет `application-state.premigration-v5.json` (не `-v6.json`, как
  предполагал первоначальный бриф задачи ADR-043) — расхождение задокументировано в самом ADR-043 как
  факт поведения существующего механизма, а не как новое решение.
- Диапазон разрешения — **только** `schemaVersion`/DTO `pages`/маппинг в `ApplicationStateService`.
  `JsonApplicationStateStore` (атомарная запись, `.bak`, карантин, advisory lock), `StateMigrationRunner`,
  `StateRestoreCommand`, `LegacySettingsImporter`, `LayoutSnapshotService` — без диффа.

---

## Amendment 2026-09-22 (ADR-025): схема v3→v4 — `accentColor` (оформлено ретроспективно, ADR-audit WP-E)

Исходный текст этого ADR описывает схему v3. Бамп **v3→v4** (`V3ToV4Migration`, `JsonObject`-only, D-1) был
введён [ADR-025](ADR-025-ambient-accent-visual-language.md) (Ambient Accent, D-5) и в этом документе не
оформлялся (Amendment 2026-09-22 (ADR-028) выше уже ссылается на «v4 (ADR-025)»). Фактическое содержание шага
(сверено с `src/DesktopOrganizer.Services/Storage/Migrations/V3ToV4Migration.cs`):

- В `panels[].appearance` ключи `backgroundColor`/`backgroundOpacity` (мёртвые ключи v3) заменяются на
  `accentColor`/`surfaceOpacity`/`surfaceStyle`.
- **Единственный лоссивный шаг:** `backgroundColor == "#1E1E1E"` (исторический дефолт заливки v1–v3) →
  `accentColor = "#6B7280"` (`Accent.Neutral`); любое другое значение переносится в `accentColor` дословно.
  Принят как trade-off (ADR-025 «D-5 лоссивность»).
- Все прочие ключи документа копируются без изменений; принцип D-9 («версия — реальный шаг») соблюдён.
- Откат на сборку до v4 уходит в rung 7 (`NewerSchema`, D-7): read-only, файл не перезаписывается.

## Amendment 2026-10-09 (ADR-audit WP-B): read-only сессия видна пользователю и не двигает файлы

**Что изменилось (только поведение UI вокруг уже существующего флага, не механизм хранения).** Rung 7
(«`NewerSchema` → read-only», D-7) и потеря advisory lock (D-8) и раньше превращали сессию в read-only: каждый
`Save()` — тихий no-op, `IApplicationStateStore.IsWritable == false`. Аудит ADR 2026-10-09 показал, что сам
пользователь об этом не узнавал, а два UI-пути двигали файлы так, будто запись возможна. Теперь:

- **Единственная точка правды — `IApplicationStateStore.IsWritable`** (читается живьём; `JsonApplicationStateStore`:
  `_storageLock.IsHeld && !_newerSchemaLockout`). Никакого второго флага в UI нет.
- **Drop внешних файлов в панель** (`ChildWindowViewModel.AddDroppedFiles`, `MovesDroppedItemsIntoStorage`): в
  read-only сессии вызов `MoveIn`/`MoveInDirectory` пропускается — панель ссылается на исходный путь, файл остаётся на
  месте (иначе он ушёл бы со стола в скрытое хранилище при том, что ссылка на него никогда не сохранится). Эффект drop —
  `DragDropEffects.Copy`, а не `Move` (`ChildWindow.ExternalDropEffect`; при `Move` источник — Проводник — удалил бы
  оригинал). Вместо one-time Move-хинта (`HasShownMoveHint`) показывается hint у заголовка панели
  (`ReadOnlyDropHintRequested` → `HintPopupController.ShowReadOnlyDropHint`) — **один раз на экземпляр панели**
  (in-memory guard, не персистится).
- **Уведомление `ReadOnlySessionNotice`** (singleton, `App.xaml.cs` после старта): один раз за сессию показывает
  `StatusSeverity.Caution` через `IStatusNotifier`/InfoBar (ADR-047 D-9), три коротких RESX-предложения (что
  происходит / что безопасно / что делать). Суффикс tooltip трея `Tray_Tooltip_ReadOnly_Format`
  (`ITrayIconManager.SetReadOnlySession`); итоговый tooltip усекается до 127 символов (лимит `NotifyIcon.Text`; фикс-цикл
  `8219acc`, описано по сообщению координатора).
- **`LayoutSnapshotService.Restore` в read-only сессии не возвращает файлы** (подробности восстановления — ADR-004,
  Amendment 2026-10-09): удаление панелей там не персистится, осиротеть на диске нечему, а физический перенос забрал бы
  файл с рабочего стола без сохранённой ссылки.

**Формат и семантика записи НЕ менялись:** `schemaVersion` остаётся 6, миграций, DTO, ключей, `IAppSettings`/
`user.config`, атомарной записи, бэкапов, карантина, advisory lock и самой лестницы чтения/восстановления нет — изменено
только то, что UI-слой *читает* уже существующий `IsWritable` и как реагирует на него. Единственное касание
замороженного контура — поведение `LayoutSnapshotService.Restore` (раньше «без диффа», см. Amendment ADR-043 выше):
выполнено по **явному указанию пользователя «делай»** на итемизированный список дефектов аудита 2026-10-09 (в списке —
пункт про возврат managed-файлов при восстановлении снапшота). Сигнатура `ILayoutSnapshotService` и формат снапшотов
(`PanelJsonItems`) не менялись; конструктор `LayoutSnapshotService` получил зависимости `IStatusNotifier`,
`ILocalizationService`, `IApplicationStateStore`.

**Известные ограничения:** уведомление показывается только в InfoBar окна настроек (баннер, привязанный к
`IsWritable`, не построен); удаление панели/страницы в read-only сессии по-прежнему отправляет managed-файлы в Корзину
(ADR-035/ADR-048) — см. `docs/ROADMAP.md`, «Follow-up’ы после аудита ADR».

## Amendment 2026-10-10: аддитивные ревизии схемы (v7, `schemaRevision`)

**Решение пользователя 2026-10-10** («Аддитивная ревизия схемы» — пункт 1 раздела «Решения, ожидающие пользователя»
в `docs/ROADMAP.md`): явное разрешение на замороженный контур в объёме, ограниченном ниже («Границы разрешения»).
Реализация — коммит `5633bb4` (ветка `bugfix/adr-audit-fixes`); план и обоснование —
[`docs/PLANS/schema-additive-revision-v7.md`](../docs/PLANS/schema-additive-revision-v7.md).

**Зачем.** До этого любое новое persisted-поле требовало бампа `schemaVersion` и, значит, превращало откат на
предыдущую сборку в read-only сессию (инцидент 2026-09-24, rung 7). Цель — чтобы будущая фича с новым **опциональным**
полем **не ломала** старую сборку того же major и **не требовала** миграции. Что мешало (сверено с кодом до правки):
(F1) любой `schemaVersion > Current` → rung 7; (F2) `ApplicationStateService.PersistNow` собирал документ заново из живых
моделей — неизвестные ключи терялись при первом сохранении; (F3) типизированные enum-свойства DTO на незнакомой
строке бросали `JsonException` → карантин и **пустое writable** состояние (следующий save затирал primary); (F4)
`V4ToV5Migration` копирует только allow-list ключей (top-level и снапшота); (F5) `StateMigrationRunner` при
`StateRestoreCommand` не переносил бы новый envelope-ключ.

### Версионирование: major + revision

- `schemaVersion` остаётся целым **major** и теперь равен **7**. Major меняется только для несовместимых изменений
  (rename/remove/reshape/смена типа или смысла/«must-understand» поле) — с миграцией и rung 7, как раньше.
- Новый, **четвёртый** зарезервированный envelope-ключ `schemaRevision` (int ≥ 0) — аддитивный minor.
  `StateEnvelope.CurrentSchemaRevision = 0`. Отсутствие ключа ≡ `0`; значение не-int или `< 0` ≡ `0` плюс
  diag-событие `StateRevisionMalformed`, **не** карантин (ревизия — справочные метаданные, не payload).
- **Таблица чтения** (rungs 1/2/3 — везде, где документ реально используется как загруженное состояние):

| На диске | Сборка v7.rN | Результат |
|---|---|---|
| major < 7 | 7.rN | миграции до v7, premigration-бэкап `application-state.premigration-v{major}.json`, writable (как раньше) |
| major = 7, ревизия ≤ rN | 7.rN | `Loaded`, writable |
| major = 7, ревизия > rN | 7.rN | **`Loaded`, writable**, неизвестные поля сохраняются (см. ниже), diag `StateNewerRevisionOpened {revisionOnDisk, buildRevision}` |
| major > 7 | 7.rN | rung 7 без изменений: `NewerSchema`, read-only, файл не трогается |

- **Правило записи:** `schemaRevision = max(revisionOnDisk, CurrentSchemaRevision)` (`JsonApplicationStateStore.WriteRevision`) —
  high-water mark, монотонен, старая сборка того же major его не понижает. Кто писал последним — по-прежнему
  информационный `appVersion` (D-5: решения по `appVersion` не принимаются). `StateLoaded` получил поле
  `schemaRevisionOnDisk`, `StateSaved` — `schemaRevision`.
- **Отвергнуто (1): остаться на `schemaVersion = 6` и добавить опциональный `schemaRevision`.** Сборка 0.8.0 открыла бы
  такой файл writable, но не умеет ни round-trip неизвестных полей, ни толерантный enum: при первом save она срезала бы
  все новые поля, а на новом значении enum ушла бы в карантин и перезаписала primary пустым документом. Major 7 —
  «черта»: каждая сборка, способная писать v7, гарантированно revision-safe; все небезопасные писатели (≤0.8.0)
  отсекаются rung 7 — громко и без потери данных.
- **Отвергнуто (2): «семантическая» строка `"schemaVersion": "7.0"`.** `StateEnvelope.TryGetSchemaVersion` во всех
  выпущенных сборках читает `GetValue<int>()`; строка ⇒ документ **corrupt** ⇒ карантин и перезапись пустым — тот же
  класс инцидента 2026-09-24. Плюс `AtomicJsonFileWriter` stage 3 (D-6) валидирует int-`schemaVersion`.

### Round-trip неизвестных полей

- Все **10 DTO** в `src/DesktopOrganizer.UI/State/Dto/` получили `[JsonExtensionData] Dictionary<string, JsonElement>?
  Extras`: `ApplicationStateDocumentDto`, `PanelStateDto` (и `panels[]`, и `layoutSnapshots[].panels[]`),
  `PanelGeometryDto`, `PanelAppearanceDto`, `PanelContentDto`, `PagesStateDto`, `PageStateDto`, `LayoutSnapshotStateDto`,
  `SystemIconsPanelStateDto`, `SystemIconsPanelItemStateDto`.
- **Носители между `Load` и `PersistNow`:**
  - панели — `ChildWindowModel.PersistedExtras` (`PanelPersistedExtras`: неизвестные ключи на уровнях panel/geometry/
    appearance/content + `Enums`; иммутабельный, без INPC, UI его не читает). Свойство на модели, а не side-table по `Id`,
    потому что снапшоты хранят панели как сериализованный `ChildWindowModel` (`PanelJsonItems`), а `Restore` выдаёт новые
    Guid — свойство переживает эти JSON round-trip'ы без диффа в `LayoutSnapshotService`;
  - всё остальное — `UnknownFieldLedger` (internal, принадлежит `ApplicationStateService`): корень документа, секция
    `pages`, страницы (по id), снапшоты (по id), секция `systemIconsPanel`, неизвестный `iconSize`, элементы с неизвестным
    `kind` (дописываются в конец `items`; порядок задаёт `order`). `PageDescriptor` (record) не менялся — extras сломали
    бы value-equality.
- **`PanelCloneBuilder` extras сбрасывает** (`PanelStateMapper.ToDto(source, includeUnknownFields: false)`): клон — новая
  сущность, неизвестное поле может быть привязано к идентичности, а «поля нет ⇒ дефолт» всегда валидно. Восстановление
  снапшота extras **сохраняет** (это та же панель).
- Факты сериализации, закреплённые тестами: ключи extension data пишутся дословно (без naming policy); `"x": null`
  внутри extras сохраняется; дубликатов ключей нет (raw неизвестного enum пишется через само свойство, не через extras).
- Известное безвредное следствие: порядок ключей меняется (extras после известных), поэтому первый `PersistNow` после
  загрузки может пройти no-op guard (`_lastCommittedPayloadJson`) и записать файл один раз.

### Толерантные enum

- Шесть enum-полей DTO стали `string`: `PanelGeometryDto.WindowState`, `PanelAppearanceDto.SurfaceStyle`,
  `PanelContentDto.ViewMode`/`ContentFilter`/`SortMode`, `SystemIconsPanelItemStateDto.Kind` (`GeometryMode`/
  `SizingMode`/`IconLayoutPolicy`/`IconSize` были строками раньше, ADR-028/029). Формат на диске не меняется —
  конвертер без naming policy и раньше писал имя члена как есть.
- `TolerantEnumParser.Parse<TEnum>(raw, fallback, out recognized)`: без `,`, `Enum.TryParse(ignoreCase)`, `Enum.IsDefined`
  **и отказ на числовой строке** (`"2"`, `"-1"`) — сохраняет смысл `allowIntegerValues: false`. Три прежних парсера
  (`GeometryMode`/`SizingMode`/`IconLayoutPolicy`) набор принимаемых значений не меняли.
- **Незнакомое значение** → документированный дефолт + запись `PreservedEnumValue(Raw, Fallback)` (в
  `PersistedExtras.Enums` по пути поля, например `geometry.windowState`; для `systemIconsPanel.iconSize` — в ledger) +
  diag-событие со счётчиками, **не карантин**. Если на save модель всё ещё держит `Fallback` (пользователь поле не менял),
  на диск пишется **raw-строка**; изменил — пишется новое значение. Иначе значение `Mica` из будущей ревизии превращалось
  бы в `Ambient` при любом перемещении панели.
- **Ограничение (R8):** явный выбор пользователем значения, совпадающего с фолбэком, неотличим от «не трогал» — raw
  сохраняется (в старой сборке визуально ничего не меняется).
- **Число** в enum-поле по-прежнему `JsonException` → карантин, как раньше: будущие сборки чисел не пишут, это порча, а
  не forward-compat. `StateJson.Options` (`allowIntegerValues: false`) не менялся; гейт — reflection-тест
  `NoDtoProperty_IsEnumTyped`.
- Диагностика: одно событие `StateForwardCompatFieldsPreserved {unknownKeyCount, unknownEnumCount,
  unknownSystemIconItemCount}` после `Load()` (только если хоть один счётчик > 0). Только счётчики — ни имён ключей, ни
  raw-значений (в ручной правке это могут быть пользовательские данные; дисциплина `state`-событий, см. Invariants).

### Миграция v6→v7 и правило для будущих миграций

- `V6ToV7Migration` (`FromVersion 6 → ToVersion 7`) — **тождественная**: deep-clone всех ключей. `schemaRevision: 0` миграция
  **не добавляет** (контракт `ISettingsMigration`: миграция видит только payload и не создаёт reserved-ключи) — ревизию
  пишет envelope при `PersistAfterLoad`: итоговый файл `{"schemaVersion":7,"schemaRevision":0,…}`. Premigration-бэкап —
  `application-state.premigration-v6.json` (имя по исходной версии, механизм ADR-043 Amendment).
- **`MigrationJson.CloneAllExcept(source, reshapedKeys)` обязателен для всех миграций от v6 и выше:** «копировать всё, кроме
  явно перестраиваемого», allow-list запрещён (иначе ключи более поздней аддитивной ревизии были бы срезаны при
  миграции). Гейт — `MigrationsPreserveUnknownKeysTests`. `V4ToV5Migration` **не менялась** (историческая, golden-входы
  иммутабельны) и остаётся известным **анти-примером**: она копирует только allow-list top-level ключей и ключей
  снапшота (`id/name/createdAt/panels`).
- **Исправленный дефект (F5):** `StateMigrationRunner.MigrateToCurrent` при сборке результата возвращал только
  `schemaVersion/writtenAtUtc/appVersion`; `schemaRevision` терялся бы (в том числе в `StateRestoreCommand`). Теперь он
  переносится дословно (`MigrateToCurrent_CarriesSchemaRevisionVerbatim`).

### Правило аддитивности (критерий «revision или major»)

Изменение допустимо как **revision** (bump `CurrentSchemaRevision`, без миграции) только если выполнено всё:

1. только **новый опциональный ключ** объекта (или новое значение существующего enum-поля); существующие ключи не
   переименовываются, не удаляются, не меняют тип/форму/смысл;
2. **отсутствие ключа ⇒ документированный дефолт**, воспроизводящий прежнее поведение байт-в-байт;
3. поле **не «must-understand»**: игнорирующая его сборка не делает ничего неверного с известными данными;
4. поле **терпит устаревание** относительно известных соседей (старая сборка правит известные поля и сохраняет новое как
   есть) — новая сборка при чтении валидирует/лечит рассогласование;
5. не новый envelope-ключ и не новый вид элементов в существующих массивах (кроме `systemIconsPanel.items` с неизвестным
   `kind`, обработанным явно);
6. новый enum-член: фолбэк старой сборки приемлем для отображения.

Иначе — **major** (миграция + rung 7 + read-only для всех более старых сборок, пока файл не восстановлен из premigration-бэкапа). Bump ревизии без новой golden-фикстуры
`v7-rN-full.json` не проходит тесты (`SchemaContractTests.RevisionFixtures_ExistForEveryRevisionUpToCurrent`).
**Разрешение пользователя по-прежнему поштучное:** каждое новое persisted-поле, даже аддитивное, требует своего явного
«да» в самой задаче (`CLAUDE.md`, раздел «Настройки и persistence — ЗАМОРОЖЕНЫ»); меняется только *цена* разрешённого
аддитивного поля (без миграции и без read-only для старой v7-сборки).

### Известное последствие — связано с событием major-bump

После перехода файла на v7 **каждый** запуск сборки **≤0.8.0** на этом файле остаётся read-only (rung 7, `NewerSchema`):
панели не показываются, файл не перезаписывается, — пока пользователь не восстановит
`application-state.premigration-v6.json` вручную (payload v6 и v7 **совпадают**, т. к. миграция тождественная) или не
вернётся на сборку ≥v7. «Однократно» относится к *событию* major-bump (последнему такому разрывом совместимости: дальнейшие
аддитивные изменения идут через `schemaRevision` без bump major), а **не** к одной сессии. Уведомление
`ReadOnlySessionNotice` (Amendment 2026-10-09) есть только в сборках, выпущенных после 0.8.0; сама 0.8.0 открывает файл
read-only без уведомления. Формулировка вынесена в release notes (`CHANGELOG.md`).

**NIT (ручная правка файла).** `schemaRevision` вида `1.0` или значение больше `int.MaxValue` не читается как `int` —
считается malformed → `0` (событие `StateRevisionMalformed`, не карантин). Тогда high-water mark может понизиться на
записи. Достижимо только ручной правкой файла; сборки приложения пишут целое `schemaRevision`. **Ограничение (R9):** любой будущий хотфикс ветки 0.8.x (сборка с v6) на профиле,
открывавшемся v7-сборкой, тоже будет read-only — хотфиксы v6-линии после выхода v7 не выпускать.

### Границы разрешения (что менялось и что нет)

Менялось: `schemaVersion` 6→7 + `schemaRevision`, `StateEnvelope`/`JsonApplicationStateStore`/`StateMigrationRunner`,
`V6ToV7Migration`/`MigrationJson`, DTO и маппинги `UI/State`, `ChildWindowModel.PersistedExtras`, `UnknownFieldLedger`,
`TolerantEnumParser`. **Не менялись:** новые persisted-поля с данными (`CurrentSchemaRevision` остаётся `0` — введён
механизм, а не поля), `IAppSettings`/`user.config`, протокол атомарной записи (D-6), `.bak`, карантин, advisory lock (D-8),
пути, `LayoutSnapshotService`, `PageManagerService`, `SystemIconsPanelManager`, `StateJson`, миграции V1–V5.
`user.config`/`IAppSettings` forward-compat этим Amendment **не покрыт** (R12).

## Amendment 2026-10-10 (ADR-050, решение владельца D-1=A): путь корня хранилища зависит от канала распространения

**Контекст.** Второй канал распространения — Microsoft Store (MSIX, [ADR-050](ADR-050-microsoft-store-channel-msix.md)).
`%LOCALAPPDATA%` процесса с package identity виртуализируется в `LocalCache` пакета, поэтому корень Amendment
2026-09-25 (`%LOCALAPPDATA%\DesktopOrganizer`) в Store-сборке привёл бы к потере файлов пользователя при Uninstall/Reset
(Move-семантика, [ADR-009](ADR-009-drag-drop-move-semantics.md) Amendment 8).

**Явное разрешение владельца (2026-10-10).** Путь корня хранилища входит в замороженный контур persistence
(`CLAUDE.md`); владелец разрешил его изменить **ровно в объёме пути корня для процесса с package identity**.

**Решение.** `AppStorageRoot.Default` выбирает родителя по identity, имя папки — по конфигурации сборки (Amendment
2026-09-25 не отменяется, а дополняется):

| Identity | Debug | Корень |
|---|---|---|
| нет | нет | `%LOCALAPPDATA%\DesktopOrganizer` |
| нет | да | `%LOCALAPPDATA%\DesktopOrganizer.Dev` |
| есть | нет | `%USERPROFILE%\DesktopOrganizer` |
| есть | да | `%USERPROFILE%\DesktopOrganizer.Dev` |

При пустом `%USERPROFILE%` упакованный процесс откатывается на `%LOCALAPPDATA%` (`AppStorageRoot.IsPackagedLocation`
= `false`). `State\`, `PanelItems\`, `Logs\` и `.storage-lock` (D-8) по-прежнему выводятся из единого
`AppStorageRoot.Default`; одна установка использует только один корень, миграции данных между каналами нет.
Identity определяется одной пробой (`GetCurrentPackageFullName`, статический `Lazy` в `NativeApi`, читается только
`AppStorageRoot` — задокументированное исключение из «P/Invoke только за `INativeApi`», [ADR-050](ADR-050-microsoft-store-channel-msix.md) D-1).

**Что НЕ менялось (границы разрешения).** `schemaVersion` (7), `schemaRevision` (0), `StateEnvelope`, DTO и маппинги,
миграции (лестница до `V6ToV7Migration` включительно), `JsonApplicationStateStore` (протокол записи D-6, `.bak`, карантин, advisory lock D-8,
recovery-лестница D-7, rung «newer schema → read-only»), `ApplicationStateService`, `PanelStateMapper`, round-trip неизвестных
полей и толерантные enum, `IAppSettings`/`AppSettings`/`Settings.settings`/`user.config`, `LayoutSnapshotService`,
семантика загрузки/сохранения. **Новых persisted-полей нет**: баннер первого запуска не сохраняет признак показа,
`README.txt` в корне — не состояние (никто не читает, нет в схеме и бэкапах). Inno-корень не тронут. Это разрешение не
открывает общего права на будущие изменения контура: каждое новое поле/настройка по-прежнему требует отдельного «да».

**Известное последствие вне разрешения (P1).** `user.config` Store-сборки сбрасывается при каждом обновлении пакета
(`_Url_<hash>` от пути exe, новая папка `WindowsApps\<Name>_<Version>_…`). Исправление меняет `IAppSettings`/`user.config` и
отложено до отдельного явного «да» ([ADR-050](ADR-050-microsoft-store-channel-msix.md), Known limitations).

## Amendments (сводка)

| Дата | Источник | Что изменилось | Какие разделы/решения больше не действуют |
|---|---|---|---|
| 2026-09-22 | [ADR-025](ADR-025-ambient-accent-visual-language.md) | v3→v4: `accentColor`/`surfaceOpacity`/`surfaceStyle` вместо `backgroundColor`/`backgroundOpacity`; лоссивное правило `#1E1E1E` → `#6B7280` | Описание схемы как v3 в теле (D-9: группировка v3); ключи `backgroundColor`/`backgroundOpacity` |
| 2026-09-22 | [ADR-028](ADR-028-workspace-grid.md)/[ADR-029](ADR-029-icon-layout-policy.md) | v4→v5: аддитивные поля геометрии/раскладки | — |
| 2026-09-25 | (без ADR) | `AppStorageRoot`: Debug → `DesktopOrganizer.Dev`, Release → `DesktopOrganizer` | Допущение «один корень `%LOCALAPPDATA%\DesktopOrganizer` для всех сборок» |
| 2026-09-27 | [ADR-032](ADR-032-ui-localization.md) | Аддитивный скаляр `IAppSettings.Language` (с явного разрешения) | Допущение «9 оставшихся скаляров» в разделе Decision (список вырос) |
| 2026-09-29 | [ADR-043](ADR-043-page-display-names.md) | v5→v6: `pages.items[{id,name?}]` | Описание `pages.ids[]` |
| 2026-10-09 | ADR-audit WP-B | Read-only сессия: UI читает `IsWritable` (drop без `MoveIn`, эффект `Copy`, `ReadOnlySessionNotice`, hint, суффикс tooltip трея); `LayoutSnapshotService.Restore` в read-only не возвращает файлы. Формат/семантика записи не менялись | Допущение «read-only сессия невидима пользователю» (D-7 rung 7, D-8) |
| 2026-10-10 | Решение пользователя «Аддитивная ревизия схемы» (коммит `5633bb4`, [план](../docs/PLANS/schema-additive-revision-v7.md)) | v6→v7 (тождественная) + envelope-ключ `schemaRevision` (`CurrentSchemaRevision = 0`); round-trip неизвестных полей (`[JsonExtensionData]` на 10 DTO, `ChildWindowModel.PersistedExtras`, `UnknownFieldLedger`); толерантные enum (`TolerantEnumParser`, `PreservedEnumValue`); файл того же major с более высокой ревизией открывается writable; `MigrationJson.CloneAllExcept` обязателен для миграций ≥v6; дефект `StateMigrationRunner` (терял бы `schemaRevision`) исправлен | D-5 «три reserved-ключа» (теперь четыре); D-7 rung 1 «`schemaVersion ≤ Current`» как единственное условие (теперь и любая ревизия того же major); допущение «любое новое поле = bump major»; «версия схемы — 6» |
| 2026-10-10 | [ADR-050](ADR-050-microsoft-store-channel-msix.md), явное разрешение владельца D-1=A | Корень хранилища зависит от identity: Store (package identity) → `%USERPROFILE%\DesktopOrganizer[.Dev]`, иначе `%LOCALAPPDATA%\DesktopOrganizer[.Dev]`; fallback на `%LOCALAPPDATA%` при пустом профиле; схема/DTO/миграции/`IAppSettings` не менялись | Допущение Amendment 2026-09-25 «Release → `%LOCALAPPDATA%\DesktopOrganizer`» как единственного корня (теперь это Inno-канал) |

Текущая версия схемы — **7, ревизия 0** (`schemaVersion = 7`, `schemaRevision = 0`); механизм хранения заморожен
(`CLAUDE.md`): bump major, новые миграции, **каждое** новое persisted-поле (даже аддитивное через `schemaRevision`) и
скаляры `IAppSettings` — только с явного разрешения пользователя в самой задаче.

