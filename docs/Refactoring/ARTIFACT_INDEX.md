# Artifact Index

## Planning
- `README.md`
- `ROADMAP.md`
- `PROJECT_OBSERVATIONS.md`
- `Templates/implementation-order.md`
- `docs/PLANS/phase-1-resize-stabilization.md` — Phase 1 implementation master plan (branch
  `fix/resize-lifecycle`, commits C1–C9)
- `docs/PLANS/phase-2-native-panel-sizing.md` — Phase 2 implementation master plan (branch
  `feat/native-panel-sizing`, commits C1–C7)
- `docs/PLANS/phase-3-single-instance-ipc.md` — Phase 3 implementation master plan (branch
  `feat/single-instance-ipc`, commits C1–C7)
- `docs/PLANS/phase-4-versioned-json-store.md` — Phase 4 implementation master plan (branch
  `refactor/versioned-json-store`, commits C1–C8)
- `docs/PLANS/phase-5-ambient-accent-ui.md` — Phase 5 implementation master plan (branch
  `ui/ambient-accent-design-tokens`, commits C1–C7)
- `docs/PLANS/phase-6-edit-layout-ux.md` — Phase 6 implementation master plan (branch
  `ui/ambient-accent-design-tokens`, points P6-1–P6-7)

## Phase Specs
- `PHASES/PHASE-0-baseline-and-diagnostics.md`
- `PHASES/PHASE-1-resize-stabilization.md`
- `PHASES/PHASE-2-native-resize-and-host.md`
- `PHASES/PHASE-3-single-instance-ipc.md`
- `PHASES/PHASE-4-storage-migration.md`
- `PHASES/PHASE-5-ambient-accent-ui.md`
- `PHASES/PHASE-6-edit-layout-ux.md`
- `PHASES/PHASE-7-product-features.md`
- `PHASES/PHASE-8-native-modules.md`
- `PHASES/PHASE-9-hardening-release.md`

## ADR
- ADR-021 Interaction State (Accepted, Phase 1 code-complete)
- ADR-022 Native Panel Sizing (Accepted, Phase 2 code-complete)
- ADR-023 Single Instance + IPC (Accepted, Phase 3 code-complete)
- ADR-024 Versioned JSON Persistence (Accepted, Phase 4 code-complete)
- ADR-025 Ambient Accent Visual Language (Accepted, Phase 5 code-complete)
- ADR-026 Edit Layout Mode (Accepted, Phase 6 code-complete)
- ADR-027 Structured JSONL Diagnostics Logging for Gesture Lifecycle

## UI Drafts
- design tokens XAML
- `PanelInteractionState.cs` (draft; shipped implementation:
  `src/DesktopOrganizer.UI/Interaction/PanelInteractionState.cs`)
- pure resize geometry seam (draft; shipped implementation:
  `src/DesktopOrganizer.UI/Interaction/ResizeGeometryCalculator.cs`)

## Interaction (Phase 1, shipped — `src/DesktopOrganizer.UI/Interaction/`)
- `PanelInteractionState.cs` — Idle/Hover/Editing/Dragging/Resizing (`Hover`/`Editing` reserved for
  Phase 6)
- `PanelInteractionTracker.cs` — owns `PanelInteractionState` per panel, transition matrix (ADR-021)
- `ResizeEdge.cs` — typed 8-direction enum + `ResizeEdgeParser`/anchor extensions
- `ResizeConstraints.cs` — min size / snap cell / chrome record struct
- `ResizeGeometryCalculator.cs` — pure snap/clamp/reposition math, Phase-2-`WM_SIZING`-compatible API
- `ResizeGestureSession.cs` — idempotent Begin/Update/End gesture state machine, `ResizeEndReason`
- corresponding tests: `tests/DesktopOrganizer.Tests/Unit/Interaction/`
  (`ResizeGeometryCharacterizationTests.cs` — C1 golden values pinned pre-extraction,
  `ResizeGeometryCalculatorTests.cs`, `PanelInteractionTrackerTests.cs`, `ResizeGestureSessionTests.cs`)

