# Phase 2 — Native Panel Sizing (Win32 sizing loop вместо WPF `Thumb`)

Источники правды: `docs/Refactoring/PHASES/PHASE-2-native-resize-and-host.md`,
`adr/ADR-022-native-panel-sizing.md` (Proposed), `adr/ADR-021-interaction-state-and-gesture-lifecycle.md`
(§ «`ResizeGeometryCalculator.Apply` — deliberate Phase 2 compatibility», R6),
`docs/Refactoring/Testing/diagnostics-log-events.md` (зарезервированные `SizeMoveEntered`/`SizeMoveExited`),
`docs/PLANS/phase-1-resize-stabilization.md`.

**Scope: XL** (нативный слой + UI + persistence/настройки + диагностика). Разбит на 7 логических
коммитов C1–C7; удаление `ResizeOverlay` вынесено ЗА пределы фазы (C8, следующий релиз).

---

## 0. Ключевые архитектурные решения (ADR-022 delta)

Ниже — решения, которые ADR-022 в текущем черновике НЕ фиксирует и которые обязаны попасть в него
при переводе Proposed → Accepted (см. C7).

### D-1. C++ не вводится. P/Invoke расширяется через существующий `INativeApi`

Критерии `PHASE-2 §«C++ критерий»` не выполняются: нужен ровно один оконный хук и 6 сообщений,
marshaling — два `RECT` и `POINT`, измеримого performance-выигрыша нет, а в проекте уже есть
централизованный адаптер `src/DesktopOrganizer.Services/Interfaces/INativeApi.cs` +
`Tools/NativeApi.cs`. Инвариант CLAUDE.md («P/Invoke только за `INativeApi`») сохраняется: новые
примитивы добавляются в `INativeApi`, нигде больше `[DllImport]` не появляется.
Отдельная сборка `DesktopOrganizer.Native` (ROADMAP «Architectural target») — **не в этой фазе**,
кандидат на Фазу 8.

Черновики `docs/Refactoring/Native/*.cs` используются **только как форма API**: их namespace
`DesktopOrganizer.Native` и сигнатура `Attach(nint, Func<Rect>, Action<Rect>)` в продакшен не
переносятся (нет такого проекта; `Func<Rect>/Action<Rect>` не позволяет отличить edge/DPI/coordinate
space). Черновики остаются на месте как артефакты refactoring-пакета, не удаляются.

### D-2. Слои: политика/геометрия — в UI, декодирование сообщений — в Services

`INativeApi` — `internal` для `DesktopOrganizer.Services` (есть `InternalsVisibleTo("DesktopOrganizer.UI")`,
но опираться на него для новой фичи нельзя: это чёрный ход, добавленный для одного DI-теста).
`ResizeGeometryCalculator` живёт в `DesktopOrganizer.UI.Interaction` и зависит от `UI.Models`
(`GridMetrics`/`AdaptiveGridLayout`) — переносить его в Services/Core нельзя (потянет половину UI-моделей
и сломает bit-for-bit контракт коммита C2 Фазы 1).

Поэтому направление зависимостей — **callback вниз**: сервис ничего не знает о snap/grid, он
декодирует сообщение, приводит координаты и спрашивает клиента.

```
ChildWindow (UI)
  └── NativePanelResizeController (UI)        ← вся политика: session, calculator, диагностика
        ├── ResizeGestureSession (Ph1)        ← переиспользуется как есть
        ├── ResizeGeometryCalculator (Ph1)    ← переиспользуется как есть, НЕ копируется
        └── implements INativePanelSizingClient
              ▲
              │ (Services.Abstractions)
        INativePanelSizingService.Attach(hwnd, client) → IDisposable
              │
        NativePanelSizingService (Services/Tools)   ← HwndSource.AddHook + перевод сообщений
              └── INativeApi (internal)             ← единственный P/Invoke
```

### D-3. Пиксели наружу, DIP внутри — ровно одна точка конверсии

`INativePanelSizingClient` получает **физические пиксели** и текущий `dpiScale`; перевод в DIP делает
чистый `NativeSizingCoordinateMapper` в UI. Причина: конвертировать в сервисе — значит завести вторую
DPI-политику рядом с `MonitorService.ToDipRect` (system DPI scale) и гарантированно разойтись с ней.
Мэппер — чистая функция, тестируется на 1.0/1.25/1.5/2.0 и на отрицательных экранных координатах
(монитор слева от основного) без GUI.

### D-4. `Accumulate` на нативном пути НЕ используется — только `Apply`

