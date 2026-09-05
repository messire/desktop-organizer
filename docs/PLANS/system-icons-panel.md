# План: SystemIconsPanel (системная панель ярлыков)

**Scope: XL** (нативный shell-слой + новый тип окна WPF + persistence + настройки).
**Тип:** новая независимая фича. Окно НЕ является ChildWindow/Fence и не участвует в логике фенсов.

---

## 1. Архитектурное решение (ADR-style)

### Context
Нужна отдельная вертикальная панель у левого края основного монитора с системными иконками
(This PC, Control Panel, Recycle Bin, Network, User folder), нативными значками, нативным контекстным
меню, drag-reorder и drop в корзину. Это самостоятельная сущность, не должна загрязнять
`ChildWindowModel` / `WindowManager` / логику фенсов.

### Decision
- **Отдельная модель** `SystemIconsPanelModel` (UI/Models, INPC) — НЕ переиспользуем `ChildWindowModel`.
- **Отдельное окно** `SystemIconsPanelWindow` + `SystemIconsPanelViewModel`. Встраивание в desktop-слой
  через существующий `IDesktopIntegrationService.EmbedInDesktop` (тот же WorkerW/Progman-подход, тот же
  Z-order, что у фенсов) — переиспользуем, НЕ дублируем.
- **Отдельный менеджер жизненного цикла** `ISystemIconsPanelManager` (UI/Services) — создаёт/уничтожает
  окно по глобальному тогглу, владеет persistence. App.xaml.cs дёргает его при старте. НЕ через
  `WindowManager` (чтобы не смешивать с состоянием фенсов и `ChildWindowsState`).
- **Нативный слой** расширяет `INativeApi` (Services) новыми shell-операциями за тем же фасадом —
  инвариант «P/Invoke только за INativeApi» соблюдён. Бизнес-обёртка — новый сервис
  `ISystemShellService` (Abstractions) / `SystemShellService` (Services/Tools).
- **Persistence** — отдельная секция: одно строковое свойство `SystemIconsPanel` (JSON) в `IAppSettings`,
  по аналогии с `SortingRules`/`LayoutSnapshots`/`Pages`. НЕ в `ChildWindowsState`.

### Alternatives considered
- *Переиспользовать ChildWindow с флагом «системная панель»* — отклонено: загрязняет модель фенсов,
  ломает snapping/roll-up/layout-инварианты, нарушает разделение из CLAUDE.md.
- *AppBar (SHAppBarMessage, резервирование места)* — отклонено явным требованием: место под окна НЕ
  резервируется, окно живёт на desktop-уровне за обычными окнами.

### Consequences
- (+) Полная изоляция от фенсов; (+) переиспользование desktop-embed и icon-инфраструктуры.
- (−) Нативный shell-код (pidl/IContextMenu/SHFileOperation) сложен и непроверяем вне Windows.

---

## 2. Мастер-план по слоям

### Слой Core (модели данных)
Нейтральные перечисления/идентификаторы системных элементов (без WPF-зависимостей).
- `SystemIconKind` (enum): `ThisPc, ControlPanel, RecycleBin, Network, UserFolder`.
- `SystemIconDescriptor` (record/класс): `Kind`, `KnownFolderId`/CLSID или признак pidl-источника,
  дефолтная подпись. Таблица соответствия Kind → known-folder GUID / CSIDL.

### Слой Services.Abstractions (интерфейсы + DTO)
- `ISystemShellService` — фасад над нативным shell:
  - `ImageSource GetSystemIcon(SystemIconKind kind, int sizePx)` — нативный значок по pidl/known-folder.
  - `void OpenSystemItem(SystemIconKind kind)` — shell-execute по pidl.
  - `void ShowContextMenu(SystemIconKind kind, IntPtr ownerHwnd, int screenX, int screenY)` — нативное `IContextMenu`.
  - `RecycleBinState GetRecycleBinState()` — `SHQueryRecycleBin` (пусто/непусто, кол-во).
  - `bool SendToRecycleBin(IReadOnlyList<string> paths)` — `SHFileOperation`/`IFileOperation` (move to bin).
  - событие `RecycleBinChanged` (или метод опроса для refresh).
- `RecycleBinState` (DTO): `IsEmpty`, `ItemCount`.

