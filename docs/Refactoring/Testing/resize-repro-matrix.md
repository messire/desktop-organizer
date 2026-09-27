# Resize Repro Matrix

Run each scenario against legacy and native path during rollout.

## Как захватить лог для триажа

1. Запустить `DesktopOrganizer.UI.exe --diag` (или выставить `DESKTOPORGANIZER_DIAG=1`) — см.
   `diagnostics-log-events.md`.
2. Воспроизвести сценарий из таблицы ниже.
3. Открыть свежий файл в `%LOCALAPPDATA%\DesktopOrganizer\Logs\app-*.jsonl`, найти последнюю запись
   с `scope`, соответствующим зависшей панели (первые 8 символов её `Guid`).
4. Записать `name` последнего события в колонку **Last logged event** — это последнее зафиксированное
   состояние gesture lifecycle непосредственно перед зависанием/аномалией. Приложить путь к файлу
   в **Log file** при эскалации бага.

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

> ⚠️ **Manual verify (Win10/Win11) — не выполнено.** Среда разработки ограничена `dotnet build`/`dotnet test`,
> нет живого Windows GUI для воспроизведения этих 15 сценариев. Таблица готова к заполнению человеком.

## Phase 1 — дополнительные пункты manual verify

Найдены по ходу реализации `docs/PLANS/phase-1-resize-stabilization.md` (ветка `fix/resize-lifecycle`,
commits C1–C9 + пост-ревью фиксы). Логически расширяют сценарии 1–15 выше; каждый закрыт кодом и
регрессионным unit-тестом (`ResizeGestureSessionTests`/`RollUpBehavior`-регрессия), но сама
поведенческая проверка на живом Windows GUI ещё не выполнена.

| # | Scenario | Expected | Root cause / fix | Last logged event | Log file |
|---|---|---|---|---|---|
| 16 | обычный mouse-up (drag завершается штатно), запуск с `--diag` | в логе `ResizeCompleted` с `endReason: "Completed"`, **не** `"CaptureLost"` | M-1: `OnResizeDragCompleted` ранее не различал штатное завершение и потерю capture | | |
| 17 | roll-up → roll-down → вертикальный resize сразу после | resize работает визуально (Height реально меняется) | H3: `RollUpBehavior` — `DoubleAnimation` без `FillBehavior` (`FillBehavior.HoldEnd` по умолчанию) навсегда удерживала `Window.HeightProperty`; фикс — снятие держателя в `Completed` + переустановка `Height` как локального значения | | |
| 18 | быстрый двойной roll-up toggle (два клика < 150мс) | итоговое состояние модели (`IsRolledUp`) и экрана совпадают, без рассинхрона/мигания | M-6: гонка двух пересекающихся `DoubleAnimation`; фикс — monotonic generation counter, устаревший `Completed`-хендлер выходит без побочных эффектов | | |
| 19 | залипшая ручка после потери native capture без `LostMouseCapture` на `Thumb` (симуляция H4 недостижима без реального Alt+Tab/перехвата capture другим HWND — например, реальный Alt+Tab во время resize, затем клик по той же ручке) | первый же клик по ТОЙ ЖЕ ручке начинает новый жест, а не проглатывается (`Thumb.IsDragging` не остаётся мёртвым навсегда) | M-3/H4: `RecoverStaleDrag` на `PreviewMouseLeftButtonDown` оверлея — если `thumb.IsDragging && !ReferenceEquals(Mouse.Captured, thumb)`, принудительно `CancelDrag()` + `AbortResize(SourceChanged)` до того, как отработает штатный `Thumb`-хендлер | | |

Сценарии 1–15 (100 живых resize по каждой грани/углу, release мыши вне окна, Alt+Tab, контекстное
меню, virtual desktop/смена DPI/монитора, sleep/wake, перезапуск Explorer, resize↔drag вплотную,
закрытие панели в середине жеста) остаются как были — manual verify, не переопределены Фазой 1.

## Phase 2 — native path (`NativePanelSizing=True`) дополнительные пункты

