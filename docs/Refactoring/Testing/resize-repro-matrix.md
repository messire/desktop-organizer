# Resize Repro Matrix

Единственный путь ресайза панели — native (Win32 sizing loop, ADR-022). С 2026-10-10 (ADR-022 Фаза 1) WPF-путь через `Thumb`,
флаг `NativePanelSizing` и CLI/env-переключатели удалены: все сценарии ниже прогоняются на native-пути, «сравнения путей» нет.
Строки, которые описывали Thumb-механику, переписаны под native (помечены «Фаза 1 ADR-022»).

## Как захватить лог для триажа

1. Запустить `DesktopOrganizer.UI.exe --diag` (или выставить `DESKTOPORGANIZER_DIAG=1`) — см.
   `diagnostics-log-events.md`.
2. Воспроизвести сценарий из таблицы ниже.
3. Открыть свежий файл в `%LOCALAPPDATA%\DesktopOrganizer\Logs\app-*.jsonl`, найти последнюю запись
   с `scope`, соответствующим зависшей панели (первые 8 символов её `Guid`).
4. Записать `name` последнего события в колонку **Last logged event** — это последнее зафиксированное
   состояние gesture lifecycle непосредственно перед зависанием/аномалией. Приложить путь к файлу
   в **Log file** при эскалации бага.

Признак здорового жеста в логе: `SizeMoveEntered` → `ResizeStarted` (`captureOwner=NativeSizingLoop`) → `ResizeDelta`… →
`SizeMoveExited` → `ResizeCompleted`.

| # | Scenario | Expected | Last logged event | Log file |
|---|---|---|---|---|
| 1 | 100x resize right edge | never stuck | | |
| 2 | 100x resize bottom-right | never stuck | | |
| 3 | release mouse outside window | gesture ends | | |
| 4 | Alt+Tab during resize | cleanup -> Idle | | |
| 5 | open context menu during/after resize | no stale capture | | |
| 6 | switch virtual desktop | no stale resize state | | |
| 7 | change DPI / move to other monitor | geometry valid | | |
| 8 | sleep/wake monitor | resize still works | | |
| 9 | restart Explorer | reattach + resize works | | |
| 10 | rapid resize then panel drag | gestures don't overlap | | |
| 11 | rapid panel drag then resize | gestures don't overlap | | |
| 12 | snap on/off | behavior deterministic | | |
| 13 | grid/list mode | respective semantics preserved | | |
| 14 | minimum size clamp | opposite edge remains anchored | | |
| 15 | panel disposal mid-gesture | no exception/stale capture | | |

> ⚠️ **Manual verify (Win10/Win11) — не выполнено построчно.** Среда разработки ограничена `dotnet build`/`dotnet test`,
> нет живого Windows GUI для воспроизведения этих сценариев. Native-путь целиком проверен пользователем на Windows 11 по
> сокращённому списку MV-0…MV-13 (гейт Фазы 1 ADR-022, 2026-10-10, по сообщению пользователя); результаты по строкам в
> репозитории не записаны. Таблица готова к заполнению человеком.

## Phase 1 — дополнительные пункты manual verify

Найдены по ходу реализации `docs/PLANS/phase-1-resize-stabilization.md` (ветка `fix/resize-lifecycle`,
commits C1–C9 + пост-ревью фиксы). Логически расширяют сценарии 1–15 выше. Строки 16 и 19 исходно описывали Thumb-механику
(`OnResizeDragCompleted`, `Thumb.IsDragging`/`RecoverStaleDrag`) и **переписаны под native-путь** (ADR-022 Фаза 1,
2026-10-10); 17–18 (roll-up) к пути ресайза не привязаны и остаются.

| # | Scenario | Expected | Root cause / fix | Last logged event | Log file |
|---|---|---|---|---|---|
| 16 | обычное отпускание кнопки (жест завершается штатно), запуск с `--diag` | в логе `ResizeCompleted` с `endReason: "Completed"`, **не** `"CaptureLost"` и не `"Canceled"` | native: `WM_EXITSIZEMOVE` с изменённым rect → `Completed`; `CaptureLost` — только `SizeMoveAborted` (система прервала цикл). Исходно (Thumb) — M-1: `OnResizeDragCompleted` не различал штатное завершение и потерю capture | | |
| 17 | roll-up → roll-down → вертикальный resize сразу после | resize работает визуально (Height реально меняется) | H3: `RollUpBehavior` — `DoubleAnimation` без `FillBehavior` (`FillBehavior.HoldEnd` по умолчанию) навсегда удерживала `Window.HeightProperty`; фикс — снятие держателя в `Completed` + переустановка `Height` как локального значения | | |
| 18 | быстрый двойной roll-up toggle (два клика < 150мс) | итоговое состояние модели (`IsRolledUp`) и экрана совпадают, без рассинхрона/мигания | M-6: гонка двух пересекающихся `DoubleAnimation`; фикс — monotonic generation counter, устаревший `Completed`-хендлер выходит без побочных эффектов | | |
| 19 | Alt+Tab (или иное отнятие capture) посреди системного sizing-цикла, затем клик по краю той же панели | цикл завершается (`ResizeCompleted`, в т.ч. `endReason: "CaptureLost"`), **следующий клик по краю начинает новый жест** — сессия не «залипает» | native: `WM_CAPTURECHANGED` → `SizeMoveAborted` → `EndResize(CaptureLost)`; исходно (Thumb) — M-3/H4: `RecoverStaleDrag` при `Thumb.IsDragging && !ReferenceEquals(Mouse.Captured, thumb)` | | |

