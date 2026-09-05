# Мастер-план Фазы 5 «Продвинутое» — DesktopOrganizer

> Цель фазы: закрыть последний блок ROADMAP на пути к паритету со Stardock Fences.
> Источник правды: `docs/ROADMAP.md` (Фаза 5 + Сквозные задачи), ADR-001..004.
> Дата: 2026-06-27. Ветка: `feature/add-fucntionality`.
> **Статус на 2026-06-28: ВЫПОЛНЕНО (пункты 1–5).** Сквозные задачи частично (см. ROADMAP).

## Карта текущего состояния (что уже есть в коде)

- **Folder Portals частично реализованы**: интерфейсы `IFolderWatcher`/`IFolderWatcherFactory`,
  `FolderChangeEventArgs`, реализации `FolderWatcher`/`FolderWatcherFactory`, регистрация в `DiConfig.cs`,
  потребление в `ChildWindowViewModel` (`LoadFromModel` → `OnFolderChanged`, marshaling на UI-поток,
  обработка `Created/Deleted/Renamed/ResyncRequired`). Модель уже несёт `SourceFolderPath`.
- **Persistence**: `WindowManager.SaveWindowsState/LoadWindowsState` — JSON-сериализация
  `List<ChildWindowModel>` в `Properties.Settings.Default.ChildWindowsState`.
- **Модель панели**: `ChildWindowModel : INotifyPropertyChanged` (`Position`, `Size`, `IsVisible`,
  `SourceFolderPath`, `FilePaths`, `ViewMode`, …). Здесь же придётся добавлять поля для пунктов 3/4/5.
- **DI**: `Services/Configuration/DiConfig.cs` (Services-слой). UI-сервисы (`WindowManager`) регистрируются в UI.
- **Нативка** за `INativeApi` (`Services/Providers/NativeApi.cs`).

---

## Сводная таблица

| # | Пункт | Scope | Слои | Исполнитель | Параллельно? | Зависит от |
|---|-------|-------|------|-------------|--------------|-----------|
| 1 | Folder Portals | **S** (доделка) | Services, UI | dotnet-services-engineer + wpf-ui-builder | да | — | **[x] DONE** |
| 2 | Автоправила сортировки | **L** | Core, Abstractions, Services, UI | dotnet-services-engineer | частично | 1 | **[x] DONE** |
| 3 | Страницы / swipe | **L** | Core/UI-Models, UI | wpf-ui-builder | да | — | **[x] DONE** |
| 4 | Снапшоты раскладки | **M** | Abstractions/UI-Services, UI | dotnet-services-engineer | да | 5 (желательно) | **[x] DONE** |
| 5 | Multi-monitor | **L** | Abstractions, Services (native), UI | win32-interop-specialist + wpf-ui-builder | частично | — | **[x] DONE** |
| СК | Сквозные тесты | **M** | Tests | tdd-test-engineer | после каждого пункта | 1–5 | **[~] PARTIAL** |
| СК | Обои/DPI/виртуальные столы | **M (native)** | Services (native), UI | win32-interop-specialist | да | 5 | **[ ] TODO** |

**Рекомендуемый порядок реализации:** 1 → (3 ∥ 5) → 2 → 4 → сквозные (обои/DPI/vdesktop) → финальные тесты+ревью.
Пункты 1, 3, 5 не конфликтуют по файлам и могут вестись параллельно (≤5 агентов). Пункт 2 опирается на
портал-инфраструктуру из 1. Пункт 4 (снапшоты) проще делать после 5, чтобы снапшот сразу хранил monitor-binding.

---

## Пункт 1 — Folder Portals (доделка)

### Scope
**Входит:** довести уже начатую реализацию до production-готовности.
- Команда/UI создания панели-портала: выбор папки (`Microsoft.Win32.OpenFolderDialog`, .NET 8) →
  установка `SourceFolderPath`, переключение панели в «портальный» режим.
- Фильтрация служебных файлов (`desktop.ini`, скрытые/системные — по аналогии с Fences) в `GetSourceFiles`
  и в `OnFolderChanged` (`Created`).
- Дебаунс всплеска событий FSW (массовое копирование) — коалесцировать в один resync/батч
  (таймер на UI-слое или внутри `FolderWatcher`).
- Корректный lifecycle: смена `SourceFolderPath` на лету (Stop старого watcher, Start нового),
  гарантированный `Dispose` watcher при закрытии панели (проверить путь `RemoveChildWindow` → закрытие окна → `Dispose` VM).
