# Phase 2 — Native Resize / Desktop Host Boundary

## Цель

Заменить эмуляцию системного resize через overlay `Thumb` на Windows sizing loop, не ломая GridMetrics и desktop hosting.

## Решение

Использовать Win32 message path:

- `WM_NCHITTEST` -> `HTLEFT`, `HTRIGHT`, `HTTOP`, `HTBOTTOM`, corners;
- `WM_ENTERSIZEMOVE`;
- `WM_SIZING` для quantization / min constraints;
- `WM_EXITSIZEMOVE` для final snap + save;
- `WM_DPICHANGED` для корректных bounds;
- `SetCapture/ReleaseCapture` только если потребуется custom fallback.

WPF остаётся UI layer, native hook устанавливается через `HwndSource.AddHook` либо thin C++ bridge.

## C++ критерий

C++ DLL оправдана, только если:

- нужен стабильный Win32 subclass/message handling, который заметно проще из native;
- требуется COM shell code с тяжёлым marshaling;
- измерения показывают performance benefit;
- API можно сделать маленьким C ABI.

Если эти условия не выполнены, оставить P/Invoke в одном centralized native adapter.

## Работы

1. Добавить `INativePanelSizingService`.
2. Реализовать `HwndSource` hook prototype в C#.
3. Перенести resize hit testing из XAML overlay.
4. Скрыть/удалить `ResizeOverlay` после feature-flag rollout.
5. Сохранить existing grid snap math как managed pure calculator.
6. DPI + multi-monitor handling связать с текущим monitor service.
7. Добавить feature flag `NativePanelSizing` на переходный период.

## Артефакты

- `ADR/ADR-022-native-panel-sizing.md`
- `Native/INativePanelSizingService.cs`
- `Native/NativePanelSizingService.skeleton.cs`
- optional `Native/DesktopOrganizer.Native/README.md`
- migration checklist

## DoD

- Resize работает без WPF Thumb overlay.
- Все 8 hit-test zones корректны.
- Системный resize не крадёт activation вопреки desktop-window policy.
- Grid snap совпадает с текущей реализацией по golden tests.
- DPI 100/125/150/200% проверен минимум на 2 мониторах.
- Explorer restart/WorkerW reattach не ломает sizing.
- Feature flag позволяет вернуться к legacy path в одном релизе.

## Статус

**`[~]` Код + автотесты завершены (1287/1287 тестов зелёные, `dotnet build` чистый); manual verify
на живом Windows GUI (Win10/Win11) ожидает ручного прогона.**

Ветка `feat/native-panel-sizing` (от `rc/0.4.0`). Мастер-план реализации:
`docs/PLANS/phase-2-native-panel-sizing.md`. Коммиты C1–C5 (`6ea4aa7`..`8377bf7`) + тесты
(`9a3bcd5`, `ee74bc6`) + пост-ревью фиксы (`646e04c`, `a346d67`, `d7858e4`, `7a5e8bb`) + документация
C6/C7. Прошли `code-quality-reviewer` (1 CRITICAL + 4 MAJOR — все исправлены, включая реальный баг
координатной симметрии origin offset в `WM_SIZING`) и `fences-ux-parity-reviewer` (default-behavior
parity при флаге `NativePanelSizing=False` подтверждена, регрессий не найдено; нашёл 2 дополнительных
edge-case для manual-матрицы, см. ниже). ADR-022: `Proposed` → `Accepted`.

### Реализовано (code-complete)

- Native Win32 sizing loop за feature-флагом `NativePanelSizing` (`IAppSettings`, default `False`,
  override-приоритет env `DESKTOPORGANIZER_NATIVESIZING` > CLI `--native-sizing`/`--no-native-sizing`
  > setting, `NativeSizingSwitch`) — `WM_NCHITTEST`/`WM_ENTERSIZEMOVE`/`WM_SIZING`/
  `WM_EXITSIZEMOVE`/`WM_CAPTURECHANGED`/`WM_MOUSEACTIVATE`/`WM_DPICHANGED`/
  `WM_DPICHANGED_AFTERPARENT` декодируются в `NativePanelSizingService` (Services/Tools,
  `HwndSource.AddHook`) и передаются `INativePanelSizingClient` (`NativePanelResizeController`, UI).
- Новые примитивы `GetWindowRect`/`ReadRect`/`WriteRect`/`GetDpiScaleForWindow` добавлены строго в
  `INativeApi`/`NativeApi` — ни одного нового `[DllImport]` вне этого файла (инвариант CLAUDE.md
  сохранён).
