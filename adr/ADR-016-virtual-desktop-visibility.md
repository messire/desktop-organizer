# ADR-016: Видимость desktop-окон на виртуальных рабочих столах

**Статус:** ~~Accepted~~ → **Superseded by [ADR-019](ADR-019-sidebar-top-level-virtual-desktop.md)** (2026-09-02)
**Дата:** 2026-09-01

> **Что из этого ADR остаётся действующим (ADR-audit WP-E, 2026-10-09).** Статус `Superseded by ADR-019`
> относится к *подложке* (встраивание боковой панели в `WorkerW`), но **не** к механизму и контрактам ниже:
> `IVirtualDesktopManager` через `IVirtualDesktopService`/`VirtualDesktopService` (`IsSupported`,
> `IsWindowOnCurrentDesktop`, `TryMoveWindowToCurrentDesktop`, STA-контракт, жизненный цикл RCW), poll-таймер,
> circuit breaker `MaxIneffectiveMoves = 3` и **обоснование COM-исключения из инварианта «P/Invoke только за
> `INativeApi`»** (раздел «Осознанное отступление…» ниже) продолжают действовать и используются
> [ADR-019](ADR-019-sidebar-top-level-virtual-desktop.md); обоснование COM-исключения продублировано в
> ADR-019 (раздел «Инвариант P/Invoke и COM виртуальных столов»), чтобы действующий контракт не жил
> только в superseded-документе. Не действует: re-embed-логика/`IsEmbedded` (удалены ADR-019) и
> допущение «исход A безопасен».

> ⚠️ **Superseded.** Механизм, зафиксированный здесь (poll + `IVirtualDesktopManager.
> MoveWindowToDesktop`), сам по себе остался верным и переиспользован без изменений в ADR-019. Но
> **подложка**, к которой этот механизм применялся, — панель как child `WorkerW` — оказалась
> первопричиной бага: `WorkerW` — единый слой на все виртуальные столы, поэтому окно, встроенное в
> него, не отслеживается менеджером виртуальных столов вообще (`GetWindowDesktopId` → `S_OK` +
> `GUID_NULL`), и весь механизм этого ADR был тихим no-op **при любом положении чекбокса**. ADR-019
> убирает `SetParent`-встраивание для боковой панели (делает её top-level `Topmost`-окном) и
> переиспользует poll-логику этого документа поверх окна, которое API реально видит. См. раздел
> «Ревизия» ниже и полный текст [ADR-019](ADR-019-sidebar-top-level-virtual-desktop.md).
>
> Fence-панели (`ChildWindow`) этим пересмотром **не затронуты** — они по-прежнему встраиваются в
> `WorkerW` через `EmbedInDesktop`, как описано ниже; это решение остаётся в силе только для них.
>
> Документ сохранён целиком как исторический контекст: измерения spike E0/E0.2 остаются валидными и
> ценными — именно они (в частности, поведение `GetWindowDesktopId` после `SetParent`) впоследствии
> доказали первопричину бага и обосновали решение ADR-019.

---

## Ревизия (2026-09-02, по итогам ADR-019)

Вывод «исход A безопасен для обоих сценариев» (см. §E0.3 ниже) был **неверным по существу**: при
исходе A (панель уже видна на всех столах через `WorkerW`, что и подтвердилось) настройка «Show on all
virtual desktops» была no-op **в обе стороны** — не только в направлении «показать на всех», но и в
направлении «выключить и показывать только на одном». Развилка A/B из §6.1 плана `ui-polishing-1` так
и не была проверена визуально (интерактивная сессия недоступна), и именно отсутствие этого
manual-verify-гейта позволило архитектурно некорректному решению получить статус `Accepted`. Урок:
тихая деградация (`IsSupported`/try-catch) защищает от падений, но не является заменой проверке того,
что фича вообще может дать наблюдаемый эффект — для этого класса багов (первопричина в подложке окна,
а не в логике опроса) визуальный manual-verify обязателен до, а не после присвоения статуса `Accepted`.

---

