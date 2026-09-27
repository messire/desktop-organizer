# Roadmap: путь к паритету со Stardock Fences

**Цель:** приблизить DesktopOrganizer по функциональности и поведению к Stardock Fences.

**Статус на 2026-09-27.** Фазы 0–9 закрыты; Фаза 10 «Release hardening 1.0» закрыта; manual verify
выполнена пользователем на Windows 11 24H2 (сборка 26200), 2026-09-27, признана достаточной — Windows 10
отдельно не проверяется. Реализованы: встраивание панелей в слой рабочего стола (Progman/WorkerW), убирание из taskbar/Alt+Tab, перетаскивание за заголовок, привязка к сетке, два вида (список/плитка), drag&drop файлов, сохранение состояния в JSON, окно настроек и трей; файловые операции, поведение панелей, внешний вид, десктоп-жесты, Folder Portals, страницы/swipe, снапшоты раскладки, multi-monitor; ресайз кратно сетке иконок, фильтр содержимого панели, кнопка roll-up; скрытие/показ иконок рабочего стола (единый переключатель, без выборочного режима — см. ADR-006); панель системных иконок (SystemIconsPanel) как второй независимый тип desktop-window. Плюс 2026-07-26 (см. `docs/prompts/outstanding-followups.md`, пункты 1–4): one-time Move-хинт, тест кросс-volume `Restore`, очистка managed-копий при удалении панели ([ADR-010](../adr/ADR-010-managed-storage-cleanup-on-panel-removal.md)) и re-clamp панелей при уменьшении рабочей области монитора. Плюс ещё 2026-07-26 (см. `docs/prompts/grid-snap-drag-and-settings-order.md`, [ADR-011](../adr/ADR-011-manual-drag-with-magnetic-grid-snap.md)): магнитное покадровое прилипание к сетке во время перетаскивания панели (не только на drop), новая настройка `GridSnapDistance`, и реорганизация окна настроек (порядок групп + перенос «Привязки к сетке» в «Автозапуск и рабочий стол»). Плюс 2026-08-15 — **Фаза 7 «Производительность, память и отказоустойчивость»** ([docs/PLANS/phase-7-performance-hardening.md](PLANS/phase-7-performance-hardening.md), [ADR-012](../adr/ADR-012-performance-and-resilience-hardening.md)): все 11 находок [docs/PERFORMANCE_AUDIT.md](PERFORMANCE_AUDIT.md) закрыты 6 блоками (A–F) — debounced persistence с синхронным flush для структурных изменений, try/catch вокруг колбэка глобального хука жестов + вынос чистой машины состояний, ограниченный LRU-кеш иконок и замороженные кеш-кисти конвертеров, batch-уведомления и O(1) дедуп коллекций ViewModel, авторитетный in-memory кеш манифеста `PanelItemStorageService` с write-through. Плюс 2026-08-16 — **Фаза 8 «UX-паритет значков: выделение и переименование»** ([docs/PLANS/icon-selection-and-rename.md](PLANS/icon-selection-and-rename.md), [ADR-013](../adr/ADR-013-panel-item-selection-and-rename.md)): визуальное выделение значка в обоих видах панели (`ListBox`-контейнеры, подсветка не зависит от `IsSelectionActive`) и честные имена + inline-переименование значков (F2/контекстное меню, физическое переименование на диске, GUID перенесён из имени файла в имя каталога, синхронизация manifest). Плюс ещё 2026-08-16 — **Фаза 9 «Сортировка и ручной порядок значков панели»** ([docs/PLANS/panel-item-sorting.md](PLANS/panel-item-sorting.md), [ADR-014](../adr/ADR-014-panel-item-sorting-and-manual-order.md)): новая настройка `PanelSortMode` (Manual/Name) в окне настроек и контекстном меню заголовка, немутирующая сортировка по имени (собственный natural-order компаратор, папки вперёд), drag-to-reorder значков мышью в обоих видах панели с трансляцией видимого индекса в полный набор при активном `ContentFilter` и авто-переключением в `Manual`. Остаются сквозные задачи (см. ниже).

Легенда: `[ ]` — не сделано, `[~]` — частично, `[x]` — готово.

---

## Фаза 0. Чистка хвостов (быстро, разблокирует остальное)

Технический долг после рефакторинга ADR-002. Маленькие задачи, но мешают дальше.

- [x] Удалить мёртвую настройку `SavedFiles` из `Settings` (заменена на `ChildWindowsState`).
      Файлы: `src/DesktopOrganizer.UI/Properties/Settings.*`, `App.config`.
- [x] Убрать пустые TODO-заглушки `TitleBar_MouseMove` / `TitleBar_MouseLeftButtonUp`
      в `ChildWindow.xaml.cs` (на тот момент drag работал через `DragMove` — заглушки лишние). Заодно удалены неиспользуемые поля `_isDragging` / `_dragOffset`.
      **Устарело:** `DragMove()` с 2026-07-26 заменён на ручной драг с покадровым магнитным
      снаппингом — см. Фазу 2 и [ADR-011](../adr/ADR-011-manual-drag-with-magnetic-grid-snap.md)
      (поле `_isDragging` при этом снова появилось в code-behind, уже с другим назначением).
- [x] Привести трей к роли «Настройки» из ADR-002: текст пунктов на русском («Настройки» / «Скрыть настройки» / «Выход»), исправлено стартовое состояние видимости (окно скрыто при запуске).
      Файл: `src/DesktopOrganizer.Services/Providers/TrayIconManager.cs`.
- [x] Синхронизировать статус ADR-001/002: выставлен `Accepted` в обоих файлах.

**Готово, когда:** нет мёртвого кода, трей соответствует ADR-002, статусы ADR согласованы.

---

## Фаза 1. Базовое взаимодействие с файлами (критично)

Без этого панели — просто контейнеры. Это самое заметное отличие от Fences.

- [x] **Открытие файла** по двойному клику → `IFileLauncher.Open` (`Process.Start`, `UseShellExecute=true`).
      Сервис `IFileLauncher` (Abstractions) / `FileLauncher` (Services/Tools), вызывается из `ChildWindowViewModel`; двойной клик ловится в `ChildWindow.xaml.cs`.
- [x] **Контекстное меню на файле**: Открыть · Открыть папку с файлом · Свойства (shell-диалог через `ShellExecuteEx`) · Убрать из панели.
      Ресурс `FileContextMenu` в `Resources/Templates.xaml`, команды — в `ChildWindowViewModel`.