Найдены при реализации `docs/PLANS/phase-2-native-panel-sizing.md` (ветка `feat/native-panel-sizing`,
коммиты C1–C7 + пост-ревью фиксы). Код + автотесты (T-HIT-\*, T-MAP-\*, T-ORIG-\*, T-SW-\*, T-EQ-\*,
T-SVC-\*, T-CTRL-\*, T-DIAG-\*, T-ROLL-\*) зелёные, `ResizeGeometryCalculator.cs` не изменён
(bit-for-bit golden-эквивалентность легаси/нативного пути пинится тестом). Сама поведенческая проверка
на живом Windows GUI ещё не выполнена.

> ⚠️ Строки 1–19 существующей матрицы (выше и в разделе «Phase 1») должны быть прогнаны **повторно, с
> флагом `NativePanelSizing=True`** — физически не дублируются в этой таблице, но входят в объём
> manual verify Фазы 2 наравне со строками 20–31.

| # | Scenario | Expected | Risk | Last logged event | Log file |
|---|---|---|---|---|---|
| 20 | Resize за каждую из 8 граней/углов при `NativePanelSizing=True` | системный курсор грани, панель тянется, snap совпадает с легаси визуально | R-1 | | |
| 21 | Resize при активном стороннем окне | фокус НЕ уходит с чужого окна | R-2 | | |
| 22 | Живой resize на layered-окне | нет чёрных/незакрашенных полос, нет «призрачной» рамки, нет артефактов | R-1 | | |
| 23 | Упор в MinWidth/MinHeight и обратно | грань сразу следует за курсором, «отвязки» нет | R-4 | | |
| 24 | DPI 100/125/150/200%, 2+ монитора + resize | геометрия и snap корректны на обоих | R-7 | | |
| 25 | Смена масштаба/разрешения во время сессии | панель не «уезжает», resize продолжает работать | R-7 | | |
| 26 | Перезапуск Explorer → reattach → resize | sizing работает без перезапуска приложения | R-3 | | |
| 27 | roll-up → roll-down → native resize по вертикали | высота реально меняется (регрессия H3 на новом пути) | R-6/H3 (новый путь) | | |
| 28 | Alt+Tab/контекстное меню посреди native resize | `endReason=CaptureLost` в `--diag`-логе, ручка не залипает | R-5 | | |
| 29 | Откат — снять флаг, перезапустить | полностью легаси-поведение, сохранённая геометрия валидна | — | | |
| 30 | Resize из угла (не одной грани) при упоре в MinWidth И MinHeight одновременно, движение курсора по диагонали обратно | обе оси «отвязываются» независимо — не должны залипать/дёргаться совместно | R-4 (найдено `fences-ux-parity-reviewer`) | | |
| 31 | Edge-case переиспользования HWND — Windows переиспользует значение HWND для новой панели вскоре после закрытия старой в той же сессии процесса | `NativePanelSizingService.Attach`'s «already attached»-guard не отклоняет легитимный новый `Attach` (в норме `OnClosing` диспозит токен синхронно до уничтожения окна, но это не покрыто в manual-матрице) | R-10 (найдено `fences-ux-parity-reviewer`) | | |

### R-8 — задокументированная девиация (не баг)

ESC во время системного sizing loop: `WM_EXITSIZEMOVE` приходит так же, как при штатном завершении —
Win32 не даёт способа отличить отмену от нормального конца жеста внутри `DefWindowProc`-based sizing
loop. Поэтому на нативном пути `endReason` в логе будет **`Completed`**, а не `Canceled`, даже если
пользователь нажал ESC. Это принятое архитектурное решение (см. ADR-022 §R-8), а не регрессия —
**тестировщик не должен репортить это как баг**. Триаж-контракт (`CaptureLost` без последующего
`ResizeCompleted` = утечка capture) при этом не страдает: ESC либо завершает жест нормально
(`Completed`, геометрия на момент ESC применена как финальная), либо, если capture перехвачен кем-то
другим в процессе, сработает штатный `SizeMoveAborted`/`CaptureLost`-путь.
