# План рефакторинга и доработок: паритет со Stardock Fences

**Дата:** 2026-06-27
**Обновлено:** 2026-09-05 (аудит рефакторинга, см. раздел 5; исполнение §5.5 пп.1-6, см. раздел 7)
**Цель:** довести DesktopOrganizer до функционального и поведенческого паритета с Stardock Fences, удержав качество кода.
**Связано:** [ROADMAP.md](ROADMAP.md), [ADR](../adr/README.md), [.claude/agents/](../.claude/agents/README.md)

---

## 1. Текущее состояние (по факту анализа)

**Стек:** .NET 8 (`net8.0-windows`), WPF, C# (nullable, latest), MVVM, DI (`Microsoft.Extensions.DependencyInjection`), Central Package Management, StyleCop + ruleset-линтер, xUnit + Moq + coverlet.

**Архитектура (слои):**

- `DesktopOrganizer.Attributes` — атрибуты (NativeName).
- `DesktopOrganizer.Core` — доменные модели (`FileItem` и т.п.).
- `DesktopOrganizer.Services.Abstractions` — интерфейсы сервисов.
- `DesktopOrganizer.Services` — реализации: нативный слой (Progman/WorkerW, hooks `WH_MOUSE_LL`, shell-API), провайдеры, инструменты.
- `DesktopOrganizer.UI` — WPF/MVVM: Views, ViewModels, Models (INPC), Converters, Commands, Services (WindowManager, AppSettings, Autostart).

**Готовность по ROADMAP:** Фазы 0–7 закрыты (встраивание в десктоп, файловые операции, поведение панели, внешний вид/настройки, жесты/захват иконок, продвинутое (Folder Portals/страницы/снапшоты/multi-monitor), SystemIconsPanel, производительность/память/отказоустойчивость — см. [ADR-012](../adr/ADR-012-performance-and-resilience-hardening.md)). Остаются сквозные задачи (см. ROADMAP).

**Сильные стороны:** чистые слои, DI, XML-док, нет TODO/HACK в коде, централизованные версии пакетов, линтер.

**Зоны риска:**

- Весь нативный код (WorkerW-хак, глобальный `WH_MOUSE_LL`, кросс-процессный `LVM_HITTEST`) непроверяем вне Windows.
  Риск «ошибка в колбэке хука тормозит ввод системы» смягчён 2026-08-15 (Фаза 7, блок B,
  [ADR-012](../adr/ADR-012-performance-and-resilience-hardening.md)): весь `HookCallback` теперь под
  `try/catch`, `CallNextHookEx` вызывается на всех путях. Дополнительно закрыто 2026-09-05 (§7.3): сам
  кросс-процессный `LVM_HITTEST`-вызов внутри хука получил жёсткий `SendMessageTimeout`-бюджет (50мс, с
  запасом от `LowLevelHooksTimeout` ОС) — см. addendum к [ADR-003](../adr/ADR-003-desktop-gestures-and-capture.md).
  Manual verify на реальной Windows по-прежнему не выполнена.
- Тестовое покрытие тонкое для новых сервисов (`IFileLauncher`, ресайз/roll-up, автозапуск, жесты).
  Ресайз частично улучшен: вычислительная логика вынесена в `GridMetrics` и может быть покрыта тестами без WPF.
  Жесты (`DesktopGestureStateMachine`) и persistence-планировщик (`SaveScheduler`) покрыты тестами с 2026-08-15 (Фаза 7).
- Поведение при смене обоев и виртуальных рабочих столах не отработано. DPI/разрешение монитора —
  закрыто частично 2026-07-26 (re-clamp панелей при уменьшении рабочей области, см. ROADMAP,
  «Сквозные задачи»); полноценная per-monitor DPI-осведомлённость (`WM_DPICHANGED`,
  Per-Monitor-V2) осознанно оставлена открытым вопросом.
- ~~`ChildWindow.xaml.cs` (291 стр.) и `MainWindowViewModel` (257 стр.) — кандидаты на декомпозицию.~~
  Устарело: реальные размеры на момент аудита 2026-09-05 были 1239/531 строк (см. §5.2/§5.3), оба
  декомпозированы 2026-09-05 до 566/459 строк — см. раздел 7. `ChildWindowViewModel.cs` (973 строки)
  остаётся кандидатом на декомпозицию тем же заходом, сознательно не тронут.

---

## 2. Что осталось до паритета (сквозное)

Фазы 0–7 закрыты. Реализованы все ключевые фичи паритета с Fences, включая SystemIconsPanel
(Фаза 6, [ADR-007](../adr/ADR-007-system-icons-panel-window-type.md)) и производительность/память/
отказоустойчивость (Фаза 7, [ADR-012](../adr/ADR-012-performance-and-resilience-hardening.md)).

Открытые сквозные задачи:

1. **Верификация на Windows 10 и 11** — весь нативный слой (WorkerW, хуки, IContextMenu, PIDL,
   SHQueryRecycleBin) непроверяем вне Windows. Manual verify обязателен для каждой нативной фичи,
   включая все 6 блоков Фазы 7 (см. [docs/ROADMAP.md](ROADMAP.md), «Сквозные задачи»).
   Известный класс ошибок: `AccessViolationException` при неверном маршалинге PIDL-массивов или
   вызове Shell COM не с STA-потока — устранён в `NativeApi.cs` (см. [ADR-008](../adr/ADR-008-shell-com-sta-threading-and-pidl-marshaling.md)).
2. **Поведение при смене обоев, DPI и виртуальных рабочих столов** — обои и виртуальные рабочие
   столы не реализованы. Реакция на уменьшение рабочей области монитора (re-clamp) закрыта частично
   2026-07-26; полноценная per-monitor DPI-осведомлённость (Per-Monitor-V2) — открытый вопрос, см.
   [docs/ROADMAP.md](ROADMAP.md), «Сквозные задачи».
3. **Тестовое покрытие:** не покрыты `IFileLauncher`, ресайз/roll-up-логика в code-behind, `IAutostartService`.
4. **Single-instance guard между процессами — не реализован** (обнаружено при документировании Фазы 7,
   [ADR-012](../adr/ADR-012-performance-and-resilience-hardening.md) Решение 4): write-through-кеш
   манифеста `PanelItemStorageService` делает «один процесс — единственный владелец storage root»
   несущим инвариантом сильнее, чем раньше, но межпроцессной защиты по-прежнему нет технически. Подтверждено аудитом 2026-09-05 (детали — §5.2, §5.5).