`WM_SIZING` приносит уже готовый proposed rect (origin жеста + смещение курсора), то есть ровно ту же
величину, которую `Accumulate` на Thumb-пути накапливает из дельт:
`raw == (startExtent ± Σdelta) == proposedExtent`. Следовательно нативный путь строит
`ResizeRawExtent(proposed.Width, proposed.Height)` напрямую и зовёт **тот же** `Apply` с теми же
`anchorRight`/`anchorBottom` из `ResizeGestureSession.TryBegin`. Дрейфа накопления нет (нативный путь
пересчитывает от origin каждый раз — строго не хуже). Эквивалентность обоих путей пиннится golden-тестом
(T-EQ-01, §5) — это и есть DoD-критерий «grid snap совпадает с текущей реализацией».

**Открытый вопрос (решается только на живом Windows, см. R-4):** использует ли sizing loop наш
записанный обратно rect как новую базу для следующего `WM_SIZING`. Если да — при упоре в MinWidth и
обратном движении курсор «отвяжется» от грани. План закладывает второй мэппер
(`CursorDerivedExtentMapper`, использует уже существующий `INativeApi.GetCursorPos()` + rect на старте
жеста), переключаемый константой; выбор фиксируется после manual verify.

### D-5. Hit-test отдаёт ТОЛЬКО sizing-коды, никогда `HTCAPTION`

Перетаскивание панели остаётся WPF-жестом (`PanelDragMoveBehavior`: anti-overlap ADR-017 + магнитный
snap ADR-011). Возврат `HTCAPTION` из `WM_NCHITTEST` увёл бы move в системный modal loop и выключил бы
обе фичи. Значит `WM_ENTERSIZEMOVE`/`WM_EXITSIZEMOVE` на панелях приходят **только** для resize —
что удобно: `SizeMoveEntered.edge` всегда валиден.

### D-6. DPI-awareness приложения в этой фазе НЕ меняется

`app.manifest` в проекте отсутствует (`src/DesktopOrganizer.UI/DesktopOrganizer.UI.csproj` — ни
`dpiAware`, ни `dpiAwareness`). Перевод приложения в Per-Monitor-V2 — глобальное изменение поведения
всех окон, WPF-лейаута и `MonitorService` — отдельная задача, не Фаза 2.
Следствия, которые план обязан учитывать:
- `WM_DPICHANGED` (0x02E0) приходит **только top-level** окнам PMv2. Панель — `SetParent`-нута в
  `WorkerW` (`DesktopIntegrationService.EmbedInDesktop`), то есть child → сообщение может не прийти
  никогда. Обработчик пишется (он в спецификации), но **источником правды о смене DPI/геометрии
  считается `IMonitorService.MonitorsChanged`** (у `MonitorService` собственное top-level
  message-window, `WM_DISPLAYCHANGE`/`WM_SETTINGCHANGE` — оно работает уже сейчас).
- Дополнительно обрабатывается `WM_DPICHANGED_AFTERPARENT` (0x02E3) — child-вариант, на случай если
  awareness поменяют в будущем; сегодня, скорее всего, не приходит. Ветка помечается
  `MANUAL VERIFY`, ветка не должна ничего ломать, если не вызывается.

---

## 1. Новые и изменяемые файлы

### 1.1. Новое — `DesktopOrganizer.Services.Abstractions`

| Файл | Содержимое |
|---|---|
| `Interfaces/INativePanelSizingService.cs` | `INativePanelSizingService`, `INativePanelSizingClient`, `NativeSizingEdge`, `NativeSizingHit`, `NativeRectPx`, `NativePointPx` |

