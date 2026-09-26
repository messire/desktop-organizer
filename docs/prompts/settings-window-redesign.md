# Фича: переустройство окна настроек — боковая навигация + диалог «Customize panel»

Ветка: `feature/settings-window-redesign` (от `rc/0.6.0`). Дата постановки: 2026-09-26.
Связанные документы: план `docs/PLANS/settings-window-redesign.md`, ADR-031, обзор с вайрфреймами
(артефакт «Settings Window Redesign», вариант B выбран пользователем).

## Задача

Окно настроек (`MainWindow`, ADR-002) сейчас — один скролл из семи раскрытых `Expander`'ов плюс
полоска вкладок страниц сверху. В нём смешаны четыре области действия: приложение целиком, текущая
страница, одна конкретная панель и разовые команды. Нужно переустроить окно по **варианту B**:

1. **Боковая навигация** (как в конфигураторе Stardock Fences и в «Параметрах» Windows 11) с
   разделами глобального scope: General · Layout & snapping · Panels · New panel defaults · Pages ·
   System icons · Backup · About.
2. **Per-panel настройки уезжают из окна** в новый **немодальный диалог «Customize panel…»**, который
   открывается из контекстного меню панели и кнопкой в разделе Panels. Это модель Fences:
   «Configure Fences» для глобального, правый клик по fence — для локального.
3. **Режимы и команды сессии уходят из настроек**: чекбоксы «Edit layout mode» и «Show desktop icons»
   удаляются из окна (остаются трей / контекстное меню / жест; в трей возвращается пункт
   «Show desktop icons»).
4. Каждая настройка — **карточка** «заголовок + пояснение + контрол справа» (паттерн SettingsCard),
   вместо тултипов. Отключённый контрол объясняет причину в описании.

## Инвентарь (что есть сейчас, что куда едет)

| Область | Настройка | Сейчас | Целевое место |
|---|---|---|---|
| Приложение | Start with Windows | SW | **General** |
| Приложение | Show desktop icons | SW чекбокс + dbl-click по столу | **убрать из SW**, вернуть пункт в трей |
| Приложение | Native resize (experimental) | SW | General → **Advanced** (expander) |
| Приложение | `HasShownMoveHint` | нет UI | General → Advanced → «Reset one-time hints» |
| Раскладка | `GridSize` (1 = выкл) | SW слайдер 1–128 | **Layout & snapping**: toggle «Snap panel position to grid» + шаг; при выкл. = 1 |
| Раскладка | `GridSnapDistance` | SW | Layout & snapping, **enabled только при GridSize > 1** |
| Раскладка | `SnapResizeToIconGrid` | SW | Layout & snapping |
| Раскладка | Edit layout mode (Ctrl+E) | SW + трей + CM + Ctrl+E + Esc | **убрать чекбокс из SW**; Ctrl+E в окне оставить |
| Раскладка | `IsLayoutLocked` | SW + трей + CM | Layout & snapping (toggle), дубль трея сохраняется |
| Раскладка | `NormalModeLayoutGestures` | SW combo | Layout & snapping → радиогруппа из 3 |
| Панели | список панелей, Add / Remove / Align all | SW | **Panels**: селектор страницы (бывшая полоска вкладок) + список с подзаголовком (view · N items · Portal) + Add · Customize… · Move to page ▾ · Remove · Align all (текущая страница) |
| Панель | Title, Accent, Text color, Opacity, Surface style, Icon size, View, Sort | SW «Selected panel settings» | **диалог Customize panel** |
| Панель | Content filter, Icon layout, Auto-size, Align to grid, Size preset | только CM | **диалог Customize panel** (CM остаётся) |
| Панель | Folder Portal (`SourceFolderPath`) | **нет UI** (команда не привязана) | **диалог Customize panel**: Browse… / Clear |
| Панель | `PageId` | нет UI | Panels → «Move to page» |
| Дефолты | Accent, Text color, Opacity, Icon size, Panel geometry, Icon layout | SW | **New panel defaults** |
| Дефолты | `DefaultSurfaceStyle` | **нет UI** (есть в `GlobalDefaultsViewModel`) | New panel defaults → combo |
| Дефолты | «Take from selected panel» | — | New panel defaults → кнопка (копирует Accent/Text/Opacity/IconSize/SurfaceStyle/Geometry/IconLayout из выбранной в Panels панели) |
| Страницы | add / remove / switch | полоска вкладок сверху SW; Ctrl+PgUp/PgDn | **Pages**: список, Add, Remove (не «default»), «Make current». Rename/reorder — волна 2 |
| Сист. панель | Enable, all virtual desktops (+warning), size, visible items | SW | **System icons** без изменений содержимого |
| Снапшоты | Save / Restore / Delete | SW | **Backup**: «Save as…» с запросом имени; Restore с подтверждением (есть); **Delete с подтверждением** (сейчас нет — расхождение с XML-doc); Export/Import — бэклог DO-012, в UI не показывать |
| About | — | нет | **About**: версия сборки, папка хранения (`%LOCALAPPDATA%\DesktopOrganizer`, кнопка «Open»), ссылка на диагностику |