5. **Закрыто 2026-09-05 (§5.5 п.2, см. раздел 7):** `linters/linter.ruleset`/`MSB3884` — путь в
   `Directory.Build.props` заменён `$(SolutionDir)` → `$(MSBuildThisFileDirectory)linters\linter.ruleset`
   (резолвится независимо от того, собирается ли отдельный `.csproj` или `.sln`); недостающий
   `<PackageReference Include="StyleCop.Analyzers" PrivateAssets="all"/>` добавлен в
   `Directory.Build.props`. StyleCop анализатор физически загружен и репо-wide применяется впервые —
   это вскрыло ~450-457 pre-existing SA*-предупреждений (0 ошибок), сознательно НЕ исправленных в этом
   заходе. **Новый открытый пункт:** массовый фикс ~450 StyleCop-предупреждений — см. §7.2.

Потенциальный рефакторинг (задокументирован в ADR-007 как риск):

6. **`IDesktopWindowManager`** — при добавлении третьего типа desktop-window стоит вынести общий
   интерфейс управления окнами для `WindowManager` (Fences) и `SystemIconsPanelManager`.

---

## 3. Техдолг и рефакторинг

- Декомпозировать `ChildWindow.xaml.cs`: вынести ресайз-оверлей, roll-up и inline-rename в отдельные helper-классы/behaviors.
  Частично закрыто (feature/fancy-windows): ресайз-логика вынесена в `GridMetrics` (UI/Models), roll-up переведён на INPC.
  Остаток: code-behind по-прежнему содержит аккумуляторы дельт и логику анимации — кандидат для Behavior/AttachedProperty.
- Изолировать P/Invoke за тонкими тестируемыми обёртками (часть уже в `INativeApi`) — расширить покрытие интерфейсом для остального нативного кода.
- ~~Ввести единый слой безопасного выполнения хуков (try/catch + быстрый выход из колбэка) как инвариант.~~
  Закрыто 2026-08-15 (Фаза 7, блок B): `DesktopGestureService.HookCallback` целиком под `try/catch`,
  `CallNextHookEx` вызывается на всех путях; чистая машина состояний вынесена в `DesktopGestureStateMachine`
  (расширение прецедента [ADR-011](../adr/ADR-011-manual-drag-with-magnetic-grid-snap.md)). См.
  [ADR-012](../adr/ADR-012-performance-and-resilience-hardening.md) Решение 2.
- Поднять покрытие тестами не-нативной логики (модели, конвертеры, persistence-сериализация, правила сортировки).
  Новые кандидаты (feature/fancy-windows): `GridMetrics` (pure static, тривиально), `PanelContentFilter`-проекция
  в `BaseViewModel.OnSourceChanged()`, `BoolToGlyphConverter`.
  Закрыто 2026-08-15 (Фаза 7): `SaveScheduler`, `DesktopGestureStateMachine`, `ObservableRangeCollection<T>`,
  `HexBrushConverter`/`ColorOpacityBrushConverter` кеш, LRU-кеш `IconProvider` — все покрыты юнит-тестами.

---

## 4. Принцип формирования субагентов

Из плана вытекают роли, закрытые отдельными субагентами. Каждый агент:

- следует структуре из `AGENTS_ARCHITECTURE.md` (frontmatter + секции + cross-recommendations);
- использует паттерн `CLARIFICATION_NEEDED` из `AGENT_INTERACTION_PATTERN.md` там, где теряет уверенность;
- подключает профильный скилл из `.claude/skills/`.

| Роль в плане | Субагент | Скилл |
|---|---|---|
| Декомпозиция фич Фазы 5 в планы | `fences-feature-planner` | architecture-designer |
| Нативный слой (WorkerW, hooks, shell) | `win32-interop-specialist` | csharp-developer |
| WPF/XAML/MVVM | `wpf-ui-builder` | wpf |
| Слой сервисов/Core (.NET) | `dotnet-services-engineer` | dotnet-core-expert |
| Тесты (TDD) | `tdd-test-engineer` | test-driven-development |
| Диагностика багов/падений | `debug-healer` | systematic-debugging |
| Ревью кода и безопасности | `code-quality-reviewer` | code-reviewer |
| Поведенческий паритет с Fences (UX) | `fences-ux-parity-reviewer` | ux-designer |
| Поддержка ADR/ROADMAP | `architecture-documenter` | architecture-designer |

**Конвейер по умолчанию:**
`fences-feature-planner` → (`win32-interop-specialist` ∥ `wpf-ui-builder` ∥ `dotnet-services-engineer`) → `tdd-test-engineer` → [`debug-healer` при падениях] → (`code-quality-reviewer` ∥ `fences-ux-parity-reviewer`) → `architecture-documenter`.

## 5. Аудит рефакторинга 2026-09-05 — сводка и приоритизированный план

Проведён 5 параллельными субагентами (роли dotnet-services-engineer, win32-interop-specialist,
wpf-ui-builder, tdd-test-engineer, architecture-documenter) + ручным аудитом git-состояния
оркестратором. Ограничение среды на момент аудита: нет Windows/dotnet SDK — только чтение кода,
`git diff`/`git log`, grep. Всё, что требует запуска — помечено «manual verify».

### 5.0 Главный вывод

Код сам по себе НЕ в критическом состоянии: слои чистые, инварианты (P/Invoke за `INativeApi`,
try/catch в хуке, `Freeze()` кистей/иконок) в основном соблюдены, ROADMAP/ADR не врут (кроме одной
мелкой неточности в названии классов). «Ужасное состояние» — это прежде всего СОСТОЯНИЕ ВЕТКИ
`polishing/refactoring-1`: 186 файлов, ~30 900 / ~30 965 строк (+/-) не закоммичено, и подавляющая
часть diff'а — шум от смены концов строк LF→CRLF (в репозитории нет `.gitattributes`, `core.autocrlf`
не выставлен; проверено: файлы в HEAD — LF, те же файлы в рабочем дереве — CRLF). Под этим шумом
спрятаны настоящие структурные изменения (перенос файлов по подпапкам с несогласованными
неймспейсами) и настоящие правки UI-слоя. Именно нечитаемый diff и создаёт ощущение хаоса и не даёт
нормально отревьюить/закоммитить/откатить работу.

### 5.1 P0 — гигиена репозитория (сделать первым, до любого другого рефакторинга)

1. **[CRITICAL] Нет `.gitattributes` → построчный diff-шум почти на всех файлах.** Подтверждено:
   `MainWindow.xaml.cs` в HEAD — LF, в рабочем дереве — CRLF; `.editorconfig` (374/374),
   `Directory.Build.props` (29/29 строк) и большинство файлов в diff показывают 1:1 insert/delete —
   классический признак перевода концов строк, а не реальных правок.
   Fix: добавить `.gitattributes` (`* text=auto eol=lf`), выполнить `git add --renormalize .`,
   закоммитить это ОТДЕЛЬНЫМ коммитом («chore: normalize line endings») до всего остального.
