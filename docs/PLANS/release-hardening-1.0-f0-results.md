# Release hardening 1.0 — F0: результаты DPI-спайка

> Пакет F, под-пакет F0 из [release-hardening-1.0.md](release-hardening-1.0.md) (§6). Дата: 2026-09-27.
> Машина: Windows 11 **build 26200** (десктоп-раскладка 24H2+), **один монитор** 1920x1080 @ 100 %.
> Проб: [`tools/dpi-probe/`](../../tools/dpi-probe/) (консольный PerMonitorV2, вне `DesktopOrganizer.sln`; `src/` не менялся).
> Примечание: план называл файл `release-hardening-1.0-F0-dpi-spike.md`; по поручению оркестратора результат лежит здесь.

## Вердикт (коротко)

1. **Гейт F0 сработал — F1–F4 в текущем виде НЕ стоят, нужен перепланинг (CLARIFICATION_NEEDED, варианты в §6).**
2. Причина не та, которую проверял гейт («дети получают per-monitor DPI»), а более фундаментальная: **на Windows 11 24H2+
   (измерено на build 26200) панели вообще не являются дочерними окнами WorkerW.** Поиск `DesktopIntegrationService.GetOrCreateWorkerW()`
   на этой раскладке возвращает `0`, `EmbedInDesktop` молча пропускает `SetParent`, и панели остаются **top-level** окнами
   (подтверждено на живом процессе `DesktopOrganizer.UI`: 7 видимых окон, у всех `GA_PARENT = #32769` (desktop), ни одного
   ребёнка у `Progman` или его `WorkerW`). «Ключевой архитектурный факт» §6 плана верен только для классической раскладки.
3. Отсюда два режима с противоположным DPI-поведением под PerMonitorV2:

| Режим | Где | Что с панелями | DPI панели под PMv2 |
|---|---|---|---|
| **T (top-level)** | Win11 24H2+ (измерено: 26200) | `SetParent` не выполняется, панели — top-level `WS_POPUP` | **per-monitor**: DPI монитора, на котором лежит окно; `WM_DPICHANGED` при переходе между мониторами (документировано, §3; на одном мониторе смены нет) |
| **E (embedded)** | Win10, Win11 ≤ 23H2 (классическая раскладка: top-level `WorkerW` за хостом `SHELLDLL_DefView`) | дочерние окна WorkerW | **единый DPI top-level предка** (WorkerW); смена — только через `WM_DPICHANGED_BEFOREPARENT/AFTERPARENT`, когда меняется DPI самого WorkerW (документировано; здесь не измеримо — manual verify) |

4. Единый «desktop-layer scale» (F2: `GetDesktopLayerScale() = GetDpiForWindow(WorkerW)`) в режиме T не имеет WorkerW (fallback на
   system DPI), а WPF будет масштабировать каждую top-level панель по DPI её монитора. На смешанном DPI DIP-прямоугольники
   `MonitorService` (один масштаб) разойдутся с тем, как WPF переводит `Window.Left/Top` панели на неосновном мониторе → смещение
   и неверный размер панелей. На односкейловых конфигурациях (включая эту машину) разницы нет — поэтому спайк на одном мониторе
   ничего не «ломает», но и не может подтвердить план.

## 1. Что сделано

`tools/dpi-probe/` — `net8.0-windows` console, собственный `app.manifest` (`<dpiAwareness>PerMonitorV2, PerMonitor</dpiAwareness>`
+ `<dpiAware>true/pm</dpiAware>`, `asInvoker`, supportedOS Win10) — ровно та декларация, которую F1 планирует для UI.
Изолирован от репозитория собственными `Directory.Build.props`/`Directory.Packages.props` (без StyleCop/WPF/CPM корня).
Сборка: `dotnet build -c Release` — **0 warnings, 0 errors** (`TreatWarningsAsErrors`, `AnalysisLevel=latest-recommended`).