### Слой Services (реализация + нативный interop)
- `INativeApi` (внутр.) расширить методами:
  - `SHGetFileInfo` для CLSID/pidl (уже есть `GetFileInfo` для путей — добавить перегрузку по pidl),
  - `SHGetKnownFolderIDList` / `SHParseDisplayName` → pidl,
  - `SHGetFileInfo(pidl, SHGFI_PIDL|SHGFI_ICON|SHGFI_SYSICONINDEX)`,
  - `SHQueryRecycleBin`,
  - `SHFileOperation` (или `IFileOperation` COM) для удаления в корзину,
  - получение `IContextMenu` (`SHBindToParent` + `GetUIObjectOf`) и `TrackPopupMenuEx` + `InvokeCommand`.
- `NativeApi.cs` / `NativeMethods` — соответствующие `[LibraryImport]`/`DllImport`.
- `SystemShellService : ISystemShellService` — оборачивает нативку, ловит ошибки (битый элемент →
  деградировать молча: placeholder-иконка, no-op для меню/открытия). Кэш иконок по `(kind,size)`.

### Слой UI
- `Models/SystemIconsPanelModel.cs` (INPC): `IsEnabled` (глоб. тоггл), `IconSize` (enum S/M/L → px),
  `Items` (упорядоченный список `SystemIconItemModel`).
- `Models/SystemIconItemModel.cs` (INPC): `Kind`, `IsVisible`, `Order`, runtime `Icon`/`Label`/`Caption`.
- `Models/SystemIconSize.cs` (enum S/M/L) + хелпер px (32/48/64, согласовать с `GridMetrics`).
- `ViewModels/SystemIconsPanelViewModel.cs`: загрузка элементов из модели, фильтр по `IsVisible`,
  команды Open/ShowContextMenu, reorder, refresh корзины, drop-в-корзину; подписка на
  `RecycleBinChanged`. Реализует `IDisposable`.
- `Views/SystemIconsPanelWindow.xaml(.cs)`: chromeless `Window`, `SizeToContent=Width`,
  `WindowStyle=None`, `ResizeMode=NoResize`, `ShowInTaskbar=False`. `OnSourceInitialized` →
  `IDesktopIntegrationService.EmbedInDesktop`. Позиционирование: left=рабочая область primary.Left,
  top=workArea.Top, height=workArea.Height (через `IMonitorService.GetPrimaryMonitor()`). Двойной клик,
  ПКМ (нативное меню по экранным координатам), drag-reorder (адорнер/insertion index), drag-over+drop
  на элемент корзины.
- `Services/ISystemIconsPanelManager.cs` + `SystemIconsPanelManager.cs`: `Apply()` (создать/закрыть окно
  по тогглу), `Save()`/`Load()` (JSON ↔ `IAppSettings.SystemIconsPanel`), реакция на смену primary-монитора
  (подписка на `IMonitorService.MonitorsChanged` — репозиционирование/высота).
- `Services/IAppSettings.cs` + `AppSettings.cs` + `Settings.settings/.Designer`: новое свойство
  `string SystemIconsPanel` (JSON, дефолт `""`).
- `App.xaml.cs`: в `StartApp` после `MonitorService.Start()` — `SystemIconsPanelManager.Load()+Apply()`;
  в `OnExit` — `Save()`.
- `Views/MainWindow.xaml` + `MainWindowViewModel`: секция «Системная панель» — глоб. тоггл, чекбоксы
  видимости по каждому из 5 элементов, выбор размера S/M/L. Правки → INPC-модель → `Manager.Apply()` + `Save()`.
- `Configuration/DiConfig.cs`: регистрация `ISystemShellService`, `ISystemIconsPanelManager`,
  `SystemIconsPanelViewModel` (Transient), `SystemIconsPanelWindow` (Transient).
- (опц.) `Resources/Templates.xaml`: DataTemplate элемента (иконка + подпись под ней).

### Слой Tests
- `SystemShellServiceTests` — на моках `INativeApi`: маппинг Kind→дескриптор, graceful-degrade при
  нулевом pidl/ошибке, парсинг `RecycleBinState`, кэш иконок.
- `SystemIconsPanelManagerTests` — JSON round-trip persistence, фильтрация невидимых, тоггл создаёт/закрывает.
- `SystemIconsPanelViewModelTests` — reorder меняет Order и сохраняет, фильтр по IsVisible.
- Нативные методы `NativeApi`/окно WPF — НЕ юнит-тестируемы (manual verify).

---

## 3. Список файлов