2. **[CRITICAL] Расцепить текущий незакоммиченный diff на независимые коммиты**, иначе реальные
   изменения физически не видно за шумом: (a) renormalize концов строк; (b) `.DotSettings`/ReSharper
   cleanup; (c) перенос файлов `Core`/`Services.Abstractions` по папкам, с ОДНОВРЕМЕННЫМ исправлением
   неймспейсов (см. п.3); (d) реальные правки UI (`ChildWindow`/`MainWindow`/ViewModels/`.xaml`) —
   их нужно вычитать вручную построчно, это не шум, это фактическая логика.
3. **[MAJOR] Несогласованные неймспейсы после переноса файлов.** `Core/Comparers/*.cs` →
   `namespace DesktopOrganizer.Core;` (суффикс `.Models` убрали); `Core/Enums/SystemIconKind.cs` и
   `Core/Validations/FileNameValidation.cs` — всё ещё `namespace DesktopOrganizer.Core.Models;`
   (не тронуто, папка разъехалась с неймспейсом); `Services.Abstractions/Events/*` и
   `Services.Abstractions/Interfaces/*` — все `namespace DesktopOrganizer.Services.Abstractions;`
   (плоский неймспейс, без под-неймспейсов под новые папки). Нужно явно выбрать одну конвенцию —
   либо «неймспейс всегда = путь к папке», либо «папки — чисто файловая раскладка, неймспейс не
   меняем» — и применить её единообразно везде, а не тремя разными способами одновременно.
4. Новый нетрекнутый файл `src/DesktopOrganizer.UI/Views/VisualTreeWalker.cs` — добавить в git
   (`git add`) перед коммитом, иначе потеряется при любом `git clean`/переключении веток.

### 5.2 P1 — находки по слоям (параллельное ревью)

**Core / Services.Abstractions / Services (не-нативное)** — слой здоровый, находки узкие:
- **[MAJOR] Закрыто 2026-09-05** (§5.5 п.6, раздел 7): `Tools/FolderWatcher.cs:40-51` —
  `EnableRaisingEvents = true` выставлен ДО подписки на `Created`/`Deleted`/`Renamed`/`Error` → событие
  ОС в этом окне тихо теряется. Fix применён как описано: все 4 обработчика подписаны до
  `EnableRaisingEvents = true`.
- [MAJOR] **Закрыто Фазой 3 refactoring-пакета, см. ADR-023.** Single-instance guard отсутствовал:
  `App.xaml.cs:24` создавал `Mutex("DesktopOrganizerSingleInstanceMutex", initiallyOwned:false)` только
  для того, чтобы инсталлятор видел запущенный процесс — `WaitOne`/владение никогда не проверялось. В
  сочетании с in-memory write-through кешем манифеста `PanelItemStorageService` два одновременных
  процесса тихо затирали правки друг друга (DO-001). Ветка `feat/single-instance-ipc`: реальный guard
  mutex (per-session) + машинно-глобальный Named Pipe тай-брейкер поверх него (ADR-023 D-1/D-4), guard
  выполняется в собственном `Program.Main` до создания `Application`, так что второй процесс никогда не
  доходит до `App.OnExit` (ADR-023 D-2, ключевая находка R-1). Остаточный риск — одновременная запись в
  storage при Fast User Switching до появления файлового лока storage-root — задокументирован как R-15
  и явно передан в Фазу 4 (ADR-024).
- **[MINOR] Закрыто 2026-09-05:** `Providers/TrayIconManager.cs` — мёртвое поле
  `_isMainWindowVisible` удалено. **Новая находка, вскрытая этим фиксом:**
  `UpdateContextMenu(bool isMainWindowVisible)` фактически мёртвый API — ни один вызывающий не
  передаёт реальное значение (единственный вызов — `UpdateContextMenu(isMainWindowVisible: false)` в
  конструкторе), т.е. текст пункта Show/Hide в трее навсегда «залипает». Дефект предсуществующий, стал
  виден только после удаления мёртвого поля. Не исправлено в этом заходе — трактуется как отдельный
  MINOR-тикет, не блокирующий (см. §7.4).
- **[MINOR] Закрыто 2026-09-05:** `Tools/SystemShellService.cs:145-176` — `Dispose()` теперь дожидается
  активного `PollRecycleBin` (ограничено 2с, см. §7.1 второй раунд), гонка устранена.
- **[MINOR] Закрыто 2026-09-05:** `Tools/PanelItemStorageService.cs:740-766` — `TryCopyFileVerified`
  апгрейжен с сравнения только по длине файла до полного побайтового (chunked) сравнения содержимого;
  изначально реализовано через SHA256, затем упрощено до прямого byte-for-byte сравнения (same-machine
  проверка целостности копии, не защита от подмены — крипто-хэш избыточен).
- [MINOR, архитектурная заметка] **Открыто, не в объёме сессии.** `Core`/`Services.Abstractions`
  зависят от `System.Windows.Media.ImageSource` — WPF просачивается в «домен». Приемлемо для WPF-only
  продукта, но лишает `Core` возможности юнит-тестироваться без `PresentationCore`.

**Нативный слой Win32** — самое рискованное место, 2 CRITICAL:
- **[CRITICAL] Закрыто 2026-09-05** (§5.5 п.3, раздел 7): `Tools/DesktopGestureService.cs:279` →
  теперь `NativeApi.IsListViewPointEmpty` использует `SendMessageTimeoutW` с
  `SMTO_ABORTIFHUNG | SMTO_BLOCK`. Первая версия фикса взяла таймаут 200мс; код-ревью в рамках этой же
  сессии поймало, что 200мс слишком близко к дефолту `LowLevelHooksTimeout` ОС (300мс) — вызов идёт
  ИЗНУТРИ `WH_MOUSE_LL`-хука, и Windows тихо снимает хук, если он не возвращается вовремя.
  Скорректировано до 50мс (`NativeApi.NativeMethods.HitTestTimeoutMs`), с явным запасом под
  `OpenProcess`/`VirtualAllocEx`/`WriteProcessMemory`/`ReadProcessMemory` вокруг самого вызова. Этот
  бюджет теперь зафиксирован как инвариант — см. addendum к [ADR-003](../adr/ADR-003-desktop-gestures-and-capture.md).