Что проб делает:
- окружение: осведомлённость процесса/потока, `GetDpiForSystem`, `GetSystemDpiForProcess`, виртуальный экран;
- все мониторы: `EnumDisplayMonitors` + `GetMonitorInfo` + `GetDpiForMonitor(MDT_EFFECTIVE_DPI / MDT_RAW_DPI)`, px-прямоугольники;
- слой рабочего стола **ровно как `GetOrCreateWorkerW()`**: `FindWindow("Progman")` → `SendMessage(Progman, 0x052C, 0, 0)` →
  `EnumWindows` → хост `SHELLDLL_DefView` → `FindWindowEx(NULL, host, "WorkerW", NULL)`; плюс дамп детей `Progman`, всех top-level
  `WorkerW`, `GetDpiForWindow(Progman / WorkerW)`;
- read-only осмотр окон работающего `DesktopOrganizer.UI` (где реально живут панели и их DPI/awareness);
- эксперименты: окно `WS_POPUP` + `WS_EX_TOOLWINDOW|WS_EX_NOACTIVATE` (как панели); снимок `GetDpiForWindow` +
  `GetAwarenessFromDpiAwarenessContext(GetWindowDpiAwarenessContext)` + `GA_PARENT/GA_ROOT` + стили до и после `SetParent`;
  перемещение `SetWindowPos` на px-прямоугольник каждого монитора с повторным чтением DPI, `MonitorFromWindow`, дельты
  «запрошено ↔ `GetWindowRect`» и логом `WM_DPICHANGED / _BEFOREPARENT / _AFTERPARENT / WM_GETDPISCALEDSIZE` из WndProc;
  очистка `SetParent(hwnd, NULL)` + снимок + `DestroyWindow`.
  - **E0** — PMv2 top-level, без reparent (контроль = то, что продукт делает на 24H2+);
  - **E1/E2** — PMv2 / SYSTEM_AWARE → `SetParent(WorkerW из продуктового поиска)` (здесь пропущены: поиск вернул 0);
  - **E3** — PMv2 → `SetParent(WorkerW-ребёнок Progman)` (раскладка 24H2);
  - **E4/E5** — PMv2 / SYSTEM_AWARE → `SetParent(Progman)`.
- `--hold N` держит каждое тестовое окно ещё N секунд и логирует DPI-сообщения — для ручной смены масштаба «на лету».

Запуск: `cd tools/dpi-probe && dotnet run -c Release [-- [processName] [--hold N]]`.

## 2. Результаты на этой машине (Win11 26200, 1 монитор @ 96 DPI)

| Измерение | Значение |
|---|---|
| Осведомлённость процесса проба | `PER_MONITOR_AWARE (V2)` — манифест применён к apphost |
| Мониторы | 1: `\\.\DISPLAY1` `[0,0-1920,1080]`, effective 96 (100 %), raw 127 |
| Виртуальный экран | origin (0,0), 1920x1080 |
| `Progman` | top-level, `[0,0-1920,1080]`, DPI 96, **PMv2**, сам хостит `SHELLDLL_DefView` |
| Дети `Progman` | `SHELLDLL_DefView` и **`WorkerW` (ребёнок Progman — раскладка 24H2)**, оба `[0,0-1920,1080]`, DPI 96 |
| **Продуктовый поиск WorkerW** | **`0` (not found)** — top-level WorkerW после Progman нет; единственный top-level WorkerW Explorer-потока рабочего стола — скрытый `0x0` |
| `GetDpiForWindow(WorkerW продукта)` | n/a (поиск = 0) |
| `GetDpiForWindow(Progman)` / `(Progman>WorkerW)` | 96 / 96 |
| Живой `DesktopOrganizer.UI` (0.5.1 Debug; `DesktopIntegrationService` с v0.5.1 не менялся) | 7 видимых окон `HwndWrapper[...]`, все **top-level** (`GA_PARENT=#32769`), `SYSTEM_AWARE`, DPI 96 |
| E0 (top-level PMv2) | DPI 96, PMv2, без DPI-сообщений; `SetWindowPos(100,100)` → `GetWindowRect` (100,100), дельта 0 |
| E3 (→ Progman>WorkerW) | `SetParent` успешен (err 0); `GA_PARENT=WorkerW`, **`GA_ROOT=Progman`**; стиль остаётся `WS_POPUP` (не `WS_CHILD`); DPI 96 до/после; DPI-сообщений нет; дельта позиции 0 (origin клиентской области родителя (0,0) = origin экрана) |
| E4 (→ Progman) | то же, `GA_ROOT=Progman`, DPI 96, без сообщений |
| **E5 (SYSTEM_AWARE → Progman)** | `SetParent` успешен; **awareness окна `SYSTEM_AWARE` → `PER_MONITOR_AWARE (V2)` и остаётся PMv2 даже после `SetParent(NULL)`** |