## Native Sizing (Phase 2, shipped; since 2026-10-10 the ONLY resize path — the `NativePanelSizing` flag and the WPF Thumb path were removed, ADR-022 Phase 1)
- `src/DesktopOrganizer.Services.Abstractions/Interfaces/INativePanelSizingService.cs` —
  `INativePanelSizingService`/`INativePanelSizingClient`, `NativeSizingEdge`, `NativeRectPx`, `NativePointPx`
- `src/DesktopOrganizer.Services/Tools/NativePanelSizingService.cs` — `HwndSource.AddHook`, decodes
  6 Win32 sizing messages, zero snap/geometry policy
- `src/DesktopOrganizer.UI/Interaction/Native/NativeHitTestMapper.cs` — `WM_NCHITTEST` → edge/corner zones
- `src/DesktopOrganizer.UI/Interaction/Native/NativeSizingCoordinateMapper.cs` — px↔DIP rounding reference; no production callers since ADR-037 (tests only, DO-050)
- `src/DesktopOrganizer.UI/Interaction/Native/NativeSizingOriginResolver.cs` — self-calibrating `WM_SIZING`
  coordinate-space offset (R-3)
- `src/DesktopOrganizer.UI/Interaction/Native/NativeSizingEdgeMap.cs` — `NativeSizingEdge` ↔ `ResizeEdge` ↔ `WMSZ_*`
- ~~`NativeSizingSwitch.cs` — feature-flag resolution (env > CLI > setting)~~ — removed (ADR-022 Phase 1)
- `src/DesktopOrganizer.UI/Behaviors/NativePanelResizeController.cs` — `INativePanelSizingClient`
  implementation, reuses Phase 1 `ResizeGestureSession`/`ResizeGeometryCalculator` unmodified
- `src/DesktopOrganizer.UI/Behaviors/IPanelResizeController.cs` — narrow seam (`AbortResize`/`SnapWidthToGrid`) between `ChildWindow` and the native controller
- `src/DesktopOrganizer.UI/Behaviors/ResizeConstraintsFactory.cs` — constraints builder (gesture + `SnapWidthToGrid` share its predicates)
- `src/DesktopOrganizer.UI/Behaviors/IResizeAffordance.cs` + `NativeSizingAffordance.cs` — roll-up-aware
  resize-grip visibility abstraction (R-9); `ThumbOverlayAffordance.cs` removed (ADR-022 Phase 1)
- corresponding tests: `tests/DesktopOrganizer.Tests/Unit/Interaction/Native/`
  (`NativeHitTestMapperTests`, `NativeSizingCoordinateMapperTests`, `NativeSizingOriginResolverTests`,
  `NativeSizingEdgeMapTests`, `ResizeGeometryCalculatorApplyEquivalenceTests` — renamed from `NativeResizeEquivalenceTests`;
  `NativeSizingSwitchTests` removed), `NativePanelSizingServiceTests`, `NativePanelResizeControllerTests`
  (regression-golden against `ExpectedPipelineOracle`), `Unit/Views/ResizeGripVisualsTests`, `Unit/Views/NativePanelSizingKeyRemovedTests`

## Single Instance + IPC (Phase 3, shipped — see ADR-023)
- `src/DesktopOrganizer.Services.Abstractions/Interfaces/ISingleInstanceCoordinator.cs` —
  `ISingleInstanceCoordinator` (`Start`/`TryBeginServing`/`ActivationRequested`/`EnterShutdown`/
  `CompleteShutdown`), `SingleInstanceRole`, `SingleInstanceStartupAction`, `ActivationSendResult`,
  `ActivationCommand`, `ActivationRequest`, `SingleInstanceStartupDecision`
- `src/DesktopOrganizer.Services/SingleInstance/SingleInstanceNaming.cs` — detection/guard mutex names,
  activation pipe name, `{sidHash}` derivation (SHA-256, no raw SID in object names)
- `src/DesktopOrganizer.Services/SingleInstance/ActivationMessageCodec.cs` — `DO/1` wire format
  encode/decode
- `src/DesktopOrganizer.Services/SingleInstance/SingleInstanceHandshakePolicy.cs` — pure acquire/send
  schedule (6 attempts, 3s budget, injectable clock)