- **[CRITICAL] Закрыто 2026-09-05:** `Tools/DesktopGestureService.cs` и `Tools/MonitorService.cs`
  держали СОБСТВЕННЫЕ приватные `NativeMethods` (~17 и ~12 `DllImport`) — перенесены в
  `INativeApi`/`NativeApi.cs`, оба сервиса переведены на конструкторную инъекцию `INativeApi`.
  Подтверждено: `grep -rl "DllImport|LibraryImport" src/` теперь находит ровно один файл
  (`Tools/NativeApi.cs`).
- [MAJOR] **Частично закрыто 2026-09-05** (§5.5 п.5, раздел 7): `DesktopGestureService` и
  `MonitorService` получили 52 новых юнит-теста (unlocked P/Invoke-инъекцией из предыдущего пункта).
  **Остаются без юнит-тестов:** `VirtualDesktopService`, `DesktopIntegrationService`,
  `DesktopIconsService`, `IconHandler`, `NotifyIconAdapter` — не тронуты в этом заходе.
- **[MINOR] Закрыто 2026-09-05:** регистрация хука/оконного класса больше не проглатывает ошибку молча
  — залогирован `Marshal.GetLastWin32Error()` при сбое.
- **[MINOR] Закрыто 2026-09-05:** `Models/ShellFileInfo.cs` — `CharSet.Auto` → явный `CharSet.Unicode`.
- Подтверждено БЕЗ регрессии: PIDL-маршалинг (ADR-008), `Freeze()` иконок (ADR-012), try/catch +
  безусловный `CallNextHookEx` в хуке (ADR-012), STA для Shell COM (ADR-008).

**WPF UI слой** — органический рост, не критично, но требует декомпозиции:
- **Закрыто 2026-09-05** (§5.5 п.4, раздел 7): декомпозиция `ChildWindow.xaml.cs` (1239→566 строк) и
  `MainWindowViewModel.cs` (531→459 строк) выполнена по плану §5.3 — см. раздел 7 за деталями и
  единственным сознательно НЕ тронутым файлом (`ChildWindowViewModel.cs`, 973 строки).
- **[MAJOR] Закрыто 2026-09-05:** жест переупорядочивания drag&drop, продублированный между
  `ChildWindow.xaml.cs` и `SystemIconsPanelWindow.xaml.cs`, вынесен в общий
  `Behaviors/ListReorderGestureController<TItem>`; оба окна используют один и тот же контроллер.
  Побочный эффект — `SystemIconsPanelWindow` заодно получила `try/finally` вокруг `DoDragDrop`,
  которого у неё раньше не было (в отличие от `ChildWindow`) — задокументировано как найденный, а не
  внесённый баг.
- [MINOR] **Закрыто (Phase 5 пакета `docs/Refactoring/`, ADR-025 Accepted, ветка
  `ui/ambient-accent-design-tokens`, см. `docs/Refactoring/BACKLOG.md` DO-003).** Было:
  `Views/MainWindow.xaml:63-97` — инлайновые стили с захардкоженными цветами вместо ресурсов. Фикс:
  вынесены в токены `Color.Settings.*` (`Resources/Styles/Colors.xaml`) и стили
  `Settings.PageTabButtonStyle`/`Settings.PageTabCloseButtonStyle` (`Controls.xaml`).
- [MINOR] **Открыто, не в объёме сессии.** `RelayCommand`/`RelayCommand<T>` — `CanExecuteChanged`
  только через `CommandManager.RequerySuggested`, нет ручного `RaiseCanExecuteChanged()`.
- Позитив: подписка `Model.PropertyChanged` в `ChildWindowViewModel` корректно отписывается в
  `Dispose` — ранее найденная и уже закрытая (см. PERFORMANCE_AUDIT.md, находка №2) утечка не
  регрессировала.

**Тесты / сборка / линтер:**
- **[CRITICAL] Закрыто 2026-09-05** (§5.5 п.2, раздел 7): `linters/linter.ruleset`/`MSB3884` — путь в
  `Directory.Build.props` заменён `$(SolutionDir)` → `$(MSBuildThisFileDirectory)linters\linter.ruleset`.
- **[CRITICAL] Закрыто 2026-09-05:** `<PackageReference Include="StyleCop.Analyzers" PrivateAssets="all"/>`
  добавлен в `Directory.Build.props`. Это разблокировало реальный анализ впервые и вскрыло
  ~450-457 pre-existing SA*-предупреждений (0 ошибок) — намеренно НЕ исправлены в этом заходе,
  см. §7.2/новый открытый пункт ROADMAP.
- [MAJOR] **Частично закрыто 2026-09-05** (§5.5 п.5): 52 новых теста для `DesktopGestureService`/
  `MonitorService` (846→898). **Остаётся нулевое покрытие:** `DesktopIconsService`, `FileLauncher`,
  `FolderWatcherFactory`, `NotifyIconAdapter`, `VirtualDesktopService`, `TrayIconManager`,
  `AppSettings`, `AutostartService`, `DispatcherDebounceTimer`, `RenameEditBehavior`,
  `AdaptiveIconGridPanel`, `RelayCommand`/`RelayCommand<T>`, часть конвертеров и UI-моделей,
  `LayoutSnapshotsViewModel`, весь code-behind Views — не тронуто, следующий кандидат на заход.
- [MINOR] **Открыто, не в объёме сессии.** Таксономия `[Trait]` в тестах несогласована (`"UI"` /
  `"DesktopOrganizer.UI"` / `nameof(Core)` / `"Core"` и т.п.) — фильтрация `dotnet test --filter` по
  категории ненадёжна.
- **[MINOR] Закрыто 2026-09-05:** `Directory.Packages.props` — опечатка `MSLibVerion` → `MSLibVersion`
  исправлена.
- Цифра «324/330 (6 упавших)» из `PERFORMANCE_AUDIT.md` датирована 2026-07-27 и не обновлялась;
  более поздние записи ROADMAP цитируют другие числа (623/635, 754, 780, 804, теперь 898), не называя,
  какие именно 6 тестов падают — стоит фиксировать дату/коммит последнего реального прогона рядом с
  числом (актуальный прогон см. раздел 7).

**Doc drift (ROADMAP/ADR):**
- В целом высокое доверие: 72 сверенных grep'ом символа из Фаз 5-9 — все на месте и нетривиальны;
  статусы ADR-013…020 в `adr/README.md` совпадают с самими файлами ADR дословно.