```csharp
namespace DesktopOrganizer.Services.Abstractions;

/// <summary>Грань/угол в терминах WM_NCHITTEST. Намеренно ОТДЕЛЬНЫЙ enum от UI.Interaction.ResizeEdge:
/// Abstractions не должен зависеть от UI. Маппинг — в UI (NativeSizingEdgeMap), покрыт T-MAP-05.</summary>
public enum NativeSizingEdge { None, Left, Top, Right, Bottom, TopLeft, TopRight, BottomLeft, BottomRight }

/// <summary>Прямоугольник в ФИЗИЧЕСКИХ пикселях экрана (never DIP) — см. D-3.</summary>
public readonly record struct NativeRectPx(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

public readonly record struct NativePointPx(int X, int Y);

/// <summary>Политика sizing'а. Реализуется в UI (NativePanelResizeController) — там живут
/// ResizeGestureSession/ResizeGeometryCalculator. Все прямоугольники — пиксели ЭКРАНА.</summary>
public interface INativePanelSizingClient
{
    /// <summary>WM_NCHITTEST. <paramref name="screenPoint"/> — экранные пиксели,
    /// <paramref name="windowRect"/> — GetWindowRect окна. Вернуть None ⇒ HTCLIENT (WPF обрабатывает сам).</summary>
    NativeSizingEdge HitTest(NativePointPx screenPoint, NativeRectPx windowRect, double dpiScale);

    /// <summary>WM_ENTERSIZEMOVE. Вернуть false ⇒ жест отклонён (tracker занят) — сервис не будет
    /// трогать последующие WM_SIZING этого цикла.</summary>
    bool SizeMoveEntered(NativeSizingEdge edge, NativeRectPx windowRect, double dpiScale);

    /// <summary>WM_SIZING. Вернуть скорректированный rect (snap/clamp). Вернуть <paramref name="proposed"/>
    /// без изменений — значит «не вмешиваюсь».</summary>
    NativeRectPx ProposeBounds(NativeSizingEdge edge, NativeRectPx proposed);

    /// <summary>WM_EXITSIZEMOVE.</summary>
    void SizeMoveExited(NativeRectPx finalRect);

    /// <summary>WM_CAPTURECHANGED во время активного цикла — capture украли (Alt+Tab, Explorer restart).</summary>
    void SizeMoveAborted();

    /// <summary>WM_DPICHANGED / WM_DPICHANGED_AFTERPARENT. См. D-6 — может не прийти никогда.</summary>
    void DpiChanged(double newScale, NativeRectPx suggestedRect);
}

public interface INativePanelSizingService
{
    /// <summary>Ставит хук на HWND панели. Dispose возвращённого токена снимает хук.
    /// Безопасно вызывать повторно для одного HWND — второй Attach вернёт no-op токен и запишет warning.</summary>
    IDisposable Attach(IntPtr hwnd, INativePanelSizingClient client);
}
```

### 1.2. Новое — `DesktopOrganizer.Services`

| Файл | Содержимое |
|---|---|
| `Tools/NativePanelSizingService.cs` | `internal sealed class NativePanelSizingService(INativeApi) : INativePanelSizingService`. `HwndSource.FromHwnd(hwnd).AddHook(...)`, switch по 6 сообщениям, ноль геометрии/snap. Каждый case — в `try/catch`, исключение никогда не уходит в WndProc (инвариант, тот же что у `MonitorService.WndProcImpl`). |

Константы сообщений держать в одном месте класса: `WM_NCHITTEST 0x0084`, `WM_ENTERSIZEMOVE 0x0231`,
`WM_SIZING 0x0214`, `WM_EXITSIZEMOVE 0x0232`, `WM_CAPTURECHANGED 0x0215`, `WM_DPICHANGED 0x02E0`,
`WM_DPICHANGED_AFTERPARENT 0x02E3`, `WM_MOUSEACTIVATE 0x0021` (см. R-2).
`wParam` у `WM_SIZING` — `WMSZ_*` (1..9); это дублирующий источник грани, используется как cross-check
против грани, запомненной на `WM_ENTERSIZEMOVE` (расхождение ⇒ `ResizeEndReason.SourceChanged`, ровно
как на Thumb-пути).

### 1.3. Изменяемое — `INativeApi` / `NativeApi` (C1)

Добавляются (и только они):

```csharp
/// <summary>GetWindowRect — ВСЕГДА экранные координаты, независимо от родителя (см. R-3).</summary>
bool GetWindowRect(IntPtr hwnd, out NativeRectPx rect);

/// <summary>Читает RECT из lParam WM_SIZING/WM_DPICHANGED. Прямой аналог уже существующего
/// ReadMouseHookPoint(IntPtr) — тот же приём, тот же уровень абстракции.</summary>
NativeRectPx ReadRect(IntPtr lParam);

/// <summary>Пишет RECT обратно в lParam (WM_SIZING требует модификации на месте).</summary>
void WriteRect(IntPtr lParam, NativeRectPx rect);

/// <summary>GetDpiForWindow с фолбэком на GetSystemDpiScale() при недоступности (см. D-6).</summary>
double GetDpiScaleForWindow(IntPtr hwnd);
```

`NativeRectPx` объявлен в Abstractions (публично) и переиспользуется `INativeApi` — Services уже
ссылается на Abstractions (`MonitorService`), новых ссылок не появляется.
`GetCursorPos()` уже есть — переиспользуется для `CursorDerivedExtentMapper` (D-4).

### 1.4. Новое — `DesktopOrganizer.UI/Interaction/Native/` (чистое ядро, 100% тестируемо)

