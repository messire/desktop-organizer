# Roadmap

## Правило выполнения

Каждая фаза должна завершаться отдельным reviewable набором изменений. Нельзя начинать следующую фазу, пока её prerequisite не закрыт по DoD. Фазы 5 и 7 могут частично идти параллельно только после стабилизации Phase 1-4.

| Phase | Назначение | Риск | Примерный тип работ | Статус |
|---|---|---:|---|---|
| 0 | Baseline + диагностика | Low | тесты, логирование, repro | `[~]` код завершён (ADR-027, `IDiagnosticsLog`/`IGestureDiagnostics`, 983/983 тестов); baseline CPU/RAM/startup, screenshots и repro-matrix — manual verify (Win10/Win11), не выполнено в этой среде |
| 1 | Стабилизация resize и gesture lifecycle | High | WPF/UI behavior | `[~]` код + автотесты завершены (ADR-021 Accepted, явный gesture lifecycle, `ResizeGeometryCalculator`, capture hardening, self-healing, condition persist, roll-up H3-фикс, 1084/1084 тестов); 15 строк repro-matrix + H3/M-1/M-6/M-3 live-проверки — manual verify (Win10/Win11), не выполнено в этой среде |
| 2 | Native resize + desktop boundary | High | Win32/C#/optional C++ | `[~]` код + автотесты завершены (ADR-022 Accepted, `NativePanelResizeController`/`NativePanelSizingService`, 1287/1287 тестов на момент завершения фазы; **с 2026-10-10 native — единственный путь ресайза: флаг `NativePanelSizing`, `NativeSizingSwitch` и Thumb-путь удалены (ADR-022 Фаза 1), golden-эквивалентность с легаси-путём заменена regression-golden `ExpectedPipelineOracle`**); native-путь проверен пользователем на Windows 11 (гейт Фазы 1, 2026-10-10), открытый риск MV-7 (ESC), матрица `resize-repro-matrix.md` обновлена под единственный путь |
| 3 | Single instance + IPC | Medium | app lifecycle | `[~]` код + автотесты завершены (ADR-023 Accepted, реальный per-session guard mutex + машинно-глобальный Named Pipe тай-брейкер, guard в собственном `Program.Main` до создания `Application`, 1387/1387 тестов); матрица M-1..M-13 (`docs/Refactoring/Testing/single-instance-manual-matrix.md`) — manual verify (Win10/Win11), не выполнено в этой среде |
| 4 | Versioned JSON persistence | Medium | storage/migration | `[~]` код + автотесты завершены (ADR-024 Accepted, единый JSON-store вместо четырёх писателей в `user.config`, атомарная запись + 7-ступенчатая recovery-лестница + 3-версионная цепочка миграций + advisory storage lock (закрывает ADR-023 R-15), 1551/1551 тестов); матрица S-1..S-11 (`docs/Refactoring/Testing/storage-manual-matrix.md`) — manual verify (Win10/Win11), не выполнено в этой среде |
| 5 | Ambient Accent visual system | Medium | WPF styles/resources | `[~]` код + автотесты завершены (ADR-025 Accepted, нейтральная поверхность + локальный семантический акцент, `PanelChrome`/`PanelTheme`/`SurfaceComposer`/`AccentPalette`/`InteractionVisualState`, миграция схемы v3→v4, 3 полных раунда `code-quality-reviewer`/`fences-ux-parity-reviewer` + финальный фикс-цикл); DoD-чеклист `docs/Refactoring/PHASES/PHASE-5-ambient-accent-ui.md` закрыт; V-1..V-18 (`docs/PLANS/phase-5-ambient-accent-ui.md` §7.2, `docs/Refactoring/Testing/ambient-accent-visual-matrix.md`) — manual verify (Win10/Win11), не выполнено в этой среде |
| 6 | Edit Layout UX | Medium | state/UI interaction | `[~]` код + автотесты завершены (ADR-026 Accepted, глобальный `LayoutInteractionMode`/`ILayoutEditModeService`, per-gesture политика `NormalModeLayoutGestures` default `MoveOnly`, `Lock layout`, gate move/resize + видимые resize-грипы + усиленный accent border, magnetic snap-guides `SnapGuideCalculator` между `GridSnap`/`PanelCollision`, три точки переключения режима — трей/меню панели/настройки+`Ctrl+E`, схема JSON не мигрирована, P6-1..P6-5 реализованы; P6-6 визуальный оверлей направляющих сознательно отброшен в этой итерации — DO-017/DO-018; 3 полных раунда `code-quality-reviewer`/`fences-ux-parity-reviewer` + финальный вердикт APPROVE, 0 CRITICAL/0 MAJOR/5 MINOR, 1925/1925 тестов); DoD-чеклист `docs/Refactoring/PHASES/PHASE-6-edit-layout-ux.md` закрыт; V-1..V-19+ (`docs/PLANS/phase-6-edit-layout-ux.md` §7.2, `docs/Refactoring/Testing/edit-layout-manual-matrix.md`) — manual verify (Win10/Win11), не выполнено в этой среде |
| 7 | Новые продуктовые функции | Medium | tabs/peek/rules | `[ ]` |
| 8 | Native shell/performance modules | Medium/High | C++/COM/Win32 | `[ ]` |
| 9 | Hardening + release | High | tests/perf/package | `[ ]` |

## Architectural target

```text
DesktopOrganizer.UI (WPF)
    |
    +-- UI State / Interaction Controllers
    |
DesktopOrganizer.Application / orchestration
    |
    +-- abstractions --------------------------------+
    |                                               |
DesktopOrganizer.Services                    DesktopOrganizer.Native
(settings, storage, business-ish             (Win32 desktop host,
integration, shell facades)                   sizing, shell COM bridge)
    |                                               |
DesktopOrganizer.Core -------------------------------+
```

`DesktopOrganizer.Native` может быть либо C# P/Invoke-only assembly, либо thin C++ DLL с C ABI. Решение принимается отдельно по каждому модулю, а не глобально.

## Global DoD

Любая фаза считается закрытой только если:

- решение отражено в ADR/документации;
- код компилируется без новых warnings;
- новые critical paths покрыты тестами или имеют задокументированный manual test matrix;
- нет regression в move/resize/drop/rename/view mode;
- upgrade существующего user state проверен;
- rollback path понятен;
- performance baseline не ухудшен больше согласованного бюджета.