- Уточнение по п.5 ниже — см. «Тесты/сборка/линтер» выше (закрыто 2026-09-05).
- **Закрыто 2026-09-05** (§5.5 п.7, раздел 7): мелкая неточность в названиях (Фаза 6) — в доке
  фигурировали `SystemIconsPanelItemModel`/`SystemIconsPanelSize`, в коде — `SystemIconItemModel`/
  `SystemIconSize` (без инфикса Panel). Исправлено в `docs/ROADMAP.md` и
  [ADR-007](../adr/ADR-007-system-icons-panel-window-type.md).
- Ранее найденный и закрытый дрифт (ADR-006 hide-files-only, удалённые SortingRule) не регрессировал.

### 5.3 План декомпозиции самых крупных файлов

**`ChildWindow.xaml.cs` (1239 строк)** → оставить только жизненный цикл окна (`Initialize`,
`SyncToModel`, `OnClosing`, `OnDragEnter`/`Drop` для внешних файлов) и тонкую разводку XAML-событий;
вынести:
- `Behaviors/PanelDragMoveBehavior` — перетаскивание окна (`TitleBar_MouseLeftButtonDown/Move/Up/
  LostMouseCapture`, `EndDrag`, `_isDragging`/`_dragObstacles`).
- `Behaviors/PanelResizeController` (или чистый статический `ResizeGestureCalculator`) —
  `Resize_DragStarted/Delta/Completed`, аккумуляторы `_rawWidth/_rawHeight/_rawRight/_rawBottom`,
  снаппинг к сетке — пиксельная математика уже почти чистая, вынести в тестируемый статический метод.
- `Behaviors/RollUpBehavior` — `ToggleRollUp`/`ApplyRollUp`/`_expandedHeight`.
- `Behaviors/PanelTitleRenameBehavior` — `BeginRename/CommitRename/CancelRename/TitleEditBox_*`, по
  аналогии с уже существующим `RenameEditBehavior.cs`.
- `ListReorderGestureController` (общий с `SystemIconsPanelWindow`) — `FileListHost_*` обработчики,
  `_reorderCandidate/_isReordering/_reorderStartPoint`, индикатор вставки;
  `ComputeInsertionGap`/`ComputeDropInsertionIndex` оставить как есть (уже чистые).
- `HintPopupController` — авто-скрывающиеся подсказки/ошибки переименования.

**`ViewModels/MainWindowViewModel.cs` (531 строка)** → оставить владение коллекцией панелей/страниц;
вынести:
- `SelectedPanelSettingsViewModel` — прокси свойств выбранной панели (заголовок/фон/текст/
  прозрачность/размер иконок/режим вида/сортировка).
- `GlobalDefaultsViewModel` — глобальные настройки по умолчанию (сетка, снаппинг, цвета, прозрачность,
  размер иконок).
- `SystemIconsSettingsViewModel` — обёртка над `_systemIconsPanelManager`, по аналогии с уже вынесенным
  `LayoutSnapshotsViewModel`.
- Автозапуск/видимость системных иконок и CRUD панелей/страниц — оставить в облегчённой корневой VM.

`ChildWindowViewModel.cs` (973 строки) стоит декомпозировать по тому же принципу следующим заходом
(rename lifecycle / FileSystemWatcher attach-detach / existence-polling / view-sort-filter /
reorder / cross-panel selection — уже сейчас в одном классе).

### 5.4 Manual verify на Windows (сводный список)
- Двойной клик/лассо при зависшем/занятом Explorer (CRITICAL находка §5.2).
- `dotnet build`/`dotnet test` до и после фикса пути ruleset — реальные числа падающих тестов и
  покрытия (сейчас недоступны в этой среде).
- Поведение при двух одновременно запущенных экземплярах (single-instance) — код+автотесты закрыты
  Фазой 3 (ADR-023 Accepted), живая проверка матрицы M-1..M-13
  (`docs/Refactoring/Testing/single-instance-manual-matrix.md`) остаётся manual verify.
- Z-order/встраивание WorkerW на Windows 10 vs 11.
- Ресайз/snap-математика после декомпозиции `ChildWindow.xaml.cs` при масштабировании 125%/150%.
- Долгая сессия — утечки GDI/handle при перезапусках Explorer.
- `Debug.Assert`-only проверки STA не имеют эффекта в Release-сборке.

### 5.5 Рекомендуемый порядок работ

**Исполнено 2026-09-05 (пп.1-6 полностью, п.7 частично) — полный отчёт см. раздел 7.**

1. **[x]** P0 — гигиена git/repo (`.gitattributes`, renormalize, разбивка diff на коммиты, единая конвенция
   неймспейсов для перенесённых файлов).
2. **[x]** Фикс пути ruleset + подключение `StyleCop.Analyzers` (дёшево, разблокирует реальный статический
   анализ до дальнейшего рефакторинга).
3. **[x]** Два CRITICAL нативного слоя (`SendMessageTimeout` в хуке; вынос P/Invoke из
   `DesktopGestureService`/`MonitorService` в `INativeApi`) — самое рискованное для пользователя.
4. **[x]** Декомпозиция `ChildWindow.xaml.cs` / `MainWindowViewModel.cs` (см. §5.3) — после того как
   P/Invoke уже за одним интерфейсом, тестировать становится проще.
5. **[~]** Долив тестов на список нулевого покрытия (§5.2), в первую очередь на классы, которые трогают п.3-4.
   Сделано для `DesktopGestureService`/`MonitorService` (52 теста); остальной список §5.2 всё ещё открыт.
6. **[x]** Точечные MINOR-фиксы (мёртвый код, Dispose-гонки, verify-по-хэшу при копировании, опечатки).
7. **[~]** Поправить сами доки: переформулировать п.5 ниже (ruleset) точнее — снято само собой, т.к.
   п.2 фактически исправил ruleset (переформулировка стала не нужна, обновлено по факту вместо неё);
   имена классов (`SystemIconItemModel`/`SystemIconSize`) в ROADMAP Фаза 6 и ADR-007 — исправлены.

## 6. Уточнение аудита 2026-09-05 — структурные проблемы, пропущенные в первом проходе

Первый проход раздела 5 сфокусировался на git-гигиене и построчных находках по файлам и упустил
структурные/архитектурные проблемы, видимые только при взгляде на дерево проекта и `.sln` целиком.
Ниже — то, что было пропущено, проверено заново вручную (полный листинг `src/`+`tests/` без
фильтров по расширению, разбор `.sln`, подсчёт top-level объявлений типов на файл, граф
`ProjectReference`).

### 6.1 Дублирование «tools»: solution-folder vs физическая папка