| Файл | Ответственность |
|---|---|
| `NativeHitTestMapper.cs` | `static NativeSizingEdge Map(NativePointPx screenPoint, NativeRectPx windowRect, double dpiScale, double borderDip, bool enabled)`. Полосы/углы **в точности** как в `ChildWindow.xaml` `ResizeOverlay` (Grid 6/*/6 × 6/*/6 ⇒ полоса 6 DIP, угол 6×6 DIP). `enabled == false` (roll-up) ⇒ `None`. |
| `NativeSizingCoordinateMapper.cs` | `Rect ToDip(NativeRectPx, double scale)` / `NativeRectPx ToPx(Rect, double scale)`. Округление — `Math.Round` к целым пикселям; round-trip-инвариант пиннится тестом. |
| `NativeSizingOriginResolver.cs` | Самокалибровка координатного пространства `WM_SIZING` (см. R-3): `NativeVectorPx Resolve(NativeRectPx windowRectScreen, NativeRectPx firstSizingRect)` → постоянный offset, применяемый ко всем rect'ам цикла. Вычисляется один раз на `WM_ENTERSIZEMOVE`+первый `WM_SIZING`. |
| `NativeSizingEdgeMap.cs` | `NativeSizingEdge ↔ ResizeEdge` + `WMSZ_* → NativeSizingEdge`. Таблично, `[Theory]` по всем 8+8 значениям. |
| `NativeSizingSwitch.cs` | `static bool IsEnabled(IReadOnlyList<string> args, Func<string,string?> env, bool settingValue)`. Порядок приоритета: env `DESKTOPORGANIZER_NATIVESIZING` (`0`/`1`) → CLI `--native-sizing` / `--no-native-sizing` → значение настройки. Точная калька с `DiagnosticsSwitch` (`src/DesktopOrganizer.Services/Diagnostics/DiagnosticsSwitch.cs`). |

### 1.5. Новое — `DesktopOrganizer.UI/Behaviors/NativePanelResizeController.cs`

`INativePanelSizingClient`-адаптер. Ровно тот же набор зависимостей, что у существующего
`PanelResizeController` (окно, `IAppSettings`, `IWindowManager`, `getModel`, `onResizeGestureStarting`,
`clearDragObstacles`, `snapToGrid`, `titleBarHeight`, `IGestureDiagnostics`, `PanelInteractionTracker`)
плюс `INativePanelSizingService` и `Func<bool> isResizeEnabled` (roll-up).

Переиспользует без изменений: `ResizeGestureSession`, `ResizeGeometryCalculator`, `ResizeConstraints`,
`ResizeEdge`/`ResizeEdgeParser`, `PanelInteractionTracker`, `ResizeEndReason`.
`BuildConstraints` из `PanelResizeController` выносится в `internal static ResizeConstraintsFactory`
(общий для обоих путей — дублировать нельзя, иначе golden-эквивалентность сломается при первом же
изменении метрик).

Отображение сообщений на lifecycle:

| Сообщение | Действие |
|---|---|
| `WM_NCHITTEST` | `NativeHitTestMapper.Map(...)`; никаких side effects, никакого `ResizeGestureSession` |
| `WM_ENTERSIZEMOVE` | `SizeMoveEntered` (диагностика) → `onResizeGestureStarting()` → `clearDragObstacles()` → `session.TryBegin(edge, startBoundsDip, constraints)` → `ResizeStarted` (диагностика). `TryBegin == false` ⇒ вернуть `false`, цикл игнорируется |
| `WM_SIZING` | offset-калибровка (первый вызов) → `ToDip` → `ResizeRawExtent(proposed.W, proposed.H)` → `ResizeGeometryCalculator.Apply(...)` → `ToPx` → вернуть. `ResizeDelta` (диагностика, троттлинг как есть). `try/catch` ⇒ `EndResize(Failed)` + вернуть proposed без изменений |
| `WM_EXITSIZEMOVE` | `EndResize(Completed)`: `session.End` → `ShouldPersist` ⇒ `snapToGrid()` + `SaveWindowsState()` → `ResizeCompleted` → `SizeMoveExited` (диагностика) |
| `WM_CAPTURECHANGED` (в активном цикле) | `EndResize(CaptureLost)` |
| `Unloaded`/`OnClosing` | `AbortResize(Unloaded/Closing)` — уже существующие вызовы в `ChildWindow`, просто маршрутизируются на активный контроллер |

### 1.6. Изменяемое — UI