- [x] **Удаление из панели** через UI: `RemoveFileCommand` → `RemoveFile` + событие `FilesChanged` → `SaveWindowsState`.
- [x] Обработка «битых» путей: `FileItem.Exists`; отсутствующие файлы не выкидываются при загрузке, а показываются серыми (Opacity 0.45).
- [x] **Move при drag&drop, а не копирование или ссылка на оригинал.** Изначальный фикс 2026-06-28
      (баг найден на ручной верификации): при переносе ярлыка с рабочего стола в панель приложение
      хранило только путь к исходному файлу — панель не копировала его. Это ломало саму идею
      приложения («очистить рабочий стол»): пользователь переносит ярлык в панель и удаляет старый
      ярлык с рабочего стола → ярлык в панели становился нерабочим. Первая итерация фикса ввела
      Copy-семантику (копия в приватное хранилище приложения). Это решило регрессию, но не саму задачу:
      после Copy на столе оставался дубликат — пользователь должен был вручную удалять исходный файл,
      что не соответствует ни ожиданию от drag&drop, ни поведению Fences.
      Эволюционировано 2026-07-07 (план [docs/PLANS/drag-drop-move-semantics.md](PLANS/drag-drop-move-semantics.md),
      архитектурные решения — [ADR-009](../adr/ADR-009-drag-drop-move-semantics.md)) в полноценную
      **Move**-семантику:
      - Drop файла/папки в панель теперь **перемещает** оригинал (не копирует, не оставляет ссылку):
        `IPanelItemStorageService.MoveIn`/`MoveInDirectory` (Services/Tools:
        `PanelItemStorageService`). Same-volume — атомарный `File.Move`/`Directory.Move`; cross-volume —
        copy-then-delete-after-verified-success (оригинал удаляется только после подтверждённого успеха
        копии — существование + сверка размера). Курсор при drag — `DragDropEffects.Move`
        (`ChildWindow.xaml.cs`), кроме портальных панелей (`None`).
      - Safety-cap (2 ГиБ / 20000 файлов) применяется **только** к cross-volume перемещению папок —
        same-volume `Directory.Move` атомарен (rename, ничего не копируется физически) и лимит не
        нужен. Сверх лимита папка при drop остаётся прямой ссылкой (referencing-фолбэк), как раньше.
      - **«Убрать из панели»** для managed-элемента теперь **возвращает файл на исходное место**
        (`IPanelItemStorageService.Restore`, вызывается из `ChildWindowViewModel.RemoveFileItem`), а не
        удаляет физическую копию безвозвратно. Коллизия имён на исходном месте — Explorer-паттерн
        (`name (2).ext`, первый свободный индекс, без перезаписи); если исходная родительская
        директория была удалена — воссоздаётся. Original path хранится в персистентном JSON-manifest
        внутри `PanelItemStorageService` (`%LocalAppData%\DesktopOrganizer\PanelItems\.manifest.json`),
        **не** в `FileItem`/персистентной схеме панели — осознанное решение сохранения инкапсуляции
        слоёв, детали и альтернативы — [ADR-009](../adr/ADR-009-drag-drop-move-semantics.md).
      - Для **legacy-панелей** (сохранённых до этого изменения, managed-копии без записи original
        path в manifest) `Restore` возвращает `RestoreOutcome.OriginalPathUnknown`, и
        `RemoveFileItem` деградирует до старого поведения (`Release` — удаляет managed-копию без
        восстановления). Никаких падений на старых панелях.
      - Folder Portals (`SourceFolderPath`) и восстановление состояния при старте не затронуты —
        `AddFiles`/`LoadFromModel` для них остаются прямыми, Move применяется только на пути живого
        drag&drop. Copy-API (`Import`/`ImportDirectory`) удалён из `IPanelItemStorageService` как
        мёртвый код после эволюции в Move; `Release`/`IsManaged` сохранены (нужны для legacy-деградации
        и будущей очистки осиротевших копий).
      - В этом же цикле реализации исправлены найденные `code-quality-reviewer` баги: CRITICAL —
        `IsManaged` неверно сравнивал путь по префиксу строки без учёта границ сегмента пути (ложные
        срабатывания на «братских» директориях с общим префиксом имени); MAJOR — отсутствовала
        per-file верификация при рекурсивном cross-volume копировании папки перед удалением оригинала.
      - Тесты: 27 тестов `PanelItemStorageServiceTests.cs` + 7 тестов новый
        `ChildWindowViewModelTests.cs` — все зелёные. Полный набор тестов проекта — 6 предсуществующих
        падений, не относящихся к этой фиче (взяты пользователем в отдельную задачу).
      - [x] **Известное ограничение — устранено 2026-07-26:** если `Restore` завершается
        `RestoreOutcome.Failed`, managed-копия могла остаться в storage без ссылки (осиротевшая
        копия), в т.ч. при удалении панели целиком — решено см. ниже, «известный остаток про
        удаление панели», и [ADR-010](../adr/ADR-010-managed-storage-cleanup-on-panel-removal.md).
      - [x] **Известный тестовый пробел — закрыт 2026-07-26:** добавлен `internal RestoreOutcome
        Restore(string storedPath, bool forceCrossVolume)` в `PanelItemStorageService` (по аналогии
        с `MoveIn`/`MoveInDirectory`); публичный `Restore(string storedPath)` не изменился. Тесты на
        успешный и провальный кросс-volume `Restore` (файл и директория) добавлены в
        `PanelItemStorageServiceTests.cs`.
      - [x] **Открытый UX-риск повышенного приоритета — закрыт 2026-07-26:** one-time хинт при первом
        Move-drop реализован. `IAppSettings.HasShownMoveHint` (переживает перезапуск) выставляется
        после первого показа; `ChildWindowViewModel.MoveHintRequested` поднимается при первом
        Move-drop; `ChildWindow.xaml` показывает ненавязчивый `Popup` (RU-текст, автозакрытие через
        4 секунды). `ToolTip` на пункте «Убрать из панели» оставлен как дополняющий хинт.
      **Папки при drop** (не изменилось этим циклом, актуально и для Move): рекурсивное
      перемещение/копирование папки — это перенос снэпшота на момент переноса, не живое зеркало: если
      исходная папка активно меняется параллельно, эти изменения сами не попадут в managed-копию (для
      живого зеркалирования — используйте Folder Portal через диалог «Customize panel…»).
      **Уточнение 2026-09-26 (ADR-031):** упомянутая здесь `SelectSourceFolderCommand` удалена —
      команда нигде не была привязана к контролу; Folder Portal теперь назначается из
      `PanelCustomizeWindow` (Browse…/Clear), реактивно на `ChildWindowModel.SourceFolderPath`.
      Более «фенсовый» механизм — не копировать/перемещать файл физически, а прятать иконку на
      рабочем столе на месте (как для «Захвата иконок») — рассматривался и в этом цикле, и раньше;
      отклонён как более рискованный и объёмный кросс-процессный Win32-код, непроверяемый в этой среде
      без GUI Windows. Решение и альтернативы задокументированы в
      [ADR-009](../adr/ADR-009-drag-drop-move-semantics.md) (Решение 1).
      Отдельно (не изменилось этим циклом): `FileItem` — `INotifyPropertyChanged` с методом
      `Refresh()`; `ChildWindowViewModel` держит поллинг-таймер (3с, по аналогии с
      `SystemShellService`'s Recycle Bin poll), который живо переоценивает `Exists`/`IsDirectory` и
      триггерит серый Opacity через существующий `DataTrigger`.
      [x] **Устранено 2026-07-26:** при удалении панели (`RemovePanelCommand`) managed-копии её
      файлов в `PanelItems` теперь подчищаются. `WindowManager.RemoveChildWindow` получил перегрузку
      `RemoveChildWindow(Guid, bool releaseManagedItems)` — `true` передаётся только из
      `MainWindowViewModel.RemovePanel` (пользователь удаляет одну панель) и
      `PageManagerService.RemovePage` (удаление страницы уничтожает её панели); обычное
      закрытие/пересоздание при restore layout-снапшота (`LayoutSnapshotService.Restore`) передаёт
      `false`, т.к. это замена набора панелей, а не удаление (снапшот пересоздаёт панели, ссылающиеся
      на те же managed-пути). `MainWindowViewModel.RemovePanel` требует подтверждения
      (`IConfirmationService.Confirm`, иконка `Warning`, явный текст о безвозвратной потере файлов)
      только если у панели есть managed-файлы (`IPanelItemStorageService.IsManaged`); иначе —
      нейтральный текст. Первая версия реализации без разделения вызывающих мест содержала
      критический баг потери данных при restore layout-снапшота, найденный ревью до мержа. Решение и
      инвариант вызывающих мест — [ADR-010](../adr/ADR-010-managed-storage-cleanup-on-panel-removal.md).
      **Открытый вопрос для бэклога (не решён)** — см. «Сквозные задачи» ниже: по духу паритета с
      Fences удаление панели могло бы возвращать managed-файлы на рабочий стол вместо их удаления.

**Готово, когда:** файл из панели запускается, есть контекстное меню, файл можно удалить из панели, состояние сохраняется.

---

## Фаза 2. Поведение панели как у Fences

- [x] **Ресайз панели.** Оверлей из 8 прозрачных `Thumb` (края + углы, стиль `ResizeThumbStyle`),
      `Resize_DragDelta` меняет `Width/Height/Left/Top` с минимумом 120px; `Resize_DragCompleted` → snap + save.
      Улучшение (feature/fancy-windows): ресайз квантуется к сетке иконок (`GridMetrics.Quantize`),
      аккумулятор дельт без дрейфа; `IAppSettings.SnapResizeToIconGrid` (дефолт true).
- [x] **Магнитное прилипание к сетке во время перетаскивания панели (не только на drop) —
      закрыто 2026-07-26** (план [docs/PLANS/grid-snap-during-drag.md](PLANS/grid-snap-during-drag.md),
      [ADR-011](../adr/ADR-011-manual-drag-with-magnetic-grid-snap.md)). `Window.DragMove()` в
      `ChildWindow.xaml.cs` заменён на ручной драг (`CaptureMouse`/`TitleBar_MouseMove`/
      `TitleBar_MouseLeftButtonUp`/`LostMouseCapture`), т.к. `DragMove()` блокирует поток и отдаёт
      перемещение нативному циклу ОС до mouse-up, что несовместимо с покадровым снаппингом.
      Чистый статический helper `GridSnap` (`UI/Models/GridSnap.cs`, без WPF/P/Invoke): `SnapAxis`
      снапает координату к ближайшей линии сетки, если она в пределах порога (магнит, а не жёсткое
      квантование); `Snap` применяет это независимо по осям X/Y; `GridSize<=1` выключает снаппинг.
      Новая persisted-настройка `GridSnapDistance` (int px, дефолт 12) — `IAppSettings`/`AppSettings`/
      `Properties/Settings.settings`+`.Designer.cs`/`MainWindowViewModel`, слайдер в `MainWindow.xaml`
      (Диапазон 0–64, `0` = снаппинг выключен). Drop-снаппинг (`ChildWindow.SnapToGrid()`) переведён
      на тот же порог `GridSnapDistance`, что и живой драг — чтобы не было визуального рывка на
      mouse-up. По пути ревью (`code-quality-reviewer`) нашёл и здесь же исправлен **MAJOR**-баг: дельта
      курсора считалась в физических device-пикселях (`PointToScreen`), а применялась к `Window.Left/Top`
      в DIU — на масштабе экрана ≠100% (125%/150%) панель двигалась быстрее курсора; исправлено делением
      дельты на `VisualTreeHelper.GetDpi(this).DpiScaleX/Y`. Тесты — чистая математика helper,
      `tests/DesktopOrganizer.Tests/Unit/Models/GridSnapTests.cs` (17 тестов). `fences-ux-parity-reviewer`
      MAJOR-расхождений с Fences не нашёл.
- [x] **Roll-up (сворачивание в заголовок)** по двойному клику по тайтлу: `ContentRow.Height=0`,
      запоминается развёрнутая высота; флаг `IsRolledUp` в `ChildWindowModel` (`OnRenderSizeChanged` не перетирает размер при свёрнутом).
      Улучшение (feature/fancy-windows): явная кнопка-тоггл рядом с SwitchView (Segoe MDL2 ChevronDown/Up),
      анимация 150 мс; `IsRolledUp` переведён на INPC через `SetField` (исправление регрессии).
- [x] **Фильтр содержимого панели.** Контекстное меню заголовка: ShowAll / HideShortcuts / FoldersOnly / HideAll.
      `PanelContentFilter` (enum, UI/Models), `ChildWindowModel.ContentFilter` (INPC, сериализуется в JSON),
      двухслойная модель `_allItems` / `FileItems` в `BaseViewModel`; `EnumEqualsConverter` для XAML-привязок.
      `FileItem` расширен полями `IsDirectory`, `IsShortcut`.
- [x] **Inline-переименование** заголовка: контекстное меню тайтла «Переименовать» → `TextBox`
      (Enter/потеря фокуса — коммит, Escape — отмена); `SyncToModel` пишет `Title`.
- [x] **Скролл** при переполнении: wrap-вид обёрнут в `ScrollViewer` (список и так скроллит сам).

**Готово, когда:** панель ресайзится мышью, сворачивается в заголовок, переименовывается, длинный список скроллится — всё переживает перезапуск.

> ℹ️ Проверено вживую пользователем на Windows 11 24H2, 2026-09-27 (переименование заголовка панели
> требует клавиатурного фокуса, а панель имеет `WS_EX_NOACTIVATE` — не активируется по клику; ввод в
> `TextBox` работает без обходных мер). Двойной клик по тайтлу (обновлено 2026-07-26, `DragMove()`
> в коде больше нет — см. выше): первый `MouseLeftButtonDown` (`ClickCount == 1`) запускает ручной
> драг (`CaptureMouse`), который тут же завершается на парном `MouseLeftButtonUp` с нулевым смещением
> курсора — `SnapToGrid()`/`SaveWindowsState()` при этом вызываются, но безвредно (позиция не меняется);
> второй `MouseLeftButtonDown` (`ClickCount == 2`) сворачивает панель через `ToggleRollUp()`, это норм.

---

## Фаза 3. Внешний вид и настройки (ADR-002, реализация)

ADR-002 описал роль окна настроек; здесь — наполнение.

- [x] Поля внешнего вида в `ChildWindowModel` (теперь `INotifyPropertyChanged`): `BackgroundColor`,
      `BackgroundOpacity`, `TextColor`, `IconSize`, `ViewMode` (enum `PanelViewMode`). `Title` тоже уведомляет.
- [x] Привязка в `ChildWindow.xaml`: фон — `MultiBinding` (цвет+прозрачность→alpha, иконки не блёкнут),
      текст — `HexBrush`. Захардкоженные цвета убраны. Конвертеры в `UI/Converters`.
- [x] Раздел «Настройки выбранной панели» в `MainWindow.xaml`: заголовок, цвет фона/текста (hex + превью),
      прозрачность, размер иконок, вид (список/плитка). Правки идут в ту же INPC-модель → панель меняется вживую.
- [x] Глобальные дефолты (цвет фона/текста, прозрачность, размер иконок) в `IAppSettings` + `Settings`;
      новые панели берут их. Секция в окне настроек.
- [x] **Автозапуск с Windows** — `IAutostartService` (реестр `HKCU\...\Run`), чекбокс в настройках.
- [x] **Порядок групп настроек и перенос «Привязки к сетке» — закрыто 2026-07-26**
      (`docs/prompts/grid-snap-drag-and-settings-order.md`, задачи 2–3). Группы `Expander` в
      `MainWindow.xaml` идут в порядке: «Автозапуск и рабочий стол» → «Системная панель иконок» →
      «Панели на рабочем столе» → «Настройки выбранной панели» → «Дефолты для новых панелей» →
      «Снапшоты раскладки». Слайдер `GridSize`, чекбокс `SnapResizeToIconGrid` и новый слайдер
      `GridSnapDistance` (см. Фазу 2) перенесены из упразднённой отдельной группы внутрь
      «Автозапуск и рабочий стол» — смысловое обоснование: обе настройки управляют поведением
      панелей на голом рабочем столе, а не конкретной выбранной панелью.

**Готово, когда:** у каждой панели свой вид, настраиваемый из окна настроек; есть глобальные дефолты и автозапуск.

> Заметки: единый источник правды для заголовка/вида — `ChildWindowModel` (та же сущность в окне настроек
> и в живой панели, поэтому правки применяются мгновенно). `IconSize` влияет на вид-плитку (список — фикс. 16px).
> Цвет вводится hex-строкой + превью (нативного color-picker в WPF нет; полноценный пикер — на будущее).
> Переименование панели в окне настроек обходит проблему фокуса из Фазы 2 (там `WS_EX_NOACTIVATE`).

---

## Фаза 4. Фирменные фишки Fences

См. [ADR-003](../adr/ADR-003-desktop-gestures-and-capture.md) — подход и компромиссы.

- [x] **Создание панели лассо**: правый drag по десктопу → панель в этой области. Реализовано через
      глобальный хук `WH_MOUSE_LL` (`DesktopGestureService`), без блокирующего оверлея; контекстное меню
      при состоявшемся drag подавляется.
- [x] **Скрытие/показ иконок** — `DesktopIconsService` (`ShowWindow` над `SHELLDLL_DefView`). Триггеры:
      двойной клик по пустому десктопу (через хук + кросс-процессный `LVM_HITTEST` для проверки пустоты)
      и пункт трея «Скрыть / показать значки рабочего стола».
- [x] **Скрыть/показать иконки рабочего стола** — `IDesktopIconsService.SetIconsVisible()`/`Toggle()`,
      единый переключатель на весь `SHELLDLL_DefView` (`ShowWindow`). Отдельный пункт в трее.
      Выборочный режим («скрыть только файлы/ярлыки, оставить папки») рассматривался — см.
      [ADR-006](../adr/ADR-006-hide-files-only-lvm-position.md) — и осознанно отклонён 2026-07-26:
      решено оставить только два состояния (показать/скрыть всё), без выборочного скрытия по типу
      иконки.
- [x] **Захват иконок** — `SourceFolderPath` в `ChildWindowModel`: панель-портал мирит содержимое папки.
      Пункт трея «Собрать значки рабочего стола» = панель папки Desktop (вид-плитка) + скрытие реальных иконок.

> **Устарело, уточнено 2026-09-26 (ADR-031).** Пункты трея «Скрыть/показать значки» и «Собрать значки
> рабочего стола», описанные выше, были убраны из трея в v0.2.0 (окно настроек взяло на себя чекбокс
> «Show desktop icons», трей — только «Настройки»/«Выход»). ADR-031 вернул в трей checkable-пункт
> **«Show desktop icons»** (синхронизирован с реальной видимостью через `ITrayIconManager
> .SetDesktopIconsVisible`/`ToggleDesktopIconsRequested`) и одновременно убрал одноимённый чекбокс из
> окна настроек — это сессионная команда, не преференция. «Собрать значки рабочего стола» («Захват
> иконок» выше) в трей **не вернулся**: Folder Portal на папку Desktop настраивается через диалог
> «Customize panel…» (Browse…), отдельного пункта трея для него нет.

**Готово, когда:** панель создаётся жестом, иконки десктопа прячутся, панель умеет показывать реальные иконки рабочего стола.

> ℹ️ Manual verify — весь нативный код Фазы 4 (глобальный `WH_MOUSE_LL`, кросс-процессный `LVM_HITTEST`)
> протестирован пользователем на Windows 11 24H2, 2026-09-27; признано достаточным, Windows 10 отдельно
> не проверяется. Захват через папку не отслеживает изменения вживую (это Фаза 5, `FileSystemWatcher`) и
> не прячет оригиналы сам — за это отвечает скрытие иконок (это уже реализованное поведение, не пробел
> верификации).

---

## Фаза 5. Продвинутое (после паритета)

- [x] **Folder Portals** — панель live-отражает содержимое реальной папки (FileSystemWatcher).
      `IFolderWatcher`/`FolderWatcherFactory`, дебаунс 300 мс, фильтр desktop.ini/hidden, guard на shutdown.
      `SelectSourceFolderCommand`, блокировка ручных операций в портале. Persistence через `SourceFolderPath`.
      **Команда удалена 2026-09-26 (ADR-031):** портал назначается из диалога «Customize panel…»
      (Browse…/Clear), реактивно на изменение модели — см. раздел «Переустройство окна настроек
      (ADR-031)» ниже.
- [x] **Страницы / swipe** между раскладками панелей.
      `PageId` в `ChildWindowModel`, `IPageManager`/`PageManagerService`. UI-полоска, Ctrl+PgDown/PgUp.
      Persistence в `IAppSettings.Pages`/`CurrentPage`.
- [x] **Снапшоты раскладки** — ручное сохранение/восстановление расположения.
      `LayoutSnapshot` (Core), `ILayoutSnapshotService`, `LayoutSnapshotService`. JSON round-trip, max 10.
      `LayoutSnapshotsViewModel` + UI в MainWindow.
- [x] **Multi-monitor** — корректная привязка панелей к мониторам, обработка смены конфигурации.
      `MonitorInfo` (Abstractions), `IMonitorService`/`MonitorService` — EnumDisplayMonitors, WM_DISPLAYCHANGE.
      `MonitorDeviceId` в `ChildWindowModel`, перенос панелей на primary при отключении монитора.

---

## Фаза 6. Системная панель иконок (System Icons Panel)

- [x] **SystemIconsPanel** — вертикальная панель системных иконок у левого края рабочего стола.
      Второй независимый тип desktop-window (не является ChildWindow/Fence).
      Core: `SystemIconKind`, `SystemIconDescriptor`.
      Abstractions: `ISystemShellService`, `RecycleBinState`.
      Services: `SystemShellService` (PIDL, IContextMenu COM, SHQueryRecycleBin, poll-таймер).
      UI: `SystemIconsPanelModel`, `SystemIconItemModel`, `SystemIconSize` (без инфикса Panel),
      `SystemIconsPanelViewModel`, `SystemIconsPanelWindow`,
      `ISystemIconsPanelManager`/`SystemIconsPanelManager`.
      Persistence: секция `SystemIconsPanel` в JSON через `IAppSettings.SystemIconsPanel`.
      Z-order: переиспользование `IDesktopIntegrationService.EmbedInDesktop` (WorkerW/Progman).
      Покрытие: 29 unit-тестов зелёные.
      См. [ADR-007](../adr/ADR-007-system-icons-panel-window-type.md).

**Готово, когда:** панель показывает системные иконки на десктопе, ПКМ открывает нативное Shell-меню,
двойной клик открывает объект, Корзина показывает индикатор заполненности, порядок иконок сохраняется.

> ℹ️ Manual verify — выполнено пользователем на Windows 11 24H2, 2026-09-27; признано достаточным
> (Windows 10 отдельно не проверяется). Исторический чек-лист из 11 пунктов — см. ADR-007 «Manual verify».

---

## Фаза 7. Производительность, память и отказоустойчивость

Источник — [docs/PERFORMANCE_AUDIT.md](PERFORMANCE_AUDIT.md) (аудит от 2026-07-27, 11 находок, только
анализ на тот момент). Мастер-план — [docs/PLANS/phase-7-performance-hardening.md](PLANS/phase-7-performance-hardening.md)
сгруппировал находки в 6 независимых по файлам блоков (A–F), каждый прошёл конвейер реализация → тесты →
code-quality-ревью (один фикс-цикл на волну) → fences-ux-parity-ревью (без блокирующих находок).
Архитектурные решения — [ADR-012](../adr/ADR-012-performance-and-resilience-hardening.md). Билд и тесты
зелёные: 324/330 passed, 6 failed — все предсуществующие, не относящиеся к этой фазе.

- [x] **Блок A — Кисти конвертеров** (аудит п.4). `HexBrushConverter`/`ColorOpacityBrushConverter`:
      замороженные (`Freeze()`), переиспользуемые кисти вместо новой на каждый вызов; ограниченный
      кеш (256 записей, нормализованный по регистру/пробелам ключ) вместо неограниченного роста.
- [x] **Блок B — Защита колбэка глобального хука** (аудит п.9). `DesktopGestureService.HookCallback`
      целиком под `try/catch`, `CallNextHookEx` вызывается на всех путях, `swallow` при исключении
      всегда `false`. Чистая машина состояний жеста вынесена в отдельный класс без P/Invoke
      (`DesktopGestureStateMachine`) — расширение прецедента [ADR-011](../adr/ADR-011-manual-drag-with-magnetic-grid-snap.md)
      (`GridSnap`) на сам нативный хук.
- [x] **Блок C — Persistence панелей** (аудит п.8, 10, 11). Двухуровневая политика: `SaveWindowsState()`
      теперь только планирует дебаунсенную (500 мс) запись через новый `SaveScheduler`, а структурные
      изменения (создание/удаление панели, снапшот-рестор, выход) используют новый синхронный
      `SaveWindowsStateNow()`. Per-item изоляция при загрузке/синхронизации (`CreateEachIsolated`/
      `SyncEachIsolated`), бэкап битого состояния перед перезаписью, запись никогда не пробрасывает
      исключение.
- [x] **Блок D — Конвейер коллекций ViewModel** (аудит п.2, 5, 6). O(1) регистронезависимый индекс
      дедупликации в `BaseViewModel` вместо O(n)-сканирования; batch `Reset`-уведомление вместо
      `Clear()`+N×`Add()` (новый `ObservableRangeCollection<T>`); `ChildWindowViewModel` отписывается
      от `Model.PropertyChanged` в `Dispose()` и при переподключении к другой модели, `LoadFromModel`
      переведён на `ReplaceAll`.
- [x] **Блок E — Память в Services** (аудит п.1, 3). `IconProvider`: неограниченный
      `ConcurrentDictionary` заменён на ограниченный (1024) LRU-кеш с freshness-check по
      `LastWriteTimeUtc` (только для расширений со вшитой иконкой — `.exe`/`.dll`/`.ico`/`.lnk`/`.cur`/
      `.url`, не для папок). `NotifyIconAdapter.SetIcon`/`Dispose` больше не текут нативным GDI-хэндлом
      иконки трея. Все `ImageSource`, покидающие Services-слой, теперь `Freeze()`-нуты — новый инвариант,
      см. ADR-012.
- [x] **Блок F — Манифест `PanelItemStorageService`** (аудит п.7). Авторитетный кеш манифеста в памяти
      с write-through вместо перечитывания всего JSON на каждую операцию (drop N файлов: было N чтений
      + N записей → стало 1 чтение + N записей). Запись в манифест теперь идёт **до** деструктивного
      шага в `MoveIn`/`MoveInDirectory` (защита origin path от потери при краше, дополняет
      [ADR-009](../adr/ADR-009-drag-drop-move-semantics.md)), с защитой от отравления кеша транзиентным
      сбоем чтения и от продолжения деструктивного шага при сбое самой записи.

**Готово, когда:** все 11 находок аудита закрыты, публичные контракты `IIconProvider`/
`IPanelItemStorageService`/`IWindowManager` (кроме добавленных методов) не изменились, `dotnet build` +
`dotnet test` зелёные.

> ℹ️ Manual verify — выполнено пользователем на Windows 11 24H2, 2026-09-27 (включая блок B —
> глобальный хук); признано достаточным, Windows 10 отдельно не проверяется. Исторический полный
> список — [docs/PLANS/phase-7-performance-hardening.md](PLANS/phase-7-performance-hardening.md),
> «Сводный список manual verify».

---

## Фаза 8. UX-паритет значков: выделение и переименование

Источник — мастер-план [docs/PLANS/icon-selection-and-rename.md](PLANS/icon-selection-and-rename.md)
(2026-08-15, две связанные UX-фичи паритета с Fences/Проводником, реализованные раздельными блоками
A–D с явными зависимостями). Архитектурные решения — [ADR-013](../adr/ADR-013-panel-item-selection-and-rename.md).
Реализация завершена, покрыта тестами и прошла два раунда `code-quality-reviewer`/
`fences-ux-parity-reviewer` с фиксами (в т.ч. изменение дисциплины перезаписи manifest при
переименовании managed-элемента на не-атомарную add-then-remove — см. ADR-013 Решение 5).

- [x] **Выделение (подсветка) значка по клику в обоих видах панели, снятие по клику в пустоту.**
      `WrapViewTemplate` переведён с `ItemsControl` на `ListBox` (общий `ItemsPanel=WrapPanel`), оба
      вида (список/плитка) используют общий `PanelItemContainerStyle` с `ControlTemplate`, реагирующим
      только на `IsSelected` — намеренно без оглядки на `IsSelectionActive`, т.к. панель несёт
      `WS_EX_NOACTIVATE` и никогда не становится активным окном (дефолтная тема `ListBoxItem` рисовала
      бы подсветку невидимой). `BaseViewModel.SelectedFile` двусторонне привязан к `SelectedItem`;
      клик в пустое место панели снимает выделение (`ChildWindow.xaml.cs`,
      `FileListHost_PreviewMouseLeftButtonDown`); ПКМ по значку выделяет его до показа контекстного
      меню. Выделение восстанавливается по пути после перестройки `FileItems`
      (`BaseViewModel.ReplaceFileItems`, компенсация известного побочного эффекта `Reset` из ADR-012).
      Multi-select — осознанно вне объёма (отдельный тикет, требует множественных команд меню).
- [x] **Отображение оригинального имени вместо GUID-имени + inline-переименование значка (F2 /
      контекстное меню), физическое переименование на диске, синхронизация manifest.**
      `PanelItemStorageService` перенёс GUID из имени файла в имя каталога
      (`PanelItems\{guid}\{originalName}`) — `FileItem.Name`/`DisplayName` теперь сам по себе
      оригинальное имя, без нового персистентного поля и без изменения схемы `ChildWindowsState`.
      Legacy-элементы старой плоской раскладки получают только косметическую деградацию отображения
      (`ManagedItemName.StripLegacyGuidSuffix`, `FileItem.DisplayName`-override), без миграции на
      старте; при переименовании такой элемент физически переезжает в новую раскладку. Новый
      `IFileItemRenameService` (Services.Abstractions/Services/Tools) — единственная точка мутации
      имени: managed-элементы делегируются в новый `IPanelItemStorageService.RenameManaged`
      (возвращает `RenameResult`, переиспользуя тип из `IFileItemRenameService` — различает
      `Renamed`/`InvalidName`/`NameTaken`/`NotFound`/`Failed`), остальные — прямой `File.Move`/
      `Directory.Move`. Manifest managed-элемента при переименовании перезаписывается не атомарным
      re-key, а двухфазно: новый ключ добавляется **до** физического `Move`, старый ключ удаляется
      только **после** подтверждённого успеха `Move` — устраняет риск потери `originalPath` при сбое
      `Move` после успешной перезаписи манифеста (ADR-013 Решение 5). Inline-редактирование — состояние
      на `ChildWindowViewModel` (`EditingItem`/`EditingName`), не на `FileItem`; `RenameEditBehavior`
      (новый attached behavior) воспроизводит commit/cancel-дисциплину заголовка панели для
      файлового `TextBox` внутри `DataTemplate`. Переименование в портальной панели разрешено (в
      отличие от `RemoveFileCommand`).

**Готово, когда:** клик подсвечивает значок в обоих видах одинаково и независимо от активности окна;
файлы/папки, перенесённые в панель, отображаются под оригинальным именем и на диске; «Переименовать»
доступно из контекстного меню и по `F2`, инлайн-редактирование коммитит по Enter/потере фокуса и
отменяется по Esc; переименование физически переименовывает файл/папку для managed, обычных и
портальных элементов; переименование managed-элемента корректно перевешивает manifest, и «Убрать из
панели» после этого возвращает файл под новым именем; невалидное/занятое имя не теряют файл и
сообщаются ненавязчивым хинтом (не `MessageBox`); имя переживает перезапуск приложения.
`dotnet build`/`dotnet test` зелёные (без роста числа предсуществующих падений сверх известных 6, см.
Фазу 7); публичные контракты не изменились, кроме аддитивного `IPanelItemStorageService.RenameManaged`.

> ℹ️ **Manual verify — выполнено пользователем на Windows 11 24H2, 2026-09-27; признано достаточным**
> (Windows 10 отдельно не проверяется), включая R1 (CRITICAL, ввод текста в inline-`TextBox` под
> `WS_EX_NOACTIVATE`). Исторический полный чек-лист из 13 пунктов — план
> [docs/PLANS/icon-selection-and-rename.md](PLANS/icon-selection-and-rename.md) §9.

---

## Фаза 9. Сортировка и ручной порядок значков панели

Источник — мастер-план [docs/PLANS/panel-item-sorting.md](PLANS/panel-item-sorting.md) (2026-08-16,
две неразрывно связанные UX-фичи паритета с Fences: режим сортировки + drag-to-reorder, реализованные
раздельными Блоками A–D с явными зависимостями). Архитектурные решения —
[ADR-014](../adr/ADR-014-panel-item-sorting-and-manual-order.md). Реализация завершена, покрыта
тестами и прошла **два параллельных раунда** `code-quality-reviewer`/`fences-ux-parity-reviewer` с
фиксами (в т.ч. явный флаг-подавитель `FilesChanged` вокруг переключения `SortMode` в `ReorderItem`,
явный `PropertyChanged` для вычисляемого `EffectiveSortMode`, и критический фикс Win32 OLE
drag&drop-контракта в `FileListHost_DragOver`/`FileListHost_Drop` — обработчики выходят до любого
касания `e.Effects`/`e.Handled` для чужого формата, а не просто не ставят `e.Handled` — см. ADR-014,
раздел «Критическая деталь реализации Win32 OLE drag&drop»).

- [x] **Режим сортировки панели (`PanelSortMode`: Manual / Name)** — настройка в окне настроек
      (`ComboBox` «Sort order», задизейбленный с тултипом для Folder Portal) и в контекстном меню
      заголовка панели (секция «Arrange icons», чекируемые пункты «Manual»/«By name»). Сортировка по
      имени — немутирующая проекция (`ChildWindowViewModel.ApplyFilterAndSort`, единая точка для всех
      путей перестройки): папки вперёд, затем `NaturalStringComparer` (собственный, без P/Invoke;
      числовые фрагменты как числа произвольной длины, `file2` перед `file10`) по `DisplayName`, с
      тай-брейком по `Path`. Ручной порядок хранится в уже существующем `FilePaths`/`_allItems` — без
      нового персистентного поля, без изменения формы схемы JSON, только аддитивное поле `SortMode`
      (`Manual = 0` ⇒ полная обратная совместимость поведения: старый JSON без этого поля
      десериализуется в `Manual`, старые панели не перетасовываются). Портальные панели: `Manual`
      недоступен, `EffectiveSortMode` всегда `Name` — чинит попутно старый дефект, при котором новый
      файл, пойманный watcher'ом портала, всегда «прилипал» к концу списка вместо алфавитного места.
- [x] **Drag-to-reorder значков внутри панели** (оба вида — список и плитка), системный порог начала
      жеста (`SystemParameters.MinimumHorizontal/VerticalDragDistance`). `BaseViewModel.MoveItem` —
      трансляция видимого индекса вставки в индекс полного набора через якорение к видимому соседу:
      при активном `ContentFilter` скрытые элементы не теряются и не «схлопываются» в конец, меняется
      только позиция перетаскиваемого элемента относительно видимых соседей. Точечное
      `ObservableCollection<T>.Move`-уведомление вместо `Reset` — скролл и выделение переживают
      перетаскивание. Первое ручное перетаскивание в режиме `Name` автоматически переводит панель в
      `Manual`, предварительно материализуя текущий отсортированный порядок в `_allItems`
      (`BaseViewModel.SortAllItems`), так что переключение визуально невидимо (без «прыжка» списка) —
      опирается на ранний выход `BaseViewModel.ReplaceFileItems` при ссылочно идентичной проекции.
      Payload перетаскивания — путь файла (строка), не сам объект `FileItem`; drop вне «своей» панели
      или в Folder Portal безопасно отклоняется.

**Готово, когда:** `ComboBox` «Sort order» в окне настроек и секция «Arrange icons» в контекстном меню
заголовка отражают и меняют режим сортировки живой панели, применяясь мгновенно и переживая
перезапуск; `SortMode.Name` сортирует видимый список (папки вперёд, natural-order по отображаемому
имени) и не изменяет `FilePaths` — цикл `Manual → Name → Manual` возвращает прежний ручной порядок
в точности; панель, сохранённая до этой фичи, показывает элементы в том же порядке, что и раньше;
значок перетаскивается мышью в обоих видах и встаёт на позицию drop'а; перетаскивание в режиме `Name`
переводит панель в `Manual`, сохраняя видимый порядок без скачка; перетаскивание при активном
`ContentFilter` не теряет и не переупорядочивает скрытые элементы; новый порядок переживает
перезапуск; drag-reorder недоступен в портальной панели и во время активного inline-переименования;
существующий drag&drop файлов с рабочего стола/из Проводника в панель продолжает работать без
изменений; перетаскивание не сбрасывает позицию скролла и выделение. `dotnet build`/`dotnet test`
зелёные (без роста числа предсуществующих падений сверх известных 6, см. Фазу 7); публичные контракты
`IPanelItemStorageService`/`IFileItemRenameService`/`IWindowManager`/`IIconProvider` не изменились;
схема JSON `ChildWindowsState` расширена только аддитивным полем `SortMode`.

> ℹ️ **Manual verify — выполнено пользователем на Windows 11 24H2, 2026-09-27; признано достаточным**
> (Windows 10 отдельно не проверяется), включая R1 и R2 (CRITICAL, входящий drag&drop с рабочего стола
> и `DoDragDrop` из окна с `WS_EX_NOACTIVATE`). Исторический полный чек-лист из 17 пунктов — план
> [docs/PLANS/panel-item-sorting.md](PLANS/panel-item-sorting.md) §9 и ADR-014.

---

## Полировочный батч `polishing/ui-1` (вне формальных Фаз)

Источник — мастер-план [docs/PLANS/ui-polishing-1.md](PLANS/ui-polishing-1.md) (2026-09-01, ветка
`polishing/ui-1`): шесть связанных UI-полировочных правок из пользовательского запроса «как есть»,
сгруппированных в блоки A–E, каждый прогоняется по полному конвейеру (реализация → тесты →
code-quality/fences-ux-parity ревью → `architecture-documenter`). Это не пункт формальной
ROADMAP-фазы, а отдельный полировочный батч.

- [x] **Блок A** — сброс `SelectedFile` при любом другом клике (координатор выделения между панелями).
      Внутриоконный сброс (заголовок/кнопки/resize) + новый межпанельный синглтон
      `IPanelSelectionCoordinator`/`PanelSelectionCoordinator` (`DesktopOrganizer.UI.Services`),
      реентрантность закрыта флагом-подавителем + идемпотентной заявкой того же владельца. Архитектурное
      решение — дополнение к [ADR-013](../adr/ADR-013-panel-item-selection-and-rename.md) (Решение 7).
      `dotnet build`/`dotnet test` зелёные (623 passed / 6 failed — все 6 падений предсуществующие).
- [x] **Блок B** — геометрия Grid-режима панели, top-align иконки, ревизия «desktop grid size».
      Разделены сетка позиции окна (`IAppSettings.GridSize`, сохранена, лейбл/тултип переименованы в
      «Panel position grid») и сетка раскладки элементов (`GridMetrics`, из `IconSize`); зафиксирован
      root-cause среза рамки выделения на первой колонке/строке (`SelectionBorder`'s компенсирующий
      отрицательный `Margin` рисовал штрих за arrange-боксом `ListBoxItem`) и фикс B-F1 (расщепление
      6px-отступа на 5px margin шаблона + 1px рамка, footprint `CellSize` не изменился); иконка прижата
      к верхней границе слота (`VerticalAlignment="Top"`, фиксированная `Grid`-обёртка). Архитектурное
      решение, research-сравнение с Explorer/WinUI/Fences и отклонённые альтернативы B-F2/B-F3 —
      [ADR-015](../adr/ADR-015-panel-icon-grid-geometry.md). `dotnet test` — все `GridMetricsTests`/
      `ChildWindowReorderGeometryTests` зелёные (6 предсуществующих падений в других файлах не в счёт).
- [x] **Блок C** — порядок кнопок заголовка панели (Sort → View → RollUp), новая иконка View
      (`ViewModeGlyphConverter`, целевой режим как глиф) и постоянная подсветка кнопки сортировки через
      `IsSortedByName` (`PanelHeaderToggleButtonStyle`, теми же hex-токенами, что выделение элемента);
      `SortModeGlyphConverter` переименован в `SortModeTooltipConverter` (ветка глифа удалена как мёртвая
      после того, как глиф Sort стал статичным). `dotnet build`/`dotnet test` зелёные.
- [x] **Блок D** — выделение в боковой панели (`SystemIconsPanel`) тем же визуальным языком, что и
      fence-панель: миграция `ItemsControl → ListBox` + переиспользование `PanelItemContainerStyle` без
      дублирования hex-токенов, подключение к координатору Блока A. Попутно закрыт предсуществующий
      мёртвый код — drag-reorder боковой панели искал `FindAncestorOrSelf<Button>` в шаблоне, который
      `Button` больше не содержит с версии «FIX 5» (недовыполненный пункт 11
      [ADR-007](../adr/ADR-007-system-icons-panel-window-type.md), теперь закрыт). Архитектурное решение —
      дополнение к [ADR-013](../adr/ADR-013-panel-item-selection-and-rename.md) (Решение 7).
      `dotnet build`/`dotnet test` зелёные (635 passed / 6 failed — все 6 падений предсуществующие).
- [x] **Блок E — «Показывать боковую панель на всех виртуальных рабочих столах».** Изначальная
      реализация (`IVirtualDesktopService`/`VirtualDesktopService`, COM `IVirtualDesktopManager`,
      «Follow-me», [ADR-016](../adr/ADR-016-virtual-desktop-visibility.md)) оказалась нерабочей: панель
      была встроена в `WorkerW` (`EmbedInDesktop`/`SetParent`) — единый слой на все виртуальные столы,
      который менеджер столов не отслеживает, поэтому чекбокс был no-op в обе стороны при любом
      положении. **Пересмотрено и исправлено** — боковая панель перестала быть `WorkerW`-child и стала
      top-level окном (`ApplyDesktopWindowStyles` строго после `Show()`, spike подтвердил, что
      порядок применения стилей относительно `Show()` критичен); ON переиспользует poll-«follow-me»
      ADR-016 на реально отслеживаемом окне; OFF закрепляет панель на «Desktop 1» через
      `ResolveAnchorDesktopId` (systemDefault ?? persisted ?? panelCurrent) с read-only реестровым
      зондом (кросс-валидация, тихий откат на эвристику). `ChildWindow`/`WindowManager`/fence-панели по
      прежнему не затронуты ни в исходной, ни в исправленной версии. Ревью нашло и закрыло 5
      MAJOR-находок (некорректный circuit breaker/self-heal якоря в OFF-режиме, игнорирование
      `IsSupported`); добавлено 24 unit-теста (780→804, все зелёные). Архитектурное решение, протокол
      spike и полный manual-verify список —
      [ADR-019](../adr/ADR-019-sidebar-top-level-virtual-desktop.md) (**supersedes**
      [ADR-016](../adr/ADR-016-virtual-desktop-visibility.md), статус которого — `Superseded by
      ADR-019`). `dotnet build`/`dotnet test` зелёные (804 passed, без роста числа
      предсуществующих падений/варнингов).
      **Follow-up (тот же батч, 2026-09-02):** по итогам визуальной проверки пользователь попросил
      убрать `Topmost="True"` (панель перекрывала обычные окна) — trade-off ADR-019 пересмотрен.
      Взамен `SystemIconsPanelWindow` больше не `Topmost`; периодический `DispatcherTimer` (300 мс,
      `SystemIconsPanelManager`) вызывает новый `IDesktopIntegrationService.SendToBackOfZOrder`,
      который явным якорем (`FindWindow("Progman")` + `GetWindow(GW_HWNDPREV)`, fallback
      `HWND_BOTTOM`) удерживает панель на один слот выше `Progman` — не буквальный `HWND_BOTTOM`,
      который уже был отклонён для fence-панелей в
      [ADR-001](../adr/ADR-001-desktop-integration.md) («Вариант 2», 2026-06-16) именно за
      негарантированность позиции «над обоями». `SWP_NOOWNERZORDER` обязателен — без него скрытое
      owner-окно WPF (`ShowInTaskbar="False"`) либо создаёт off-by-one, либо панель и owner
      бесконечно меняются местами каждый тик (оба исхода воспроизведены на эмпирическом спайке).
      Реактивный `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` рассмотрен и отклонён — второй глобальный
      хук в процессе, где уже есть `WH_MOUSE_LL`. Архитектурное решение, полный протокол спайка и
      обновлённый manual-verify список — [ADR-020](../adr/ADR-020-sidebar-zorder-back-without-topmost.md).

**Готово (весь батч, на уровне кода/тестов):** все шесть задач пользователя закрыты во всех пяти блоках
A–E (§10 плана), `dotnet build`/`dotnet test` зелёные без роста числа предсуществующих падений, все
новые UI-строки на английском. Единственное, что остаётся вне автономного цикла — полный manual-verify
список ниже (интерактивная Windows-сессия, не выполнялась ни для одного блока).

> ℹ️ **Manual verify — выполнено пользователем на Windows 11 24H2, 2026-09-27, для всех пяти блоков
> (A–E); признано достаточным**, Windows 10 отдельно не проверяется (в т.ч. Wallpaper Engine/Lively/
> Rainmeter совместимость и мультимонитор — не переверифицированы отдельно, риск принят). Исторический
> полный сводный список — `docs/PLANS/ui-polishing-1.md` §8 (17 пунктов) и
> [ADR-015](../adr/ADR-015-panel-icon-grid-geometry.md)/[ADR-019](../adr/ADR-019-sidebar-top-level-virtual-desktop.md)/[ADR-020](../adr/ADR-020-sidebar-zorder-back-without-topmost.md).

---

## Anti-overlap панелей при перетаскивании (follow-up на ветке `polishing/ui-1`, вне формальных Фаз)

Источник — отдельный пользовательский запрос от 2026-09-01 (скриншот с визуально перекрывающимися
панелями `personal`/`work`), **не** часть батча `docs/PLANS/ui-polishing-1.md` (пересечение по
файлам — только `ChildWindow.xaml.cs`, разные методы). План —
[docs/PLANS/panel-anti-overlap.md](PLANS/panel-anti-overlap.md).

- [x] **Жёсткая блокировка перекрытия панелей при живом перетаскивании.** Панель физически не может
      пересечь границу другой видимой панели во время драга («твёрдая стена» — AABB collision
      response, а не снаппинг к свободному месту и не раздвигание соседей); курсор может «убежать»
      вперёд, панель остаётся у стены. Всегда включено безусловно, без настройки и без
      Alt-байпаса — оба варианта явно отклонены пользователем. Новый чистый helper
      `PanelCollision` (`UI/Models`) — раздельное разрешение осей X/Y (скольжение вдоль стены) +
      swept-тест против туннелирования при резком движении мыши, эпсилон-порог 0.5 DIP против
      дрожания на границе. Конвейер: raw-курсор → `GridSnap` (ADR-011) → `ClampAgainstObstacles` →
      `Window.Left/Top`; `SnapToGrid()` на mouse-up тоже уважает анти-оверлап через условный
      per-axis снап (`CreatesNewOverlap`), чтобы финальное защёлкивание к сетке не впрыгнуло в
      соседа. Источник границ соседей — новый `IWindowManager.CollectOtherPanelBounds`, живая
      геометрия окна (`Window.Left/Top/ActualWidth/ActualHeight`), не модель (`ChildWindowModel`
      хранит развёрнутую высоту для свёрнутой панели). **Правило R1:** пред-существующие (сохранённые
      до этой фичи) пересечения панелей не исправляются при загрузке — блокировка действует только на
      новое перетаскивание; из пересекающегося состояния можно выйти, обратно нельзя, пока не
      разошлись полностью. Ресайз-анти-оверлап вынесен в бэклог (конфликт с квантизацией
      `GridMetrics`/ADR-015, требует отдельного решения). Архитектурное решение, отклонённые
      альтернативы (push-away, снаппинг к свободному месту, настройка, Alt-байпас) —
      [ADR-017](../adr/ADR-017-panel-anti-overlap-on-drag.md). `dotnet build`/`dotnet test` зелёные.

> ℹ️ **Manual verify — выполнено пользователем на Windows 11 24H2, 2026-09-27; признано достаточным**,
> Windows 10 отдельно не проверяется. Исторический полный список —
> [ADR-017](../adr/ADR-017-panel-anti-overlap-on-drag.md) и `docs/PLANS/panel-anti-overlap.md` §8.

---

## Адаптивная раскладка иконок в панели (follow-up на ветке `polishing/ui-1`, вне формальных Фаз)

Источник — отдельный пользовательский запрос от 2026-09-02 (дословная техническая математика на замену
жёсткой uniform-grid раскладки Grid-режима адаптивной, Fences parity), не часть предыдущих батчей этой
ветки. План — [docs/PLANS/adaptive-icon-grid.md](PLANS/adaptive-icon-grid.md).

- [x] **Адаптивное равномерное распределение иконок в Grid-режиме.** Полный ряд растягивает зазоры
      между иконками равномерно на весь остаток ширины, поля от края фиксированы (`m = x/2`); неполный
      (последний) ряд прижат к левому краю с минимальным зазором `pmin`, пустой хвост — справа.
      Вертикальные зазоры между рядами **никогда не растягиваются** (нет аналога «доступной высоты» —
      контент скроллится). Новый чистый helper `AdaptiveGridLayout` (`UI/Models`, четвёртый рядом с
      `GridMetrics`/`GridSnap`/`PanelCollision`) + новая кастомная `Panel`-наследник
      `AdaptiveIconGridPanel` (`UI/Controls`, новая папка), заменяет `WrapPanel` только в
      `GridViewTemplate` (`ListViewTemplate` не тронут). `GridMetrics.Quantize` удалён (единственный
      источник правды на округление теперь `AdaptiveGridLayout.CountForExtent`/`SnapExtent`); добавлены
      `LabelHeight`, `FileListVPadding`, `RowHeight` (заодно чинит пред-существующий off-by-4: в высоту
      контента не учитывался нижний `Margin.Bottom=4` `FileListHost`). Снап ширины/высоты панели
      (`SnapWidthToGrid`/`Resize_DragDelta`) переведён на ось-агностичный `AdaptiveGridLayout.SnapExtent`
      (`_stepX/_stepY` → `_snapCellWidth/_snapCellHeight`); для панелей с ≥2 колонками геометрия при
      загрузке не меняется, но **однoколоночные панели — известное исключение**: минимальная точка снапа
      переехала с `1×x` на `2×x` (при `IconSize=48` — 120px→168px, при `IconSize=96` — удвоение
      156px→312px при первом взаимодействии со снапом). Архитектурное решение, отображение символов
      спека на код (D-0, `x_v ≠ x` — ячейка не квадратная), MAJOR-баг M-1 (`RowCapacity` терял колонку
      при недостаче ширины <1px из-за дробного `IconSize`/DPI — исправлен допуском `LayoutTolerance`,
      `Math.Ceiling` при снапе, `IsSnapToTickEnabled="True"` на слайдерах `IconSize`) и MAJOR-trade-off
      M-2 (появление вертикального скроллбара может на пороге переполнения сдвинуть число колонок в
      ряду — принят как есть в этой итерации, варианты (a)/(b) готовы к реализации без пересмотра
      архитектуры) — [ADR-018](../adr/ADR-018-adaptive-icon-grid-layout.md). `dotnet build`/`dotnet
      test` зелёные (754/754, было 712 до прохода `tdd-test-engineer`).

> ℹ️ **Manual verify — выполнено пользователем на Windows 11 24H2, 2026-09-27; признано достаточным**,
> включая порог появления/исчезновения вертикального скроллбара (M-2). Windows 10 отдельно не
> проверяется. Исторический полный список (19 пунктов) —
> [ADR-018](../adr/ADR-018-adaptive-icon-grid-layout.md) и `docs/PLANS/adaptive-icon-grid.md` §12.

- [x] **Уточнение (та же ветка, после первого прохода выше): минимальный зазор `pmin` и явный минимум
      ширины панели.** `AdaptiveGridLayout.DefaultMinGap` изменён `0 → 4` — минимальный зазор между
      ячейками, симметрично применяемый к обеим осям (`AdaptiveIconGridPanel.MinGap` кормит и
      горизонтальный зазор неполного ряда, и вертикальный зазор между рядами), видимый шов между
      иконками вырос с ~12px до ~16px. Это **ломает** ранее задокументированный в ADR-018 факт «`Extent
      (k)` всегда кратен `CellSize`» — теперь верно только для `k = 1`; сохранённые панели с 2+
      колонками/рядами получат новую точку снапа при первом повторном срабатывании снапа. Минимальная
      ширина панели в Grid-режиме больше не плоская константа: `MinPanelWidth(iconSize) =
      2 * GridMetrics.CellSize(iconSize)` (через `AdaptiveGridLayout.Extent(1, ...)`) гарантирует минимум
      одну колонку при любом `IconSize`; применяется **строго только** при `ViewMode == Grid` — List-режим
      и минимум высоты (обе оси) по-прежнему используют плоскую константу, переименованную `MinPanelSize`
      → `MinPanelHeight`. Полное описание, обновлённый Manual verify (пункты 20–23) —
      [ADR-018](../adr/ADR-018-adaptive-icon-grid-layout.md).

> ℹ️ **Manual verify — выполнено пользователем на Windows 11 24H2, 2026-09-27; признано достаточным**
> (пункты 20–23, ADR-018). Windows 10 отдельно не проверяется.

- [x] **Финализация (та же ветка): рассинхрон трёх копий `120` и фикс скачка правого края при ресайзе.**
      Найден и исправлен баг: икон-грид-минимум ширины при `IconSize ∈ {16,20,24,28}` мог опускаться ниже
      жёсткого `Window.MinWidth="120"` — WPF коэрсировал `Width` обратно к 120, но `Left` уже был посчитан
      от несостыкованного меньшего значения, что при перетаскивании левого края давало скачок правого края
      ~48px. Первопричина — значение `120` было триплицировано (`ChildWindow.xaml`, `ChildWindow.xaml.cs`,
      `App.xaml.cs`) без единого источника правды. Исправлено: `GridMetrics.MinPanelSize` (public const,
      120) — единственный источник; новый публичный тестируемый `GridMetrics.MinPanelWidth(iconSize)` с
      явным `Math.Max` к этому полу; XAML-литерал `MinWidth="120"` остался (WPF-ограничение), снабжён
      комментарием на синхронизацию. Промежуточное переименование константы `MinPanelSize` →
      `MinPanelHeight` отменено (признано ошибочным на код-ревью) — итоговое имя `MinPanelSize`. Добавлены
      тесты `MinPanelSize_Is120`, `MinPanelWidth_NeverBelowMinPanelSize`,
      `MinPanelWidth_AboveFloor_ReturnsUnclampedIconGridValue`. Подробности —
      [ADR-018](../adr/ADR-018-adaptive-icon-grid-layout.md), раздел «Финализация: рассинхрон трёх копий
      `120`…».

> ℹ️ **Manual verify — выполнено пользователем на Windows 11 24H2, 2026-09-27; признано достаточным**
> (пункт 24, ADR-018). Windows 10 отдельно не проверяется.

---

## Ambient Accent Visual System (Фаза 5 пакета `docs/Refactoring/`, вне формальных Фаз этого ROADMAP)

Источник — отдельный refactoring-пакет `docs/Refactoring/` (собственная нумерация Phase 0–9, не
совпадает с Фазами этого документа), мастер-план [docs/PLANS/phase-5-ambient-accent-ui.md](PLANS/phase-5-ambient-accent-ui.md),
трекер — [docs/Refactoring/ROADMAP.md](Refactoring/ROADMAP.md) (Phase 5) и
[docs/Refactoring/PHASES/PHASE-5-ambient-accent-ui.md](Refactoring/PHASES/PHASE-5-ambient-accent-ui.md).
Ветка `ui/ambient-accent-design-tokens` (от `rc/0.4.0`). Архитектурное решение —
[ADR-025](../adr/ADR-025-ambient-accent-visual-language.md) (Accepted).

- [x] **Новый визуальный язык панелей: нейтральная поверхность + локальный семантический акцент**
      вместо сплошной заливки категорией (сознательное расхождение с буквальным дефолтом Fences, см.
      ADR-025 «Альтернативы»). Токенизированные словари (`Resources/Styles/*.xaml`), lookless
      `PanelChrome`, attached DP `PanelTheme` (единый appearance для Grid/List/Folder Portal/
      `SystemIconsPanel`), визуализация interaction-состояний Idle/Hover/Editing/Dragging/Resizing
      (`InteractionVisualState`, ADR-021's `PanelInteractionTracker` не изменён), миграция схемы
      persistence v3→v4 (`V3ToV4Migration`, `AccentColor`/`SurfaceStyle`/`SurfaceOpacity` заменяют
      `BackgroundColor`/`BackgroundOpacity`), обесцвечивание `MainWindow.xaml` (закрывает
      `docs/Refactoring/BACKLOG.md` DO-003). Реализация (`wpf-ui-builder`) прошла три полных раунда
      `code-quality-reviewer` ∥ `fences-ux-parity-reviewer` с фиксами между ними + финальный узкий
      фикс-цикл (MAJOR-1/MAJOR-2, MAJOR-A/MAJOR-B, MAJOR-5 — все исправлены, в т.ч. Dragging/Resizing
      различимость по ширине акцент-индикатора и border opacity). DoD-чеклист сверен и закрыт —
      [docs/Refactoring/PHASES/PHASE-5-ambient-accent-ui.md](Refactoring/PHASES/PHASE-5-ambient-accent-ui.md).
      Принятые trade-off'ы и известные проблемы, сознательно оставленные вне скоупа (не баги) —
      см. ADR-025: лоссивная миграция `#1E1E1E → Accent.Neutral` для дефолтных панелей (D-5),
      `Glass` = пониженная непрозрачность/усиленный border, не настоящий acrylic/DWM-blur (R-8),
      `TextColor`-долг (DO-013), `Color.Overlay.Hover` в High Contrast, активная вкладка настроек в HC
      без доп. индикатора, мёртвая ветка `"accent"` в `VisualStateToDoubleConverter`, High Contrast
      подхватывается только при старте приложения (DO-015).

- [x] **P00 — Baseline-аудит фактической геометрии** (`docs/fix-prompts/00-audit.md`,
      [docs/PLANS/workspace-grid-00-baseline.md](PLANS/workspace-grid-00-baseline.md)): только чтение
      (`src/` не менялся, добавлен один characterization-тест) — 21 writer геометрии панели,
      12 находок F-1…F-12 (origin сетки не привязан к монитору, IconSize меняет panel bounds, хром
      `PanelChrome` не учтён в снап-формулах, `MonitorDeviceId` не обновляется при межмониторном drag,
      лассо пишет px как DIP, дробный persisted `IconSize`, конфликт с ADR-018 stretch-gap), плюс
      измеренный на реальной Windows 11 icon-loading дефект (прозрачный placeholder, 20/26 сохранённых
      путей — устаревшие pre-Move). Передано в P01 как входные вопросы для ADR-028/ADR-029.
- [x] **P01 — Архитектура: ADR-028 (Workspace Grid) + ADR-029 (Icon Layout Policy)**
      (`docs/fix-prompts/01-architecture.md`): architecture-only проход, без изменений `src/`. Три
      независимые системы координат (physical monitor px / workspace panel grid DIP / internal icon
      layout DIP) впервые формально разделены и поимённо специфицированы — закрывает F-3-класс багов
      структурно. [ADR-028](../adr/ADR-028-workspace-grid.md): origin = work area монитора (закрывает
      F-1), модуль `U=8 DIP`/gap `G`, `PanelGeometryMode {LegacyFreeform, GridAligned}`, spans вместо
      сырых DIP (устойчивость к сдвигу origin при hotplug/DPI/taskbar), presets S/M/L/XL, `Alt`
      подавляет квантование на время жеста. [ADR-029](../adr/ADR-029-icon-layout-policy.md):
      `IconLayoutPolicy {LegacyStretch, FixedGap}` (amendment к ADR-018), `PanelMetrics.ContentLeftInset`
      как единая константа контракта на хром (F-3), `PanelSizingMode {Manual, AutoContent}` (владение
      F-4). Оба ADR остались `Proposed` до реализации/manual verify — см. «P09 (gate)» ниже.
- [x] **P02 — Чистая геометрия `WorkspaceGridMetrics`/`WorkspaceLayoutPolicy`** (`docs/fix-prompts/02-metrics.md`,
      [docs/PLANS/workspace-grid-02-metrics.md](PLANS/workspace-grid-02-metrics.md)): pure-math слой,
      без подключения к рантайму. `SnapPosition`/`BoundsToSpans`/`SpansToBounds`/`SafeClamp`/
      `PreferredSize`/`ClampAgainstObstacles` (gap-aware) в `src/DesktopOrganizer.UI/Models/`. **Fix-цикл
      1** — 7 MAJOR-находок: `Math.Ceiling` без допуска давал «ratchet» на round-trip DIP↔px (допуск
      0.5 DIP), `SafeClamp` мог дать позицию вне сетки, resize-перегрузка `SnapBounds` была структурно
      неверна (anchor не квантовался), план подключения для этапа 04 (system C не участвует в системе
      B), API slot↔position/gap-aware clamp/anti-overlap по целым слотам, решения оркестратора:
      интерактивный resize — `Round` (не `Ceiling`), округление `.5` — `Math.Floor(t+0.5)` (не
      banker's). **Fix-цикл 2** — R1-амнистия и gap-арифметика разделены (инфляция препятствия не имеет
      права амнистировать реальное пересечение), `SafeClamp`-перегрузки переименованы по владению
      режима. **Fix-цикл 3** (carry-over из верификаций P03/P04) — публичные члены
      `ClampResizeToWorkArea`/`RoundBoundsToSpans`/`ClampToMinimum` повышены до `public`,
      `PanelDragPipeline.CommittedSize` закрывает MAJOR верификации P04 (WPF `ActualWidth` отстаёт на
      layout-pass → Alt-resize клампился неверно).
- [x] **P02b — Persistence-фундамент: схема v4→v5** (`docs/PLANS/workspace-grid-migration.md`): единая
      версия схемы для обоих ADR (workspace grid + icon layout policy) — аддитивные `geometryMode`,
      `widthSpanUnits`/`heightSpanUnits`, `iconLayoutPolicy`, `sizingMode`; дефолты сохраняют сегодняшнее
      поведение (`legacyFreeform`/`legacyStretch`/`manual`), явно проставляются миграцией, а не
      наследуются молча. Откат старой сборки на v5-файл безопасен по конструкции (существующий rung 7
      ADR-024, «никогда не мигрирует вниз, никогда не перезаписывает» — подтверждено чтением кода, не
      гипотеза). **Fix-цикл 1** — MAJOR (`GridAligned` без валидных spans), 4 MINOR (`Enum.TryParse`,
      неверный JSON-тип ронял `Load()`, `LegacyFreeform` получал лишние spans в DTO, дефолты новой
      панели из двух рассинхронизированных копий → единый `PanelDefaults`), нет постоянной копии
      документа до миграции. **Fix-цикл 2 (последний)** — MAJOR N-2 (`WritePreMigrationBackupIfMissing`
      игнорировал advisory lock, писал неатомарно), MAJOR N-3 (`null` вместо коллекции в payload ронял
      `Load()`), MINOR-находки (типизированные enum-поля DTO, `ReconcileGridAlignedSpans` без клампа к
      минимуму, `AddPanel`/`GridAligned` давал 248×232 вместо прежних 260×300). **Финальная
      верификация** — MAJOR-1 (null-элементы коллекций).
- [x] **P03 — Единый drag/snap/collision и координаты мониторов** (`docs/fix-prompts/03-positioning.md`,
      [docs/PLANS/workspace-grid-03-04-integration.md](PLANS/workspace-grid-03-04-integration.md) §2/§2.8,
      ADR-028 «Реализация P03»): `WorkspaceLayoutPolicy` подключён к реальному пути перемещения панели.
      Конвейер `GridAligned` — `SnapPosition` от origin work area **конкретного** монитора (не от
      глобального `(0,0)`) → snap-guides ADR-026 (только Edit Layout) → gap-aware anti-overlap ADR-017
      («твёрдая стена», всегда последняя и не пробивается); `Alt` временно подавляет только квантование,
      на отпускании позиция квантуется всегда. `LegacyFreeform` — байт-в-байт прежний путь.
      `MonitorDeviceId` обновляется по центру панели на drop в обоих режимах (F-6) — панель больше не
      возвращается на DISPLAY1 после любого `WM_SETTINGCHANGE`. Restore и `MonitorsChanged` пересчитывают
      `GridAligned`-панель из её spans против текущего origin её же монитора (идемпотентно, без «jump on
      first launch»); `LegacyFreeform` идёт через нетронутый `ClampIntoMonitor`. Добавлены: явная команда
      «Align to workspace grid» (per-panel, `IsCheckable` — снятие галки возвращает `LegacyFreeform` без
      сдвига панели) и «Align all to grid» в окне настроек (с подтверждением, детерминированный порядок);
      лассо конвертирует px→DIP через `IMonitorService.GetSystemDpiScale()` (F-7); «Add panel» занимает
      первый свободный слот вместо `(0,0)` (M8). Попутно закрыт carry-over из финальной верификации P02
      fix-cycle 2: gap-aware clamp расширял препятствие по обеим осям и ломал «скольжение вдоль стены»
      (`PanelCollision.InflateX`/`InflateY`, `docs/PLANS/workspace-grid-02-metrics.md` §8.5).
      Открытый вопрос **OQ-6** (позиция как абсолютная точка vs индекс слота) — см. план §8.

- [x] **P03 fix-cycle 1** (code-review, `docs/PLANS/workspace-grid-03-04-integration.md` §2.8/§7/§8,
      ADR-028 «Реализация P03 — fix-цикл 1»): CRITICAL **C-1** — `SnapToGrid` резолвило `_gridState` один
      раз в начале drag и клампило drop обратно в work area СТАРТОВОГО монитора, поэтому меж-мониторный
      drop телепортировал панель назад и `MonitorDeviceId` (F-6) никогда не обновлялся; исправлено
      `PanelDragPipeline.RetargetToDropMonitor` — резолвит монитор по СЫРОМУ (до clamp) центру панели из
      уже снятого снимка `_dragMonitors`, без новой энумерации, и подставляет его origin/work area ДО
      `ResolveGridDrop`. MAJOR **M-1** — guide-шаг (`SnapGuideCalculator.Snap`) использовал замороженный
      `state.WorkArea` стартового монитора; теперь `ResolveGridFrame` резолвит work area ПО КАДРУ из того
      же снимка мониторов (как уже делал `LegacyFreeform`-путь). MAJOR **M-2** — «Align all» проходило по
      панелям ВСЕХ страниц, а `CollectOtherPanelBounds` пропускает `!IsVisible`, из-за чего панели скрытых
      страниц выравнивались без препятствий и схлопывались в один слот; область сужена до ТЕКУЩЕЙ
      страницы, текст кнопки/диалога унифицирован («Align all panels to grid», диалог явно называет
      число панелей текущей страницы). MAJOR **M-3** — `OnMonitorsChanged` писало `Width/Height` и
      запрашивало сохранение на каждый `WM_SETTINGCHANGE` (тема/локаль) без проверки, изменилось ли
      что-то; добавлен допуск 0.5 DIP (`WindowManager.GridAlignedBoundsUnchanged`) и общий флаг
      `anyChanged`, гейтящий `SaveWindowsState()`. MINOR — три писателя геометрии унифицированы на
      `Math.Round` вместо усекающего `(int)` (согласовано с `LassoPanelGeometry`); `ResolveGridDrop`
      получил перегрузку, клампящую по РАЗВЁРНУТОМУ размеру свёрнутой панели, а не по её collapsed-высоте;
      второй уровень отката в `ClampHardWall` (per-axis, сегодня no-op) оставлен как есть — снятие риска
      регрессии «стена пробита» дороже цены одного лишнего O(N)-прохода; отказ «Align to workspace grid»
      теперь пишет диагностическую строку вместо тихого no-op; `CollectOtherPanelBounds` принимает
      `Guid?` вместо сентинела `Guid.Empty`. UX — подсказка про `Alt` на пункте меню «Align to workspace
      grid». **OQ-6 закрыт (решение A)**: позиция `GridAligned`-панели остаётся абсолютной точкой,
      ре-квантуемой к новому origin (см. ADR-028) — полноценный «логический слот» требует новой schema-
      миграции вне объёма этого пакета. `dotnet build`/`dotnet test` — 2766 passed (базис worktree'а
      этого fix-цикла — 2747, +19).

- [x] **P04 — Spans, native resizing, roll-up, presets** (`docs/fix-prompts/04-resize.md`,
      [docs/PLANS/workspace-grid-03-04-integration.md](PLANS/workspace-grid-03-04-integration.md) §3):
      `GridAligned` resize квантует ОБЕ протяжённости через `WorkspaceLayoutPolicy` (не
      `GridMetrics.CellSize` — система C не участвует), две точки входа в конвейер
      (`ResizeConstraintsFactory` — какими правилами, `ResizeGestureSession.TryUpdate` — само
      квантование), новый `IPanelSizingGridHost` (даёт контроллерам монитор/препятствия/writer без
      правки `ChildWindow.xaml.cs`), anti-overlap при resize по целым слотам (закрывает открытый
      backlog-пункт ADR-017 для `GridAligned`), `RollUpBehavior` работает span'ами
      (`CollapsedHeaderSpan=5`, OQ-4=A), presets S/M/L/XL доступны в обоих режимах геометрии и НЕ меняют
      `PanelGeometryMode` (единственная явная точка входа/выхода — checkable «Align to workspace grid»).
      **Fix-цикл 1** — MAJOR-1 (clamp «на кадре», а не трансляция на mouse-up — тянуть правый край мог
      сдвинуть ЛЕВЫЙ на mouse-up), MAJOR-2 (commit писал неточный extent из-за px-round-trip погрешности
      — `RoundBoundsToSpans` вместо сырых bounds), MAJOR-3 (ленивая миграция spans без
      `ClampToMinimum` — высота могла навсегда застрять ниже минимума), `Alt` при resize (вариант B,
      симметрично drag), preset выключает `AutoContent` (UX P0), MV-21 закрыт (подменю «Size»
      дизейблится на свёрнутой панели).
- [x] **P05 — `IconLayoutPolicy` + F-3 chrome fix** (`docs/fix-prompts/05-icon-layout.md`,
      ADR-029 Решения 1–3): `IconLayoutPolicy {LegacyStretch, FixedGap}` — `FixedGap` = ветка
      «неполного ряда» ADR-018, применённая безусловно ко всем рядам (шаг между иконками не зависит от
      ширины панели); `PanelMetrics.ContentLeftInset=8`/`GridMetrics.FileListHPadding` (ссылка, не
      независимая константа) фиксирует F-3 (снап-код терял колонку хрома) для ОБЕИХ политик (Q2=A).
      **Fix-цикл, итерация 1** — MAJOR-1 (`ChildWindow.Initialize` безусловно вызывал
      `SnapWidthToGrid()` — до F-3 numeric no-op, после F-3 силовой одноразовый скачок ширины для
      практически любой персистентной Grid-панели при первой загрузке после апгрейда; решение —
      переснап при загрузке удалён ДЛЯ ВСЕХ режимов, включая `LegacyFreeform`), M-2 (`FixedGap`
      резервирует ширину скроллбара безусловно). **Fix-цикл, итерация 2 (последняя)** — CRITICAL-1
      (`AdaptiveIconGridPanel.CapacityWidth`: M-2's «безусловное резервирование» на деле двойным вычетом
      воспроизводило тот же скачок колонок, который был призван устранить — новый DP, привязанный к
      стабильной `ScrollViewer.ActualWidth`, резерв вычитается один раз), MAJOR-2 (`IconLayoutPolicy`
      теперь тоже триггерит переснап при смене на живой панели), MINOR (drag-reorder hit-test в левом
      гуттере ряда резолвился в последний элемент вместо первого; golden-эквивалентность расширена на
      `FixedGap`).
- [x] **P06 — `PanelSizingMode`/«Fit panel to content»/«Icon layout» UI** (`docs/fix-prompts/06-fit-content.md`,
      [docs/PLANS/workspace-grid-06-fit-content.md](PLANS/workspace-grid-06-fit-content.md), ADR-029
      Решение 7): явное действие «Fit panel to content» + персистентный `PanelSizingMode.AutoContent` —
      одна чистая формула (`PanelContentSizer.FitHeight`, переиспользует `GridMetrics`/
      `AdaptiveGridLayout` без дублирования), ограничено Grid-режимом (Q3=B), однонаправленно (ширина →
      высота, никогда обратно). Новые пункты контекстного меню («Icon layout», «Panel sizing» —
      «Fit panel to content»/«Auto-size to content»/«Apply to all panels…»), новые настройки
      («Defaults for new panels» — Panel geometry/Icon layout, поля уже существовали с P02b, но не были
      выведены в UI). Никаких изменений формата persisted JSON. **Fix-цикл 1** — MAJOR-1
      (`IsLayoutLocked`/`IsEnabled`-гейтинг отсутствовал у «Apply … to all panels…»), MAJOR-2
      (`ApplyFitHeight`'s `GridAligned`-ветка игнорировала fail-safe возврат резолвера — схлопывала
      панель до минимального span на fail-safe пути), MAJOR-3 (debounce-тик мог сработать посреди
      активного resize/drag-жеста — гонка геометрии), MAJOR-4 (завершённый ВЕРТИКАЛЬНЫЙ resize не
      демотировал `AutoContent → Manual` — Fences-подобное поведение добавлено), UX-P1 (сырые имена enum
      в двух `ComboBox` настроек → `EnumDisplayNameConverter`), UX-P2 (подменю «Panel sizing»
      симметрично «Icon layout»). Статус ADR-029 сознательно возвращён `Accepted → Proposed` по итогам
      этого fix-цикла (до релизной сверки P09).
- [x] **P07-visual fix-cycle 1** (`docs/fix-prompts/07-visual.md`, `docs/PLANS/workspace-grid-07-visual.md`,
      ADR-025 Amendment 2026-09-22): border radius 6→8 DIP resize-grip hit-test fix (widened
      `ResizeOverlay`/`NativePanelResizeController.BorderDip` to a shared `PanelMetrics.ResizeGripThickness`,
      corrected an overclaim about `Clip`/hit-testing in `RoundedRectClipConverter`/ADR-025 re: the real
      OS-level per-pixel-alpha click-through risk on `AllowsTransparency="True"`); header icon-button hover
      wash now direction-aware by surface luminance (`Theming/HoverOverlayPolicy.cs`,
      `Converters/HoverOverlayBrushConverter.cs`) — closes the hover-contrast regression found by the P07
      audit (idle 3.59:1 → hover was 2.72:1, now improves on idle); List-mode item label now truncates
      with an ellipsis (StackPanel → Grid item root, `Resources/Templates.xaml`); a11y wording/coverage
      fixes (`AutomationProperties.Name` on the panel window itself, "Expand/Collapse panel" wording); a
      Glass-on-light-wallpaper usability hint added next to the Surface style picker
      (`Views/MainWindow.xaml`). New debt from this pass: `SurfaceComposer.MinSurfaceOpacity` (0.35) stays
      far below WCAG contrast on a light wallpaper for every style — **DO-021**, deliberately not
      auto-raised (would silently re-render already-saved panels below the new floor).

- [x] **P07-visual fix-cycle 2 (последний)** (`docs/PLANS/workspace-grid-07-visual.md` §13): повторное
      ревью нашло, что fix-cycle 1's MAJOR-1 (единая толщина 8 DIP грипа) само создало НОВЫЙ регресс
      (header-кнопки/scrollbar перекрыты шире, чем нужно), а UX-P2's structural fix (Grid вместо
      StackPanel) был структурно верным, но не подключён к реальному `ListBox`'s `ScrollViewer`. MAJOR-A
      — `PanelMetrics.ResizeGripThickness` разделён на `ResizeCornerThickness`(8)/`ResizeEdgeThickness`(6),
      `NativeHitTestMapper.Map` теперь принимает пару порогов; остаточный оверлап численно уменьшен
      (21%→14% высоты кнопки, ~47%→~35% ширины scrollbar), но НЕ обнулён — задокументированный
      структурный предел, не молча принятый (см. `docs/REVIEWS/workspace-grid-debt.md` WGD-03/WGD-01 —
      корректность самой реализации углового грипа под вопросом, требует приоритетной живой проверки).
      MAJOR-B — `ListViewTemplate`'s `ListBox` получил `ScrollViewer.HorizontalScrollBarVisibility=
      "Disabled"` (fix-cycle 1's regression-тест измерял только `DataTemplate` в обход реального
      `ListBox`/`ScrollViewer` и не поймал этот дефект — новый честный тест на реальном `ListBox`
      добавлен). Побочно найден и исправлен инцидент теста: первая версия нового geometry-теста могла
      навсегда оставить `Application.Current.Resources` пустым для всего процесса `dotnet test`, если
      оказывалась первым владельцем `Application` (сломало 72 несвязанных теста) — см.
      `docs/REVIEWS/workspace-grid-debt.md` WGD-02 (тот же класс риска остаётся открытым для будущих
      тестовых файлов). Это последний разрешённый fix-cycle для P07 (лимит 2 итерации).
- [x] **P08 — Icon loading pipeline: диагностика + фикс** (`docs/fix-prompts/08-icons.md`,
      [docs/DIAGNOSTICS/icon-loading-findings.md](DIAGNOSTICS/icon-loading-findings.md)): симптом
      «часть ярлыков в панели без иконки, подпись есть» — подтверждён измерением на реальной Windows 11
      (P00), два независимых фактора: (1) 20/26 сохранённых путей указывают на устаревшие pre-Move
      исходники (переиспользован manifest ADR-009 для reverse lookup, `TryFindManagedPath`), (2)
      placeholder для несуществующего пути был полностью прозрачным (`RenderTargetBitmap.Render()` до
      закрытия `DrawingContext`) — оба фикса нужны одновременно (ни один не объясняет весь симптом сам
      по себе). Плюс: 4 новых diagnostics-события категории `icon` (были — немой `Console.Error` в
      `WinExe`-процессе), file-based invalidation (`FileItem.UpdateIcon` на переходе missing→present,
      переиспользован уже существующий 3с liveness-poll). **Fix-цикл 1** — CRITICAL C1 (confirm-диалог
      на удаление панели/страницы считал `hasManagedFiles` по устаревшему, ещё не ремапленному
      `Model.FilePaths` — мог показать нейтральный текст перед тем, как `Release()` тихо удалит теперь
      managed-файл мимо Корзины), 4 MAJOR (ambiguous remap между панелями, privacy-утечка в
      `SafeExtension` для путей вида `ivan.petrov`, transient `SHGetFileInfoW`-сбой навсегда «прилипал»
      к placeholder, интеграционные тесты не доказывали «не placeholder»), UX-фикс (missing-путь
      пытается shell-lookup по расширению/атрибутам ПЕРЕД fallback на placeholder). **Fix-цикл 2
      (последний)** — CRITICAL-1 (`RemovePage` игнорировал `false` от confirm-гейта, страница исчезала
      из UI даже при отмене), MAJOR-1 (`LayoutSnapshotService.Restore` не прогонял cross-panel-aware
      remap для восстановленных панелей), MINOR (missing-путь кешировался с `lastWriteTimeUtc=default`
      навсегда для большинства расширений; contested-candidate remap отдавался «первому по порядку»
      вместо явного отказа для ОБОИХ претендентов — переписано на two-pass discovery/apply). Оставлено
      задокументированным долгом (не устранено): async icon loading для 200+ элементов (≈0.56с
      блокировки, требует отдельного ADR — см. `docs/REVIEWS/workspace-grid-debt.md` WGD-21), 1/20
      путей без manifest-записи (WGD-22).

**P09 (gate) — pending.** Финальная релизная сверка (`docs/fix-prompts/09-acceptance.md`) не запущена в
этом цикле — входные артефакты подготовлены: единый бэклог долга
[docs/REVIEWS/workspace-grid-debt.md](REVIEWS/workspace-grid-debt.md) и сводная матрица
[docs/REVIEWS/workspace-grid-manual-verify.md](REVIEWS/workspace-grid-manual-verify.md) (объединяет
MV-1…MV-29 плана P03/P04, ADR-029 manual verify пп. 1–29, P07 §9/§11, P08, P02b rollback — без
дублей). До выполнения P09 оба ADR (028, 029) остаются `Proposed` — см. их шапки
(«Implemented 2026-09-22, Accepted pending manual verify — см. P09»).

> ℹ️ **Manual verify — выполнено пользователем на Windows 11 24H2, 2026-09-27; признано достаточным**
> для Ambient Accent (V-1..V-18), P07-visual fix-cycle 1 и пакета workspace-grid (P02–P08). Windows 10
> отдельно не проверяется. Исторические полные списки — `docs/PLANS/phase-5-ambient-accent-ui.md` §7.2,
> [docs/Refactoring/Testing/ambient-accent-visual-matrix.md](Refactoring/Testing/ambient-accent-visual-matrix.md),
> [docs/REVIEWS/workspace-grid-manual-verify.md](REVIEWS/workspace-grid-manual-verify.md). Формальный
> гейт P09 (§ выше) как отдельная релизная сверка не запускался — статус ADR-028/ADR-029 (`Proposed`)
> не пересматривается этим решением, см. отчёт.

- [x] **ADR-030 — Reference-Style Layered Panel Chrome** (ветка `feature/reference-chrome`, мастер-план
      [docs/PLANS/reference-panel-chrome.md](PLANS/reference-panel-chrome.md)). Слоистый `PanelChrome`
      поверх Ambient Accent (ADR-025) без нового persisted-состояния: `PART_HitCatcher` (закрывает
      OS-level click-through-линзу угла, ADR-025 MAJOR-1, на обоих путях ресайза), единственный
      `DropShadowEffect` (`PART_Glow`) с shader-free fallback по `RenderCapability.Tier`
      (`Theming/ChromeEffectsPolicy`), neutral/accent hairline-кольца (`PART_NeutralStroke`/
      `PART_AccentStroke`), non-semantic surface highlight (`PART_SurfaceHighlight`), изогнутый accent
      marker (`PART_AccentIndicator` теперь `Path`). Бюджет прозрачного inset ограничен 2 DIP уже
      существующим чромом (rolled-up панель — связывающее ограничение); внешний ореол уже референса —
      сознательный trade-off, решён в пользу варианта A (не трогать persisted-геометрию, `adr/ADR-030-*.md`
      Amendment 2026-09-25 Q-1). `dotnet build`/`dotnet test` — 0 errors, 0 warnings, 3459/3459 ×3.
      Архитектурное решение — [ADR-030](../adr/ADR-030-reference-panel-chrome.md) (Accepted), короткий
      amendment к [ADR-025](../adr/ADR-025-ambient-accent-visual-language.md). Новый долг —
      `docs/Refactoring/BACKLOG.md` DO-022 (doc-drift `FileListHost.Margin`), DO-023
      (`SystemIconsPanelWindow` без `PanelChrome`), DO-024 (широкий ореол — открытый продуктовый вопрос),
      DO-025 (`RenderCapability.Tier` не отслеживается на лету).

> ℹ️ **Manual verify — выполнено пользователем на Windows 11 24H2, 2026-09-27; признано достаточным**
> (матрица MV-1…MV-15). Windows 10 отдельно не проверяется. Исторический полный список —
> [docs/PLANS/reference-panel-chrome.md](PLANS/reference-panel-chrome.md) §6 и раздел «Manual verify» в
> ADR-030.

---

## Переустройство окна настроек (ADR-031, вне формальных Фаз)

Источник — бриф `docs/prompts/settings-window-redesign.md` (решения D-1..D-10) и мастер-план
[docs/PLANS/settings-window-redesign.md](PLANS/settings-window-redesign.md). Ветка
`feature/settings-window-redesign` (от `rc/0.6.0`). Архитектурное решение —
[ADR-031](../adr/ADR-031-settings-window-navigation-and-panel-customize.md) (Accepted, 2026-09-26).

- [x] **Боковая навигация в `MainWindow`** — 8 разделов (`SettingsSection`: General, Layout & snapping,
      Panels, New panel defaults, Pages, System icons, Backup, About) заменяют плоский список
      `Expander`. Полоска вкладок страниц вверху окна удалена (страницы переключаются из раздела Panels
      и Ctrl+PgUp/PgDn). Раздел помнится между Show/Hide в рамках процесса, сбрасывается на `General`
      после рестарта — персистентности не добавлено (D-1, заморозка `CLAUDE.md` не нарушена).
- [x] **Немодальный диалог `PanelCustomizeWindow`** (одно окно на приложение, live-редактирование
      общего `ChildWindowModel`, без OK/Cancel/Apply) — открывается из `HeaderContextMenu` панели
      («Customize…») и из раздела Panels. Переносит Title/Accent/Text color/Opacity/Surface
      style/Icon size/View/Sort из прежнего инлайн-раздела «Selected panel settings», плюс впервые даёт
      UI для Content filter/Icon layout/Auto-size/Folder Portal (Browse…/Clear).
      **Известное отклонение от плана:** Align to workspace grid / Size presets / Fit to content в
      диалог не перенесены — остались context-menu-only (`docs/Refactoring/BACKLOG.md` DO-027).
- [x] **UI для ранее скрытых настроек модели**: Folder Portal (Browse…/Clear в диалоге), `PageId` панели
      (Panels › Move to page ▾), `DefaultSurfaceStyle` (New panel defaults), «Take from selected panel».
- [x] **Трей: «Show desktop icons» возвращён как checkable-пункт**, синхронизированный с реальной
      видимостью иконок; одноимённый чекбокс убран из окна настроек (сессионная команда, не
      преференция — amendment к [ADR-026](../adr/ADR-026-edit-layout-mode.md) D-9: одна из трёх точек
      переключения режима заменена подсказкой, `Ctrl+E` в окне настроек сохранён).
- [x] **Фикс `PageId` лассо-панели** — панель, созданная правым drag'ом на не-default странице, теперь
      сразу видна на этой странице и остаётся там после рестарта (раньше «перескакивала» на `default`).
- [x] **Именованные снапшоты и подтверждение удаления** — «Save as…» запрашивает имя
      (`IInputDialogService`), «Delete» снапшота спрашивает подтверждение с именем.
- [x] `SelectSourceFolderCommand` удалён как мёртвый код (см. Фазу 5 и «Сквозные задачи» выше —
      аннотировано в местах упоминания).
- [ ] **Follow-up (не в объёме этого захода):** `SettingsCard`-контрол вместо ручной разметки карточек
      (`docs/Refactoring/BACKLOG.md` DO-026); Align/Size presets/Fit to content в диалоге (DO-027);
      rename/reorder страниц, «волна 2» (DO-028); Export/Import снапшотов (существующий DO-012); color
      picker для Accent/Text color вместо hex-поля.

**Готово, когда:** ни одна настройка из инвентаря плана (§5.1) не потеряна, диалог живо редактирует
`ChildWindowModel`, трей и окно настроек не дублируют сессионные команды. `dotnet build`/`dotnet test`
на HEAD `d69bfdc` (после финального fix-раунда `fc64035`) — 0 errors, 0 warnings, 3589 тестов.

> ℹ️ **Manual verify — выполнено пользователем на Windows 11 24H2, 2026-09-27; признано достаточным**
> (консолидированная матрица MV-1…MV-39, включая откат на v0.5.1/гейт инцидента 2026-09-24, MV-27).
> Windows 10 отдельно не проверяется. Исторический полный список —
> [docs/PLANS/settings-window-redesign.md](PLANS/settings-window-redesign.md), раздел «Реализация vs
> план».

---

## Фаза 10. Release hardening 1.0

Источник — [docs/PLANS/release-hardening-1.0.md](PLANS/release-hardening-1.0.md) (пакеты A–G2, мастер-план),
[docs/PLANS/release-hardening-1.0-F-dpi.md](PLANS/release-hardening-1.0-F-dpi.md) (под-план пакета F),
[docs/PLANS/release-hardening-1.0-f0-results.md](PLANS/release-hardening-1.0-f0-results.md) (спайк F0,
CLARIFICATION_NEEDED и решение оркестратора). Ветка `feature/release-hardening` от `rc/0.6.1`. **Версия
осталась 0.6.1** — по явному решению пользователя бампа не было. Архитектурные решения —
[ADR-033](../adr/ADR-033-crash-safety-and-app-log.md)…[ADR-038](../adr/ADR-038-release-engineering.md).
`dotnet build`/`dotnet test` — 0 errors, 0 warnings, 4379 тестов.

- [x] **Пакет B — Diagnostics** (фундамент): `IAppLog`/`DiagnosticsAppLog` поверх единого sink
      `JsonLinesDiagnosticsLog` (ADR-027 не меняется), уровни `Standard` (Release-дефолт, always-on)/
      `Verbose` (`--diag`/`DESKTOPORGANIZER_DIAG=1`), `LogScrubber` (пути/имя пользователя), ambient
      `AppLog.Current`, `AppVersionInfo`. См. [ADR-033](../adr/ADR-033-crash-safety-and-app-log.md).
- [x] **Пакет A — Crash safety**: `CrashCoordinator` (иконки → crash-report+лог → сохранение состояния
      (только UI-поток) → диалог → выход, код 5), `WinFormsExceptionBridge`, единый `ShutdownSequence`
      для `OnExit`/оба `SessionEnding`, `StartupIconsSelfHeal`. См.
      [ADR-033](../adr/ADR-033-crash-safety-and-app-log.md).
- [x] **Пакет D — Удаление панели/страницы → Корзина** (amends
      [ADR-010](../adr/ADR-010-managed-storage-cleanup-on-panel-removal.md)): `INativeApi.RecycleItems`,
      `RecycleOutcome`/`PanelRemovalResult`, никогда не hard-delete, fail-visibly. Закрывает открытый
      архитектурный вопрос ниже («Сквозные задачи» — «удалить панель стирает managed-файлы мимо
      Корзины»). См. [ADR-035](../adr/ADR-035-panel-deletion-recycle-bin.md).
- [x] **Пакет C — Восстановление после рестарта Explorer**: `ExplorerRestartMonitor`
      (`TaskbarCreated`+Progman-опрос), `DesktopLayerRecoveryPolicy` (коалесинг 1.5с, 6×500мс ретраев),
      `ShellRecoveryCoordinator`, `WindowManager.RecoverDesktopLayer`. На Win11 24H2+ панели top-level
      (WorkerW не найден) — amendment к
      [ADR-001](../adr/ADR-001-desktop-integration.md)/[ADR-019](../adr/ADR-019-sidebar-top-level-virtual-desktop.md)/[ADR-020](../adr/ADR-020-sidebar-zorder-back-without-topmost.md).
      См. [ADR-034](../adr/ADR-034-explorer-restart-recovery.md).
- [x] **Пакет E — Drag-out элементов панели** (amends
      [ADR-009](../adr/ADR-009-drag-drop-move-semantics.md)/[ADR-014](../adr/ADR-014-panel-item-sorting-and-manual-order.md)):
      один `DoDragDrop` с тремя форматами (reorder+`FileDrop`+`Preferred DropEffect=Move`),
      `IPanelItemDragOutSession`, `MovedOutVerifier` (0/500/2000мс). Закрывает Risk R7 (Фаза 9 бэклог,
      см. ниже). См. [ADR-036](../adr/ADR-036-panel-item-drag-out.md).
- [x] **Пакет F — PerMonitorV2 + двухрежимная шкала desktop-слоя** (amends
      [ADR-004](../adr/ADR-004-phase5-pages-snapshots-multimonitor.md)/[ADR-022](../adr/ADR-022-native-panel-sizing.md)
      D-6/[ADR-028](../adr/ADR-028-workspace-grid.md)): манифест PMv2, `DesktopScaleModel`/
      `IDesktopScaleProvider`, режим E (Embedded, Win10/≤23H2, единый масштаб WorkerW) vs T (TopLevel,
      Win11 24H2+, per-monitor) по факту (`GA_ROOT`); fast-path bit-в-bit для uniform-конфигураций, без
      миграции persistence. Закрывает открытый архитектурный вопрос «per-monitor DPI-осведомлённость»
      (см. ниже). См. [ADR-037](../adr/ADR-037-permonitor-v2-dual-mode-desktop-scale.md).
- [x] **Пакет G1 — инсталлятор, CI, документы**: ссылки паблишера/поддержки, опциональная подпись по
      SHA1-отпечатку сертификата, корневые README/LICENSE(MIT, messire)/CHANGELOG,
      `.github/workflows/ci.yml` (0-warnings гейт, pinned actions/tool versions).
- [x] **Пакет G2 — ручная проверка обновлений + ссылки в About**: `SemVersion`, `GitHubReleaseUpdateChecker`
      (без persisted state, `html_url` валидируется структурно), `IExternalLinkLauncher` (только
      `https://`), `AboutViewModel`. См. [ADR-038](../adr/ADR-038-release-engineering.md).

**Готово, когда:** все 7 пакетов реализованы, `dotnet build`/`dotnet test` — 0 errors/0 warnings, ADR-033…
ADR-038 приняты, ROADMAP/README/CHANGELOG синхронизированы с кодом. Manual verify выполнена пользователем
на Windows 11 24H2, 2026-09-27, признана достаточной.

> ℹ️ **Consolidated manual verify — выполнено пользователем на Windows 11 24H2, 2026-09-27; признано
> достаточным** (top-level mode, recovery после рестарта Explorer, краш, Корзина, drag-out, проверка
> обновлений, подпись/CI). Windows 10 (Embedded mode) и стенд с двумя физическими мониторами (E6/QF-1)
> пользователю недоступны и отдельно не проверялись — риск принят, известные ограничения
> задокументированы в ADR-037. Исторические полные чек-листы — в разделах «Manual verify» каждого
> ADR-033…ADR-038 и в планах.

**Бэклог, найденный ревью в рамках этого захода (не в объёме 1.0):**
- Локализованный кастомный диалог подтверждения вместо нативных кнопок `MessageBox` (крайние диалоги
  краша/Корзины сейчас используют системные Yes/No).
- Подсветка drag-over на целевой панели при cross-panel drag-out (пакет E) — сейчас нет визуального
  индикатора «сюда примет».
- Owner HWND для shell-диалога Корзины (`ReleaseToRecycleBin` вызывает `RecycleItems` с
  `IntPtr.Zero` — диалог не привязан к окну приложения).
- ~~Качество иконок через `SHGetImageList`~~ — **закрыто 2026-09-27 (release polish 1.0).** Реализован
  трёхстадийный фолбэк на реальные, sized-иконки через `IShellItemImageFactory` — см.
  [ADR-039](../adr/ADR-039-sized-shell-icons.md).
- ~~Нативное shell-контекстное меню на элементах панели~~ — **закрыто 2026-09-27 (release polish 1.0).**
  Собственное WPF-меню (`FileContextMenu`) удалено, элемент панели показывает то же `IContextMenu`-меню,
  что и Проводник/`SystemIconsPanel` — см. [ADR-040](../adr/ADR-040-native-shell-context-menu.md).
- ~~Сортировка по типу/дате~~ — **won't-do, решено 2026-09-27.** `PanelSortMode` остаётся `Manual`/`Name`
  окончательно, расширение не планируется — см. [ADR-014](../adr/ADR-014-panel-item-sorting-and-manual-order.md),
  «Amendment 2026-09-27».

---

## Release polish 1.0 (2026-09-27)

Источник — задача «release polish 1.0» поверх закрытой Фазы 10 (ветка `feature/release-polish-1.0`, от
`rc/0.6.1`). Версия остаётся 0.6.1. Архитектурные решения — [ADR-039](../adr/ADR-039-sized-shell-icons.md)…
[ADR-041](../adr/ADR-041-first-run-panel-and-uninstall-restore.md), плюс amendment к
[ADR-038](../adr/ADR-038-release-engineering.md).

- [x] **Реальные sized-иконки элементов панели через `IShellItemImageFactory`** — закрывает бэклог-пункт
      Фазы 10 «качество иконок через `SHGetImageList`» (soft-рендер на 150%+ и generic-иконки вместо
      реальных). Трёхстадийный фолбэк, бакеты `IconPixelSize`, кеш `(path, bucket)`, UNC/offline-скип,
      200 мс debounce, DPI-источник `PanelWindowCoordinates.GetWindowScale`. См.
      [ADR-039](../adr/ADR-039-sized-shell-icons.md).
- [x] **Нативное shell-контекстное меню на элементах панели** (amends
      [ADR-009](../adr/ADR-009-drag-drop-move-semantics.md)/[ADR-010](../adr/ADR-010-managed-storage-cleanup-on-panel-removal.md)) —
      закрывает одноимённый бэклог-пункт Фазы 10. Собственное WPF-меню удалено; «Убрать из панели» как
      команда меню больше не существует (перемещение наружу — drag-out, ADR-036); «Удалить» на
      managed-элементе → `RecycleManagedItem` (restore на исходное место, потом Корзина, amends
      [ADR-035](../adr/ADR-035-panel-deletion-recycle-bin.md)); «Переименовать» → тот же inline-редактор,
      что и `F2` (ADR-013). См. [ADR-040](../adr/ADR-040-native-shell-context-menu.md).
- [x] **Первый запуск создаёт одну пустую панель** вместо пустого рабочего стола без единой точки входа —
      строго на `StateLoadStatus.FirstRun` && 0 панелей, не на любое «0 панелей». См.
      [ADR-041](../adr/ADR-041-first-run-panel-and-uninstall-restore.md).
- [x] **Удаление приложения возвращает файлы из панелей** — `--restore-panel-items` в `[UninstallRun]`
      возвращает managed-файлы на исходное место или на рабочий стол, вместо их безвозвратной потери
      вместе с `%LocalAppData%\DesktopOrganizer`. См.
      [ADR-041](../adr/ADR-041-first-run-panel-and-uninstall-restore.md).
- [x] **Метаданные приложения/инсталлятора** (amendment к [ADR-038](../adr/ADR-038-release-engineering.md)) —
      Task Manager показывает «DesktopOrganizer» (`AssemblyTitle`, не `AssemblyName`); Copyright/Authors/
      LICENSE holder унифицированы на «khagen»; инсталлятор — English первым языком, `MinVersion=10.0`,
      `.pdb`/`.xml` исключены из payload. GitHub-URL «messire» — подтверждён пользователем, решение закрыто
      (без изменений).
- [x] **Company → khagendev + миграция `user.config`** (2026-09-28, явное разрешение пользователя,
      второй amendment к [ADR-038](../adr/ADR-038-release-engineering.md)) — пересматривает решение выше:
      `Company` больше не закреплён literal'ом на `DesktopOrganizer.UI`, `DesktopOrganizer.UI.csproj`
      наследует `Directory.Build.props`'s `<Company>khagendev</Company>`, как и остальные проекты
      (`AssemblyCompanyContractTests` теперь пиннит `"khagendev"`). Путь `user.config` переезжает с
      `%LocalAppData%\DesktopOrganizer.UI\...` на `%LocalAppData%\khagendev\...`; одноразовая миграция при
      старте (`UserConfigFolderMigration`, `Program.Main`) копирует старое дерево в новый корень (старое не
      удаляется), сохраняя настройки существующих пользователей — единственное санкционированное
      исключение из замороженного контура ADR-024 (сама схема/DTO/дефолты не тронуты).
- [x] **Документация: manual verify Фазы 10 закрыта, сортировка финализирована** — выполнено пользователем
      на Windows 11 24H2, 2026-09-27; статусы ROADMAP/ADR-014 обновлены отдельным заходом ранее в тот же
      день (см. историю коммитов/предыдущий проход `architecture-documenter`).
- [x] **Цветовые пресеты, RGB-пикер и шаговые слайдеры размера** (2026-09-28) — исходно 10 готовых цветовых
      пар (`PanelColorPresets`) как основной способ выбора цвета панели в Customize и New panel defaults,
      отдельно от legacy `AccentPalette.Presets` (6, ADR-025, код/тест-only); системный `ColorDialog` за
      `IColorPickerService` (Services) как вторичный способ, hex-поле — третий; единый шаг размера `8`
      (icon 16–128, grid step 2–128), ручной целочисленный ввод, семантика `SteppedValue` без записи значения
      при простом открытии окна. См. [ADR-042](../adr/ADR-042-color-presets-rgb-picker-stepped-size.md).
- [x] **Редизайн окна Customize (явный Light/Dark, 12-цветная палитра, карточная компоновка)** (2026-09-28,
      тот же день/ветка, amendment к ADR-042) — палитра заменена на 12 пресетов в порядке спектра с
      раздельным `DarkModeTextHex`/`LightModeTextHex` на каждый; открытый вопрос Q-1 (светлые пресеты
      нечитаемы на тёмной поверхности) закрыт явным, выбираемым пользователем `PanelThemeMode {Dark, Light}`
      (выводится из `TextColor` по WCAG-контрасту к двум базам `SurfaceComposer`, не по одному акценту);
      `PanelCustomizeWindow`/`NewPanelDefaultsSection` + новый `PanelPreviewCard` переведены на карточный flow
      в стиле Windows 11 → Персонализация → Цвета (Preview card, сегментированные Mode/Surface, 13-я ячейка
      «Custom…», `Settings.CardExpander` для Advanced). См. «Amendment 2026-09-28» в
      [ADR-042](../adr/ADR-042-color-presets-rgb-picker-stepped-size.md).

**Бэклог, найденный в рамках этого захода:**
- High Contrast: foreground текста сегмента и кольцо выбора свотча ещё не связаны с `SystemColors.
  HighlightBrush` (сейчас различимы только по 1px hairline) — требует manual verify, при подтверждении
  отдельная задача (ADR-042 Amendment, «Известный пробел High Contrast»).
- Icon layout (Grid/List) и Panel geometry (size presets) в `PanelCustomizeWindow` — опционально перевести на
  тот же сегментированный контрол (`Settings.SegmentedGroup`/`SegmentedItem`), что теперь используют Mode и
  Surface, вместо прежних `ComboBox`/radio-списков, ради визуальной согласованности карточного flow.
- RU-раскладка на ширине окна 420 DIP (`PanelCustomizeWindow`/`NewPanelDefaultsSection`) — подтвердить на
  живом Windows, что названия пресетов и подписи сегментов не обрезаются на русском (manual verify пункт уже
  в ADR-042, здесь — как явное напоминание перед релизом).
- Фоновый STA-фетч высокорезрешённых иконок (сейчас вся экстракция синхронна на UI-потоке; UNC/offline-скип
  снимает два наиболее частых многосекундных подвисания, но не все — см. ADR-039 «Known follow-up»).
- Запись, не успевшая дописаться архиватором/shell-командой к истечению 40-тикового pending-окна,
  остаётся скрытой в managed-хранилище до следующей shell-команды над тем же элементом (ADR-040 «Known
  Limitation»).
- Более дружелюбный заголовок первой панели вместо дефолтного локализованного (опционально, косметика —
  ADR-041 Consequences).
- Однострочное уведомление при удалении о возврате файлов из панелей (опционально — сейчас `--restore-panel-items`
  полностью безмолвен для типичного случая).

---

## Сквозные задачи

- [~] Тесты на новые сервисы: `FolderWatcherTests`, `PageManagerServiceTests`, `MonitorServiceTests` реализованы
      (57 pass). Не покрыты: `IFileLauncher`, ресайз/roll-up-логика, `IAutostartService` — отложено.
- [x] Проверка на Windows 10 и Windows 11 отдельно (WorkerW-хак, см. ADR-001) — **выполнено пользователем
      на Windows 11 24H2, 2026-09-27; признано достаточным, Windows 10 отдельно не проверяется.**
- [~] Поведение при смене обоев, DPI-масштабировании и переключении виртуальных рабочих столов.
      **Закрыто частично 2026-07-26** (`docs/prompts/outstanding-followups.md`, пункт 4): re-clamp
      панелей при уменьшении рабочей области монитора (смена разрешения/масштаба/появление таскбара)
      реализован — `WindowManager.OnMonitorsChanged` пересчитывает позицию панелей, когда монитор не
      исчез, но его рабочая область сократилась; `ClampIntoMonitor` учитывает полный rect (Position+Size).
      Manual verify выполнена пользователем на Windows 11 24H2, 2026-09-27; признана достаточной.
      **Закрыто 2026-09-27 (Release hardening 1.0, пакет F):** полноценная per-monitor DPI-
      осведомлённость реализована — `app.manifest` (PerMonitorV2), `DesktopScaleModel`/
      `IDesktopScaleProvider`, двухрежимная шкала (Embedded — единый масштаб WorkerW, TopLevel —
      per-monitor на Win11 24H2+). Известное ограничение сохраняется в режиме Embedded (Win10/≤23H2):
      панели на мониторе с иным DPI по-прежнему рендерятся в едином масштабе слоя, не по-настоящему
      per-monitor — задокументировано в README и [ADR-037](../adr/ADR-037-permonitor-v2-dual-mode-desktop-scale.md).
      **Не закрыто manual verify:** гипотеза якорения мультимонитора (QF-1) требует стенда с 2
      физическими мониторами, недоступного пользователю — остаётся открытым риском (не блокером).
      Обои и виртуальные рабочие столы — по-прежнему не реализовано (не функция manual verify, а
      нереализованная фича).
- [x] **Move-хинт при drag&drop — закрыто 2026-07-26** (`docs/prompts/outstanding-followups.md`, пункт 1):
      one-time хинт о Move-семантике при первом drop реализован (`IAppSettings.HasShownMoveHint`,
      `ChildWindowViewModel.MoveHintRequested`, `Popup` в `ChildWindow.xaml`, автозакрытие 4с).
- [x] **Очистка осиротевших managed-копий при удалении панели — закрыто 2026-07-26**
      (`docs/prompts/outstanding-followups.md`, пункт 3, [ADR-010](../adr/ADR-010-managed-storage-cleanup-on-panel-removal.md)).
- [x] **Тест на кросс-volume откат в `Restore` — закрыто 2026-07-26**
      (`docs/prompts/outstanding-followups.md`, пункт 2).
- [x] **Закрыто 2026-09-27 (Release hardening 1.0, пакет D)** — «удалить панель» стирает managed-файлы
      мимо Корзины. Решено *не* буквальным Fences-паритетом (вернуть файлы на рабочий стол), а
      минимально достаточным фиксом: удаление панели/страницы теперь всегда проходит через Корзину
      (`INativeApi.RecycleItems`, `FOF_ALLOWUNDO`), никогда не удаляет содержимое напрямую, и
      fail-visibly при сбое (панель/страница остаётся). См.
      [ADR-035](../adr/ADR-035-panel-deletion-recycle-bin.md) (amends ADR-010). Известное ограничение:
      восстановленные из Корзины файлы попадают в `PanelItems\<guid>\`, не обратно на панель (панель уже
      удалена к моменту восстановления) — осознанно не устранено, задокументировано в README.
- [x] **Manual verify — Move при drag&drop** (см. Фаза 1, [ADR-009](../adr/ADR-009-drag-drop-move-semantics.md))
      — **выполнено пользователем на Windows 11 24H2, 2026-09-27; признано достаточным**, Windows 10
      отдельно не проверяется. Исторический чек-лист (Move-курсор, cross-volume перенос, «Убрать из
      панели», legacy-панели, подтверждение удаления панели) — см. Фазу 1 выше и ADR-009.
- [x] **Manual verify — магнитный снаппинг при перетаскивании панели** (см. Фаза 2,
      [ADR-011](../adr/ADR-011-manual-drag-with-magnetic-grid-snap.md)) — **выполнено пользователем на
      Windows 11 24H2, 2026-09-27; признано достаточным** (включая HiDPI 125%/150%, Alt+Tab посреди
      драга, края экрана/мультимонитор, двойной клик vs драг). Windows 10 отдельно не проверяется.
- [x] **Manual verify — Фаза 7, блоки A–F** (см.
      [ADR-012](../adr/ADR-012-performance-and-resilience-hardening.md) и
      [docs/PLANS/phase-7-performance-hardening.md](PLANS/phase-7-performance-hardening.md), «Сводный
      список manual verify») — **выполнено пользователем на Windows 11 24H2, 2026-09-27; признано
      достаточным**. Windows 10 отдельно не проверяется.
- [x] **Закрыто — single-instance guard между процессами приложения**, см. ADR-012 Решение 4 (риск
      зафиксирован там же) и [ADR-023](../adr/ADR-023-single-instance-and-ipc.md): реализован
      двухуровневый guard (session-scoped mutex + Named Pipe машинно-глобальный тай-брейкер для Fast
      User Switching), активация второго экземпляра — `IWindowManager.ShowMainWindow()`. Пункт устарел
      и удалён из бэклога.
- [ ] **Известный побочный эффект, не регрессия (бэклог тикет), см. ADR-012 и ADR-013 Решение 1:**
      `Reset`-уведомление `ObservableRangeCollection.ReplaceAll` (Фаза 7, блок D) сбрасывает
      выделение/позицию скролла списка при перестройке `FileItems` — поведение существовало и раньше
      (`Clear()` давал тот же эффект), но заметнее теперь при более частых перестройках. Фаза 8
      компенсирует потерю выделения восстановлением по пути после каждой перестройки
      (`BaseViewModel.ReplaceFileItems`), но сама `Reset`-нотификация (и связанная с ней потеря
      позиции скролла) не устранена. Рекомендован отдельный тикет на точечный diff (Add/Remove вместо
      Reset), если manual verify покажет ощутимую UX-проблему.
- [x] **Repo-wide pre-existing дефект — закрыто 2026-09-05** (исполнение
      `docs/REFACTORING_PLAN.md` §5.5 п.2, детали — §7.2 там же): `linters/linter.ruleset`/`MSB3884` —
      путь в `Directory.Build.props` заменён `$(SolutionDir)` → `$(MSBuildThisFileDirectory)linters\linter.ruleset`,
      добавлен недостающий `<PackageReference Include="StyleCop.Analyzers" PrivateAssets="all"/>`.
      StyleCop теперь физически применяется repo-wide впервые — это вскрыло ~450-457 pre-existing
      SA*-предупреждений (0 ошибок), сознательно не исправленных в этом заходе.
- [x] **Закрыто — массовая чистка StyleCop-предупреждений (SA*).** Сборка repo-wide — 0 warnings
      (подтверждено в этой сессии, 2026-09-27: `dotnet build`/`dotnet test` — 0 errors, 0 warnings,
      4473 теста); исходные ~450-457 предупреждений устранены отдельным заходом
      (`bugfix/build-warnings`).
- [x] **Manual verify — Фаза 8** (см. [ADR-013](../adr/ADR-013-panel-item-selection-and-rename.md)) —
      **выполнено пользователем на Windows 11 24H2, 2026-09-27; признано достаточным**, включая R1
      (реальный ввод текста в inline-`TextBox` под `WS_EX_NOACTIVATE`) и визуальную подсветку выделения
      на разных `BackgroundColor`/`BackgroundOpacity`. Windows 10 отдельно не проверяется. Исторический
      чек-лист из 13 пунктов — план [docs/PLANS/icon-selection-and-rename.md](PLANS/icon-selection-and-rename.md) §9.
- [ ] **Бэклог-тикет, осознанно вне объёма Фазы 8 (см. план §7 Блок A, ADR-013 Решение 1):**
      multi-select значков панели (Ctrl/Shift-клик, рамка выделения при драге по пустому месту) — не
      реализован. Требует множественных команд контекстного меню (`Открыть`/`Убрать`/`Свойства` для
      набора элементов) и меняет сигнатуры существующих команд.
- [ ] **Бэклог-тикет, осознанно вне объёма Фазы 8 (см. план §7 Блок C, ADR-013 Решение 6):**
      «медленный второй клик» по уже выделенному значку как альтернативный триггер переименования
      (Explorer/Fences-паттерн) — не реализован. Отклонён для первой итерации: напрямую конкурирует с
      двойным кликом «Открыть» и требует таймера на `GetDoubleClickTime`; обычный бэклог-тикет, не
      блокер, вносится отдельным пунктом при наличии запроса.
- [x] **Manual verify — Фаза 9** (см. [ADR-014](../adr/ADR-014-panel-item-sorting-and-manual-order.md))
      — **выполнено пользователем на Windows 11 24H2, 2026-09-27; признано достаточным**, включая R1
      (входящий drag&drop файлов с рабочего стола в панель) и R2 (`DoDragDrop` из окна
      `WS_EX_NOACTIVATE`). Windows 10 отдельно не проверяется. Исторический чек-лист из 17 пунктов —
      план [docs/PLANS/panel-item-sorting.md](PLANS/panel-item-sorting.md) §9.
- [x] **Закрыто 2026-09-27 (Release hardening 1.0, пакет E) — перетаскивание значка из панели.**
      Реализован drag-out на рабочий стол/Explorer и в другую панель (один `DoDragDrop` с тремя
      форматами, `IPanelItemDragOutSession`, `MovedOutVerifier`) — см.
      [ADR-036](../adr/ADR-036-panel-item-drag-out.md) (amends ADR-009/ADR-014, закрывает Risk R7).
- [ ] **Бэклог-тикет, осознанно вне объёма Фазы 9 (см. план §8 Risk R8, ADR-014):** внешний drop файла
      с рабочего стола/из Проводника **всегда** добавляет элемент в конец списка (в режиме `Manual`),
      тогда как Fences вставляет его в точку drop'а под курсором. Требует протаскивания индекса
      вставки в `AddDroppedFiles`/`AddFilesCore` (сейчас всегда `append`); инфраструктура для этого
      (`BaseViewModel.MoveItem` + вычисление индекса вставки, `ChildWindow.ComputeInsertionGap`) уже
      готова из Фазы 9 и ляжет сверху без слома.
- [x] **Бэклог-тикет закрыт 2026-09-27** (план §4 Блок B, ADR-014; реализация —
      [ADR-032](../adr/ADR-032-ui-localization.md)): общий `EnumDisplayNameConverter` резолвит текст по
      конвенции `Enum_{Type}_{Member}` вместо `switch`, устраняя сырые имена членов enum
      (`PanelViewMode`/`PanelSortMode` и все остальные пользовательские enum §3.2 плана локализации) —
      попутный эффект инфраструктуры локализации ru/en, не отдельная фича.
- [x] **Локализация UI (ru/en) с горячей сменой языка — закрыто 2026-09-27** (бриф
      `docs/prompts/localization.md`, план `docs/PLANS/localization.md`,
      [ADR-032](../adr/ADR-032-ui-localization.md)): отдельный проект ресурсов
      `DesktopOrganizer.Localization`, `ILocalizationService` + `{loc:Loc}`/`{loc:LocBinding}` дают смену
      языка без перезапуска для всех окон/панелей/трея/панели системных значков; единственное расширение
      замороженного контура persistence — аддитивный скаляр `IAppSettings.Language` с явного разрешения
      пользователя; закрывает попутно бэклог-тикет `EnumDisplayConverter` выше; отменяет «только
      английский» из ADR-023/ADR-031 (amendments). Follow-up (не блокирует): второй язык, дубль
      `EnumDisplayName x:Key`, точечная правка копии `Settings_Panels_Subtitle_View_Format` (ru) — см.
      ADR-032 «Реализация»/follow-up-кандидаты. Manual verify — MV-L1..MV-L15
      (`docs/PLANS/localization.md` §7.3, обязательные MV-L2/L4/L6/L7/L12) **выполнена пользователем на
      Windows 11 24H2, 2026-09-27; признана достаточной**, Windows 10 отдельно не проверяется.

---

## Рекомендуемый порядок

Фаза 0 → Фаза 1 → Фаза 2 → Фаза 3 → Фаза 4 → Фаза 5 → Фаза 6 → Фаза 7 → Фаза 8 → Фаза 9 → Фаза 10.

Фазы 1–2 дают максимальный эффект «как Fences» при малых затратах. Фаза 4 (особенно захват реальных иконок) — самый крупный и концептуально важный блок, его стоит начинать только после стабилизации 1–3.