В `DesktopOrganizer.sln` объявлена ВИРТУАЛЬНАЯ solution-folder `"tools"`, в которую вложен
ровно один проект — `DesktopOrganizer.Attributes` (2 файла, 37 строк, один атрибут
`NativeNameAttribute`). Одновременно внутри `DesktopOrganizer.Services` существует РЕАЛЬНАЯ
физическая папка `Tools/` (16 файлов: `NativeApi.cs`, `DesktopGestureService.cs`,
`DesktopIconsService.cs` и т.д. — весь нативный interop). В IDE (Rider/VS) это два узла с
одинаковым именем "tools", отвечающие за совершенно разные вещи — один задаёт группировку
проекта, другой — папка внутри другого проекта. Буквальной папки/проекта `Utils` в репозитории
нет (проверено case-insensitive по всем путям всего репо) — либо имелось в виду именно это
столкновение имён, либо `DesktopOrganizer.Attributes` воспринимается как «утилитарный» проект,
который не заслуживает отдельной сборки ради одного атрибута.
**Fix:** переименовать solution-folder (`app`/`shared`/`cross-cutting`) либо вообще убрать
`DesktopOrganizer.Attributes` как отдельный проект и перенести `NativeNameAttribute` в `Core`.

### 6.2 Интерфейсы вперемешку с реализацией — 3 разные, несогласованные конвенции одновременно

- `DesktopOrganizer.Services.Abstractions/Interfaces/*.cs` — 15 интерфейсов в ОТДЕЛЬНОМ проекте.
  Это и есть заявленный в CLAUDE.md/REFACTORING_PLAN.md слой «Services.Abstractions (интерфейсы)».
- `DesktopOrganizer.Services/Interfaces/*.cs` — ЕЩЁ 4 интерфейса (`INativeApi`, `IIconFactory`,
  `IIconHandler`, `INotifyIconAdapter`) лежат ПРЯМО в проекте реализации, в папке с тем же именем
  «Interfaces». Проверено: ни один из этих 4 не используется за пределами `DesktopOrganizer.Services`
  (нет упоминаний в `UI`/`Services.Abstractions`, кроме скомпилированных `bin/`-артефактов) — то
  есть это internal test-seams для мокирования внутри слоя, а не публичный контракт. Проблема не в
  том, что они internal, а в том, что папка называется и организована НЕОТЛИЧИМО от настоящего
  слоя абстракций — по названию папки невозможно понять, что один «Interfaces» — контракт между
  проектами, а другой — деталь реализации одного проекта.
- `DesktopOrganizer.UI/Services/*.cs` — здесь интерфейс (`IWindowManager.cs`) и реализация
  (`WindowManager.cs`) лежат прямо рядом в одной папке, без какого-либо разделения — третий, ещё
  один подход к тому же вопросу.
**Fix:** выбрать ОДНУ конвенцию и применить везде. Например: публичные контракты между проектами —
только в `*.Abstractions`; internal-интерфейсы, нужные исключительно для мокирования внутри
проекта — переименовать папку во что-то однозначное (`Internal/`, `Seams/`) или пометить это
явно в XML-доке файла/папки, чтобы не путать с настоящим слоем абстракций.

### 6.3 Файлы с несколькими объявлениями типов (полный список, не только «интерфейс+enum»)

Посчитаны top-level объявления `class`/`interface`/`enum`/`record`/`struct` на файл:

- **`Tools/NativeApi.cs` — 9 типов в одном файле**: класс-реализация `NativeApi` + 5 P/Invoke-
  структур (`POINT`, `ShellExecuteInfo`, `ShQueryRbInfo`, `ShFileOpStruct`, `CmInvokeCommandInfo`)
  + 2 COM-интерфейса (`IShellFolder`, `IContextMenu`) — всё вперемешку в одном файле. Fix: вынести
  структуры маршалинга в `NativeStructs.cs`, COM-интерфейсы — в `ComInterop.cs`, оставить в
  `NativeApi.cs` только реализацию `INativeApi`.
- **`Tools/DesktopGestureService.cs` — класс + приватный `NativeMethods` (17 `DllImport`) + 3
  структуры (`POINT`, `MSLLHOOKSTRUCT`, `LVHITTESTINFO`)** — тот же файл, где ранее найдены 2
  CRITICAL нарушения (P/Invoke в обход `INativeApi`, необорванный `SendMessageW` в хуке): это не
  только архитектурное нарушение, но и в буквальном смысле «God File», совмещающий бизнес-логику
  жестов с сырыми Win32-декларациями.
- **`Tools/MonitorService.cs` — класс + приватный `NativeMethods` (12 `DllImport`) + 3 структуры
  (`WNDCLASS`, `RECT`, `MONITORINFOEX`)** — идентичная проблема.
- `Tools/VirtualDesktopService.cs` — класс + 1 приватный вложенный COM-интерфейс
  `IVirtualDesktopManager` — приемлемо (чисто внутренняя деталь COM-маршалинга).
- `Tools/DesktopGestureStateMachine.cs` — интерфейс `IDesktopGestureEnvironment` (реализуется
  `DesktopGestureService`, но объявлен в файле ДРУГОГО класса — `DesktopGestureStateMachine`) +
  сама state machine. Не критично, но сбивает с толку при поиске: интерфейс для одного класса
  лежит в файле другого.
- `Services.Abstractions/Interfaces/IFileItemRenameService.cs` и `IPanelItemStorageService.cs` —
  интерфейс + enum-результат (`RenameOutcome`, `RestoreOutcome`) в одном файле — устоявшийся,
  менее спорный паттерн, но раз наводим порядок — тоже кандидаты на разнесение для единообразия.
- `UI/Services/SystemIconsPanelManager.cs`, `Services/Providers/IconProvider.cs` — здесь по
  ОДНОМУ публичному классу, «лишние» типы из первого черновика подсчёта оказались приватными
  вложенными helper-типами — это нормальная инкапсуляция, не проблема.

### 6.4 Core и Services.Abstractions: доменные модели просочились в «интерфейсный» проект

По архитектуре (CLAUDE.md, REFACTORING_PLAN.md §1): `Core` — модели, `Services.Abstractions` —
интерфейсы. По факту в КОРНЕ `DesktopOrganizer.Services.Abstractions` (не в `Interfaces/`, не в
`Events/`) лежат `MonitorInfo.cs` (`public record MonitorInfo(...)`) и `RecycleBinState.cs`
(`public readonly record struct RecycleBinState(...)`) — обычные доменные DTO без единой строчки
интерфейсного контракта. Им место в `DesktopOrganizer.Core`, а не в проекте, который по
собственному определению должен содержать только интерфейсы. (`Events/DesktopLassoEventArgs.cs`,
`FolderChangeEventArgs.cs` — тоже конкретные классы в «интерфейсном» проекте, но это чуть более
защитимо: они существуют исключительно как типы событий конкретных интерфейсов рядом.)
**Fix:** перенести `MonitorInfo`/`RecycleBinState` в `Core/Models/`, оставить в
`Services.Abstractions` только `Interfaces/` и `Events/` (или прямо переименовать проект в
`Services.Contracts`, если хочется явно развести «контракты» от «моделей на них»).