| Файл | Изменение |
|---|---|
| `Views/ChildWindow.xaml.cs` | Выбор пути в `OnSourceInitialized` (после `EmbedInDesktop`): `NativeSizingSwitch.IsEnabled(...)` ⇒ создать `NativePanelResizeController` + `Attach`, иначе оставить `PanelResizeController`. Оба за общим интерфейсом `IPanelResizeController` (`AbortResize`, `SnapWidthToGrid`) — `Initialize`/`OnClosing` зовут его, не зная, какой путь активен |
| `Behaviors/IPanelResizeController.cs` (новый) | `void AbortResize(ResizeEndReason reason); void SnapWidthToGrid();` — минимальный общий контракт |
| `Behaviors/RollUpBehavior.cs` | Вместо прямого `_resizeOverlay.Visibility = ...` — `IResizeAffordance.SetEnabled(bool)`. Реализации: `ThumbOverlayAffordance` (текущее поведение) и `NativeSizingAffordance` (флажок, который читает `NativeHitTestMapper` через `isResizeEnabled`). Иначе roll-up молча вернёт видимость 8 Thumb'ов поверх нативного пути |
| `Behaviors/IResizeAffordance.cs` (новый) | см. выше — единственная точка, где после C8 удаляется overlay |
| `Services/IAppSettings.cs` + `Services/AppSettings.cs` | `bool NativePanelSizing { get; set; }` рядом с `SnapResizeToIconGrid` |
| `Properties/Settings.settings` + `Settings.Designer.cs` | `<Setting Name="NativePanelSizing" Type="System.Boolean" Scope="User"><Value Profile="(Default)">False</Value></Setting>` — **default False**, откат бесплатный |
| `Views/MainWindow.xaml` (+ `ViewModels/MainWindowViewModel.cs`) | Чекбокс рядом с существующим `SnapResizeToIconGrid` + подпись «применяется после перезапуска» |
| `Configuration/DiConfig.cs` (Services) | `services.AddSingleton<INativePanelSizingService, NativePanelSizingService>();` |
| `Views/ChildWindow.xaml` | **НЕ трогается в этой фазе.** `ResizeOverlay` остаётся в разметке (C8) |

### 1.7. Изменяемое — диагностика

`Diagnostics/IGestureDiagnostics.cs` + `GestureDiagnostics.cs`: **ровно два** новых метода, имена
событий строго из `diagnostics-log-events.md` (новые имена вводить запрещено):

```csharp
void SizeMoveEntered(Guid? panelId, long hwnd, string edge);
void SizeMoveExited(Guid? panelId, long hwnd, Rect finalBounds);   // payload: hwnd, finalX/Y/Width/Height
```

Privacy-инвариант соблюдён: `hwnd` — целое, `edge` — enum из закрытого набора, остальное — геометрия.
`GestureDiagnosticsTests` расширяется проверкой, что payload этих событий не содержит строк вне
закрытого набора.

---

## 2. Порядок работ (логические коммиты)

| # | Коммит | Содержимое | Кто |
|---|---|---|---|
| **C1** | `feat(native): sizing primitives behind INativeApi` | `GetWindowRect`/`ReadRect`/`WriteRect`/`GetDpiScaleForWindow` + `NativeRectPx`/`NativePointPx` в Abstractions + `NativeApiTests` | win32-interop-specialist |
| **C2** | `feat(ui): pure native-sizing mappers` | `NativeHitTestMapper`, `NativeSizingCoordinateMapper`, `NativeSizingOriginResolver`, `NativeSizingEdgeMap`, `NativeSizingSwitch` + полный тест-сьют (T-HIT-*, T-MAP-*, T-SW-*) | tdd-test-engineer ∥ dotnet-services-engineer |
| **C3** | `feat(native): INativePanelSizingService + HwndSource hook` | Abstractions-интерфейсы, `NativePanelSizingService`, DI, тесты через `Mock<INativeApi>` с перехватом хук-делегата | win32-interop-specialist |
| **C4** | `feat(ui): native resize controller on Phase-1 session/calculator` | `NativePanelResizeController`, `ResizeConstraintsFactory`, диагностика `SizeMoveEntered/Exited`, golden-эквивалентность (T-EQ-*) | wpf-ui-builder + tdd-test-engineer |
| **C5** | `feat(ui): NativePanelSizing feature flag + path selection` | `IAppSettings`/Settings/`IPanelResizeController`/`IResizeAffordance`/`RollUpBehavior`/`ChildWindow`/MainWindow UI | wpf-ui-builder |
| **C6** | `docs(testing): native sizing repro matrix + event schema` | `diagnostics-log-events.md` (снять «зарезервировано, НЕ реализовано»), `resize-repro-matrix.md` — колонка «native path», новые строки 20–27 | architecture-documenter |
| **C7** | `docs(adr): ADR-022 Proposed→Accepted; ROADMAP Phase 2` | D-1..D-6 в ADR-022, статус Фазы 2 → `[~]`, ссылка на этот план | architecture-documenter |
| ~~C8~~ | ~~`chore(ui): remove ResizeOverlay`~~ | **НЕ в этой фазе.** Делается после того, как manual-verify матрица нативного пути закрыта человеком и флаг переведён в default `True` | следующий релиз |