- Запрет ручного `RemoveFile`/drop в портальной панели (источник правды — папка, не список).

**Не входит:** рекурсивный обход подпапок (оставить `IncludeSubdirectories=false`); сетевые пути (отметить риском).

### Definition of Done
1. Из UI можно создать панель-портал, указав папку; панель показывает её содержимое.
2. Создание/удаление/переименование файла в папке отражается в панели без перезапуска.
3. Массовое изменение (50+ файлов) не вешает UI (дебаунс/батч), нет утечки watcher (Dispose проверен).
4. Портальная панель не позволяет вручную удалять/добавлять файлы (UI заблокирован).
5. `SourceFolderPath` переживает перезапуск; битый/удалённый путь не валит загрузку (graceful).
6. `desktop.ini`/системные файлы не показываются.
7. `dotnet build` + `dotnet test` зелёные.

### Ключевые файлы
- Изменить: `src/DesktopOrganizer.UI/ViewModels/ChildWindowViewModel.cs` (дебаунс, блокировка ручных операций, lifecycle при смене пути), `src/DesktopOrganizer.Services/Tools/FolderWatcher.cs` (опц. дебаунс/коалесинг), `MainWindowViewModel.cs` / трей / контекстное меню (команда «Создать панель из папки…»).
- Изменить: `src/DesktopOrganizer.UI/Models/ChildWindowModel.cs` (хелпер `IsPortal => !string.IsNullOrWhiteSpace(SourceFolderPath)`).
- UI: контекстное меню/настройки — скрыть «удалить из панели» для портала.

### Риски / компромиссы
- FSW на сетевых/съёмных дисках ненадёжен → задокументировать как ограничение, опираться на `ResyncRequired`.
- `OnFolderChanged` уже маршалит через `Application.Current.Dispatcher` — при shutdown `Application.Current` может быть null; добавить guard.
- **Manual verify (Win10/Win11):** массовое копирование, переименование папки-источника, отключение USB-носителя.

### Исполнитель / параллель
dotnet-services-engineer (watcher/дебаунс) + wpf-ui-builder (команда создания, блокировка UI). Параллелится с 3 и 5.

---

## Пункт 2 — Автоправила сортировки

### Scope
**Входит:** движок правил, раскидывающий иконки по панелям.
- Модель правила: тип (`ByExtension`, `ByNamePattern`, `ByFileKind` — документы/картинки/архивы/ярлыки), критерий, целевая панель (по `Id` или по имени).
- Сервис `ISortingRuleService` (Abstractions) / реализация (Services/Tools): принимает набор `FileItem` + правила → распределение `panelId → files`.
- Применение: ручная команда «Применить правила» (трей/настройки) для существующих панелей; опц. авто-применение к новым файлам портала Desktop.
- UI управления правилами в окне настроек (CRUD списка правил, выбор целевой панели).
- Persistence правил в Settings (новый ключ, отдельно от `ChildWindowsState`).

**Не входит:** ML/контентный анализ; правила по дате/размеру (на будущее); автосоздание панелей правилом (только в существующие).

### Definition of Done
1. Можно задать правило «расширение → панель» и применить его; подходящие иконки переезжают.
2. Поддержаны минимум: по расширению, по подстроке имени, по категории (картинки/документы/архивы/ярлыки).
3. Правила сохраняются и переживают перезапуск.
4. Файл, не подходящий ни под одно правило, остаётся на месте (не теряется).
5. Покрыто unit-тестами (матчинг, приоритет правил, отсутствие дублей).
6. `dotnet build` + `dotnet test` зелёные.

### Ключевые файлы
- Создать: `src/DesktopOrganizer.Core/Models/SortingRule.cs`, `SortingRuleKind.cs` (enum, в Core).
- Создать: `src/DesktopOrganizer.Services.Abstractions/ISortingRuleService.cs`.
- Создать: `src/DesktopOrganizer.Services/Tools/SortingRuleService.cs`; регистрация в `DiConfig.cs`.
- Изменить: окно настроек (`MainWindow.xaml` + `MainWindowViewModel.cs`) — секция «Правила».
- Изменить: Settings (новый строковый JSON-ключ `SortingRules`) + `WindowManager` или отдельный persistence-хелпер.