### 6.5 Нарушенное переиспользование — конкретный список (не только reorder-жест)

- Жест drag&drop переупорядочивания продублирован почти дословно между `ChildWindow.xaml.cs` и
  `SystemIconsPanelWindow.xaml.cs` (уже отмечено в §5.2, подтверждаю).
- **Win32-обёртки переизобретены трижды.** Слой `INativeApi`/`NativeApi.cs` существует именно
  для того, чтобы P/Invoke был в одном месте — но `DesktopGestureService.cs` и `MonitorService.cs`
  объявляют СВОИ собственные `NativeMethods` (см. §6.3) вместо того, чтобы расширить `INativeApi`.
  Это не только нарушение инварианта CLAUDE.md (см. §5.2 CRITICAL), но и прямое нарушение DRY —
  тот же класс Win32 API описан в трёх разных местах тремя разными наборами сигнатур.
**Fix:** после переноса P/Invoke-объявлений `DesktopGestureService`/`MonitorService` в
`INativeApi` (уже запланировано в §5.5 п.3) это дублирование исчезает автоматически — считать
одним и тем же рефакторингом.

## 7. Исполнение §5.5 (шаги 1–6 из 7) — 2026-09-05

Ветка `polishing/refactoring-1`. Полный цикл оркестратора (`fences-feature-planner` не привлекался —
план уже был готов в §5.5; далее `win32-interop-specialist` ∥ `dotnet-services-engineer` ∥
`wpf-ui-builder` → `tdd-test-engineer` → `code-quality-reviewer` ∥ `fences-ux-parity-reviewer` →
`architecture-documenter`) прогнал рекомендованный порядок работ §5.5 пп.1-6 целиком и п.7 частично.
13 коммитов, диапазон `90cbb12..ca328ce` (от `f30e79d` до `ca328ce`). Ниже — что фактически произошло
по каждому шагу; статус находок из §5.1/§5.2/§5.3, к которым это относится, обновлён по месту выше.

### 7.1 Шаг 1 (P0 git-гигиена) — сделано, плюс находка вне исходного плана

`.gitattributes` (`* text=auto eol=lf`) добавлен, ~62-файловый незакоммиченный diff расцеплен на
логические коммиты (перенос/переименование файлов, `.DotSettings`, выравнивание неймспейсов
`Core.Models` → `Core`, дедуп UI).

**Находка, не входившая в исходный план §5.1:** `docs/`, `adr/`, `.claude/` и `CLAUDE.md` были в
`.gitignore` и НИКОГДА не коммитились ни в одну ветку — весь ROADMAP, все ADR и вся оркестрационная
настройка существовали только на локальном диске. Это серьёзнее многих находок §5.1: сама работа по
git-гигиене (и вообще всё, что описано в этом документе) была невидима для любого нового `git clone`
до этой сессии. Пользователь подтвердил трекинг этих путей — зафиксировано коммитом `bba2b89`;
`.claude/settings.local.json` остался в `.gitignore` осознанно (персональные права, не командный
конфиг). Файл, который вы сейчас читаете, — первая версия `docs/REFACTORING_PLAN.md`, реально попавшая
в git.

### 7.2 Шаг 2 (ruleset + StyleCop) — сделано (`8843da3`)

`Directory.Build.props`: `$(SolutionDir)` → `$(MSBuildThisFileDirectory)` в `CodeAnalysisRuleSet`,
добавлен `<PackageReference Include="StyleCop.Analyzers" PrivateAssets="all"/>` (жёстче
`PrivateAssets` — в позднем коммите `ca328ce`), опечатка `MSLibVerion` → `MSLibVersion` исправлена.

Впервые в истории репозитория StyleCop физически применяется repo-wide — это вскрыло **~450-457
pre-existing SA*-предупреждений (0 ошибок)**. Сознательно НЕ исправлены в этой сессии (отдельный по
объёму заход). **Новый открытый пункт:** массовая чистка ~450 StyleCop-предупреждений — не в §5.5, но
следует из его выполнения; кандидат на отдельный тикет/сессию `dotnet-services-engineer` +
`wpf-ui-builder` (варнинги распределены по всем проектам, не только по одному слою).

### 7.3 Шаг 3 (два CRITICAL нативного слоя) — сделано в два раунда (`76a024a`, затем `edd4304`)

- `SendMessageW` → `SendMessageTimeoutW` (`SMTO_ABORTIFHUNG | SMTO_BLOCK`) в
  `NativeApi.IsListViewPointEmpty`. Первый заход взял таймаут 200мс; код-ревью в рамках этой же
  сессии (`code-quality-reviewer`) поймало, что это опасно близко к дефолту `LowLevelHooksTimeout` ОС
  (300мс) — весь вызов происходит ИЗНУТРИ `WH_MOUSE_LL`-хука, и Windows тихо снимает хук, который не
  успевает вернуться. Исправлено до 50мс в том же цикле фиксов (`edd4304`). Это теперь зафиксированный
  инвариант — см. addendum к [ADR-003](../adr/ADR-003-desktop-gestures-and-capture.md).
- P/Invoke консолидирован: приватные `NativeMethods` в `DesktopGestureService` (17 `DllImport`) и
  `MonitorService` (12 `DllImport`) удалены, всё объявлено только в `NativeApi.cs` (проверено:
  `grep -rl "DllImport|LibraryImport" src/` находит один файл). Оба сервиса переведены на
  конструкторную инъекцию `INativeApi`.
- Заодно: `Marshal.GetLastWin32Error()`-логирование при сбое установки хука/оконного класса вместо
  тихого no-op; `ShellFileInfo`'s `CharSet.Auto` → явный `CharSet.Unicode`.

`code-quality-reviewer` нашёл 3 MAJOR в рамках этого же ревью-прохода (общего с шагом 4, см. ниже),
все исправлены в `edd4304`.

### 7.4 Шаг 4 (декомпозиция) — сделано (`ad28757`, `2efd2c5`), близко к плану §5.3