Выводы из измерений:
- **Режим T подтверждён на целевой ОС**: продукт на 24H2+ не встраивает панели; они top-level и держатся внизу Z-order таймером
  `SendToBackOfZOrder` (ADR-020), а не иерархией окон.
- **Cross-process `SetParent` работает на 26200** (E3/E4) и делает корнем чужой (Explorer) top-level `Progman` — значит в режиме E
  DPI ребёнка определяется DPI окна Explorer, а не нашим процессом.
- **Forced reset подтверждён (E5)**: cross-process `SetParent` system-aware окна в PMv2-окно Explorer **необратимо** переводит
  контекст HWND в PMv2. Для текущего (system-aware) продукта в режиме E (Win10/≤23H2) это значит: WPF-панель считает себя
  system-aware, а Windows обращается с её HWND как с PMv2 — возможный источник уже существующих DPI-артефактов на Win10 при
  смешанном DPI. После F1 (процесс PMv2) расхождение исчезает — риск из §6 плана подтверждён и снимается F1.
- **MV-1 (`DesktopCoordinateSpace`) здесь не закрывается**: origin виртуального экрана и клиентской области родителя совпадают
  (0,0), поэтому нельзя отличить «экранные» координаты `SetWindowPos` от «клиентских родителя» для reparented `WS_POPUP`.
  Проб печатает дельту — закрывается его запуском на конфигурации с монитором слева/сверху (отрицательные координаты).
- Per-monitor DPI и приход `WM_DPICHANGED*` при переходе между мониторами на одном мониторе **не наблюдаемы** (всё 96, сообщений
  нет — ожидаемо). Поведение для нескольких мониторов ниже — по документации.

## 3. Документированное поведение Windows (многомониторный случай)

Цитаты приведены по документации Microsoft Learn по памяти (сеть в сессии не использовалась) — сверить формулировки при
оформлении ADR-037.

1. **`GetDpiForWindow`** — <https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-getdpiforwindow>:
   результат зависит от `DPI_AWARENESS` окна: UNAWARE → 96; SYSTEM_AWARE → system DPI; PER_MONITOR_AWARE → «the DPI of the
   monitor where the window is located». Для top-level PMv2-панели (режим T) это DPI её монитора.
2. **`DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2`** — <https://learn.microsoft.com/windows/win32/hidpi/dpi-awareness-context>:
   «An advancement over the original per-monitor DPI awareness mode, which enables applications to access new DPI-related scaling
   behaviors **on a per top-level window basis**»; среди поведений — «Child HWND DPI change notifications are sent». Единица DPI в
   PMv2 — top-level окно; дочерние окна его наследуют.
3. **`WM_DPICHANGED`** — <https://learn.microsoft.com/windows/win32/hidpi/wm-dpichanged>: шлётся, когда меняется эффективный DPI окна
   (перемещение на монитор с другим DPI или смена масштаба); `lParam` — предлагаемый прямоугольник. Адресовано top-level окнам.
4. **`WM_DPICHANGED_BEFOREPARENT`** — <https://learn.microsoft.com/windows/win32/hidpi/wm-dpichanged-beforeparent>: «For Per Monitor v2
   top-level windows, this message is sent to all HWNDs in the child HWND tree of the window that is undergoing a DPI change. This
   message occurs before the top-level window receives WM_DPICHANGED, and traverses the child tree from the bottom up.»
5. **`WM_DPICHANGED_AFTERPARENT`** — <https://learn.microsoft.com/windows/win32/hidpi/wm-dpichanged-afterparent>: то же, но «after the
   top-level window receives WM_DPICHANGED, and traverses the child tree from the top down». → Дочерний HWND узнаёт о смене DPI
   **только когда меняется DPI его top-level предка**, а не когда сам ребёнок оказался над другим монитором. В режиме E предок —
   `WorkerW`/`Progman` Explorer, покрывающий весь виртуальный экран; его DPI один (обычно — основного монитора).