- `src/DesktopOrganizer.Services/SingleInstance/SingleInstanceStartupDecisionTable.cs` — pure
  `(acquired × serverStarted × sendResult)` → `SingleInstanceStartupAction` truth table
- `src/DesktopOrganizer.Services/SingleInstance/ActivationRequestRelay.cs` — buffers activation
  requests arriving before `App`/`StartApp` exist, replays in order on first subscriber attach
- `src/DesktopOrganizer.Services/SingleInstance/IMutexGate.cs` / `MutexGate.cs` — thread-affine guard
  mutex wrapper, `AbandonedMutexException` handling
- `src/DesktopOrganizer.Services/SingleInstance/IActivationChannel.cs` / `NamedPipeActivationServer.cs`
  / `NamedPipeActivationClient.cs` — `maxNumberOfServerInstances: 1` machine-global pipe, per-user ACL
  (`ReadWrite | CreateNewInstance | TakeOwnership` — see ADR-023 deviation), bounded accept-loop
  discipline (500ms per-connection budget, bounded 2s `Stop()`)
- `src/DesktopOrganizer.Services/SingleInstance/SingleInstanceCoordinator.cs` — orchestrates the above;
  `CompleteShutdown()` (new vs. original plan, ADR-023 deviation) writes
  `SingleInstanceShutdownSequence` before the DI container is disposed
- `src/DesktopOrganizer.UI/Program.cs` — process entry point (`<StartupObject>`), runs the handshake
  before constructing `Application` (ADR-023 D-2/R-1), top-level `try/catch` (M-4)
- `src/DesktopOrganizer.UI/Lifecycle/SingleInstanceActivationHandler.cs` — activation handler, calls
  only `IWindowManager.ShowMainWindow()` (ADR-023 D-8)
- `tools/lifecycle/Invoke-SingleInstanceStress.ps1` — 20-launch stress-test helper (manual verify M-1/M-12)
- corresponding tests: `tests/DesktopOrganizer.Tests/Unit/SingleInstance/`, `Unit/Lifecycle/`,
  `Integration/SingleInstance/`, `Unit/Installer/` (`InstallerMutexContractTests`)

## Versioned JSON Store (Phase 4, shipped — see ADR-024, Accepted)
- `src/DesktopOrganizer.Services.Abstractions/Storage/` — `IApplicationStateStore` (incl.
  `QuarantineLoadedDocument(string reason)` — post-review CRITICAL fix C-1, the missing partial-load
  safety net), `StateLoadResult`, `StateLoadStatus`, `StateSaveOutcome`, `StateSaveResult`,
  `StateWriteStage`, `ISettingsMigration`, `ILegacyStateImporter`, `IStorageLock`,
  `IStateRestoreCommand` (C7), `StateRestoreResult`/`StateRestoreOutcome` (C7)
- `src/DesktopOrganizer.Services/Storage/` — `StateStorePaths` (primary/`.bak`/temp/quarantine/lock
  path scheme), `AtomicJsonFileWriter` (temp → fsync → validate → `File.Replace`, ADR-024 D-6, 5
  crash-injection stages; retries `IOException` **and** `UnauthorizedAccessException` — post-review
  MAJOR fix M-3), `AtomicWriteFailedException`, `StateEnvelope` (reserved envelope keys),
  `JsonApplicationStateStore` (7-rung read/recovery ladder D-7, write barrier + no-op guard D-11,
  `QuarantineLoadedDocument` implementation, `HandleMigrationFailure`/`TryMigrate` — post-review MAJOR
  fix M-6, an uncaught migration exception now quarantines + degrades instead of bricking startup),
  `StateMigrationRunner` (ordered, contiguous, fail-closed migration chain), `Migrations/V1ToV2Migration.cs`,
  `Migrations/V2ToV3Migration.cs`, `StorageFileLock` (`FileShare.None` advisory lock, ADR-024 D-8,
  closes ADR-023 R-15), `StateRestoreCommand` (C7 — `--restore-state-backup`, ADR-024 §7 scenario C;
  write-new-primary-before-quarantine-old ordering — post-review MAJOR fix M-4)
