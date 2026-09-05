# Roadmap: путь к паритету со Stardock Fences

**Цель:** приблизить DesktopOrganizer по функциональности и поведению к Stardock Fences.

**Статус на 2026-08-16.** Фазы 0–9 закрыты. Реализованы: встраивание панелей в слой рабочего стола (Progman/WorkerW), убирание из taskbar/Alt+Tab, перетаскивание за заголовок, привязка к сетке, два вида (список/плитка), drag&drop файлов, сохранение состояния в JSON, окно настроек и трей; файловые операции, поведение панелей, внешний вид, десктоп-жесты, Folder Portals, страницы/swipe, снапшоты раскладки, multi-monitor; ресайз кратно сетке иконок, фильтр содержимого панели, кнопка roll-up; скрытие/показ иконок рабочего стола (единый переключатель, без выборочного режима — см. ADR-006); панель системных иконок (SystemIconsPanel) как второй независимый тип desktop-window. Плюс 2026-07-26 (см. `docs/prompts/outstanding-followups.md`, пункты 1–4): one-time Move-хинт, тест кросс-volume `Restore`, очистка managed-копий при удалении панели ([ADR-010](../adr/ADR-010-managed-storage-cleanup-on-panel-removal.md)) и re-clamp панелей при уменьшении рабочей области монитора. Плюс ещё 2026-07-26 (см. `docs/prompts/grid-snap-drag-and-settings-order.md`, [ADR-011](../adr/ADR-011-manual-drag-with-magnetic-grid-snap.md)): магнитное покадровое прилипание к сетке во время перетаскивания панели (не только на drop), новая настройка `GridSnapDistance`, и реорганизация окна настроек (порядок групп + перенос «Привязки к сетке» в «Автозапуск и рабочий стол»). Плюс 2026-08-15 — **Фаза 7 «Производительность, память и отказоустойчивость»** ([docs/PLANS/phase-7-performance-hardening.md](PLANS/phase-7-performance-hardening.md), [ADR-012](../adr/ADR-012-performance-and-resilience-hardening.md)): все 11 находок [docs/PERFORMANCE_AUDIT.md](PERFORMANCE_AUDIT.md) закрыты 6 блоками (A–F) — debounced persistence с синхронным flush для структурных изменений, try/catch вокруг колбэка глобального хука жестов + вынос чистой машины состояний, ограниченный LRU-кеш иконок и замороженные кеш-кисти конвертеров, batch-уведомления и O(1) дедуп коллекций ViewModel, авторитетный in-memory кеш манифеста `PanelItemStorageService` с write-through. Плюс 2026-08-16 — **Фаза 8 «UX-паритет значков: выделение и переименование»** ([docs/PLANS/icon-selection-and-rename.md](PLANS/icon-selection-and-rename.md), [ADR-013](../adr/ADR-013-panel-item-selection-and-rename.md)): визуальное выделение значка в обоих видах панели (`ListBox`-контейнеры, подсветка не зависит от `IsSelectionActive`) и честные имена + inline-переименование значков (F2/контекстное меню, физическое переименование на диске, GUID перенесён из имени файла в имя каталога, синхронизация manifest). Плюс ещё 2026-08-16 — **Фаза 9 «Сортировка и ручной порядок значков панели»** ([docs/PLANS/panel-item-sorting.md](PLANS/panel-item-sorting.md), [ADR-014](../adr/ADR-014-panel-item-sorting-and-manual-order.md)): новая настройка `PanelSortMode` (Manual/Name) в окне настроек и контекстном меню заголовка, немутирующая сортировка по имени (собственный natural-order компаратор, папки вперёд), drag-to-reorder значков мышью в обоих видах панели с трансляцией видимого индекса в полный набор при активном `ContentFilter` и авто-переключением в `Manual`. Остаются сквозные задачи (см. ниже).

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
      живого зеркалирования — используйте Folder Portal, `SelectSourceFolderCommand`).
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