6. **`SetParent`** — <https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-setparent>, Remarks, таблица для окон с разной
   DPI-осведомлённостью: *In-proc* — Win10 1703+ **Fail (`ERROR_INVALID_STATE`)**; *Cross-proc* — **Forced reset (of child window's
   process)**. E5 выше — наблюдаемое проявление cross-proc forced reset.
7. **`SetThreadDpiHostingBehavior`** — <https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-setthreaddpihostingbehavior>:
   `DPI_HOSTING_BEHAVIOR_MIXED` разрешает in-proc детей с иной осведомлённостью; к cross-proc родителю (Explorer) не относится.
8. **Обзор** — <https://learn.microsoft.com/windows/win32/hidpi/high-dpi-desktop-application-development-on-windows>: таблица режимов
   осведомлённости (PMv2: уведомления о смене DPI для top-level и дочерних HWND, масштабирование non-client области и т. д.).

Итог по документации: **встроенная (режим E) панель получает единый DPI WorkerW, а не per-monitor** — гипотеза плана для этого
режима верна. **Top-level (режим T) панель под PMv2 получает per-monitor DPI** — и на целевой ОС панели именно такие.

## 4. Последствия для F1–F4

| Под-пакет | Режим E (Win10 / Win11 ≤ 23H2) | Режим T (Win11 24H2+) |
|---|---|---|
| F1 манифест PMv2 | как в плане; снимает forced-reset-расхождение (E5) | меняет поведение панелей: из «растянутых DWM, но геометрически согласованных» (system-aware) в per-monitor-масштабируемые WPF-окна |
| F2 `GetDesktopLayerScale()` | = DPI WorkerW — корректно | WorkerW нет → fallback system DPI → **неверный** масштаб для панелей на неосновном мониторе с иным DPI |
| F3 `MonitorService` в едином layer-DIP | корректно | **ошибка**: панели живут в per-monitor DIP (WPF переводит `Window.Left/Top` по DPI окна), единое пространство рассыпается |
| F4 лассо `px / layer scale` | корректно | px хука нужно делить на DPI монитора под курсором |
| Инвентаризация #18 («DIP-потребители без изменений») | верно | неверно — все потребители `MonitorInfo` получают смешанное пространство |

Односкейловые конфигурации (DoD п. 2) в обоих режимах ведут себя как 0.6.1 (все DPI равны system DPI) — на этой машине это так.

## 5. Статус гейта

**Гейт F0 → «перепланировать».** Формальное условие плана («дети получают per-monitor DPI») на целевой ОС проявляется в более
сильной форме: там панели вообще не дети. F1–F4 нельзя запускать в волне 4 как есть.

## 6. CLARIFICATION_NEEDED — варианты для оркестратора

- **A. Двухрежимный F (XL+).** Определять режим по факту (`GetAncestor(hwnd, GA_ROOT) == hwnd` → T). Режим E — план как есть;
  режим T — геометрия панелей в px (`SetWindowPos`) или per-monitor DIP, `MonitorInfo` с per-monitor конверсией, лассо по DPI
  монитора. Двойной набор тестов и manual verify. Самый честный, самый дорогой.
- **B. Унифицировать встраивание на 24H2+, затем план F1–F4 как есть.** Починить поиск в `GetOrCreateWorkerW` для раскладки 24H2
  (parent в `Progman` или его ребёнка `WorkerW` — E3/E4 показывают, что `SetParent` проходит) → на всех ОС режим E, единый layer
  scale снова верен. Но это изменение ADR-001/ADR-020 на основной ОС: нужен отдельный спайк видимости (на 24H2 `SHELLDLL_DefView`
  — сосед `WorkerW` внутри `Progman` и выше него по Z; ребёнок `WorkerW` может оказаться **под** иконками), ввода, Win+D,
  перезапуска Explorer. Риск регрессии «панели пропали» на основной ОС.