## Контекст

Мастер-план [docs/PLANS/ui-polishing-1.md](../docs/PLANS/ui-polishing-1.md) §6 (батч `polishing/ui-1`,
Блок E) фиксирует пятую пользовательскую задачу: настройка «показывать боковую панель
(`SystemIconsPanel`) на всех виртуальных рабочих столах Windows» — Task View / Win+Ctrl+←/→. Речь
именно о **Windows Virtual Desktops**, а не о физических мониторах (`IMonitorService`/`MonitorInfo` к
этой задаче отношения не имеют).

`SystemIconsPanelWindow`, как и все desktop-окна этого приложения, при старте проходит через
`IDesktopIntegrationService.EmbedInDesktop` (`DesktopIntegrationService.cs:14-27`):

1. `ApplyDesktopWindowStyles(hwnd)` — безусловно добавляет `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`.
2. `GetOrCreateWorkerW()` — шлёт `Progman` сообщение `0x052C` и ищет `WorkerW`.
3. Если `WorkerW` не найден — **fallback-ветка**: стили применены, `SetParent` **не** вызывается,
   окно остаётся top-level tool-окном (см. коммит `8bfd6f8`, «keep panels hidden from Alt+Tab even
   when WorkerW lookup fails»).
4. Иначе — `SetParent(hwnd, workerW)`: окно становится **дочерним** окном слоя рабочего стола.

Окно, ставшее ребёнком `WorkerW`, перестаёт быть top-level-окном с точки зрения оболочки и, скорее
всего, не отслеживается менеджером виртуальных рабочих столов — оно рисуется как часть слоя рабочего
стола. Это важно вдвойне, потому что API виртуальных столов (`IVirtualDesktopManager::
MoveWindowToDesktop`/`GetWindowDesktopId`) в принципе рассчитан на top-level-окна.

---

## Обязательный spike E0 — что было измерено