## Решения (зафиксированы, не пересматривать в реализации)

- **D-1. Persistence не трогаем** (CLAUDE.md, ADR-024, schemaVersion 5). Никаких новых ключей в
  `IAppSettings`/`user.config`, никаких DTO/миграций. Последний открытый раздел окна **не** сохраняется
  между запусками (держим в памяти синглтона `MainWindowViewModel`). Всё, что «нужно поле» — вне объёма,
  фиксируется в плане как `CLARIFICATION_NEEDED` для пользователя.
- **D-2. Навигация — самодельная**: `ListBox` разделов слева + `ContentControl` с `DataTemplate` по
  `SettingsSection` (enum) справа. Никаких сторонних NavigationView-библиотек. Каждый раздел — отдельный
  `UserControl` в `src/DesktopOrganizer.UI/Views/Settings/`. Окно `MainWindow` остаётся настройками
  (ADR-002 не меняется), размер ~ 720×560, `ResizeMode=CanMinimize` можно оставить.
- **D-3. Диалог `PanelCustomizeWindow`** — обычное активируемое окно (без `WS_EX_NOACTIVATE`), немодальное,
  одно на приложение (повторное открытие для другой панели переиспользует окно и переключает модель),
  заголовок `Customize “{Title}”`, применяет изменения **вживую** через ту же INPC-модель
  `ChildWindowModel` (единый источник правды — инвариант CLAUDE.md). Кнопок OK/Cancel нет, только закрыть.
  Открывается через `IPanelCustomizeService.Show(ChildWindowModel)` (интерфейс уже в репозитории,
  `src/DesktopOrganizer.UI/Services/IPanelCustomizeService.cs`).
- **D-4. Folder Portal в диалоге**: Browse… (через `OpenFolderDialog`) / Clear. Логика подключения
  watcher'а сейчас живёт в `ChildWindowViewModel.SelectSourceFolder`; её нужно сделать реакцией на
  изменение `Model.SourceFolderPath`, чтобы диалог менял только модель. Команда `SelectSourceFolderCommand`
  либо переиспользуется, либо удаляется как мёртвая.
- **D-5. Массовые операции** («Apply to all…» из CM, «Align all») не переносятся в диалог; «Align all»
  остаётся в разделе Panels и, как раньше, действует на **текущую страницу** с подтверждением.
- **D-6. Трей**: возвращается пункт-переключатель «Show desktop icons» (checkable) рядом с Edit/Lock layout.
  Событие в `ITrayIconManager`, подписка в `App.xaml.cs`.
- **D-7. Баг лассо**: панель, созданная правым drag'ом, должна получать `PageId = IPageManager.CurrentPage`
  (`App.xaml.cs` `NewPanelWithDefaults`), как «Add panel». Чинится в этой же фиче отдельным коммитом.
- **D-8. Локализация UI-строк**: только английский, как везде в проекте (см. `docs/PLANS/ui-polishing-1.md`).
- **D-9. Стили**: карточки настроек — новый словарь `Resources/Styles/Settings.xaml` (подключается в
  `App.xaml`), с использованием существующих токенов `Brush.Settings.*`/Typography. Toggle для on/off —
  `CheckBox` с toggle-стилем допустим (WPF без `ToggleSwitch`); радиогруппа для 2–5 взаимоисключающих.