### Зависимости
Опирается на портальную инфраструктуру и `FileItem`/`FilePaths` (пункт 1). Чистая логика матчинга — независима и тестируема первой (TDD).

### Риски / компромиссы
- Конфликт правил (файл подходит двум панелям) → ввести приоритет/порядок, документировать «первое совпадение».
- Перемещение между панелями = редактирование `FilePaths` обеих панелей + сохранение; следить за атомарностью/save.

### Исполнитель / параллель
dotnet-services-engineer (движок) → tdd-test-engineer (матчинг) → wpf-ui-builder (UI правил). Логику можно вести параллельно остальным; UI-часть после стабилизации 1.

---

## Пункт 3 — Страницы / swipe между раскладками панелей

### Scope
**Входит:** несколько именованных «страниц» (раскладок), между которыми переключается весь набор панелей.
- Модель страницы: `Id`, имя, привязка панелей к странице (поле `PageId` в `ChildWindowModel` или коллекция страниц с членством).
- Переключатель страниц: пункт трея / хоткей / опц. жест (правый swipe по пустому десктопу — переиспользует `DesktopGestureService`, но это уже native → см. риск).
- При переключении страницы: показать панели текущей, скрыть остальные (`IsVisible`/`Show/Hide`), без пересоздания окон.
- Persistence: страницы + текущая активная страница в Settings.

**Не входит:** анимация перехода уровня Fences (только show/hide); раскладки на разных мониторах одновременно (это пункт 5).

### Definition of Done
1. Можно создать ≥2 страницы и переключаться между ними; видны только панели активной страницы.
2. Привязка панели к странице сохраняется и переживает перезапуск; активная страница восстанавливается.
3. Переключение не пересоздаёт окна (нет мигания/потери позиций).
4. Новая панель попадает на текущую активную страницу.
5. `dotnet build` + `dotnet test` зелёные.

### Ключевые файлы
- Изменить: `src/DesktopOrganizer.UI/Models/ChildWindowModel.cs` (+`PageId : Guid`) **или** создать `src/DesktopOrganizer.UI/Models/DesktopPage.cs` + коллекция в новом state.
- Создать: `src/DesktopOrganizer.UI/Services/IPageManager.cs` + `PageManager.cs` (переключение, фильтрация видимости через `IWindowManager`).
- Изменить: `WindowManager` (учитывать активную страницу при создании/показе), `MainWindowViewModel` (UI переключения), трей.
- Изменить: persistence (страницы + `ActivePageId`).

### Зависимости
Независим от 1/2. Жестовый swipe (если делать) зависит от native-слоя `DesktopGestureService` → отдать win32-interop-specialist отдельным под-пунктом.

### Риски / компромиссы
- Если swipe-жест реализуем нативно — риск как у Фазы 4 (`WH_MOUSE_LL`); по умолчанию начать с трея/хоткея, жест опционален.
- Решение «поле PageId vs отдельная коллекция страниц» — ADR-нота: предпочтительно `PageId` в модели (минимум миграций JSON, обратная совместимость: пустой = страница по умолчанию).
- **Manual verify:** переключение при встроенных в десктоп окнах (Progman/WorkerW) — убедиться, что Hide/Show не выбрасывает панель из desktop-слоя (может требовать повторного `EmbedInDesktop`).

### Исполнитель / параллель
wpf-ui-builder (основное) + win32-interop-specialist (опц. жест). Параллелится с 1 и 5.

---

## Пункт 4 — Снапшоты раскладки

### Scope
**Входит:** ручное сохранение/восстановление полного расположения панелей.
- Снапшот = именованная копия всего состояния панелей (позиции/размеры/вид/портал/страница/монитор).
- Сервис `ISnapshotService` (UI-Services): `Capture(name)`, `Restore(id)`, `List()`, `Delete(id)`.
- Хранение: отдельный JSON-ключ Settings `LayoutSnapshots` (список снапшотов), переиспользует сериализацию `ChildWindowModel`.
- UI: секция «Снапшоты» в настройках (создать/восстановить/удалить) + опц. пункты трея.
- Restore: применить позиции/размеры к существующим панелям по `Id`; недостающие создать, лишние — по политике (оставить/скрыть — документировать).

**Не входит:** авто-снапшоты по расписанию; облачная синхронизация; diff/merge снапшотов.