C1 и C2 независимы → можно параллельно. C3 зависит от C1. C4 зависит от C2+C3. C5 зависит от C4.

---

## 3. Что переиспользуется без изменений (жёсткое требование)

- `src/DesktopOrganizer.UI/Interaction/ResizeGeometryCalculator.cs` — **ни строки правок**. Вторая копия
  snap-математики = автоматический fail ревью (ADR-021, R6).
- `ResizeGestureSession` / `ResizeEndReason` / `ResizeEndOutcome` — правил persist-семантики
  (`ShouldPersist = AppliedDeltaCount > 0 && Reason != Failed`) не меняем.
- `ResizeConstraints` / `ResizeEdge` / `ResizeEdgeParser` / `PanelInteractionTracker`.
- `tests/.../Unit/Interaction/ResizeGeometryCalculatorTests.cs` и
  `ResizeGeometryCharacterizationTests.cs` — эталон; они НЕ редактируются, нативный путь
  доказывает совпадение отдельным сьютом поверх них.

---

## 4. Риски

| ID | Риск | Митигация | Проверяемо здесь? |
|---|---|---|---|
| **R-1** | `AllowsTransparency="True"` ⇒ окно layered (`WS_EX_LAYERED`). Системный sizing loop на layered-окнах исторически даёт артефакты отрисовки/отсутствие live-preview | `ResizeMode` у `ChildWindow` не задан ⇒ default `CanResize` ⇒ `WS_THICKFRAME` есть, loop технически доступен. Если артефакты неприемлемы — fallback: не отдавать управление `DefWindowProc`, а вести собственный loop через `SetCapture`/`ReleaseCapture` (прямо разрешено PHASE-2 §Решение). Точка решения — после первого manual verify | ❌ manual |
| **R-2** | **`WS_EX_NOACTIVATE` + системный sizing loop крадёт activation.** `DefWindowProc(WM_NCLBUTTONDOWN)` перед входом в loop пытается активировать окно | Явно обрабатывать `WM_MOUSEACTIVATE` → возвращать `MA_NOACTIVATE` (3), `handled = true`. Плюс регрессия: после resize фокус остаётся в предыдущем foreground-приложении | ❌ manual (код — да, эффект — нет) |
| **R-3** | **Координатное пространство `WM_SIZING`.** Панель `SetParent`-нута в `WorkerW`, для child-окон документация обещает координаты клиента родителя, для top-level — экранные. Реальное поведение реparent-нутого `WS_POPUP` не документировано | Не гадать: `NativeSizingOriginResolver` самокалибруется на первом `WM_SIZING` цикла (`GetWindowRect` всегда экранный ⇒ offset = разница). Пересчитывается **каждый** жест ⇒ переживает Explorer restart/смену родителя | ✅ логика (тест на offset ≠ 0), ❌ реальное значение |
| **R-4** | Sizing loop может вести proposed rect от НАШЕГО записанного rect, а не от исходного ⇒ после упора в `MinWidth` курсор «отвяжется» от грани | Два мэппера за константой (D-4): `ProposedRectExtentMapper` (основной) и `CursorDerivedExtentMapper` (`GetCursorPos` + rect на старте). Оба покрыты тестами, переключение — одна строка | ❌ выбор — manual |
| **R-5** | Modal sizing loop = вложенный нативный message loop. `DispatcherTimer`'ы (`SystemIconsPanelManager` Z-order/virtual desktop, `DispatcherDebounceTimer` автосейва) тикают ВНУТРИ него и могут дёргать `SetWindowPos`/сохранение посреди жеста | Флаг `IsSizing` на контроллере; `SaveWindowsState` внутри цикла не вызывается (persist только на `WM_EXITSIZEMOVE`). Z-order-таймер относится к сайдбару (`ResizeMode="NoResize"`), панелей не касается — зафиксировать это в ADR как проверенное, чтобы не «чинили» вслепую | ⚠️ частично (unit-тест на «persist ровно один раз за жест») |
| **R-6** | Roll-up: `RollUpBehavior` анимирует `Window.HeightProperty`. ADR-021/H3 — `FillBehavior.HoldEnd` держал property и глушил resize. На нативном пути высоту меняет система, а WPF синхронизирует property обратно ⇒ висящий clock может «откатывать» окно | H3 уже исправлен в Фазе 1 (detach + re-assert). Добавить в repro-matrix строку «roll-up → roll-down → native resize» (строка 17 переиспользуется для нативного пути) | ❌ manual |
| **R-7** | `WM_DPICHANGED` не придёт reparent-нутому child-окну при текущем (не PMv2) awareness ⇒ ветка мертва | D-6: источник правды — `IMonitorService.MonitorsChanged`; `WM_DPICHANGED`/`_AFTERPARENT` обрабатываются, но не являются единственным путём. Ветка помечена MANUAL VERIFY в коде | ❌ manual |
| **R-8** | ESC во время системного цикла: `WM_EXITSIZEMOVE` приходит, но отличить отмену от штатного завершения нельзя ⇒ `endReason` всегда `Completed` | Задокументированная девиация: `canceled=false`, `endReason=Completed`. Триаж-контракт (`CaptureLost` без последующего `ResizeCompleted`) не страдает. Записать в `diagnostics-log-events.md` и ADR-022 | ✅ документируется |
| **R-9** | Двойная обработка: WPF-`Thumb`'ы всё ещё в разметке; при включённом флаге они не должны ловить клики | `IResizeAffordance` выключает overlay (`Visibility=Collapsed` + `IsHitTestVisible=false`) при нативном пути; `RollUpBehavior` больше не включает его напрямую. Unit-тест: при native-пути `RollUpBehavior.ApplyRollUp(false)` не делает overlay видимым | ✅ |
| **R-10** | Утечка хука: `HwndSource.RemoveHook` не вызван при закрытии панели ⇒ делегат держит контроллер и окно | Токен из `Attach` — `IDisposable`, диспозится в `ChildWindow.OnClosing` рядом с `AbortResize(Closing)`. Тест: повторный `Attach` для того же HWND возвращает no-op, `Dispose` идемпотентен | ✅ |