- **D-10. Contracts для параллельной разработки** — единые имена, чтобы пакеты собрались после слияния:
  - `IPanelCustomizeService.Show(ChildWindowModel model)` — UI/Services (есть).
  - `MainWindowViewModel.CustomizePanelCommand` (параметр — `ChildWindowModel`, по умолчанию `SelectedPanel`).
  - `MainWindowViewModel.MovePanelToPageCommand` (параметр — `string pageId`) → `IWindowManager.MovePanelToPage(ChildWindowModel, string)`.
  - `SettingsSection` enum: `General, LayoutSnapping, Panels, NewPanelDefaults, Pages, SystemIcons, Backup, About`.
  - `ITrayIconManager.ToggleDesktopIconsRequested` (event) + `SetDesktopIconsVisible(bool)` для синхронизации галки.
  - `LayoutSnapshotsViewModel.CaptureSnapshotCommand` запрашивает имя через новый `IInputDialogService.PromptText(title, prompt, defaultValue) : string?`.

## Пакеты работ (параллельно, непересекающиеся файлы)

| Пакет | Агент | Владеет файлами |
|---|---|---|
| **WP-A Settings shell** | `wpf-ui-builder` | `Views/MainWindow.xaml(.cs)`, новые `Views/Settings/*.xaml(.cs)`, `ViewModels/MainWindowViewModel.cs`, `ViewModels/GlobalDefaultsViewModel.cs`, `Models/SettingsSection.cs`, `Resources/Styles/Settings.xaml`, `App.xaml` (merge словаря), `Services/IWindowManager.cs` + `WindowManager.cs` (MovePanelToPage), тесты `MainWindowViewModelTests`, `GlobalDefaultsViewModelTests` |
| **WP-B Customize panel** | `wpf-ui-builder` | новые `Views/PanelCustomizeWindow.xaml(.cs)`, `ViewModels/PanelCustomizeViewModel.cs`, `Services/PanelCustomizeService.cs` (заменяет заглушку), `ViewModels/SelectedPanelSettingsViewModel.cs`, `ViewModels/ChildWindowViewModel.cs`, `Views/ChildWindow.xaml(.cs)` (+ `ChildWindow.*.cs`), `Configuration/DiConfig.cs` (регистрация), тесты `ChildWindowViewModelTests` + новые |
| **WP-C Behaviour & fixes** | `wpf-ui-builder` | `ViewModels/LayoutSnapshotsViewModel.cs`, новые `Services/IInputDialogService.cs`/`InputDialogService.cs`, `Views/InputDialog.xaml(.cs)`, `Services/Providers/TrayIconManager.cs` + `ITrayIconManager`, `App.xaml.cs` (лассо-фикс, подписка трея), тесты снапшотов/трея/лассо |

Регистрация в DI: WP-B правит `DiConfig.cs`; WP-C добавляет **одну** строку регистрации
`IInputDialogService` — конфликт при слиянии ожидаем и разрешается оркестратором.

После слияния: `tdd-test-engineer` (пробелы покрытия) → `debug-healer` при красной сборке →
`code-quality-reviewer` ∥ `fences-ux-parity-reviewer` → `architecture-documenter` (ADR-031 → Accepted,
ROADMAP, ADR-002/ADR-026 amendments).

## Definition of Done

1. Окно настроек открывается с боковой навигацией из 8 разделов; содержимое каждого раздела соответствует
   таблице выше; ни одна ранее доступная настройка не потеряна (кроме двух осознанно убранных команд).
2. «Customize panel…» есть в контекстном меню панели и в разделе Panels; диалог меняет панель вживую;
   Folder Portal можно назначить и снять из UI.
3. В трее есть «Show desktop icons»; лассо-панель создаётся на текущей странице.
4. Снапшот сохраняется с введённым именем, удаляется с подтверждением.
5. `dotnet build` и `dotnet test` — **0 errors, 0 warnings**; новые VM покрыты тестами.
6. Список «manual verify (Win10/Win11)» в плане заполнен (открытие диалога поверх панели с
   `WS_EX_NOACTIVATE`, фокус, DPI, повторное открытие для другой панели, трей-галка).