### Definition of Done
1. Можно сохранить текущую раскладку под именем и позже её восстановить.
2. Restore возвращает позиции/размеры/вид/страницу/монитор панелей.
3. Снапшоты переживают перезапуск; список отображается в UI.
4. Удаление снапшота работает; восстановление несуществующих путей не падает.
5. Покрыто unit-тестами (сериализация round-trip, restore по Id).
6. `dotnet build` + `dotnet test` зелёные.

### Ключевые файлы
- Создать: `src/DesktopOrganizer.UI/Models/LayoutSnapshot.cs` (имя, дата, `List<ChildWindowModel>`).
- Создать: `src/DesktopOrganizer.UI/Services/ISnapshotService.cs` + `SnapshotService.cs` (поверх `IWindowManager`).
- Изменить: настройки (`MainWindow.xaml`/`MainWindowViewModel`), Settings (`LayoutSnapshots`), трей (опц.).

### Зависимости
Лучше после пункта 5 (чтобы снапшот хранил `MonitorIndex/MonitorId`) и пункта 3 (`PageId`). Может стартовать параллельно, если модель снапшота просто сериализует весь `ChildWindowModel` целиком.

### Риски / компромиссы
- Совместимость формата при добавлении полей в `ChildWindowModel` (страницы/мониторы) — снапшот сериализует модель целиком, поэтому новые поля приедут автоматически; старые снапшоты — терпимы к отсутствию полей (дефолты).
- Политика «лишних» панелей при restore — зафиксировать (рекомендация: скрывать, не удалять).

### Исполнитель / параллель
dotnet-services-engineer + tdd-test-engineer (round-trip), UI — wpf-ui-builder. Параллелится, но финализировать после 3/5.

---

## Пункт 5 — Multi-monitor

### Scope
**Входит:** корректная привязка панелей к мониторам и реакция на смену конфигурации.
- Хранить привязку панели к монитору устойчиво к перестановке (по стабильному идентификатору монитора, не по индексу): `MonitorDeviceId`/`DeviceName` + относительная позиция внутри рабочей области монитора.
- Native-сервис `IMonitorService` (Abstractions) / реализация в Services за `INativeApi`: перечисление мониторов (`EnumDisplayMonitors`/`GetMonitorInfo`), маппинг точки/прямоугольника на монитор, work area с учётом DPI.
- Реакция на `WM_DISPLAYCHANGE` / смену числа мониторов: перепривязка панелей в видимую область (панель не должна «уехать» за пределы экранов при отключении монитора).
- Создание панели на мониторе под курсором (лассо-жест) — учитывать монитор.

**Не входит:** per-monitor разные страницы (пересекается с 3 — на будущее); глубокая поддержка mixed-DPI анимаций.

### Definition ofNed (DoD)
1. Панель открывается и сохраняется на нужном мониторе; после перезапуска возвращается туда же.
2. При отключении монитора панели с него переезжают в видимую область (не теряются).
3. При смене разрядной конфигурации/добавлении монитора позиции остаются валидными.
4. Корректный учёт DPI: позиции/размеры в DIP↔px пересчитываются верно (панель не «прыгает» на mixed-DPI).
5. `dotnet build` + `dotnet test` зелёные (логика маппинга монитор/координаты — юнит-тестируема через абстракцию `INativeApi`/`IMonitorService`).

### Ключевые файлы
- Создать: `src/DesktopOrganizer.Services.Abstractions/IMonitorService.cs`, `MonitorInfo.cs` (Abstractions/Core).
- Создать: `src/DesktopOrganizer.Services/Tools/MonitorService.cs`; расширить `Services/Providers/NativeApi.cs` (`EnumDisplayMonitors`, `GetMonitorInfo`, `MonitorFromPoint`); регистрация в `DiConfig.cs`.
- Изменить: `src/DesktopOrganizer.UI/Models/ChildWindowModel.cs` (+`MonitorDeviceId`, относительные координаты или хелпер пересчёта).
- Изменить: `WindowManager` (валидация/перепривязка позиций при создании), хук на `WM_DISPLAYCHANGE` (`ChildWindow.xaml.cs` или App-уровень через `HwndSource`).

### Зависимости
Независим. Желательно завершить до финала пункта 4 (снапшот хранит monitor-binding).