- `src/DesktopOrganizer.UI/State/` — `IApplicationStateService`/`ApplicationStateService` (single
  load-once + `SaveScheduler`, ADR-024 D-11/D-12; `ConsumersReady`/`MarkConsumersReady()` — post-review
  MAJOR fix M-1, the second write-barrier half; `ReportPartialLoad(string reason)` — delegates to
  `IApplicationStateStore.QuarantineLoadedDocument`, C-1), `Dto/ApplicationStateDocumentDto.cs` +
  `Dto/PanelStateDto.cs`/`PanelGeometryDto.cs`/`PanelAppearanceDto.cs`/`PanelContentDto.cs`/
  `PagesStateDto.cs`/`LayoutSnapshotStateDto.cs`/`SystemIconsPanelStateDto.cs` (schema-v3 shape),
  `PanelStateMapper.cs`/`SystemIconsPanelStateMapper.cs` (UI-type ↔ schema boundary), `StateJson.cs`
  (single `JsonSerializerOptions` for the whole document), `LegacySettingsImporter.cs` (v0 `user.config`
  → schema v1)
- `src/DesktopOrganizer.UI/Services/WindowManager.cs` — `LoadWindowsState()` calls
  `IApplicationStateService.ReportPartialLoad("PartialPanelLoad")` whenever `CreateEachIsolated`
  reports `skipped > 0` (C-1)
- `src/DesktopOrganizer.UI/Services/LayoutSnapshotService.cs` — `Load()` idempotence guard (`_loaded`)
  — post-review MAJOR fix M-2, a second call no longer wipes snapshots created earlier in the session
- `src/DesktopOrganizer.UI/App.xaml.cs` — `StartApp` calls `ApplicationState.MarkConsumersReady()` as
  the last startup step, after all four state-owning consumers have loaded (M-1)
- `src/DesktopOrganizer.UI/Program.cs` — storage lock acquire → `ApplicationStateService.Load()` →
  `new App(...)` ordering (D-11); `--restore-state-backup` CLI switch (C7), handled entirely before
  the single-instance handshake
- corresponding tests: `tests/DesktopOrganizer.Tests/Unit/Storage/` (`AtomicJsonFileWriterTests`,
  `JsonApplicationStateStoreTests`, `StateMigrationRunnerTests`, `Migrations/V1ToV2MigrationTests`,
  `Migrations/V2ToV3MigrationTests`, `GoldenMigrationTests`, `LegacyStateImporterTests`,
  `StorageFileLockTests`, `SchemaContractTests`, `StateRestoreCommandTests`), `Unit/State/`
  (`PanelStateMapperTests`, `ApplicationStateServiceTests`), `Unit/Services/` (`WindowManagerTests`,
  `LayoutSnapshotServiceTests`), golden fixtures under
  `tests/DesktopOrganizer.Tests/Resources/Storage/golden/`. 1551/1551 tests green.
- docs: `Storage/rollback.md` (C7), `Testing/storage-manual-matrix.md` (C7, S-1..S-11),
  `Testing/diagnostics-log-events.md` `state` category (C7 + post-review: `StateMigrationFailed`,
  `StateRestoreFailed`, `StateQuarantined.reason`, `StateWriteBarrierViolation.caller` two-value set)

## Edit Layout UX (Phase 6, shipped — see ADR-026, Accepted)
- `src/DesktopOrganizer.UI/Models/LayoutInteractionMode.cs` — `Normal`/`EditLayout`, exactly two values
  (D-1)
- `src/DesktopOrganizer.UI/Models/NormalModeLayoutGestures.cs` — `MoveAndResize`/`MoveOnly`/`None` +
  parser (default `MoveOnly`, D-2)
- `src/DesktopOrganizer.UI/Models/LayoutGesturePolicy.cs` — pure `Resolve(mode, isLayoutLocked,
  normalPolicy)` truth table (12 cases)
- `src/DesktopOrganizer.UI/Services/ILayoutEditModeService.cs` / `LayoutEditModeService.cs` — app-global
  singleton, `Mode`/`IsLayoutLocked`/`CanEnterEditLayout`, `ModeChanged` event, `LayoutModeChangeSource`