### Создать
| Путь | Слой |
|---|---|
| `src/DesktopOrganizer.Core/Models/SystemIconKind.cs` | Core |
| `src/DesktopOrganizer.Core/Models/SystemIconDescriptor.cs` | Core |
| `src/DesktopOrganizer.Services.Abstractions/ISystemShellService.cs` | Abstractions |
| `src/DesktopOrganizer.Services.Abstractions/RecycleBinState.cs` | Abstractions |
| `src/DesktopOrganizer.Services/Tools/SystemShellService.cs` | Services |
| `src/DesktopOrganizer.UI/Models/SystemIconsPanelModel.cs` | UI |
| `src/DesktopOrganizer.UI/Models/SystemIconItemModel.cs` | UI |
| `src/DesktopOrganizer.UI/Models/SystemIconSize.cs` | UI |
| `src/DesktopOrganizer.UI/ViewModels/SystemIconsPanelViewModel.cs` | UI |
| `src/DesktopOrganizer.UI/Views/SystemIconsPanelWindow.xaml(.cs)` | UI |
| `src/DesktopOrganizer.UI/Services/ISystemIconsPanelManager.cs` | UI |
| `src/DesktopOrganizer.UI/Services/SystemIconsPanelManager.cs` | UI |
| `tests/.../Unit/Services/SystemShellServiceTests.cs` | Tests |
| `tests/.../Unit/Services/SystemIconsPanelManagerTests.cs` | Tests |
| `tests/.../Unit/ViewModels/SystemIconsPanelViewModelTests.cs` | Tests |

### Изменить
| Путь | Что |
|---|---|
| `src/DesktopOrganizer.Services/Interfaces/INativeApi.cs` | +методы pidl/known-folder/SHQueryRecycleBin/SHFileOperation/IContextMenu |
| `src/DesktopOrganizer.Services/Tools/NativeApi.cs` (+ NativeMethods) | реализации P/Invoke |
| `src/DesktopOrganizer.Services/Configuration/DiConfig.cs` | регистрация `ISystemShellService` |
| `src/DesktopOrganizer.UI/Services/IAppSettings.cs` | +`string SystemIconsPanel` |
| `src/DesktopOrganizer.UI/Services/AppSettings.cs` + `Properties/Settings.*` | реализация свойства |
| `src/DesktopOrganizer.UI/Configuration/DiConfig.cs` | регистрация manager/VM/Window |
| `src/DesktopOrganizer.UI/App.xaml.cs` | Load+Apply при старте, Save при выходе |
| `src/DesktopOrganizer.UI/Views/MainWindow.xaml` + `ViewModels/MainWindowViewModel.cs` | секция настроек |

---

## 4. Зависимости / порядок реализации

```
1. Core: SystemIconKind, SystemIconDescriptor                    (нет зависимостей)
2. Abstractions: ISystemShellService, RecycleBinState            (зависит от Core)
3. Services: INativeApi-расширение → NativeApi → SystemShellService   (зависит от 2)
   └ tests: SystemShellServiceTests (моки INativeApi)
4. UI Models: SystemIconSize, SystemIconItemModel, SystemIconsPanelModel
5. IAppSettings.SystemIconsPanel (persistence-свойство)
6. UI Services: SystemIconsPanelManager (зависит от 4,5, IMonitorService, IDesktopIntegrationService)
   └ tests: SystemIconsPanelManagerTests
7. UI ViewModel: SystemIconsPanelViewModel (зависит от 3,4)
   └ tests: SystemIconsPanelViewModelTests
8. UI View: SystemIconsPanelWindow (зависит от 6,7, EmbedInDesktop)
9. App.xaml.cs wiring + DiConfig
10. MainWindow settings UI (зависит от всего; включает глоб. тоггл/чекбоксы/размер)
```
Параллелить можно: (1→2→3 нативная ветка) и (4→5 модель/persistence) — независимы до шага 6.

---

## 5. Риски

### Нативные (manual verify на Win10/Win11 — непроверяемо в этой среде)
- **IContextMenu**: получение через `SHBindToParent`/`GetUIObjectOf`, `TrackPopupMenuEx` + `InvokeCommand`
  — сложно, требует владельца-hwnd с фокусом. Окно имеет `WS_EX_NOACTIVATE` (как фенсы, Фаза 2) →
  меню может не получать ввод/не закрываться. Возможно временно снимать `NOACTIVATE`/`SetForegroundWindow`
  на время показа меню. **Высокий риск.**
- **pidl/known-folder иконки**: `SHGetKnownFolderIDList` + `SHGetFileInfo(SHGFI_PIDL)`; не у всех
  элементов есть путь (This PC/Network — виртуальные). Освобождать pidl (`CoTaskMemFree`), `DestroyIcon`.
- **SendToRecycleBin**: `SHFileOperation` (FO_DELETE|FOF_ALLOWUNDO) или `IFileOperation` (COM, STA).
  Из desktop-embed окна с NOACTIVATE — проверить, что диалог подтверждения появляется корректно.