> ⚠️ Проверить вживую: переименование требует клавиатурного фокуса, а панель имеет `WS_EX_NOACTIVATE`
> (не активируется по клику). Если `TextBox` не получает ввод — на время правки временно снимать `WS_EX_NOACTIVATE`
> или принудительно ставить фокус нативно. Двойной клик по тайтлу (обновлено 2026-07-26, `DragMove()`
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

**Готово, когда:** панель создаётся жестом, иконки десктопа прячутся, панель умеет показывать реальные иконки рабочего стола.

> ⚠️ Весь нативный код Фазы 4 непроверяем в этой среде (WPF/Windows-only, без `dotnet`) — обязательно
> протестировать на Windows 10 и 11. Особое внимание: глобальный `WH_MOUSE_LL` (ошибка в колбэке тормозит
> ввод системы) и кросс-процессный `LVM_HITTEST` (зависит от прав/разрядности; при сбое детекция пустоты
> возвращает false → двойной клик не сработает, остаётся тоггл из трея). Захват через папку не отслеживает
> изменения вживую (это Фаза 5, `FileSystemWatcher`) и не прячет оригиналы сам — за это отвечает скрытие иконок.

---

## Фаза 5. Продвинутое (после паритета)

- [x] **Folder Portals** — панель live-отражает содержимое реальной папки (FileSystemWatcher).
      `IFolderWatcher`/`FolderWatcherFactory`, дебаунс 300 мс, фильтр desktop.ini/hidden, guard на shutdown.
      `SelectSourceFolderCommand`, блокировка ручных операций в портале. Persistence через `SourceFolderPath`.
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

> ⚠️ Нативный слой (PIDL, IContextMenu, SHQueryRecycleBin) непроверяем вне Windows — обязательно
> верифицировать на Windows 10 и 11 (11 пунктов, см. ADR-007 «Manual verify»).

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

> ⚠️ Ни один из блоков A–F не проверен вживую на Windows — среда разработки ограничена
> `dotnet build`/`dotnet test`. Особое внимание блоку B (единственный блок с риском подтормаживания
> ввода системы при регрессии в глобальном хуке) — см. полный список manual verify в
> [docs/PLANS/phase-7-performance-hardening.md](PLANS/phase-7-performance-hardening.md), «Сводный
> список manual verify».

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

> ⚠️ **Manual verify (Win10/Win11) — не выполнено, среда разработки ограничена
> `dotnet build`/`dotnet test`.** Полный список (см. план, §9):
> - Клик по значку в плитке и в списке — подсветка видна, стиль одинаковый, читается на тёмном и
>   светлом `BackgroundColor` при `BackgroundOpacity` 0.3/0.8/1.0.
> - Клик по пустому месту снимает выделение; клик по заголовку панели не «залипает».
> - ПКМ по значку выделяет его и открывает меню на нём же.
> - **R1 (CRITICAL, не верифицировано никогда — тот же открытый риск, что и для переименования
>   заголовка панели, Фаза 2):** контекстное меню → «Переименовать» → в `TextBox` реально печатается
>   текст под окном с `WS_EX_NOACTIVATE`; `Enter` коммитит, `Esc` отменяет, клик вне — коммитит. То же
>   для `F2`. **Если ввод не приходит — эскалация на `win32-interop-specialist`**: добавить
>   `IDesktopIntegrationService.SetActivationAllowed(IntPtr hwnd, bool)` (временно снимать
>   `WS_EX_NOACTIVATE` на время правки, возвращать на коммит/отмену) + `SetForegroundWindow`.
> - Перетащить файл и папку с рабочего стола в панель → отображаются под своими именами; проверить в
>   Проводнике: `%LocalAppData%\DesktopOrganizer\PanelItems\<guid>\<оригинальное имя>`.
> - Панель, созданная до обновления, с уже перенесёнными файлами → имена без GUID-хвоста;
>   переименование такого элемента → он переехал в `<guid>\<новое имя>` и продолжает открываться.
> - Переименовать managed-элемент → «Убрать из панели» → файл вернулся на рабочий стол под новым
>   именем; повторить при существующем одноимённом файле на столе (`name (2).ext`).
> - Переименовать элемент в портальной панели → имя изменилось в реальной папке; панель не задвоила
>   элемент.
> - Занятое имя и невалидное имя (`a/b`, `CON`, пустое) → файл цел, показан ненавязчивый хинт.
> - Ресайз панели в плиточном виде остаётся кратным сетке иконок; перенос строк, скролл при
>   переполнении, `IconSize` 32/48/64 — раскладка не ломается (риск замены `ItemsControl` → `ListBox`
>   в wrap-виде).
> - Смена `ContentFilter` и тик Folder Portal при выделенном элементе — выделение сохраняется.
> - Двойной клик по значку по-прежнему открывает файл (не начинает переименование).
> - DPI 125%/150%: подсветка и `TextBox` не съезжают относительно иконки.

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

> ⚠️ **Manual verify (Win10/Win11) — не выполнено, среда разработки ограничена
> `dotnet build`/`dotnet test`.** Ничего из геометрии drag&drop, поведения курсора и
> `WS_EX_NOACTIVATE`-допущений не воспроизводимо вне живого Windows-десктопа. Полный список (см. план,
> §9, 17 пунктов) — здесь наивысший приоритет:
> - **R1 (CRITICAL, уже пофикшено в коде, но требует обязательной первоочередной проверки):**
>   перетащить файл и папку **с рабочего стола** в панель по-прежнему работает как раньше (курсор
>   `Move`, оригинал исчезает с рабочего стола, элемент появился в панели, one-time Move-хинт при
>   первом разе). Это первое, что могло сломаться при добавлении `AllowDrop`/обработчиков reorder на
>   `FileListHost` — см. ADR-014, «Критическая деталь реализации Win32 OLE drag&drop»: по контракту
>   OLE drag&drop, `Effects == None` на последнем `DragOver` перед отпусканием кнопки приводит к
>   `DragLeave` вместо `Drop`, независимо от `Handled`, — если бы обработчики трогали `e.Effects` для
>   чужого формата хоть раз, входящий drag&drop сломался бы функционально, а не только визуально.
> - **R2 (CRITICAL, тот же класс риска, что ADR-013 R1):** `DoDragDrop` из окна с `WS_EX_NOACTIVATE` —
>   перетащить значок внутри панели в плиточном и в списочном виде: жест стартует, курсор показывает
>   `Move`, `DragOver` приходит, drop срабатывает. Механизм скопирован с уже рабочего прецедента
>   `SystemIconsPanelWindow` (тот же desktop-слой), но никогда не верифицирован вживую. **При отказе —
>   эскалация на `win32-interop-specialist`** (тот же план-фолбэк, что ADR-013 R1:
>   `IDesktopIntegrationService.SetActivationAllowed` на время жеста).
> - Перетащить значок в начало/середину/конец списка (обе стороны — вверх и вниз); позиция после drop
>   совпадает с местом, где отпустили кнопку.
> - Панель в режиме `By name` → перетащить значок → режим переключился на `Manual`, список **не
>   дёрнулся**, элемент встал ровно туда, куда тянули; порядок переживает перезапуск.
> - Фильтр `Hide shortcuts`/`Folders only` включён → переставить элемент → вернуть `Show all` —
>   скрытые элементы на месте, их относительный порядок сохранён, переставленный — на новой позиции.
> - Двойной клик по-прежнему открывает файл; F2/inline-редактирование не запускает drag; короткий клик
>   с микро-дрожанием руки не начинает перетаскивание (системный порог).
> - Портальная панель: `ComboBox` в настройках задизейблен с тултипом, пункты «Arrange icons»
>   недоступны, содержимое отсортировано по имени; новый файл в зеркалируемой папке появляется на
>   алфавитном месте, а не в конце; попытка перетащить значок — жест не проходит.
> - Индикатор вставки: вертикальная линия в плиточном виде, горизонтальная — в списочном; исчезает при
>   уходе курсора и после drop'а. DPI 125%/150%: индекс вставки вычисляется корректно.

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

> ⚠️ **Manual verify (Win10/Win11) — не выполнено ни для одного блока батча, среда разработки
> ограничена `dotnet build`/`dotnet test`.** Полный сводный список — `docs/PLANS/ui-polishing-1.md` §8
> (17 пунктов, блоки A–E); архитектурно значимые пункты продублированы в
> [ADR-015](../adr/ADR-015-panel-icon-grid-geometry.md) (Блок B, пп. 1–5) и
> [ADR-019](../adr/ADR-019-sidebar-top-level-virtual-desktop.md)/[ADR-020](../adr/ADR-020-sidebar-zorder-back-without-topmost.md)
> (Блок E, ADR-020 заменяет пункт 7/8 списка ADR-019 после отказа от `Topmost`; ADR-019 заменяет
> устаревший список из Superseded [ADR-016](../adr/ADR-016-virtual-desktop-visibility.md)). Наивысший
> приоритет:
> - **Блок B** — рамка выделения на первой колонке/строке не срезана; левый край симметричен шву;
>   top-align иконки на неквадратных источниках; пересчёт сетки на **обоих краях** диапазона `IconSize`
>   (16px и 96px), не только на дефолтном 48px (ADR-015, «Manual verify»);
> - **Блок A** — клик по заголовку/кнопке/resize снимает выделение; клик в другой панели снимает
>   выделение в первой; клик внутри активного inline-rename не отменяет переименование (регресс ADR-013);
> - **Блок C** — глифы `Segoe MDL2 Assets` отрисованы ожидаемыми пиктограммами (не «тофу»-квадратики),
>   в т.ч. новый `ViewAll`/`List` для кнопки View;
> - **Блок D** — визуал выделения боковой панели совпадает с fence-панелью; ПКМ/двойной клик/drop на
>   «Корзину» не сломаны миграцией на `ListBox`; **drag-reorder иконок боковой панели действительно
>   работает** (закрывает недовыполненный пункт 11 [ADR-007](../adr/ADR-007-system-icons-panel-window-type.md));
> - **Блок E** — ON: панель следует за активным столом с задержкой ~1 с (Win+Ctrl+←/→); OFF: панель
>   остаётся на «Desktop 1» и не видна на других столах; удаление якорного стола → self-heal без
>   зависаний; панель по-прежнему отсутствует в Alt+Tab/панели задач/Task View, несмотря на новый
>   порядок применения стилей (после `Show()`); поведение на системе без виртуальных столов
>   (`IsSupported == false`) — no-op, не падает. Полный список из 16 пунктов —
>   [ADR-019](../adr/ADR-019-sidebar-top-level-virtual-desktop.md). **Follow-up (`Topmost` убран,
>   ADR-020):** визуальная видимость панели над обоями/иконками (Z-order по хэндлам не доказывает
>   рендеринг); поведение на Windows 10 (не проверено — там реально происходит `WorkerW`-расщепление,
>   в отличие от машины разработки); совместимость со сторонними wallpaper-движками (Wallpaper Engine,
>   Lively, Rainmeter); заметность ~300 мс мигания при переключении окон; клик по панели при частичном
>   перекрытии другим (не topmost) окном. Полный список —
>   [ADR-020](../adr/ADR-020-sidebar-zorder-back-without-topmost.md).

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

> ⚠️ **Manual verify (Win10/Win11) — не выполнено, среда разработки ограничена `dotnet build`/`dotnet
> test`.** Полный список — [ADR-017](../adr/ADR-017-panel-anti-overlap-on-drag.md) и
> `docs/PLANS/panel-anti-overlap.md` §8: упор в границу без дрожания; «убежавший» курсор возвращается
> 1:1; скольжение вдоль границы с блоком по перпендикулярной оси; быстрый рывок мыши без
> туннелирования; mouse-up у стены с включённым grid-snap не впрыгивает в соседа; свёрнутая панель
> блокирует только высотой заголовка; панель на другой странице/десктопе никогда не блокирует;
> пред-существующие пересечения из сохранённого состояния не трогаются при загрузке; мультимонитор/
> смешанный DPI — риск некритичный.

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

> ⚠️ **Manual verify (Win10/Win11) — не выполнено, среда разработки ограничена `dotnet build`/`dotnet
> test`.** Полный список (19 пунктов) — [ADR-018](../adr/ADR-018-adaptive-icon-grid-layout.md) и
> `docs/PLANS/adaptive-icon-grid.md` §12: воздушность полей (`m=x/2`) не выглядит чрезмерной; полный ряд
> растягивает зазоры равномерно, неполный прижат влево; живой ресайз ширины/высоты без дрожания;
> прокрутка колесом доскролливает последний ряд полностью; drag&drop reorder/файлов и выделение без
> регресса; `IconSize` на обоих краях диапазона (16px/96px); **однoколоночные сохранённые панели —
> ожидаемое разовое увеличение ширины при первом снапе, не баг**; пустая/однoэлементная панель без
> артефактов; List-режим не изменился; DPI 125%/150% без потери колонки (регресс-защита M-1); дробный
> `IconSize` без снапа к тику действительно недостижим через UI. **Отдельная ключевая проверка** —
> порог появления/исчезновения вертикального скроллбара (M-2, trade-off, ещё не оценён визуально
> пользователем): насколько заметен «скачок» числа колонок при пересечении этого порога — результат
> определяет, нужен ли follow-up на вариант (a) резервирования ширины скроллбара или (b) `Visible`
> всегда (оба уже спроектированы в ADR-018, не блокируют текущий батч).

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

> ⚠️ **Manual verify (Win10/Win11) — не выполнено.** Дополнительно к списку выше — пункты 20–23
> [ADR-018](../adr/ADR-018-adaptive-icon-grid-layout.md): увеличенный зазор (~12px→~16px) заметен и
> приемлем на обеих осях (горизонталь И вертикаль); точки снаппинга для панелей с 2+ колонками/рядами
> сдвинулись — сдвиг воспринимается как разовая корректировка, не дрожание; новый минимум ширины в
> Grid-режиме гарантирует минимум одну колонку на всём диапазоне `IconSize` (16px/96px); List-режим —
> минимум ширины не зависит от `IconSize` (регресс-проверка).

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

> ⚠️ **Manual verify (Win10/Win11) — не выполнено.** Пункт 24 [ADR-018](../adr/ADR-018-adaptive-icon-grid-layout.md):
> Grid-панель, `IconSize=16` (минимум слайдера) — потянуть левый край внутрь мимо 120px; правый край не
> должен скакать. Математика покрыта юнит-тестами, сам визуальный скачок `Left`/`Width` при живом
> драге — WPF-специфичное поведение, не проверяемое юнит-тестами.

---

## Сквозные задачи

- [~] Тесты на новые сервисы: `FolderWatcherTests`, `PageManagerServiceTests`, `MonitorServiceTests` реализованы
      (57 pass). Не покрыты: `IFileLauncher`, ресайз/roll-up-логика, `IAutostartService` — отложено.
- [ ] Проверка на Windows 10 и Windows 11 отдельно (WorkerW-хак, см. ADR-001). **Manual verify — не выполнена** (среда без GUI).
- [~] Поведение при смене обоев, DPI-масштабировании и переключении виртуальных рабочих столов.
      **Закрыто частично 2026-07-26** (`docs/prompts/outstanding-followups.md`, пункт 4): re-clamp
      панелей при уменьшении рабочей области монитора (смена разрешения/масштаба/появление таскбара)
      реализован — `WindowManager.OnMonitorsChanged` пересчитывает позицию панелей, когда монитор не
      исчез, но его рабочая область сократилась; `ClampIntoMonitor` учитывает полный rect (Position+Size).
      Manual verify (Win10/Win11) для этого сценария остаётся открытым.
      **Осознанно не реализовано, открытый архитектурный вопрос:** полноценная per-monitor DPI-
      осведомлённость (`WM_DPICHANGED`) — приложение работает в режиме System-DPI-aware (нет
      `app.manifest`), под которым это сообщение не приходит; переход на Per-Monitor-V2 — отдельное
      архитектурное решение с широким кругом затрагиваемых мест (WorkerW embedding, координаты во всех
      chromeless-окнах), требует отдельного планирования (`fences-feature-planner`) при взятии в работу.
      Обои и виртуальные рабочие столы — по-прежнему не реализовано.
- [x] **Move-хинт при drag&drop — закрыто 2026-07-26** (`docs/prompts/outstanding-followups.md`, пункт 1):
      one-time хинт о Move-семантике при первом drop реализован (`IAppSettings.HasShownMoveHint`,
      `ChildWindowViewModel.MoveHintRequested`, `Popup` в `ChildWindow.xaml`, автозакрытие 4с).
- [x] **Очистка осиротевших managed-копий при удалении панели — закрыто 2026-07-26**
      (`docs/prompts/outstanding-followups.md`, пункт 3, [ADR-010](../adr/ADR-010-managed-storage-cleanup-on-panel-removal.md)).
- [x] **Тест на кросс-volume откат в `Restore` — закрыто 2026-07-26**
      (`docs/prompts/outstanding-followups.md`, пункт 2).
- [ ] **Открытый архитектурный вопрос (не решён, бэклог): «удалить панель» стирает managed-файлы
      мимо Корзины.** В эталонном Stardock Fences удаление fence никогда не уничтожает файлы — fence
      чисто визуальная группировка, иконки остаются на рабочем столе. В этом приложении из-за
      архитектуры `IPanelItemStorageService` (файлы физически перемещаются при попадании в панель,
      [ADR-009](../adr/ADR-009-drag-drop-move-semantics.md)) удаление панели с managed-файлами —
      единственный сценарий, где реально стираются пользовательские файлы мимо Корзины. Confirm-диалог
      ([ADR-010](../adr/ADR-010-managed-storage-cleanup-on-panel-removal.md), Решение 2) митигирует
      риск неожиданности, но не устраняет расхождение с духом Fences. Возможное направление на
      будущее — «удалить панель» = вернуть managed-файлы на рабочий стол (переиспользуя
      `IPanelItemStorageService.Restore`, как уже делает «Убрать из панели» для одного элемента) вместо
      их стирания. Не реализовывать без отдельного обсуждения с пользователем/`fences-feature-planner`.
- [ ] **Manual verify (Win10/Win11) — Move при drag&drop** (см. Фаза 1, [ADR-009](../adr/ADR-009-drag-drop-move-semantics.md)):
      - Drag файла/папки с рабочего стола в панель → курсор показывает **Move** (не Copy), оригинал
        исчезает с десктопа сразу после drop, элемент в панели рабочий.
      - Реальный cross-volume перенос (перетащить файл/папку с одного тома на другой, где живёт
        `%LocalAppData%`) → файл перемещён полностью, оригинал удалён только после завершения копии.
      - «Убрать из панели» → файл физически вернулся на исходное место; отдельно проверить коллизию
        имён (на исходном месте уже лежит одноимённый файл → `name (2).ext`) и воссоздание удалённой
        исходной родительской папки.
      - Старая панель (сохранённая до перехода Copy→Move) → «Убрать из панели» не падает, деградирует
        до удаления managed-копии без восстановления (ожидаемо).
      - Manual verify (Win10/Win11) — подтверждение удаления панели: диалог показывает предупреждение
        с иконкой Warning только когда у панели есть managed-файлы; для панелей без них — нейтральный
        текст без предупреждения.
- [ ] **Manual verify (Win10/Win11) — магнитный снаппинг при перетаскивании панели** (см. Фаза 2,
      [ADR-011](../adr/ADR-011-manual-drag-with-magnetic-grid-snap.md)):
      - Реальная плавность магнита при перетаскивании мышью — залипание в пределах `GridSnapDistance`
        без рывков, свободное движение вне порога, оси X/Y снапаются независимо.
      - Поведение на HiDPI-масштабах 125%/150% — именно там был найден и исправлен **MAJOR**-баг
        (device-пиксели vs DIU при пересчёте дельты курсора), вживую не проверялся.
      - Alt+Tab / потеря фокуса окна посреди драга — `LostMouseCapture` должен корректно завершать
        жест (без «залипшего» `_isDragging`).
      - Поведение у краёв экрана и на мультимониторе (в т.ч. отрицательные координаты монитора слева
        от primary).
      - Двойной клик vs одиночный драг по тайтлбару (тайминги `ClickCount`) — не должно быть ложного
        срабатывания roll-up при быстром одиночном драге или наоборот.
      - Подбор дефолта `GridSnapDistance=12` px «на ощупь» под привычное поведение Fences — при
        необходимости скорректировать значение по умолчанию.
- [ ] **Manual verify (Win10/Win11) — Фаза 7, блоки A–F** (см.
      [ADR-012](../adr/ADR-012-performance-and-resilience-hardening.md) и
      [docs/PLANS/phase-7-performance-hardening.md](PLANS/phase-7-performance-hardening.md), «Сводный
      список manual verify»): дебаунс сохранения не пишет в `user.config` на каждое движение панели;
      read-only `user.config` не роняет приложение; лассо создаёт панель и подавляет контекстное меню, а
      искусственно бросающий подписчик не тормозит ввод системы; долгая сессия с активным Folder Portal
      не растит private bytes/GDI-хэндлы неограниченно; drop 20+ файлов заметно быстрее, «Убрать из
      панели» возвращает файл даже после kill процесса сразу после drop.
- [ ] **Открытый архитектурный риск (не решён, бэклог), см. ADR-012 Решение 4:** нет single-instance
      guard между процессами приложения. Write-through-кеш манифеста `PanelItemStorageService`
      (Фаза 7, блок F) делает «один процесс — единственный владелец storage root в рамках сессии»
      более несущим инвариантом, чем раньше; межпроцессная синхронизация по-прежнему не гарантирована
      технически (только поведенчески — приложение не предполагает нескольких одновременных запусков).
      Возможное направление — именованный mutex/pipe guard, отдельным пунктом.
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
- [ ] **Новый пункт (обнаружен закрытием предыдущего, 2026-09-05):** массовая чистка ~450-457
      StyleCop-предупреждений (SA*), впервые видимых после фикса выше. Не блокирует, требует отдельного
      захода (`dotnet-services-engineer`/`wpf-ui-builder`, варнинги распределены по всем проектам).
- [ ] **Manual verify (Win10/Win11) — Фаза 8 (см.
      [ADR-013](../adr/ADR-013-panel-item-selection-and-rename.md)):** полный чек-лист приведён внутри
      секции Фазы 8 выше (13 пунктов, перенесены из плана
      [docs/PLANS/icon-selection-and-rename.md](PLANS/icon-selection-and-rename.md) §9) — наивысший
      приоритет: R1 (реальный ввод текста в inline-`TextBox` под `WS_EX_NOACTIVATE` — эскалация на
      `win32-interop-specialist` при отказе) и визуальная подсветка выделения на разных
      `BackgroundColor`/`BackgroundOpacity`, поскольку оба поведения принципиально не воспроизводимы
      без живого Windows-десктопа.
- [ ] **Бэклог-тикет, осознанно вне объёма Фазы 8 (см. план §7 Блок A, ADR-013 Решение 1):**
      multi-select значков панели (Ctrl/Shift-клик, рамка выделения при драге по пустому месту) — не
      реализован. Требует множественных команд контекстного меню (`Открыть`/`Убрать`/`Свойства` для
      набора элементов) и меняет сигнатуры существующих команд.
- [ ] **Бэклог-тикет, осознанно вне объёма Фазы 8 (см. план §7 Блок C, ADR-013 Решение 6):**
      «медленный второй клик» по уже выделенному значку как альтернативный триггер переименования
      (Explorer/Fences-паттерн) — не реализован. Отклонён для первой итерации: напрямую конкурирует с
      двойным кликом «Открыть» и требует таймера на `GetDoubleClickTime`; вносится отдельным пунктом
      после ручной верификации F2/контекстного меню (в частности R1 выше).
- [ ] **Manual verify (Win10/Win11) — Фаза 9 (см.
      [ADR-014](../adr/ADR-014-panel-item-sorting-and-manual-order.md)):** полный чек-лист приведён
      внутри секции Фазы 9 выше (17 пунктов, перенесены из плана
      [docs/PLANS/panel-item-sorting.md](PLANS/panel-item-sorting.md) §9) — наивысший приоритет:
      **R1** (уже пофикшено в коде, но требует первоочередной проверки — входящий drag&drop файлов с
      рабочего стола в панель по-прежнему работает после добавления `AllowDrop`/reorder-обработчиков
      на `FileListHost`; это функциональная регрессия главного сценария приложения, а не косметика,
      см. ADR-014 «Критическая деталь реализации Win32 OLE drag&drop») и **R2** (`DoDragDrop` из окна
      `WS_EX_NOACTIVATE` — тот же класс непроверенных нативных допущений, что ADR-013 R1; эскалация на
      `win32-interop-specialist` только по результату ручной проверки).
- [ ] **Бэклог-тикет, осознанно вне объёма Фазы 9 (см. план §8 Risk R7, ADR-014):** перетаскивание
      значка **из** панели (на рабочий стол или в другую панель) — не реализован; сейчас drag-reorder
      работает только внутри одной панели, и drop чужого reorder-payload'а (путь не из своей панели)
      безопасно отклоняется. Требует семантики Move + `Restore` из managed storage и межпанельного
      протокола.
- [ ] **Бэклог-тикет, осознанно вне объёма Фазы 9 (см. план §8 Risk R8, ADR-014):** внешний drop файла
      с рабочего стола/из Проводника **всегда** добавляет элемент в конец списка (в режиме `Manual`),
      тогда как Fences вставляет его в точку drop'а под курсором. Требует протаскивания индекса
      вставки в `AddDroppedFiles`/`AddFilesCore` (сейчас всегда `append`); инфраструктура для этого
      (`BaseViewModel.MoveItem` + вычисление индекса вставки, `ChildWindow.ComputeInsertionGap`) уже
      готова из Фазы 9 и ляжет сверху без слома.
- [ ] **Бэклог-тикет, осознанно вне объёма Фазы 9 (план §4 Блок B, ADR-014):** общий
      `EnumDisplayConverter` для пользовательских enum-`ComboBox` (`PanelViewMode`, `PanelSortMode`) —
      сейчас оба отображаются как сырые имена членов enum (`List`/`Wrap`, `Manual`/`Name`), без
      локализуемого/дружественного текста.

---

## Рекомендуемый порядок

Фаза 0 → Фаза 1 → Фаза 2 → Фаза 3 → Фаза 4 → Фаза 5 → Фаза 6 → Фаза 7 → Фаза 8 → Фаза 9.

Фазы 1–2 дают максимальный эффект «как Fences» при малых затратах. Фаза 4 (особенно захват реальных иконок) — самый крупный и концептуально важный блок, его стоит начинать только после стабилизации 1–3.