Сценарии 1–15 (100 живых resize по каждой грани/углу, release мыши вне окна, Alt+Tab, контекстное
меню, virtual desktop/смена DPI/монитора, sleep/wake, перезапуск Explorer, resize↔drag вплотную,
закрытие панели в середине жеста) остаются как были — manual verify, не переопределены Фазой 1.

## Phase 2 — native path, дополнительные пункты

Найдены при реализации `docs/PLANS/phase-2-native-panel-sizing.md` (ветка `feat/native-panel-sizing`,
коммиты C1–C7 + пост-ревью фиксы). Код + автотесты (T-HIT-\*, T-MAP-\*, T-ORIG-\*, T-SW-\*, T-EQ-\*,
T-SVC-\*, T-CTRL-\*, T-DIAG-\*, T-ROLL-\*) зелёные, `ResizeGeometryCalculator.cs` не изменён. После Фазы 1 ADR-022
bit-for-bit golden «легаси/native» заменён regression-golden против чистого конвейера (`ExpectedPipelineOracle`).

> Флага `NativePanelSizing` больше нет, повторный прогон строк 1–19 «с флагом» не требуется — они выполняются на
> единственном native-пути (см. выше). Строка 29 («откат — снять флаг») **снята**: откат — предыдущий release candidate.

| # | Scenario | Expected | Risk | Last logged event | Log file |
|---|---|---|---|---|---|
| 20 | Resize за каждую из 8 граней/углов | системный курсор грани, панель тянется, snap как в прежних версиях | R-1 | | |
| 21 | Resize при активном стороннем окне | фокус НЕ уходит с чужого окна | R-2 | | |
| 22 | Живой resize на layered-окне | нет чёрных/незакрашенных полос, нет «призрачной» рамки, нет артефактов | R-1 | | |
| 23 | Упор в MinWidth/MinHeight и обратно | грань сразу следует за курсором, «отвязки» нет | R-4 | | |
| 24 | DPI 100/125/150/200%, 2+ монитора + resize | геометрия и snap корректны на обоих | R-7 | | |
| 25 | Смена масштаба/разрешения во время сессии | панель не «уезжает», resize продолжает работать | R-7 | | |
| 26 | Перезапуск Explorer → reattach → resize | sizing работает без перезапуска приложения | R-3 | | |
| 27 | roll-up → roll-down → native resize по вертикали | высота реально меняется (регрессия H3) | R-6/H3 | | |
| 28 | Alt+Tab/контекстное меню посреди native resize | `endReason=CaptureLost` в `--diag`-логе, ручка не залипает | R-5 | | |
| 29 | *(снята 2026-10-10: флага отката нет)* | — | — | | |
| 30 | Resize из угла (не одной грани) при упоре в MinWidth И MinHeight одновременно, движение курсора по диагонали обратно | обе оси «отвязываются» независимо — не должны залипать/дёргаться совместно | R-4 (найдено `fences-ux-parity-reviewer`) | | |
| 31 | Edge-case переиспользования HWND — Windows переиспользует значение HWND для новой панели вскоре после закрытия старой в той же сессии процесса | `NativePanelSizingService.Attach`'s «already attached»-guard не отклоняет легитимный новый `Attach` (в норме `OnClosing` диспозит токен синхронно до уничтожения окна, но это не покрыто в manual-матрице) | R-10 (найдено `fences-ux-parity-reviewer`) | | |

## Phase 3 — единственный native-путь (ADR-022 Фазы 0–1, 2026-10-10)

Список MV-0…MV-13 и пробелы G-1…G-6 из `docs/PLANS/native-resize-single-path.md` как строки матрицы. Гейт Фазы 1 пройден
пользователем (по его сообщению); построчные результаты не записаны — колонки логов для будущих прогонов.