- **SHQueryRecycleBin refresh**: нет дешёвого события; вариант — `SHChangeNotifyRegister` (сложно) или
  опрос по таймеру / refresh при drop и при показе панели. Выбрать прагматично (таймер 2–5 с или
  `SHChangeNotify`). **Средний риск.**
- **Z-order/высота**: окно на WorkerW при смене primary-монитора/DPI/обоев может «слететь» — нужна
  реакция на `MonitorsChanged` и, возможно, повторный `EmbedInDesktop`.

### WPF
- `SizeToContent=Width` + фиксированная Height (workArea) — следить, чтобы auto-width пересчитывался при
  смене размера иконок S/M/L.
- Drag-reorder в chromeless NOACTIVATE-окне: стандартный WPF DragDrop требует мыши/фокуса — проверить;
  fallback — ручной трекинг через адорнер.
- DPI: координаты для нативного меню — экранные пиксели, а WPF — DIP; нужен пересчёт через
  `PresentationSource`/`CompositionTarget` (как и в существующем resize-коде).

### Инварианты
- P/Invoke строго за `INativeApi`; колбэки (если `SHChangeNotify`) — минимум работы + try/catch + dispatcher.
- Битый/недоступный элемент не валит загрузку панели (placeholder-иконка, no-op).
- Слои не нарушать: UI не вызывает P/Invoke напрямую, только через `ISystemShellService`.

### Manual verify (Win10/Win11) — обязательный раздел
1. Панель у левого края primary, полная рабочая высота, за обычными окнами, не в taskbar/Alt+Tab.
2. Нативные значки всех 5 элементов; размер S/M/L меняет ширину.
3. Двойной клик открывает каждый элемент; ПКМ показывает системное меню и его пункты работают.
4. Drag-reorder сохраняется после рестарта.
5. Корзина: индикатор пусто/непусто обновляется; drop файла перемещает в корзину.
6. Глоб. тоггл и чекбоксы видимости; смена primary-монитора/разрешения/DPI/обоев не ломает панель.

---

## 6. DoD (трассировка к требованиям)
1. ✅ Левый край primary, полная высота, auto-ширина, за окнами — шаги 6,8 + manual verify 1.
2. ✅ Нативные значки+подписи, видимость+размер из настроек, глоб. тоггл — шаги 3,4,10 + mv 2.
3. ✅ Двойной клик / ПКМ нативное меню / drag-reorder+persist — шаги 7,8 + mv 3,4.
4. ✅ Корзина: состояние + drop — шаги 3,7,8 + mv 5.
5. ✅ JSON save/restore секции `SystemIconsPanel` — шаги 5,6 + тесты.
6. ✅ build+test зелёные, покрытие сервис-слоя, список manual verify — Tests + раздел 5.
```
```

---

## 7. Recommended Next Actions (для оркестратора)

Порядок прогона конвейера для этой XL-фичи:

1. 🚨 **CRITICAL — win32-interop-specialist**: расширение `INativeApi`/`NativeApi` (pidl, known-folder,
   `SHQueryRecycleBin`, `SHFileOperation`, `IContextMenu`/`TrackPopupMenuEx`) + `SystemShellService`.
   Reason: самый высокий риск, отдельная компетенция, блокирует UI.
2. 🚨 **CRITICAL — dotnet-services-engineer**: Core-модели, `ISystemShellService`-контракт,
   `IAppSettings.SystemIconsPanel` persistence, `SystemIconsPanelManager`, DI-регистрация.
   Может идти **параллельно** с (1) до точки интеграции.
3. 🚨 **CRITICAL — wpf-ui-builder**: `SystemIconsPanelWindow`/ViewModel, drag-reorder, drop-в-корзину,
   секция настроек в MainWindow. Зависит от (1) и (2).
4. ⚠️ **RECOMMENDED — tdd-test-engineer**: `SystemShellServiceTests`, `SystemIconsPanelManagerTests`,
   `SystemIconsPanelViewModelTests` (моки `INativeApi`). Желательно писать до/параллельно реализации (TDD).
5. 💡 **OPTIONAL — debug-healer**: если build/test красные.
6. ⚠️ **RECOMMENDED (parallel) — code-quality-reviewer ∥ fences-ux-parity-reviewer**: после реализации.
7. ⚠️ **RECOMMENDED — architecture-documenter**: новый ADR (отдельный тип desktop-окна вне модели фенсов)
   + отметка в ROADMAP; зафиксировать раздел manual verify.
