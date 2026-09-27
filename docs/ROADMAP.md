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

> **Устарело, уточнено 2026-09-26 (ADR-031).** Пункты трея «Скрыть/показать значки» и «Собрать значки
> рабочего стола», описанные выше, были убраны из трея в v0.2.0 (окно настроек взяло на себя чекбокс
> «Show desktop icons», трей — только «Настройки»/«Выход»). ADR-031 вернул в трей checkable-пункт
> **«Show desktop icons»** (синхронизирован с реальной видимостью через `ITrayIconManager
> .SetDesktopIconsVisible`/`ToggleDesktopIconsRequested`) и одновременно убрал одноимённый чекбокс из
> окна настроек — это сессионная команда, не преференция. «Собрать значки рабочего стола» («Захват
> иконок» выше) в трей **не вернулся**: Folder Portal на папку Desktop настраивается через диалог
> «Customize panel…» (Browse…), отдельного пункта трея для него нет.

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

> ⚠️ **Manual verify (Win10/Win11) — не выполнено, среда разработки ограничена `dotnet build`/`dotnet
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

> ⚠️ **Manual verify (Win10/Win11) — не выполнено, среда разработки ограничена `dotnet build`/`dotnet
> test`.** Полный список (18 пунктов, V-1..V-18) — `docs/PLANS/phase-5-ambient-accent-ui.md` §7.2 и
> [docs/Refactoring/Testing/ambient-accent-visual-matrix.md](Refactoring/Testing/ambient-accent-visual-matrix.md).
> Наивысший приоритет:
> - новая нейтральная поверхность видима, заливки сплошным цветом категории нет (V-2);
> - все 4 состояния High Contrast читаемы, включая известные ограничения (`Color.Overlay.Hover`,
>   активная вкладка настроек) из ADR-025 (V-14);
> - Dragging vs Resizing отличимы по курсору в реальном жесте, а не только по расчётным opacity/
>   толщине индикатора (V-11/V-12);
> - selected+hover читаемы на разных `SurfaceOpacity`/`SurfaceStyle` для Grid/List/Folder Portal/
>   `SystemIconsPanel` (V-5..V-8);
> - ARGB-акцент из существующего v3-бэкапа (`user.config`/JSON-стейта до апгрейда) корректно
>   переезжает в `AccentColor` при миграции v3→v4, дефолтные панели получили нейтральный акцент
>   (V-18);
> - DPI 125%/150%: индикатор акцента не размывается, border 1px не исчезает (V-15);
> - отсутствие layout-shift акцент-полосы при смене interaction-состояния (толщина индикатора меняется
>   визуально, но не должна сдвигать соседний контент/геометрию панели).
>
> **Добавлено P07-visual fix-cycle 1** (см. выше): 8-DIP скругление угла с расширенным resize-grip —
> диагональный ресайз ловится в прежней зоне на 100/125/150% DPI; header-кнопка при наведении читаема на
> светлых И тёмных обоях (не только на тёмных, как раньше); длинное имя файла в List-режиме обрезается
> многоточием, не вылезает за границу панели; `AutomationProperties.Name` панели/кнопок — через
> Narrator/Accessibility Insights.
>
> **Добавлено P02–P08 (пакет workspace-grid, см. пункты выше в этом же разделе).** Полная сводная
> manual-verify матрица для ЭТОЙ части работы (не дублируется здесь построчно) —
> [docs/REVIEWS/workspace-grid-manual-verify.md](REVIEWS/workspace-grid-manual-verify.md) (WG-MV/IL-MV/
> VIS-MV/ICO-MV/MIG-MV, объединяет план P03/P04 §7, ADR-029 manual verify, P07-visual §9/§11, P08
> findings, P02b rollback); известный/задокументированный долг — [docs/REVIEWS/workspace-grid-debt.md](REVIEWS/workspace-grid-debt.md).

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

> ⚠️ **Manual verify (Win10/Win11) — не выполнено, среда разработки ограничена `dotnet build`/`dotnet
> test`.** Полная матрица MV-1…MV-15 —
> [docs/PLANS/reference-panel-chrome.md](PLANS/reference-panel-chrome.md) §6 и ADR-030's собственный раздел
> «Manual verify». Наивысший приоритет: отсутствие «линии среза» glow на 100–175% DPI (MV-2); hairline-рамки
> не размыты на дробном DPI (MV-3); ноль layout jitter в реальном жесте (MV-4); hit catcher реально ловит
> клик на внешних 2 DIP и в геометрическом углу на обоих путях ресайза (MV-5/MV-6); сохранённая раскладка
> (≥5 панелей) не теряет колонки и не переписывает `application-state.json` только из-за загрузки (MV-10);
> все 4 темы High Contrast (MV-11); 10/20 панелей без заметного падения FPS, включая скролл/hover в большой
> панели (MV-12, glow пересчитывается на любую перерисовку внутри панели, не только resize).

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

> ⚠️ **Manual verify (Win10/Win11) — не выполнено, среда разработки ограничена `dotnet build`/`dotnet
> test`.** Консолидированная матрица (план MV-1…MV-27 + дополнения MV-28…MV-39 по итогам FIX-A/FIX-B) —
> [docs/PLANS/settings-window-redesign.md](PLANS/settings-window-redesign.md), раздел «Реализация vs
> план». Наивысший приоритет: фокус диалога поверх `WS_EX_NOACTIVATE`-контекстного меню, в т.ч. после
> Win+D (MV-3/MV-4/MV-28); retarget диалога между панелями без visual residue (MV-5/MV-29); лассо на
> не-default странице переживает рестарт (MV-16); галка трея «Show desktop icons» не расходится с
> реальностью (MV-19/MV-32); откат на v0.5.1 поверх данных этой ветки не даёт read-only-сессию (MV-27,
> гейт инцидента 2026-09-24).

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
      ADR-032 «Реализация»/follow-up-кандидаты. Manual verify (Win10/Win11) — MV-L1..MV-L15
      (`docs/PLANS/localization.md` §7.3), обязательные MV-L2/L4/L6/L7/L12, не выполнена (среда без GUI).

---

## Рекомендуемый порядок

Фаза 0 → Фаза 1 → Фаза 2 → Фаза 3 → Фаза 4 → Фаза 5 → Фаза 6 → Фаза 7 → Фаза 8 → Фаза 9.

Фазы 1–2 дают максимальный эффект «как Fences» при малых затратах. Фаза 4 (особенно захват реальных иконок) — самый крупный и концептуально важный блок, его стоит начинать только после стабилизации 1–3.