- **C (рекомендуется для 1.0). Вынести F1–F4 за 1.0.** Оставить system-aware (как 0.6.1: геометрия согласована в обоих режимах, на
  мониторе с иным DPI панели размыты DWM-растяжением), задокументировать «смешанный DPI» в известных ограничениях (пункт уже есть
  в плане, пакет G, README). Перепланировать F после 1.0 по A или B (B — начать со спайка видимости на 24H2). `tools/dpi-probe`
  остаётся инструментом для этого.
- **D. Top-level везде (отказ от WorkerW и на Win10) + полноценный PMv2.** Максимальная близость к Fences по per-monitor DPI,
  но редизайн ADR-001; вне объёма release hardening.

## 7. Manual verify (Win10/Win11) — нужна машина с двумя мониторами

Запускать `dotnet run -c Release -- --hold 30` из `tools/dpi-probe/` и прикладывать вывод к этому документу:
1. **Win11 24H2/25H2, 100 % + 150 %, второй монитор слева (отрицательные координаты):** E0 — ожидается `GetDpiForWindow` = DPI
   монитора и `WM_DPICHANGED` при переходе; E3/E4 — ожидается DPI = DPI `Progman` на обоих мониторах, без `WM_DPICHANGED`;
   дельта `SetWindowPos` ↔ `GetWindowRect` для reparented `WS_POPUP` (закрывает MV-1).
2. **Win10 22H2 и Win11 23H2, та же конфигурация:** продуктовый поиск должен вернуть top-level WorkerW (E1/E2 выполнятся);
   ожидается единый DPI WorkerW на обоих мониторах; E2 — forced reset SYSTEM_AWARE → PMv2.
3. **Смена масштаба основного монитора во время `--hold`:** ожидается `WM_DPICHANGED_BEFOREPARENT/AFTERPARENT` у reparented окна
   (E1/E3/E4) и `WM_DPICHANGED` у top-level (E0), если окно на основном мониторе.
4. Подтвердить, что `GetOrCreateWorkerW` на Win11 23H2 и Win10 находит WorkerW (режим E), а на 24H2 (26100) ведёт себя как на 26200.

## Приложение A. Полный вывод прогона (Win11 26200)

`dotnet run -c Release` из `tools/dpi-probe/`, exit code 0. Из списка «All top-level WorkerW windows» опущены 17 скрытых окон
`WorkerW` 136x39 (служебные окна других потоков Explorer, pid 41588, и ещё одного процесса, pid 13100) — к слою рабочего стола не
относятся. Остальное — дословно.