### Риски / компромиссы
- **Полностью native, непроверяемо в этой среде** → раздел «ручная проверка на Windows 10/11» обязателен.
- Стабильный ID монитора: `DeviceName` (`\\.\DISPLAY1`) может меняться при перестановке; рассмотреть EDID/`DISPLAY_DEVICE`. Компромисс: хранить и DeviceName, и геометрию-fallback.
- Взаимодействие с Progman/WorkerW: встроенные в desktop-слой панели — координаты в системе desktop-окна; перепривязка не должна «вынимать» панель из слоя.
- DPI awareness приложения (PerMonitorV2 в манифесте?) — проверить текущий режим; если System DPI, координаты на втором мониторе будут смещены.

### Исполнитель / параллель
win32-interop-specialist (native + `IMonitorService` + `WM_DISPLAYCHANGE`) → wpf-ui-builder (привязка модели/окон) → tdd-test-engineer (маппинг). Параллелится с 1 и 3.

---

## Сквозные задачи

### СК-1. Тесты на новые сервисы (и долг по `IFileLauncher`/ресайз/roll-up/автозапуск)
- Покрыть: `FolderWatcher`/дебаунс (через абстракцию событий), `SortingRuleService` (матчинг/приоритет),
  `SnapshotService` (round-trip), `MonitorService`/маппинг координат (через мок `INativeApi`),
  `PageManager` (фильтрация видимости).
- Закрыть долг ROADMAP: тесты `IFileLauncher`, ресайз/roll-up-логики, `IAutostartService`.
- Исполнитель: **tdd-test-engineer** — желательно писать тесты ДО реализации каждого пункта (TDD).
- DoD: новые сервисы имеют unit-тесты; `dotnet test` зелёный; покрытие новых сервисов не ниже текущего baseline.

### СК-2. Поведение при смене обоев, DPI-масштабировании, виртуальных рабочих столах
- Смена обоев пересоздаёт WorkerW → панели могут выпасть из desktop-слоя: подписаться на смену обоев/
  `WM_SETTINGCHANGE`/`WM_DISPLAYCHANGE` и при необходимости повторно `EmbedInDesktop`.
- DPI: пересчёт координат (связано с пунктом 5).
- Виртуальные рабочие столы: панели в desktop-слое обычно видны на всех столах (как у Fences) —
  проверить поведение, задокументировать; при необходимости `IVirtualDesktopManager`.
- Исполнитель: **win32-interop-specialist**. Полностью native.
- DoD (всё **manual verify Win10/Win11**): после смены обоев панели остаются встроены; на mixed-DPI не «прыгают»;
  поведение на виртуальных столах задокументировано и соответствует Fences.

---

## Архитектурные решения (ADR-ноты для architecture-documenter)

1. **Страницы: поле `PageId` в `ChildWindowModel`** (а не внешняя структура членства) — минимум миграций JSON,
   обратная совместимость (пустой = дефолтная страница). Trade-off: членство «один-к-одному», панель на одной странице.
2. **Привязка к монитору по стабильному ID + геометрия-fallback**, а не по индексу — устойчивость к перестановке мониторов.
3. **Снапшот сериализует `ChildWindowModel` целиком** — переиспользует существующую сериализацию, автоматически
   подхватывает новые поля (страницы/мониторы). Trade-off: снапшоты крупнее, но проще и устойчивее.
4. **Правила сортировки — отдельный Settings-ключ**, не смешивать с `ChildWindowsState`.

Эти решения требуют нового **ADR-004 (Phase 5: pages, snapshots, multi-monitor)** — задача architecture-documenter в финале.

---

## Рекомендация оркестратору

Прогонять конвейер **в цикле по каждому пункту** (не по одному за вызов):

```
fences-feature-planner (этот мастер-план)
  → пункт 1 [dotnet-services-engineer ∥ wpf-ui-builder] → tdd-test-engineer → ревью
  → пункт 5 [win32-interop-specialist ∥ wpf-ui-builder] → tdd → ревью   ⟍ можно ∥ с 3
  → пункт 3 [wpf-ui-builder (+win32 опц.)] → tdd → ревью                ⟋
  → пункт 2 [dotnet-services-engineer] → tdd → ревью
  → пункт 4 [dotnet-services-engineer ∥ wpf-ui-builder] → tdd → ревью
  → СК-2 [win32-interop-specialist]
  → architecture-documenter (ADR-004 + закрыть [x] в ROADMAP)
```

Все native-части (5, СК-2, опц. жест в 3) — **manual verify (Win10/Win11)**, в CI только `dotnet build`/`dotnet test`.