- `src/DesktopOrganizer.UI/Behaviors/ResizeAffordanceGate.cs` — `IResizeAffordance` composite (roll-up
  gate × layout-policy gate, D-5)
- `src/DesktopOrganizer.UI/Behaviors/LayoutEditModeBehavior.cs` — bridges the service to
  `PanelTheme.IsLayoutEditing` attached DP
- `src/DesktopOrganizer.UI/Theming/LayoutEditVisuals.cs` — additive emphasis layer over
  `InteractionVisualState` (border floor 0.60, chrome forced 1.0, accent ×1.35, D-6)
- `src/DesktopOrganizer.UI/Models/SnapGuideCalculator.cs` — pure inter-panel alignment-guide math,
  inserted between `GridSnap` and `PanelCollision.ClampAgainstObstacles` (D-7); P6-6 visual overlay
  (`LayoutGuideOverlayWindow`) not implemented this iteration — see `DO-018`
- `src/DesktopOrganizer.UI/Behaviors/PanelDragMoveBehavior.cs` — `canMove` gate, drag-start threshold,
  public `AbortDrag()`, guide-snap pipeline insertion
- `src/DesktopOrganizer.Services/Providers/TrayIconManager.cs` — "Edit layout"/"Lock layout" checkable
  tray items, tooltip when disabled
- `src/DesktopOrganizer.UI/Diagnostics/IGestureDiagnostics.cs` — `LayoutModeChanged` (category `layout`,
  D-11)
- corresponding tests: `tests/DesktopOrganizer.Tests/Unit/Models/` (`LayoutGesturePolicyTests`,
  `SnapGuideCalculatorTests`), `Unit/Services/` (`LayoutEditModeServiceTests`, `TrayIconManagerTests`),
  `Unit/Behaviors/` (`ResizeAffordanceGateTests`), `Unit/Theming/` (`LayoutEditVisualsTests`).
  1925/1925 tests green.
- docs: `Testing/edit-layout-manual-matrix.md` (V-1..V-23), `Testing/diagnostics-log-events.md`
  `layout` category

## Native Drafts
- sizing service abstraction (superseded by shipped `INativePanelSizingService` above; kept as
  reference form per ADR-022 D-1 — `Native/*.cs` namespace/signature not carried into production)
- HwndSource implementation skeleton
- optional C++ module ABI guidance

## Storage Drafts
- state store contract
- migration plan

## Testing
- resize repro matrix (`Testing/resize-repro-matrix.md`)
- performance baseline template (`Testing/performance-baseline-template.md`) + procedure (`Testing/performance-baseline-procedure.md`)
- release test matrix
- diagnostics log event schema (`Testing/diagnostics-log-events.md` — includes `state` category, Phase 4,
  and `layout` category, Phase 6)
- single-instance manual verify matrix, M-1..M-13 (`Testing/single-instance-manual-matrix.md`)
- storage manual verify matrix, S-1..S-11 (`Testing/storage-manual-matrix.md`, Phase 4)
- edit layout UX manual verify matrix, V-1..V-23 (`Testing/edit-layout-manual-matrix.md`, Phase 6)
- screenshot baseline checklist (`Testing/screenshot-baseline-checklist.md`) + `Screenshots/`
- STA/WPF test conventions (`Testing/sta-test-conventions.md`) — one owner for `Application.Current`,
  never shut the owner's dispatcher down, stop every timer you start; includes the case study of the
  intermittent `Test Run Aborted` these rules came from

## Backlog
- `BACKLOG.md` — issue list seeded from REFACTORING_PLAN.md §5.2 (Phase 0 §5 п.5)

## Tooling
- `tools/perf/New-PerfFixture.ps1` — generates N-panel/M-item layout fixture; writes directly to
  `%LOCALAPPDATA%\DesktopOrganizer\State\application-state.json` in schema-v3 shape (rewritten in
  Phase 4 C7 — previously wrote to `user.config`, which Phase 4 C6 stopped reading, ADR-024 R-13)
- `tools/perf/Measure-Startup.ps1` — measures startup time/working set over N runs