```text
=== ENVIRONMENT ===
OS                         : Microsoft Windows NT 10.0.26200.0 (64-bit process: True)
Process                    : pid 15960, C:\Workspace\0.csharp\0.Desktop\DesktopOrganizer\tools\dpi-probe\bin\Release\net8.0-windows\dpi-probe.exe
Process awareness          : PER_MONITOR_AWARE (V2)
Thread awareness           : PER_MONITOR_AWARE (V2)
GetDpiForSystem            : 96 (100%)
GetSystemDpiForProcess     : 96 (100%)
Virtual screen (px)        : origin (0,0) size 1920x1080, monitors 1

=== MONITORS (EnumDisplayMonitors + GetDpiForMonitor) ===
\\.\DISPLAY1   hmon=0x10001 primary=True  rc(px)=[0,0 - 1920,1080] 1920x1080 work(px)=[0,0 - 1920,1032] 1920x1032 effectiveDpi=96 (100%) rawDpi=127

=== SHELL DESKTOP LAYER ===
FindWindow(Progman)        : 0x20460 class=Progman pid=41588 tid=6456 visible=True rect=[0,0 - 1920,1080] 1920x1080 dpi=96 awareness=PER_MONITOR_AWARE (V2) GA_PARENT=0x1000C(#32769)
SendMessage(Progman,0x052C): delivered
Top-level SHELLDLL_DefView host: 0x20460 class=Progman pid=41588 tid=6456 visible=True rect=[0,0 - 1920,1080] 1920x1080 dpi=96 awareness=PER_MONITOR_AWARE (V2) GA_PARENT=0x1000C(#32769)
PRODUCT LOOKUP RESULT (WorkerW) : 0 (not found)

Direct children of Progman:
  0x30418 class=SHELLDLL_DefView pid=41588 tid=6456 visible=True rect=[0,0 - 1920,1080] 1920x1080 dpi=96 awareness=PER_MONITOR_AWARE (V2) GA_PARENT=0x20460(Progman)
  0x1905BA class=WorkerW pid=41588 tid=6456 visible=True rect=[0,0 - 1920,1080] 1920x1080 dpi=96 awareness=PER_MONITOR_AWARE (V2) GA_PARENT=0x20460(Progman)

All top-level WorkerW windows:
  0x40076 class=WorkerW pid=41588 tid=6456 visible=False rect=[0,0 - 0,0] 0x0 dpi=96 awareness=PER_MONITOR_AWARE (V2) GA_PARENT=0x1000C(#32769)

GetDpiForWindow(Progman)          = 96 (100%)
GetDpiForWindow(product WorkerW)  = n/a (lookup returned 0)
GetDpiForWindow(Progman>WorkerW)  = 96 (100%)

=== RUNNING PROCESS 'DesktopOrganizer.UI' (read-only inspection) ===
pids: 30592
Top-level windows owned by the process (visible only):
  0x110B6C class=HwndWrapper[DesktopOrganizer.UI;;e2675b7b-d748-484b-8fdb-fbb375b506d7] pid=30592 tid=32420 visible=True rect=[-32000,-32000 - -31840,-31972] 160x28 dpi=96 awareness=SYSTEM_AWARE ctxDpi=96 GA_PARENT=0x1000C(#32769)
  0x5B135E class=HwndWrapper[DesktopOrganizer.UI;;324ed619-d382-423f-9740-45763eaeecc9] pid=30592 tid=32420 visible=True rect=[286,88 - 572,220] 286x132 dpi=96 awareness=SYSTEM_AWARE ctxDpi=96 GA_PARENT=0x1000C(#32769)
  0x211306 class=HwndWrapper[DesktopOrganizer.UI;;97fa5614-8a17-4a64-97eb-ee5f95818d18] pid=30592 tid=32420 visible=True rect=[946,88 - 1144,572] 198x484 dpi=96 awareness=SYSTEM_AWARE ctxDpi=96 GA_PARENT=0x1000C(#32769)
  0x250988 class=HwndWrapper[DesktopOrganizer.UI;;ebcd30c7-c02c-4a2f-9eb5-365ba780c700] pid=30592 tid=32420 visible=True rect=[1144,88 - 1364,286] 220x198 dpi=96 awareness=SYSTEM_AWARE ctxDpi=96 GA_PARENT=0x1000C(#32769)
  0x60792 class=HwndWrapper[DesktopOrganizer.UI;;c7e7e4d4-b599-4f97-8cf7-9fdbc6fa4791] pid=30592 tid=32420 visible=True rect=[572,88 - 946,572] 374x484 dpi=96 awareness=SYSTEM_AWARE ctxDpi=96 GA_PARENT=0x1000C(#32769)
  0xA07DA class=HwndWrapper[DesktopOrganizer.UI;;61b03916-1115-41be-9de1-ff704f79f094] pid=30592 tid=32420 visible=True rect=[286,220 - 572,572] 286x352 dpi=96 awareness=SYSTEM_AWARE ctxDpi=96 GA_PARENT=0x1000C(#32769)
  0x4A0E44 class=HwndWrapper[DesktopOrganizer.UI;;141fc2d5-0938-42a4-aca5-a167578b0dc8] pid=30592 tid=32420 visible=True rect=[0,0 - 80,1032] 80x1032 dpi=96 awareness=SYSTEM_AWARE ctxDpi=96 GA_PARENT=0x1000C(#32769)
Direct children of Progman owned by the process:
Direct children of Progman>WorkerW owned by the process:

=== E0 top-level PMv2 popup, NOT reparented (control) ===
[created (top-level)]
  GetDpiForWindow(child)     = 96 (100%)
  awareness(child)           = PER_MONITOR_AWARE (V2)
  hostingBehavior            = 0 (0=DEFAULT,1=MIXED)
  GA_PARENT                  = 0x1000C(#32769)
  GA_ROOT                    = 0x28802F8(DesktopOrganizer.DpiProbe.Window)
  style=0x94000000 (POPUP=True, CHILD=False, VISIBLE=True) exStyle=0x08000080
  GetWindowRect              = [100,100 - 420,300] 320x200
  DPI messages received      : none

Move to \\.\DISPLAY1 (effective 96 (100%)): SetWindowPos(100,100) ok=True err=0 -> GetWindowRect=[100,100 - 420,300] 320x200 (delta vs requested: 0,0)
  MonitorFromWindow=\\.\DISPLAY1  GetDpiForWindow(child)=96 (100%)  awareness=PER_MONITOR_AWARE (V2)
  DPI messages received      : none
cleanup: DestroyWindow -> True

E1/E2 skipped: the product's WorkerW lookup returned 0 on this build (EmbedInDesktop would NOT reparent).

=== E3 PMv2 popup -> SetParent(WorkerW child of Progman, 24H2 layout) ===
[created (top-level)]
  GetDpiForWindow(child)     = 96 (100%)
  awareness(child)           = PER_MONITOR_AWARE (V2)
  hostingBehavior            = 0 (0=DEFAULT,1=MIXED)
  GA_PARENT                  = 0x1000C(#32769)
  GA_ROOT                    = 0x28902F8(DesktopOrganizer.DpiProbe.Window)
  style=0x94000000 (POPUP=True, CHILD=False, VISIBLE=True) exStyle=0x08000080
  GetWindowRect              = [100,100 - 420,300] 320x200
  DPI messages received      : none
SetParent(child, 0x1905BA(WorkerW)) -> prev=0x1000C, lastError=0, reparented=True
[after SetParent]
  GetDpiForWindow(child)     = 96 (100%)
  awareness(child)           = PER_MONITOR_AWARE (V2)
  hostingBehavior            = 0 (0=DEFAULT,1=MIXED)
  GA_PARENT                  = 0x1905BA(WorkerW)
  GA_ROOT                    = 0x20460(Progman)
  style=0x94000000 (POPUP=True, CHILD=False, VISIBLE=True) exStyle=0x08000080
  GetWindowRect              = [100,100 - 420,300] 320x200
  DPI messages received      : none
  parent client origin (screen px) = (0,0), parent window rect = [0,0 - 1920,1080] 1920x1080

Move to \\.\DISPLAY1 (effective 96 (100%)): SetWindowPos(100,100) ok=True err=0 -> GetWindowRect=[100,100 - 420,300] 320x200 (delta vs requested: 0,0)
  MonitorFromWindow=\\.\DISPLAY1  GetDpiForWindow(child)=96 (100%)  awareness=PER_MONITOR_AWARE (V2)
  DPI messages received      : none

cleanup: SetParent(child, NULL) -> prev=0x1905BA, lastError=0
[after SetParent(NULL)]
  GetDpiForWindow(child)     = 96 (100%)
  awareness(child)           = PER_MONITOR_AWARE (V2)
  hostingBehavior            = 0 (0=DEFAULT,1=MIXED)
  GA_PARENT                  = 0x1000C(#32769)
  GA_ROOT                    = 0x28902F8(DesktopOrganizer.DpiProbe.Window)
  style=0x94000000 (POPUP=True, CHILD=False, VISIBLE=True) exStyle=0x08000080
  GetWindowRect              = [100,100 - 420,300] 320x200
  DPI messages received      : none
cleanup: DestroyWindow -> True

=== E4 PMv2 popup -> SetParent(Progman) ===
[created (top-level)]
  GetDpiForWindow(child)     = 96 (100%)
  awareness(child)           = PER_MONITOR_AWARE (V2)
  hostingBehavior            = 0 (0=DEFAULT,1=MIXED)
  GA_PARENT                  = 0x1000C(#32769)
  GA_ROOT                    = 0x28A02F8(DesktopOrganizer.DpiProbe.Window)
  style=0x94000000 (POPUP=True, CHILD=False, VISIBLE=True) exStyle=0x08000080
  GetWindowRect              = [100,100 - 420,300] 320x200
  DPI messages received      : none
SetParent(child, 0x20460(Progman)) -> prev=0x1000C, lastError=0, reparented=True
[after SetParent]
  GetDpiForWindow(child)     = 96 (100%)
  awareness(child)           = PER_MONITOR_AWARE (V2)
  hostingBehavior            = 0 (0=DEFAULT,1=MIXED)
  GA_PARENT                  = 0x20460(Progman)
  GA_ROOT                    = 0x20460(Progman)
  style=0x94000000 (POPUP=True, CHILD=False, VISIBLE=True) exStyle=0x08000080
  GetWindowRect              = [100,100 - 420,300] 320x200
  DPI messages received      : none
  parent client origin (screen px) = (0,0), parent window rect = [0,0 - 1920,1080] 1920x1080

Move to \\.\DISPLAY1 (effective 96 (100%)): SetWindowPos(100,100) ok=True err=0 -> GetWindowRect=[100,100 - 420,300] 320x200 (delta vs requested: 0,0)
  MonitorFromWindow=\\.\DISPLAY1  GetDpiForWindow(child)=96 (100%)  awareness=PER_MONITOR_AWARE (V2)
  DPI messages received      : none

cleanup: SetParent(child, NULL) -> prev=0x20460, lastError=0
[after SetParent(NULL)]
  GetDpiForWindow(child)     = 96 (100%)
  awareness(child)           = PER_MONITOR_AWARE (V2)
  hostingBehavior            = 0 (0=DEFAULT,1=MIXED)
  GA_PARENT                  = 0x1000C(#32769)
  GA_ROOT                    = 0x28A02F8(DesktopOrganizer.DpiProbe.Window)
  style=0x94000000 (POPUP=True, CHILD=False, VISIBLE=True) exStyle=0x08000080
  GetWindowRect              = [100,100 - 420,300] 320x200
  DPI messages received      : none
cleanup: DestroyWindow -> True

=== E5 SYSTEM_AWARE popup -> SetParent(Progman) ===
[created (top-level)]
  GetDpiForWindow(child)     = 96 (100%)
  awareness(child)           = SYSTEM_AWARE ctxDpi=96
  hostingBehavior            = 0 (0=DEFAULT,1=MIXED)
  GA_PARENT                  = 0x1000C(#32769)
  GA_ROOT                    = 0x28B02F8(DesktopOrganizer.DpiProbe.Window)
  style=0x94000000 (POPUP=True, CHILD=False, VISIBLE=True) exStyle=0x08000080
  GetWindowRect              = [100,100 - 420,300] 320x200
  DPI messages received      : none
SetParent(child, 0x20460(Progman)) -> prev=0x1000C, lastError=0, reparented=True
[after SetParent]
  GetDpiForWindow(child)     = 96 (100%)
  awareness(child)           = PER_MONITOR_AWARE (V2)
  hostingBehavior            = 0 (0=DEFAULT,1=MIXED)
  GA_PARENT                  = 0x20460(Progman)
  GA_ROOT                    = 0x20460(Progman)
  style=0x94000000 (POPUP=True, CHILD=False, VISIBLE=True) exStyle=0x08000080
  GetWindowRect              = [100,100 - 420,300] 320x200
  DPI messages received      : none
  parent client origin (screen px) = (0,0), parent window rect = [0,0 - 1920,1080] 1920x1080

Move to \\.\DISPLAY1 (effective 96 (100%)): SetWindowPos(100,100) ok=True err=0 -> GetWindowRect=[100,100 - 420,300] 320x200 (delta vs requested: 0,0)
  MonitorFromWindow=\\.\DISPLAY1  GetDpiForWindow(child)=96 (100%)  awareness=PER_MONITOR_AWARE (V2)
  DPI messages received      : none

cleanup: SetParent(child, NULL) -> prev=0x20460, lastError=0
[after SetParent(NULL)]
  GetDpiForWindow(child)     = 96 (100%)
  awareness(child)           = PER_MONITOR_AWARE (V2)
  hostingBehavior            = 0 (0=DEFAULT,1=MIXED)
  GA_PARENT                  = 0x1000C(#32769)
  GA_ROOT                    = 0x28B02F8(DesktopOrganizer.DpiProbe.Window)
  style=0x94000000 (POPUP=True, CHILD=False, VISIBLE=True) exStyle=0x08000080
  GetWindowRect              = [100,100 - 420,300] 320x200
  DPI messages received      : none
cleanup: DestroyWindow -> True

DONE
```