| # | Scenario | Expected | MV / G | Last logged event | Log file |
|---|---|---|---|---|---|
| 32 | Ресайз за каждый из 8 краёв/углов; курсор соответствует краю | противоположный край неподвижен; курсоры системные (`DefWindowProc`, DO-053) | MV-1 / G-9 | | |
| 33 | Snap: icon-grid (Grid, `SnapResizeToIconGrid`), `GridSize` > 1, workspace-grid (`GridAligned`) | шаги как прежде; после отпускания нет «доскока» | MV-2 | | |
| 34 | `Alt` во время ресайза (`GridAligned` и `GridSize` > 1) | свободный ресайз, на отпускании — нормализация к решётке | MV-3 / G-7 | | |
| 35 | Свёрнутая панель | ресайзить нельзя; после разворота — можно | MV-4 | | |
| 36 | Anti-overlap (ADR-017, `GridAligned`): ресайз в соседа | упор в слот соседа, без наложения | MV-5 | | |
| 37 | Режимы: Normal+MoveOnly — края не ресайзят; Edit Layout — ресайз + accent-грипы; Lock — ресайза нет; смена Normal-жестов в настройках — без рестарта | см. `edit-layout-manual-matrix.md` V-3/V-8/V-13/V-14 | MV-6 / G-3 | | |
| 38 | ESC внутри цикла **при неизменном rect** (не двигать мышь, ESC) | `ResizeCompleted` с `endReason: "Canceled"`, `canceled: true`; нет персиста; AutoContent-панель (Top/Bottom) не становится Manual; после рестарта размер исходный. ESC **после** движения: система восстанавливает исходный rect — ожидание то же; если в логе `Completed`/демоция `AutoContent` — это открытый риск порядка `WM_EXITSIZEMOVE` и восстановления rect | MV-7 / G-4 | | |
| 39 | Mixed-DPI (100 % + 150 %): ресайз через границу мониторов | жест не обрывается (нет `SourceChanged`), снап до конца, размер сохраняется после рестарта; без скачков/дрожания (допустим ≤ 1 кадр) | MV-8 / G-1 | | |
| 40 | ADR-049 (режим T): во время и после ресайза | панель остаётся под окнами приложений, не «мигает» поверх; Win+D не ломается | MV-9 | | |
| 41 | Активация/клавиатура после клика по панели и по краю | F2, Delete, Enter, Esc, закрытие меню кликом мимо работают; фокус активного приложения при ресайзе за край не отнимается (`MA_NOACTIVATE` только на HT 10–17) | MV-10 / G-5 | | |
| 42 | Двойной клик по верхнему/нижнему краю; верхний край к верху экрана | нет вертикального maximize/Aero Snap (`WM_NCLBUTTONDBLCLK` на краях проглочен) | MV-11 / G-6 | | |
| 43 | Hover у края; правый клик по краю | приемлемая косметика (хром «гаснет» над краем — known, DO-052); системного меню нет | MV-12 / G-8 | | |
| 44 | Рестарт приложения, перезапуск Explorer, смена страницы, clone панели, Folder Portal — затем ресайз | работает, размеры сохраняются | MV-13 | | |
| 45 | Смена режима (Edit Layout → Normal из трея) или страницы **посреди** ресайза | системный цикл реально прерван (`CancelSizingLoop` → `WM_CANCELMODE`), панель не продолжает тянуться без снапа, следующий жест стартует нормально | G-2 | | |
| 46 | Видимые грипы Edit Layout | 8 accent-грипов (`ResizeGripVisuals`), не перехватывают клики; скрыты на свёрнутой/заблокированной панели | G-3 | | |
| 47 | Старый `user.config` с `<setting name="NativePanelSizing">`=`False` | приложение стартует без ошибок, остальные настройки на месте, ресайз native; элемент остаётся в файле нетронутым | ADR-022 план §4 | | |
| 48 | Touch/pen-ресайз | **не проверяется** — принятое ограничение «mouse-only» (DO-054) | — | | |

### R-8 — ESC в системном sizing loop (обновлено 2026-10-10, ADR-022 G-4)

Исходная формулировка («ESC всегда `Completed`») **устарела частично**. Сегодня:

- ESC при **неизменном rect** (итоговый rect == стартовый на `WM_EXITSIZEMOVE`) → `ResizeEndReason.Canceled`,
  `canceled: true` в `ResizeCompleted`, **без** персиста и **без** демоции `AutoContent → Manual`. Это ожидаемое поведение;
  тот же guard действует на отложенный персист после `AbortResize` → `CancelSizingLoop`.
- ESC, когда rect к моменту `WM_EXITSIZEMOVE` отличается от стартового: Win32 не даёт отличить отмену от нормального конца
  внутри `DefWindowProc`-цикла, поэтому `endReason` будет `Completed`. Система при этом восстанавливает исходный rect, но если
  она делает это **после** `WM_EXITSIZEMOVE`, сравнение не сработает и жест завершится как `Completed` (с демоцией
  `AutoContent`) — это **открытый риск MV-7** (порядок сообщений), проверяется только на живой Windows (строка 38);
  контрмера по результату — `win32-interop-specialist`. До результата проверки тестер **не репортит** `Completed` после ESC
  с движением как новый баг, но фиксирует его в строке 38.
- Сигнатура зависшего цикла — `SizeMoveEntered` без `SizeMoveExited`/`ResizeCompleted`; `endReason: "CaptureLost"` — прерванный
  системой цикл (`SizeMoveAborted`), не утечка (см. `diagnostics-log-events.md`).