---

## 5. Тесты (всё headless, без GUI)

Новый каталог `tests/DesktopOrganizer.Tests/Unit/Interaction/Native/`.

**T-HIT-01..10** `NativeHitTestMapperTests` — `[Theory]` по всем 9 зонам (8 граней/углов + центр) при
scale 1.0/1.25/1.5/2.0; точки ровно на границах полос; точка вне `windowRect`; `enabled == false` ⇒
`None` во всех 8 зонах; окно меньше 2×border (полосы перекрываются) ⇒ приоритет угла.

**T-MAP-01..06** `NativeSizingCoordinateMapperTests` — px↔DIP round-trip; отрицательные координаты
(монитор слева от основного); scale 1.25 (полупиксели); `NativeRectPx.Width/Height` при инвертированном
rect.

**T-ORIG-01..03** `NativeSizingOriginResolverTests` — offset 0 (top-level), offset ≠ 0 (child WorkerW),
пересчёт при смене offset между жестами.

**T-MAP-05** `NativeSizingEdgeMapTests` — `[Theory]` по всем `NativeSizingEdge`↔`ResizeEdge` и по
`WMSZ_*` 1..9.

**T-SW-01..05** `NativeSizingSwitchTests` — приоритет env > CLI > setting; мусорные значения.

**T-EQ-01..08** `NativeResizeEquivalenceTests` — **ключевой DoD-тест**: для каждой из 8 граней и обоих
режимов (Grid+snap / List+free) прогнать одинаковое суммарное смещение
(a) через `Accumulate`-цепочку из N дельт + `Apply` (legacy) и (b) через один
`Apply(ResizeRawExtent(proposedW, proposedH))` (native) — результаты обязаны совпадать бит-в-бит.
Кейсы из `ResizeGeometryCharacterizationTests` переиспользуются как входные данные.

**T-SVC-01..08** `NativePanelSizingServiceTests` — `Mock<INativeApi>`, хук-делегат перехватывается так
же, как `NativeWindowProc` в `MonitorServiceNativeApiTests`/`DesktopGestureServiceTests`:
`WM_NCHITTEST` → клиент вызван, вернулось `HTLEFT`…`HTBOTTOMRIGHT`, `handled == true`;
`HitTest == None` ⇒ `handled == false` (WPF обрабатывает сам);
`WM_SIZING` → `ReadRect`/`WriteRect` вызваны с ожидаемыми значениями;
`WM_ENTERSIZEMOVE` вернул `false` ⇒ последующие `WM_SIZING` клиенту не идут;
исключение из клиента не выходит из хука;
`Dispose` снимает хук и идемпотентен.