- Переиспользование без изменений Фазы-1 `ResizeGeometryCalculator`/`ResizeGestureSession` —
  `Accumulate` на нативном пути не используется, каждый `WM_SIZING` даёт self-correcting synthetic
  delta через `Apply`, пересчитываемый от `_session.Raw`. Bit-for-bit golden-эквивалентность
  легаси/нативного путей доказана честными тестами (`NativeResizeEquivalenceTests`, через реальный
  `ProposeBounds` + полный жизненный цикл контроллера, не через шорткат-мок).
- Чистые тестируемые мапперы в `UI/Interaction/Native/`: `NativeHitTestMapper` (8 граней/углов ×
  4 DPI), `NativeSizingCoordinateMapper` (px↔DIP, одна точка конверсии), `NativeSizingOriginResolver`
  (самокалибровка координатного пространства `WM_SIZING` — устраняет неопределённость child/WorkerW
  vs top-level), `NativeSizingEdgeMap` (`NativeSizingEdge` ↔ `ResizeEdge` ↔ `WMSZ_*`).
  `NativePanelResizeController` переиспользует `ResizeConstraintsFactory` — общий с легаси-путём,
  чтобы не завести вторую копию constraint-математики.
- `IPanelResizeController`/`IResizeAffordance` — общий контракт легаси (`PanelResizeController` +
  `ThumbOverlayAffordance`) и нативного (`NativePanelResizeController` + `NativeSizingAffordance`)
  путей; `ChildWindow`/`RollUpBehavior` не знают, какой путь активен.
- Диагностика: `SizeMoveEntered`/`SizeMoveExited` реализованы под зарезервированными именами
  (`diagnostics-log-events.md`, payload — `hwnd`/`edge`/`finalX/Y/Width/Height`), privacy-инвариант
  не нарушен.
- Capture/DPI/coordinate-space edge cases учтены явно: `WM_MOUSEACTIVATE` → `MA_NOACTIVATE` (R-2),
  `WM_CAPTURECHANGED` в активном цикле → `endReason=CaptureLost` (R-5), `IMonitorService.
  MonitorsChanged` как источник правды для DPI/monitor-смены, а не только `WM_DPICHANGED`/
  `_AFTERPARENT`, которые могут не прийти вовсе для child-окна в `WorkerW` (R-7, D-6).
- Хук снимается детерминированно — `IDisposable`-токен, идемпотентный `Dispose`, повторный `Attach`
  на том же HWND безопасен (R-10).
- Реальный баг найден и исправлен по ходу реализации (`code-quality-reviewer`, CRITICAL): origin
  offset калибровки `WM_SIZING` применялся асимметрично (на вход, но не на выход `ProposeBounds`) —
  сдвигал панель при каждом resize внутри `WorkerW`. Исправлено `646e04c`, задокументировано в
  ADR-022 D-4 как архитектурный урок.
- ADR-022 переведён `Proposed` → `Accepted` (D-1..D-6, R-8-девиация).

### НЕ выполнено в этой среде — manual verify (Win10/Win11)

Среда разработки ограничена `dotnet build`/`dotnet test`, нет живого Windows GUI. Остаются пунктами
`docs/Refactoring/Testing/resize-repro-matrix.md`:

- строки 1–19 (базовая resize-матрица Фазы 0/1) — повторный прогон с флагом `NativePanelSizing=True`;
- строки 20–29 из мастер-плана (8 граней/углов, фокус стороннего окна не уходит R-2, layered-окно без
  артефактов R-1, упор в MinWidth/MinHeight и обратно R-4, DPI 100/125/150/200% на 2+ мониторах и
  смена масштаба во время сессии R-7, перезапуск Explorer → reattach R-3, roll-up → roll-down →
  native resize R-6, Alt+Tab/контекстное меню → `endReason=CaptureLost` R-5, откат флага);
- строки 30–31, найденные `fences-ux-parity-reviewer` при ревью: диагональный resize из угла с
  одновременным упором в MinWidth и MinHeight (независимая «отвязка» осей, R-4), edge-case
  переиспользования HWND новой панелью вскоре после закрытия старой в одной сессии процесса (R-10);
- R-8 (девиация, не баг): ESC внутри системного sizing loop всегда даёт `endReason=Completed` — при
  manual-прогоне это ожидаемое поведение, не репортить как регрессию.

Фаза **не** закрывается как `[x]` до тех пор, пока эти сценарии не будут воспроизведены вручную на
Win10/Win11 и флаг `NativePanelSizing` не переведён в default `True` (после чего в отдельном коммите
C8, следующий релиз, удаляется легаси `ResizeOverlay`).
