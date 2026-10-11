# ADR-027: Structured JSONL Diagnostics Logging for Gesture Lifecycle

**Статус:** Accepted (2026-10-09, ADR-audit WP-E; реализован в Фазе 0 и расширялся с 2026-09-22; на нём стоят Accepted-ADR-031/033/037/045, выпущен v0.8.0) — Amended (ADR-033: always-on уровень `Standard`, `IAppLog`) — Amended 2026-09-22 (P08: категория `icon`, `IconDiagnostics.SafeExtension` allow-list) — Addendum 2026-09-27 (L12: префикс файла `gestures-` → `app-`) — Amended 2026-10-10 (ADR-050: папка `Logs\` лежит под корнем канала; новые события `IAppLog` `PackageIdentity`, `WriteIgnoredPackaged`, `StorageRootReadme/WriteFailed`)

## Context

Resize/drag gestures on panels occasionally hang or leave stale mouse-capture state (see ADR-021,
`PanelInteractionState`), but there was no way to reconstruct what actually happened during a specific
hang on a tester's machine. The project had **zero** logging infrastructure at all — a repository-wide
search for `ILogger`, `Serilog`, `Microsoft.Extensions.Logging` returns no results anywhere in `src/`.

Phase 0 of `docs/Refactoring/` needs a "structured log event list" artifact (`PHASE-0` §2, §7) so that
Phase 1 (`ADR-021`, resize/gesture-lifecycle stabilization) has repro evidence instead of guesswork: for
every scenario in `resize-repro-matrix.md`, the last logged event before a hang should be identifiable
from a file a tester can just email over.

## Alternatives Considered

- **(A) `Microsoft.Extensions.Logging` + a custom provider.** Rejected: pulls in a new package
  dependency for a diagnostics-only phase, and its `ILogger`/message-template model doesn't produce
  JSON Lines out of the box — a custom formatter would still have to be hand-rolled on top, at which
  point the abstraction buys little over writing our own.
- **(B) Serilog.** Rejected: heavier dependency (core package + JSON sink + enrichers) than warranted
  for what is, at this stage, a narrowly-scoped gesture-lifecycle tracer, not general application
  logging.
- **(C) `EventSource`/ETW.** Rejected: consuming ETW output requires PerfView or `logman` on the
  tester's machine, which breaks the "reproduce the hang → attach a file" workflow this diagnostics
  effort exists for. ETW is also awkward to gate on/off per-build without extra tooling.
- **(D) Own lightweight abstraction + JSONL file sink (chosen).** Zero new dependencies, full control
  over the non-blocking producer/backpressure contract described below, and the file format (JSON
  Lines) is both human-greppable and trivially loadable by a script for later automated triage.

## Decision

Two purpose-built interfaces, kept intentionally small and specific to gesture-lifecycle diagnostics
rather than general-purpose logging:

- **`IDiagnosticsLog`** (`DesktopOrganizer.Services.Abstractions.IDiagnosticsLog`, implemented by
  `JsonLinesDiagnosticsLog`/`NullDiagnosticsLog` in `DesktopOrganizer.Services`) — a generic
  "write a structured record" sink: `bool IsEnabled { get; }`, `void Write(string category, string
  name, string? scope, params KeyValuePair<string, object?>[] payload)`, `void Flush()`, and
  `IDisposable`. `JsonLinesDiagnosticsLog` owns a background writer thread; `Write` enqueues onto a
  bounded queue and returns immediately.
- **`IGestureDiagnostics`** (`DesktopOrganizer.UI.Diagnostics`, implemented by `GestureDiagnostics`,
  transient per panel via DI) — a typed facade over `IDiagnosticsLog` with one method per gesture
  lifecycle event (`ResizeStarted`/`ResizeDelta`/`ResizeCompleted`, `DragStarted`/`DragDelta`/
  `DragCompleted`, `CaptureGot`/`CaptureLost`, `SessionStarted`/`SessionExiting`, `PanelClosing`), so
  call sites in `PanelResizeController`/`PanelDragMoveBehavior`/`ChildWindow` never construct raw
  `KeyValuePair` payloads by hand. Delta events (`ResizeDelta`/`DragDelta`) are throttled to at most
  one emission per 50ms (`GestureDiagnostics.DefaultDeltaSampleIntervalMs`), always emitting the first
  delta of a gesture and always folding the last (possibly throttled-away) delta into the terminal
  `ResizeCompleted`/`DragCompleted` event so the final pre-hang geometry is never lost.

Enablement: on by default in Debug builds, off by default in Release; overridable via `--diag` command
line flag or `DESKTOPORGANIZER_DIAG=1` environment variable (`DiagnosticsSwitch.IsEnabled`, in
`DesktopOrganizer.Services/Diagnostics/`). When disabled, `IsEnabled` is checked by callers *before*
building a payload array — a zero-cost seam, not just a cheap no-op sink.

Output: JSON Lines at `%LOCALAPPDATA%\DesktopOrganizer\Logs\app-{yyyyMMdd-HHmmss}-{pid}.jsonl` (renamed
from the original `gestures-` prefix — see the L12 addendum below),
UTF-8 without BOM, one JSON object per line (`timestamp`, `seq`, `category`, `name`, `scope`,
`payload`). Rotates to `-1`/`-2` suffixes past 10MB per session file; retains at most 10 session files,
pruning older ones at startup. Full schema and per-event payload contract:
`docs/Refactoring/Testing/diagnostics-log-events.md`.

### Non-blocking producer invariant

`IDiagnosticsLog.Write` must never perform file I/O on the calling (UI) thread and must never throw
back into the caller — a diagnostics failure must never affect a live gesture. This was not free: the
first implementation had two CRITICAL defects here (C-1/C-2 in the `code-quality-reviewer` pass, both
fixed on this branch, with regression tests added):

- `Dispose()` disposed synchronization primitives (`SemaphoreSlim`/queue) even when the background
  writer thread had not finished draining within its shutdown timeout, causing an `ObjectDisposedException`
  race on the writer thread instead of a clean, bounded shutdown.
- `Write()` could race with a concurrent `Dispose()` and throw out of a UI mouse-event handler
  (`Thumb.DragDelta`/`Thumb.DragCompleted`) instead of silently dropping the event — i.e. diagnostics
  could crash the very gesture it was observing.

Both are now covered by the shutdown-race/backpressure regression suite (commit `399c23d`).

### Privacy invariant

`payload` may only ever contain: `scope` (an 8-character panel-id `Guid` prefix, never a full id tied
to a directory name), geometry (`double`), enum names (`string`, from a closed set), `bool`, and
count/duration (`int`/`long`). It must **never** contain panel titles, file/folder names or paths, or
`ContentFilter` text — testers attach these files to bug reports, so the log must not be able to leak
personal data. This is mechanically reinforced, not just documented: the fallback payload serializer
writes `value.GetType().Name` rather than `value.ToString()` for any object that isn't one of the
allowed primitive shapes, so a future call site that accidentally passes a rich object (e.g. a
`FileSystemInfo` or a model with a title property) degrades to a harmless type name instead of leaking
its string representation. Enforced further by `GestureDiagnosticsTests` and documented in
`DiagnosticEvent`'s XML doc.

### Schema stability across phases

The event schema already reserves `SizeMoveEntered`/`SizeMoveExited` (mapping to
`WM_ENTERSIZEMOVE`/`WM_EXITSIZEMOVE`) as **Phase 2 — not implemented yet** entries in
`diagnostics-log-events.md`, so that when Phase 2 (native resize, ADR-022) lands, it is a pure additive
schema change rather than a breaking rename/rework of the Phase 0 event set.

## Consequences

Positive:
- Every gesture hang reported by a tester can, in principle, be triaged from an attached `.jsonl` file
  alone — last logged event per panel `scope`, and specifically whether `CaptureLost` occurred without
  a following `ResizeCompleted` (the capture-leak signature Phase 1 targets, per ADR-021).
- Zero new third-party dependencies.
- The non-blocking/never-throw contract is enforced by regression tests, not just a docstring.
- Purely additive/observational: no gesture behavior, geometry, or timing was changed to add this
  instrumentation — confirmed byte-for-byte by `fences-ux-parity-reviewer`.

Negative:
- Another hand-rolled abstraction to maintain instead of an off-the-shelf logging library; if the
  project's logging needs grow beyond gesture diagnostics (e.g. general app logging, remote telemetry),
  this abstraction will likely need to be replaced or wrapped rather than simply extended.
- Background writer thread and bounded queue add a small amount of always-present (if usually idle in
  Release) lifecycle surface to reason about — shutdown ordering, drop-on-full backpressure — that a
  synchronous or no-op logger would not have.
- JSONL schema stability (see above) is a discipline that has to be actively maintained by future
  phases, not something the type system enforces.

## Invariants

- `IDiagnosticsLog.Write`/`IGestureDiagnostics.*` never block the UI thread on file I/O and never throw
  into the caller.
- `payload` values are restricted to `scope`/geometry/enum-name/bool/count/duration; no titles, file
  names, or paths — mechanically backstopped by the fallback serializer using `GetType().Name`.
- Delta events (`ResizeDelta`/`DragDelta`) throttle to ≤1 emission/50ms but never drop the gesture's
  first or final delta.
- New gesture-lifecycle events (e.g. Phase 2's `SizeMoveEntered`/`SizeMoveExited`) are additive to the
  schema in `diagnostics-log-events.md`, never a rename/removal of existing event names or fields
  within the same major schema.

## Amendment (2026-09-22, P08 fix cycle 1 — M2 finding)

**Context.** The `icon` category (added in the earlier P08 pass, see `docs/DIAGNOSTICS/icon-loading-findings.md`)
carries one narrow, documented exception to the privacy invariant above: a bounded file extension.
Its first implementation (`IconDiagnostics.SafeExtension`) only length-capped whatever
`Path.GetExtension` returned (≤16 characters) and lower-cased it. `Path.GetExtension` has no notion of
a "real" registered file extension, though — for a path like `C:\Users\ivan.petrov` (a DIRECTORY whose
own trailing name segment contains a dot — e.g. a Windows user-profile folder), it happily returns
`.petrov`, which fits comfortably under the 16-character cap. The length guard alone did nothing to stop
a surname or other personal-data fragment reachable this way from reaching the payload — a direct
violation of this ADR's own privacy invariant ("no titles, file names, or paths"), just via a narrower
channel than a raw path.

**Decision.** `IconDiagnostics.SafeExtension` now applies two guards instead of a length-only cap,
closing two different (not fully overlapping) slices of the risk:

- A directory (`Directory.Exists(path)` true) always yields `string.Empty` — its own name is never
  treated as a source of a file-type signal at all. This closes the exact `ivan.petrov` example above.
- For a FILE (not a directory), the value is only passed through verbatim if it matches
  `^\.[a-z0-9]{1,8}$` (case-insensitive, checked lower-cased) — closing the unicode/punctuation/
  implausibly-long class of fragment (e.g. `.日本語`, `.a-b`) by degrading to the closed literal
  `"other"` instead of leaking the raw fragment.

**Known, accepted residual limitation:** a short, alphanumeric FILE-name fragment that happens to look
exactly like a plausible extension (e.g. a file literally named `report.smith`) is structurally
indistinguishable from a real one and is NOT filtered by the regex — there is no way to tell "an obscure
but real extension" from "a surname that happens to be short and alphabetic" without an explicit
allow-list of known real extensions (see "Alternatives considered" below for why that isn't used
instead). Documented explicitly, not silently missed — see
`IconDiagnosticsTests.SafeExtension_ShortAlphanumericFileNameFragment_IsAcceptedAsIfItWereARealExtension`.

**Alternatives considered:**
- **Maintain an explicit allow-list of known extensions** (`.exe`, `.lnk`, `.txt`, …) — rejected: an
  unbounded, ever-growing list to maintain, and it would STILL let through the "smith"/"petrov"-shaped
  residual case above unless the personal-name fragment happened to collide with a listed real
  extension (extremely unlikely) — i.e. it doesn't actually close the residual limitation either, it
  only adds maintenance burden for the cases the regex already handles just as well structurally.
- **Hash the extension instead of an allow-list** — rejected: defeats the entire purpose of the field
  (distinguishing "a shortcut's icon failed" from "a folder's icon failed" in a human-read attached
  log), for a privacy gain the allow-list already achieves for the directory case (the one actually
  reported) without losing readability.

**Consequences.** Purely additive to the schema — no event name, category, or other field changed;
only the `extension` value's derivation got stricter. Full schema/testing detail:
`docs/Refactoring/Testing/diagnostics-log-events.md`'s `icon` category privacy note. Tests:
`tests/DesktopOrganizer.Tests/Unit/Diagnostics/IconDiagnosticsTests.cs` (new file).

## Addendum (2026-09-27): L12 — file prefix renamed `gestures-` → `app-`

**Problem (cosmetic, found by the 1.0 release-hardening code review).** The sink's file prefix was still
literally `gestures-` (`gestures-{yyyyMMdd-HHmmss}-{pid}.jsonl`), left over from when this log only ever
carried gesture lifecycle events. Since then it has carried every app diagnostics event (icon
resolution, monitor changes, shutdown steps, crash-adjacent instrumentation, …) — the name no longer
described its contents, and a tester asked to "find the `app-*.jsonl` file" would find nothing.

**Fix.** `DiagnosticsLogFileNaming.Prefix` and `JsonLinesDiagnosticsLog.FilePattern` both changed from
`"gestures-"`/`"gestures-*.jsonl"` to `"app-"`/`"app-*.jsonl"`. Purely a file-name convention — no schema,
category, event name, or payload field changed, and no migration of already-written files is needed
(this log is a rolling, retention-pruned diagnostics artifact, never a source of truth read back by the
app itself — see the non-blocking producer/backpressure contract above). Tests updated:
`DiagnosticsLogFileNamingTests`, `JsonLinesDiagnosticsLogTests`.

## Amendment (2026-09-27, ADR-033): always-on `Standard` level and `IAppLog`

[ADR-033](ADR-033-crash-safety-and-app-log.md) puts a second facade, `IAppLog`/`DiagnosticsAppLog`, on top of
the **same** single sink (`JsonLinesDiagnosticsLog`) with levels `DiagnosticsLevel { Standard, Verbose }`
(`DiagnosticsSwitch`): `Standard` is the Release default and is always on; `Verbose` is the Debug default or
`--diag` / `DESKTOPORGANIZER_DIAG=1`. `IDiagnosticsLog`'s own semantics are unchanged — it resolves to
`NullDiagnosticsLog` under `Standard`, so gesture-lifecycle tracing (this ADR's subject) still only runs under
`Verbose`. `LayoutModeChanged` (category `layout`, ADR-026) is one of its producers.

## Amendment (2026-10-10, ADR-050): `Logs\` лежит под корнем канала распространения

Папка логов (`Logs\app-*.jsonl`) по-прежнему выводится из единого корня `AppStorageRoot` (теперь через
`AppStorageRoot.LogsDirectory`), а корень зависит от канала ([ADR-024](ADR-024-versioned-json-persistence.md) Amendment
2026-10-10, [ADR-050](ADR-050-microsoft-store-channel-msix.md) D-2): Inno — `%LOCALAPPDATA%\DesktopOrganizer\Logs`, Store
(package identity) — `%USERPROFILE%\DesktopOrganizer\Logs`. Любое упоминание `%LOCALAPPDATA%\DesktopOrganizer\Logs` в этом
ADR и в [diagnostics-log-events.md](../docs/Refactoring/Testing/diagnostics-log-events.md) читается как «`Logs\` под корнем
канала». Формат, sink, уровни, ротация и privacy-инвариант не менялись.

Новые события `IAppLog` (sink `app-*.jsonl`, [ADR-033](ADR-033-crash-safety-and-app-log.md)); реестр — `diagnostics-log-events.md`:
`Program/PackageIdentity` (Info: `packaged`, `underUserProfile`, `probeResultCode`, `storageRoot`),
`AutostartService/WriteIgnoredPackaged` (Warn), `StorageRootReadme/WriteFailed` (Warn: `root`, `error`, `message`),
`StorageRootReadme/ResourceMissing` (Warn), `TrayIconManager/ShowInfoBalloonFailed` (Error). Строковые поля с путями
проходят через `LogScrubber` (корневые пути ⇒ `<path>`, имя учётной записи ⇒ `%USERPROFILE%`).

## Amendments (сводка)

| Дата | Источник | Что изменилось | Какие разделы/решения больше не действуют |
|---|---|---|---|
| 2026-09-22 | P08 fix cycle 1 (M2) | Категория `icon`; `IconDiagnostics.SafeExtension` — две защиты вместо length-only cap | — |
| 2026-09-27 | L12 (Addendum) | Префикс файла `gestures-` → `app-` | `gestures-{yyyyMMdd-HHmmss}-{pid}.jsonl` |
| 2026-09-27 | [ADR-033](ADR-033-crash-safety-and-app-log.md) | Единый sink обслуживает и always-on `IAppLog` (`Standard`), и `IDiagnosticsLog` (`Verbose`) | «Enablement: off by default in Release» — для самого файла-sink (теперь always-on на `Standard`); для gesture-трассировки остаётся в силе |
| 2026-10-10 | [ADR-050](ADR-050-microsoft-store-channel-msix.md) | `Logs\` — под корнем канала (Store: `%USERPROFILE%\DesktopOrganizer\Logs`); события `PackageIdentity`, `WriteIgnoredPackaged`, `StorageRootReadme/WriteFailed`/`ResourceMissing`, `ShowInfoBalloonFailed` | Допущение, что лог всегда лежит в `%LOCALAPPDATA%\DesktopOrganizer\Logs` |

## Related

- ADR-021 (Explicit Panel Interaction State and Gesture Lifecycle) — this diagnostics log is the
  observational prerequisite for Phase 1's resize/gesture-lifecycle stabilization work.
- ADR-022 (Native Panel Sizing) — Phase 2 consumer of the reserved `SizeMoveEntered`/`SizeMoveExited`
  schema entries.
- `docs/Refactoring/Testing/diagnostics-log-events.md` — canonical event/payload schema.
- `docs/Refactoring/Testing/resize-repro-matrix.md` — triage procedure using this log.