**T-CTRL-01..10** `NativePanelResizeControllerTests` — lifecycle: Entered→Sizing×N→Exited ⇒ ровно один
`SaveWindowsState`; Entered→CaptureChanged ⇒ `ResizeCompleted` с `endReason=CaptureLost`;
`TryBegin` отклонён (tracker занят drag'ом) ⇒ ни одного `ProposeBounds`-эффекта, ни одного persist;
исключение в `ProposeBounds` ⇒ `Failed` + `ShouldPersist == false` + proposed возвращён нетронутым;
`WMSZ_*` ≠ грани из `ENTERSIZEMOVE` ⇒ `SourceChanged`;
`SizeMoveEntered`/`SizeMoveExited` записаны ровно по разу.

**T-DIAG-0x** расширение `GestureDiagnosticsTests` — payload новых событий, privacy-инвариант.

**T-ROLL-0x** регрессия `RollUpBehavior` + `IResizeAffordance` (R-9).

Целевой прирост: ~95–110 тестов. Существующие 1084 обязаны остаться зелёными без правок.

---

## 6. DoD

### 6.1. Достижимо в этой среде (`dotnet build` / `dotnet test`)

1. `INativePanelSizingService`/`INativePanelSizingClient` в Abstractions, реализация в
   `Services/Tools`, зарегистрирована в `DiConfig`; ни одного нового `[DllImport]` вне `NativeApi.cs`.
2. Все 8 hit-test зон покрыты `[Theory]` на 4 значениях DPI, включая граничные точки и
   roll-up-выключение.
3. **Grid snap совпадает с легаси бит-в-бит** — T-EQ-01..08 зелёные;
   `ResizeGeometryCalculator.cs` не изменён (проверяется диффом).
4. `ResizeGestureSession`-lifecycle на нативном пути даёт тот же набор `ResizeEndReason` и ту же
   persist-семантику, что Thumb-путь (T-CTRL-*).
5. `SizeMoveEntered`/`SizeMoveExited` реализованы **под зарезервированными именами**, payload — как в
   `diagnostics-log-events.md`, privacy-инвариант не нарушен.
6. Feature flag `NativePanelSizing` (default `False`) + env/CLI-override; при `False` поведение
   идентично сегодняшнему (ни один существующий тест не меняется).
7. Хук снимается детерминированно (`IDisposable`-токен, идемпотентный `Dispose`), повторный `Attach`
   безопасен.
8. Координатная самокалибровка (`NativeSizingOriginResolver`) работает при ненулевом offset —
   структурная готовность к WorkerW/Explorer restart.
9. Сборка без новых warnings (StyleCop + `linters/linter.ruleset`), XML-doc на всех публичных членах.
10. `WM_MOUSEACTIVATE` → `MA_NOACTIVATE` реализован и покрыт T-SVC (проверяется возвращаемое значение
    хука, не реальный фокус).

### 6.2. MANUAL VERIFY (Win10/Win11) — не проверяемо здесь

Добавить строками 20–27 в `docs/Refactoring/Testing/resize-repro-matrix.md`, колонка «native path»:

| # | Сценарий | Ожидание | Риск |
|---|---|---|---|
| 20 | Resize за каждую из 8 граней/углов при `NativePanelSizing=True` | системный курсор грани, панель тянется, snap совпадает с легаси визуально | R-1 |
| 21 | Resize панели при активном стороннем окне | **фокус НЕ уходит с чужого окна** | **R-2** |
| 22 | Живой resize на layered-окне | нет чёрных/незакрашенных полос, нет «призрачной» рамки | R-1 |
| 23 | Упор в MinWidth/MinHeight и движение курсора обратно | грань сразу следует за курсором, «отвязки» нет | **R-4** |
| 24 | DPI 100/125/150/200%, минимум 2 монитора, перенос панели между ними + resize | геометрия и snap корректны на обоих | R-7 |
| 25 | Смена масштаба/разрешения во время сессии | панель не «уезжает», resize продолжает работать | R-7 |
| 26 | Перезапуск Explorer → reattach → resize | sizing работает без перезапуска приложения | R-3 |
| 27 | roll-up → roll-down → native resize по вертикали | высота реально меняется (регрессия H3 на новом пути) | R-6 |
| 28 | Alt+Tab / открытие контекстного меню посреди native resize | `ResizeCompleted` с `endReason=CaptureLost` в `--diag`-логе, ручка не залипает | R-5 |
| 29 | Откат: снять флаг, перезапустить | полностью легаси-поведение, сохранённая геометрия валидна | — |

Плюс прогнать строки 1–19 существующей матрицы **повторно, с флагом `True`**.

---

## 7. Границы фазы (что НЕ делаем)

- Не удаляем `ResizeOverlay` и `PanelResizeController` (C8, следующий релиз — DoD ADR-022 «откат в
  одном релизе» требует, чтобы легаси-путь оставался рабочим).
- Не переводим перетаскивание панели на `HTCAPTION`/`WM_MOVING` (D-5).
- Не меняем DPI-awareness приложения / не добавляем `app.manifest` (D-6).
- Не выделяем `DesktopOrganizer.Native` в отдельную сборку (Фаза 8).
- Не трогаем `SystemIconsPanelWindow` (`ResizeMode="NoResize"`).
- Не добавляем новых имён событий диагностики (только два зарезервированных).