План требовал не писать код до диагностического spike (`docs/PLANS/ui-polishing-1.md` §6.1, §6.6).
Работа велась в неинтерактивной сессии оркестрации — **физическое переключение виртуального стола и
визуальное наблюдение панели не проверялись в этой сессии**, это явный manual-verify пункт (см. ниже).
Всё, что описано в этом разделе, — статический разбор кода плюс не-визуальные измерения Win32/COM
(собственный тестовый процесс, окно 1×1 за пределами экрана, `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`,
`SW_SHOWNOACTIVATE`; активный виртуальный стол не переключался, фокус пользователя не отнимался,
`0x052C` в `Progman` не отправлялось — дерево окон Explorer'а не мутировалось).

**E0.1 — статический разбор `EmbedInDesktop`.** Подтвердил развилку из §6.1: панель — child `WorkerW`
в удачном сценарии embed'а, top-level tool-окно — в fallback-ветке.

**E0.2 — фактические измерения `IVirtualDesktopManager` (Windows 11, build 10.0.26200, x64):**

| Что измерялось | Результат |
|---|---|
| `CoCreateInstance(CLSID AA509086-…, IID A5CD92FF-…)` | OK — документированный COM-объект активируется |
| `GetWindowDesktopId(GetForegroundWindow())` | `S_OK`, реальный GUID стола |
| `GetWindowDesktopId(Progman)` | `0x8002802B` (`TYPE_E_ELEMENTNOTFOUND`) — слой рабочего стола менеджером виртуальных столов не отслеживается |
| Существующий `WorkerW` без отправки `0x052C` | `0x0` — на этой сборке Win11 `WorkerW` не существует, пока его не создаст клиент трюка `0x052C` |
| Наше top-level окно, ещё не показанное | `IsWindowOnCurrentVirtualDesktop` = `S_OK`/`true`; `GetWindowDesktopId` = `TYPE_E_ELEMENTNOTFOUND` |
| Наше top-level окно, показанное (off-screen) | `GetWindowDesktopId` = `S_OK` + реальный GUID текущего стола |
| То же окно **после `SetParent`** в другое окно | `GetWindowDesktopId` = `S_OK`, но **`GUID_NULL`** — окно теряет принадлежность к виртуальному столу |
| `MoveWindowToDesktop` **во всех** случаях, включая нетрекаемые окна | `S_OK` — ошибку не возвращает, **молча ничего не делает** |

**E0.3 — ключевой вывод и переоценка риска E-R2.** Изначальная формулировка риска E-R2 в плане
предполагала, что `MoveWindowToDesktop` на child-HWND вернёт **ошибку**, и заранее закладывала
митигирование «`TryMove*` возвращает `false`». Фактическое измерение показало иное: на нетрекаемом
окне метод возвращает `S_OK` и является **тихим no-op**. Это не ослабляет защиту (деградация всё равно
тихая — `TryMoveWindowToCurrentDesktop` не бросает и не ломает вызывающий код), но меняет то, на что
можно полагаться: **`true` от `TryMoveWindowToCurrentDesktop` не является доказательством, что окно
реально переехало**. Это явно задокументировано в XML-doc метода
(`IVirtualDesktopService.TryMoveWindowToCurrentDesktop`), а не осталось скрытым допущением.

Косвенно измерения подтверждают гипотезу «исход A» — панель, встроенная в `WorkerW`, уже видна на всех
виртуальных столах по умолчанию (`Progman`/слой рабочего стола вне зоны видимости VDM, а
`SetParent`-переезд сбрасывает desktop-id в `GUID_NULL`). **Строгого визуального подтверждения этому
нет** — развилка A/B (§6.1 плана) не решена автоматически. Это осознанно не блокирует реализацию:
выбранное решение (см. ниже) закрывает оба исхода тихой деградацией.

Полный протокол измерений и способ их воспроизведения — `docs/PLANS/ui-polishing-1.md` §6.6.

---

## Рассмотренные варианты

| Вариант | Механизм | Оценка |
|---|---|---|
| **E-A «Follow-me» (ВЫБРАН)** | Публичный документированный COM `IVirtualDesktopManager` (CLSID `AA509086-5CA9-4C25-8F95-589D3C07B48A`, IID `A5CD92FF-29BE-454C-8D04-D82879FB3F1B`, `shobjidl_core.h`, стабилен с Windows 10 1607). Опрос `IsWindowOnCurrentVirtualDesktop(panelHwnd)`; если `false` — взять GUID текущего стола через `GetWindowDesktopId(GetForegroundWindow())` и вызвать `MoveWindowToDesktop(panelHwnd, id)`. | Единственный документированный, стабильный API на все версии Windows. Не «настоящий pin» — окно один раз переезжает, а не закреплено; задержка до одного тика опроса после переключения стола. |
| **E-B «Настоящий pin» — ОТКЛОНЁН** | Недокументированный `IVirtualDesktopPinnedApps` через `IServiceProvider` на CLSID `CLSID_ImmersiveShell` (`C2F03A33-21F5-47FA-B4BB-156362A2F239`). | Истинное закрепление без задержки, но **IID меняется между сборками Windows** (Win10 1809/1903/2004, Win11 21H2/22H2/23H2/24H2) — потребовалась бы таблица build→IID с постоянным сопровождением, и решение ломается будущими обновлениями ОС. Отклонён на этапе выбора варианта (§6.2 плана); вернуться к нему можно только если E-A на практике окажется непригодным (что E0 не показал). |
| **E-C «Опора на слой рабочего стола»** | Ничего нативного не добавляется: полагаться на то, что успешный `EmbedInDesktop` уже даёт видимость на всех столах. | Ноль нового кода — но объясняет только поведение **по умолчанию**; не даёт **выключаемой** настройки и неприменим, когда `WorkerW`-lookup не удался (fallback-ветка, top-level окно). Используется как объяснение базовой линии, не как реализация. |

**Решение:** реализован **E-A**. Деградация (по образцу [ADR-012](ADR-012-performance-and-resilience-hardening.md)
«Решение 4», раздел о недоверенном кеше — общий инвариант проекта «любая ошибка COM/HRESULT — тихий
`catch`, безопасный дефолт, ничего не падает наружу»): любая ошибка COM/HRESULT →
`IsSupported = false`; настройка ведёт себя как выключенная; панель продолжает работать штатно. Ни
одно исключение не покидает `VirtualDesktopService`.

---

## Архитектура

**Abstractions** — `src/DesktopOrganizer.Services.Abstractions/IVirtualDesktopService.cs` (новый):

```csharp
public interface IVirtualDesktopService
{
    bool IsSupported { get; }
    bool IsWindowOnCurrentDesktop(IntPtr hwnd);
    bool TryMoveWindowToCurrentDesktop(IntPtr hwnd);
}
```

`IsWindowOnCurrentDesktop` возвращает `true` не только когда окно действительно на активном столе, но
и когда состояние неопределимо (неподдерживаемая система, ошибочный HRESULT, нетрекаемое окно) —
осознанный выбор «не трогать окно при неизвестности», раз вызывающий код использует этот метод как
guard перед перемещением.

**Thread affinity.** Все члены `IVirtualDesktopService` вызываются **только со STA-потока** (в этом
приложении — WPF UI-поток): за интерфейсом стоит apartment-threaded shell-COM. Это тот же контракт,
что у `NativeApi.ShowShellContextMenu` ([ADR-008](ADR-008-shell-com-sta-threading-and-pidl-marshaling.md)),
и он зафиксирован в XML-doc интерфейса и проверяется `Debug.Assert` в каждом публичном методе
реализации.

**Services** — `src/DesktopOrganizer.Services/Tools/VirtualDesktopService.cs` (новый):
`[ComImport, Guid, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]`-объявление
`IVirtualDesktopManager` объявлено `private` внутри самого класса; активация через
`Activator.CreateInstance(Type.GetTypeFromCLSID(...))`, ленивая инициализация с кешированием,
`IsSupported` вычисляется один раз за время жизни экземпляра.

**Жизненный цикл RCW.** Сам COM-вызов выполняется под тем же `_gate`, что и активация: иначе
конкурентный `Dispose()` мог бы освободить RCW под активным вызовом. `Dispose()` при этом **не**
вызывает `Marshal.FinalReleaseComObject` — принудительное убийство RCW превратило бы любую ещё
живущую ссылку в `InvalidComObjectException`; поле просто обнуляется, освобождение делает финализатор
RCW. Гонка закрыта by construction, а объект — один дешёвый shell-объект, эагерно освобождать нечего.
Если активация прошла, но каст к `IVirtualDesktopManager` дал `null`, осиротевший `__ComObject`
освобождается через `Marshal.ReleaseComObject`, а не утекает до ближайшего GC.

**Детекция смены стола** — `DispatcherTimer` (~1 с) в `SystemIconsPanelManager`, по образцу
poll-таймера Корзины ([ADR-007](ADR-007-system-icons-panel-window-type.md)). Таймер запускается
**только** когда настройка включена И окно существует И `IsSupported`; останавливается в
`ClosePanel`/`Dispose`. Политика вынесена в чистую функцию
`SystemIconsPanelManager.ShouldPollVirtualDesktop(panelEnabled, showOnAllVirtualDesktops, hasWindow,
virtualDesktopsSupported)` — таблица истинности покрыта юнит-тестами, включая ветку «окно есть»,
которую нельзя воспроизвести без STA-диспетчера. `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)`
рассмотрен и отклонён: второй глобальный хук в процессе, где уже есть `WH_MOUSE_LL` со своими
жёсткими инвариантами, — неоправданный риск ради экономии одного тика в секунду.

### Политика re-embed и circuit breaker (пересмотрено после ревью)

Первая редакция вызывала `EmbedInDesktop` **безусловно после каждого** `TryMoveWindowToCurrentDesktop
() == true`, полагаясь на идемпотентность. С учётом измерения E0.2 (`MoveWindowToDesktop` отвечает
`S_OK` **всегда**, включая нетрекаемые/child-окна, где он тихий no-op) это означало: если
`IsWindowOnCurrentDesktop` для встроенной панели стабильно даёт `false`, то `EmbedInDesktop` —
синхронный `SendMessage(0x052C)` в Explorer без таймаута плюс полный `EnumWindows` плюс `SetParent` —
выполнялся бы **каждую секунду бесконечно**: подвисание UI-потока при занятом Explorer, мерцание и
риск размножения `WorkerW`. Политика заменена на две защиты:

1. **Re-embed только при реально слетевшем parenting.** `IDesktopIntegrationService` расширен методом
   `bool IsEmbedded(IntPtr hwnd)` — две дешёвые `User32`-функции (`GetParent` + `GetClassName`,
   оба за `INativeApi`), без `0x052C` и без `EnumWindows`. Тик вызывает `EmbedInDesktop` только когда
   `IsEmbedded(hwnd) == false`. Риск E-R3 (переезд между столами сбрасывает `SetParent`) закрыт
   по-прежнему, но цена платится по факту, а не превентивно на каждом тике.
2. **Проверка эффекта + circuit breaker.** `true` от `TryMoveWindowToCurrentDesktop` — только «вызов
   принят», поэтому сразу после переезда состояние перепроверяется `IsWindowOnCurrentDesktop`. Если
   переезд «успешен», но эффекта нет, счётчик растёт; после `MaxIneffectiveMoves = 3` подряд менеджер
   выставляет `SystemIconsPanelModel.VirtualDesktopFeatureIneffective` (runtime-only, не
   персистится) и **останавливает таймер**. На системах, где документированный API неприменим к
   данному окну, приложение перестаёт долбить Explorer раз в секунду. Счётчик обнуляется, как только
   окно снова оказывается на активном столе, а латч снимается при явном действии пользователя
   (`Apply()` / `ApplyVirtualDesktopSetting()`) — это точка ретрая.

Тело тика вынесено в `internal void PollVirtualDesktopOnce(IntPtr hwnd)`, чтобы вся
последовательность guard → move → проверка эффекта → условный re-embed проверялась юнит-тестами на
моках `IVirtualDesktopService`/`IDesktopIntegrationService` без реального окна.

### Переключение настройки не пересоздаёт окно

`ISystemIconsPanelManager` получил `ApplyVirtualDesktopSetting()`; сеттер
`MainWindowViewModel.SystemIconsPanelOnAllVirtualDesktops` вызывает именно его, а не общий `Apply()`.
`Apply()` — это `ClosePanel() + OpenPanel()`, т.е. уничтожение и пересоздание HWND панели с лишним
`EmbedInDesktop` и видимым мерцанием; настройка же влияет **только** на poll-таймер. Когда живого
окна нет, `ApplyVirtualDesktopSetting()` делегирует в `Apply()` — решение о создании окна остаётся
за ним.

### Настройка и persistence

Новое свойство добавлено **аддитивно** в существующую JSON-секцию `SystemIconsPanel`, не как новое
свойство верхнего уровня `IAppSettings`:

- `SystemIconsPanelManager.PanelDto.ShowOnAllVirtualDesktops` (`bool`, default `false`).
- `SystemIconsPanelModel.ShowOnAllVirtualDesktops` (INPC).
- Отсутствующий ключ в старом JSON ⇒ `false`; миграция не нужна — обратная совместимость по
  построению.
- `MainWindowViewModel.SystemIconsPanelOnAllVirtualDesktops`, `MainWindow.xaml`: чекбокс под
  «Show system icons panel», `IsEnabled="{Binding SystemIconsPanelEnabled}"`, `Content = "Show on all
  virtual desktops"`, `ToolTip = "Keep the panel visible when you switch Windows virtual desktops
  (Task View)."`.

---

## Осознанное отступление от инварианта «P/Invoke только за `INativeApi`»

Инвариант проекта требует, чтобы весь P/Invoke жил за `INativeApi`. COM-активация
`IVirtualDesktopManager` в `VirtualDesktopService` формально не является P/Invoke (это не
`[DllImport]`/`[LibraryImport]`-вызов нативной функции, а активация COM-объекта через
`Type.GetTypeFromCLSID` + `Activator.CreateInstance`, с приватным COM-интерфейсом, объявленным
непосредственно в самом сервисе). Решение — **не выносить это объявление за `INativeApi`**:

- Граница абстракции здесь — сам `IVirtualDesktopService`, а не уровень P/Invoke. Инвариант проекта
  защищает от размазывания raw Win32-вызовов по кодовой базе; COM-интерфейс `IVirtualDesktopManager`
  и так уже инкапсулирован единственным классом-реализацией и не «протекает» наружу.
  `IVirtualDesktopService` играет для COM ровно ту же роль изоляции, что `INativeApi` — для P/Invoke.
- Настоящие P/Invoke-вызовы, добавленные этим блоком, — `GetForegroundWindow` (нужен, чтобы узнать
  GUID активного стола через окно активного приложения) и пара `GetParent`/`GetClassName` для
  `IsEmbedded`. Все они добавлены туда, где положено: в `INativeApi`/`NativeApi`
  (`src/DesktopOrganizer.Services/Interfaces/INativeApi.cs`, `Tools/NativeApi.cs`), а не в
  `VirtualDesktopService`/`DesktopIntegrationService`. `GetClassName` объявлен классическим
  `DllImport` (буфер `StringBuilder` не поддерживается генератором `LibraryImport`) — но по-прежнему
  внутри `NativeApi.NativeMethods`.

Итог: ни один вызов COM виртуальных столов не выходит за пределы `VirtualDesktopService`; ни один
P/Invoke-вызов не объявлен в обход `INativeApi`.

---

## Manual verify (Win10 / Win11)

Перенесено из `docs/PLANS/ui-polishing-1.md` §8, пп. 14–17 — не покрывается автотестами, требует
интерактивной Windows-сессии. **Manual verify выполнена на Windows 11 24H2 2026-09-27 (для актуального
механизма — ADR-019), признана достаточной; Windows 10 не проверяется** — см. [docs/ROADMAP.md](../docs/ROADMAP.md)
(строки 6–8). Пункты ниже — исторический чек-лист (пп. 5–6 относились к удалённой re-embed-логике и
неприменимы):

1. **E0** — панель на другом виртуальном столе: видна / не видна (зафиксировать фактический исход A/B
   на обеих ОС — до сих пор проверено только статически/через COM-зонд на Win11 26200, не через
   реальное переключение стола глазами; Windows 10 не проверялась вовсе).
2. **E** — с включённой настройкой `Show on all virtual desktops` панель видна на всех виртуальных
   столах после Win+Ctrl+←/→ и через Task View; с выключенной — прежнее поведение (видна только там,
   где создана, при исходе B, либо не отличается от исхода A).
3. **E** — переезд между столами не ломает Z-order (панель остаётся поверх обоев, под окнами
   приложений — та же гарантия, что даёт `EmbedInDesktop` изначально).
4. **E** — приложение не падает и не тормозит при недоступном/отсутствующем COM-объекте виртуальных
   столов (`IsSupported = false`): настройка визуально доступна, но не даёт эффекта, панель работает
   штатно.
5. **E (после ревью)** — что фактически возвращает `IsWindowOnCurrentVirtualDesktop` для
   **child-HWND панели, встроенной в `WorkerW`**. В E0.2 измерялся только `GetWindowDesktopId`
   (`S_OK` + `GUID_NULL`); поведение `IsWindowOnCurrentVirtualDesktop` на таком окне **не
   измерялось**. От него зависит, попадёт ли тик вообще в ветку переезда. Оба исхода безопасны:
   `true` ⇒ тик выходит на guard'е (ноль работы), `false` ⇒ через 3 неэффективных попытки
   срабатывает circuit breaker. Проверить и зафиксировать здесь фактический ответ.
6. **E (после ревью)** — при включённой настройке в течение нескольких минут: `EmbedInDesktop` **не**
   вызывается на каждом тике (нет мерцания панели, нет размножения `WorkerW`, UI-поток не подвисает
   при занятом Explorer); переключение чекбокса `Show on all virtual desktops` **не** приводит к
   пересозданию окна панели (нет вспышки/перерисовки, панель не всплывает поверх окон).

Особый приоритет — пункт 1: без него развилка A/B (§6.1 плана) остаётся неподтверждённой визуально,
хотя архитектурно она уже закрыта тихой деградацией (пункт 2 будет верным при любом исходе).

---

## Последствия

- **Публичные контракты расширены аддитивно.** Новый `IVirtualDesktopService`
  (Services.Abstractions) и его единственная реализация `VirtualDesktopService` (Services/Tools).
  `INativeApi` получил `GetForegroundWindow`, а для дешёвой проверки parenting — `GetParent` и
  `GetWindowClassName` (голых `DllImport` вне `NativeApi` не появилось).
  `IDesktopIntegrationService` получил `IsEmbedded(IntPtr)`; `ISystemIconsPanelManager` —
  `ApplyVirtualDesktopSetting()`. `SystemIconsPanelManager.PanelDto`/
  `SystemIconsPanelModel` получили аддитивное поле `ShowOnAllVirtualDesktops` (default `false`) —
  старые JSON-файлы настроек читаются без ошибок и без миграции; `VirtualDesktopFeatureIneffective`
  добавлено только в модель (runtime-only, в JSON не пишется).
- **`ChildWindow`/`WindowManager` не затронуты.** Блок E целиком живёт в системе `SystemIconsPanel`;
  поведение fence-панелей на виртуальных столах не менялось (риск E-R7 закрыт по построению,
  проверено `git diff --name-only`).
- **`TryMoveWindowToCurrentDesktop() == true` не гарантирует фактического переезда окна** — это
  задокументированное поведение API (`MoveWindowToDesktop` возвращает `S_OK` как no-op на нетрекаемых
  окнах), а не дефект реализации. Любой будущий код, читающий этот метод, обязан относиться к нему
  как к «попытка принята», не как к подтверждению результата. `SystemIconsPanelManager` теперь
  соблюдает это явно: перепроверяет результат через `IsWindowOnCurrentDesktop` и после трёх подряд
  безрезультатных «успехов» гасит опрос (см. «Политика re-embed и circuit breaker»).
- **Риск на будущее.** Решение целиком опирается на задокументированный, но не гарантированный
  контрактом Microsoft nюанс поведения (`S_OK`-no-op вместо ошибки на нетрекаемых окнах, отсутствие
  desktop-id у `WorkerW`/дочерних окон). Если будущая версия Windows начнёт трекать окна слоя рабочего
  стола иначе, изменит семантику `MoveWindowToDesktop` на child-HWND, либо начнёт возвращать ошибку
  вместо тихого no-op — тихая деградация (`IsSupported`/try-catch) продолжит защищать от падений, но
  фактическое поведение настройки может измениться без предупреждения. Если Microsoft закроет
  `IVirtualDesktopManager` целиком (маловероятно — API документирован и стабилен с 2016 года) —
  `IsSupported` перейдёт в `false`, настройка станет no-op, деградация сработает штатно. Пересмотр
  архитектуры (например, возврат к рассмотрению E-B при появлении публичного pin-API) — отдельный
  тикет, не блокирует текущее решение.
- **Развилка A/B (панель уже видна на всех столах / видна только на «своём») не подтверждена
  визуально** — см. Manual verify выше. Это не блокирует Accepted-статус решения: выбранная
  архитектура (E-A + тихая деградация) корректна при любом из двух исходов по построению, а не
  зависит от того, какой из них окажется верным.

**Manual verify** — см. раздел выше и [docs/ROADMAP.md](../docs/ROADMAP.md) (строки 6–8; раздел
«Полировочный батч polishing/ui-1»): выполнена на Windows 11 24H2 2026-09-27 для актуального механизма
(ADR-019).