- `ChildWindow.xaml.cs`: 1239 → 566 строк. Вынесены
  `Behaviors/{PanelDragMoveBehavior,PanelResizeController,RollUpBehavior,PanelTitleRenameBehavior,
  ListReorderGestureController<TItem>,ListReorderGeometry,HintPopupController}.cs`.
  `SystemIconsPanelWindow.xaml.cs` переиспользует тот же `ListReorderGestureController<TItem>` для
  ранее продублированного drag-reorder жеста (закрывает находку §5.2/§6.5). Побочный эффект — попутный
  фикс скрытого бага: у `SystemIconsPanelWindow` раньше не было `try/finally` вокруг `DoDragDrop`
  (в отличие от `ChildWindow`), теперь есть у обоих через общий контроллер.
- `MainWindowViewModel.cs`: 531 → 459 строк. Вынесены
  `ViewModels/{SelectedPanelSettingsViewModel,GlobalDefaultsViewModel,SystemIconsSettingsViewModel}.cs`,
  все изначально публичные свойства/команды проброшены как есть, `PropertyChanged` переподнимается
  дословно по имени. `ChildWindowViewModel.cs` (973 строки) **осознанно не тронут** — остаётся
  открытым следующим заходом, как и было отмечено в §5.3.
- `fences-ux-parity-reviewer` не нашёл поведенческих расхождений с ADR-011/ADR-014/ADR-015.
- `code-quality-reviewer` нашёл 3 MAJOR (исправлены в `edd4304`, общий цикл фиксов с шагом 3):
  1. бюджет `SendMessageTimeout` (см. 7.3);
  2. регрессия `RelayCommand<SystemIconKind>` vs `RelayCommand<SystemIconKind?>` в
     `SystemIconsPanelViewModel.OpenItemCommand` — переход на общий generic `RelayCommand<T>` поменял
     семантику null-параметра для non-nullable value type (эмпирически подтверждено: `Cast(null)`
     возвращает `Some(default)`, а не `null`, для голого enum type parameter — сбойный биндинг
     `CommandParameter` тихо открывал бы «Этот компьютер» вместо no-op);
  3. `CS8602` при инициализации полей в конструкторе `ChildWindow` (косметика/анализ, без рантайм-бага).

### 7.5 Шаг 5 (тесты для классов, тронутых шагами 3-4) — частично сделано (`90add5a`)

`DesktopGestureService` и `MonitorService` получили 52 новых юнит-теста (846 → 898 всего), unlocked
инъекцией `INativeApi` из шага 3. Остаток списка нулевого покрытия из §5.2 (`DesktopIconsService`,
`FileLauncher`, `FolderWatcherFactory`, `NotifyIconAdapter`, `VirtualDesktopService`, `TrayIconManager`,
`AppSettings`, `AutostartService`, `DispatcherDebounceTimer`, `RenameEditBehavior`,
`AdaptiveIconGridPanel`, `RelayCommand`/`RelayCommand<T>`, `LayoutSnapshotsViewModel`, весь code-behind
Views) — **не тронут**, остаётся открытым (см. §5.2 выше).

### 7.6 Шаг 6 (точечные MINOR/MAJOR-фиксы) — сделано, два раунда

Первый раунд (`10a81a9`): гонка `FolderWatcher` (события включались до подписки обработчиков — реальное
окно потери события ОС, теперь исправлено), гонка `SystemShellService.Dispose` с таймером (теперь
дожидается активного `PollRecycleBin`, ограничено 2с более поздним фиксом), `PanelItemStorageService.
TryCopyFileVerified` апгрейжен с верификации только по длине до полного сравнения содержимого (сначала
SHA256, затем упрощено до chunked byte-for-byte сравнения в `ca328ce`, т.к. это same-machine проверка
целостности, не защита от подмены — крипто-хэш избыточен), мёртвое поле
`TrayIconManager._isMainWindowVisible` удалено.

Второй раунд, по итогам code-review прохода (`ca328ce`): ограничение ожидания в
`SystemShellService.Dispose` (2с), `StyleCop.Analyzers` `PrivateAssets="all"` ужесточён.

Это закрывает MAJOR/MINOR-находки §5.2 по `FolderWatcher`, `SystemShellService`,
`PanelItemStorageService`, `TrayIconManager`. **НЕ закрыто, новая находка:**
`Providers/TrayIconManager.cs`'s `UpdateContextMenu(bool)` теперь de facto мёртвый API — никто не
вызывает его с реальным значением (единственный вызов — `isMainWindowVisible: false` в конструкторе),
т.е. текст пункта Show/Hide в трее навсегда «залипает». Дефект предсуществующий, стал виден только
после удаления мёртвого поля в этом же раунде. Заведён как отдельный MINOR-тикет (не исправлен здесь,
не блокирует).

### 7.7 Шаг 7 (правка доков) — частично сделано

`SystemIconsPanelItemModel`/`SystemIconsPanelSize` → `SystemIconItemModel`/`SystemIconSize` исправлено
в `docs/ROADMAP.md` и [ADR-007](../adr/ADR-007-system-icons-panel-window-type.md) (коммит в диапазоне
`f30e79d..ca328ce`, см. `git log` для точного хеша). Вторая половина шага 7 — «переформулировать п.5
ниже (ruleset) точнее» — стала неактуальна: шаг 2 фактически исправил ruleset, так что вместо
переформулировки формулировка обновлена по факту фикса (см. §2 п.5 и §5.2 «Тесты/сборка/линтер» выше).

### 7.8 Итоговое состояние

`dotnet build`/`dotnet test` зелёные, 898 тестов (846 до сессии). StyleCop применяется repo-wide
впервые, ~450-457 SA*-предупреждений зафиксированы как известный техдолг (не блокируют, не входили в
объём этой сессии). Публичные контракты `INativeApi`/`IWindowManager`/`IPanelItemStorageService` не
менялись семантически (только аддитивно там, где менялись сигнатуры конструкторов сервисов на
DI-инъекцию — обёрнуто DI-контейнером, не публичный breaking change для потребителей интерфейсов).
Ничего из нативного/WPF-поведения не проверено вживую на Windows — как и раньше, вне объёма среды
разработки; полный manual-verify список не расширился (шаги 1-6 — рефакторинг существующего
поведения, не новые фичи, риски описаны по месту в §5.2/§5.3 выше).

**Не начато:** §5.5 п.5 (остаток нулевого покрытия), §6 целиком (структурные находки — solution-folder
`tools` vs физическая `Tools/`, три конвенции интерфейсов, `NativeApi.cs`/`DesktopGestureService.cs`/
`MonitorService.cs` как «несколько типов в файле», `MonitorInfo`/`RecycleBinState` не на своём месте в
`Services.Abstractions`) — это следующий кандидат на отдельный заход, план для него уже готов в §6.
