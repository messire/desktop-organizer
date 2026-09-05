# ADR-013: Выделение и переименование значков панели

**Статус:** Accepted
**Дата:** 2026-08-16

---

## Контекст

Мастер-план [docs/PLANS/icon-selection-and-rename.md](../docs/PLANS/icon-selection-and-rename.md)
(2026-08-15) зафиксировал две связанные UX-фичи паритета с Fences/Проводником, реализованные одним
циклом (Блоки A–D) и разделяющие `FileItem`, `Templates.xaml` и путь загрузки/сохранения панели:

- **Фича A — нет визуального выделения значка.** `WrapViewTemplate` был `ItemsControl` + `WrapPanel`
  без какого-либо понятия выделения; `ListViewTemplate` был `ListBox`, но `SelectedItem` ни к чему не
  привязывался. Ключевая находка: панель несёт `WS_EX_NOACTIVATE`
  (`DesktopIntegrationService.ApplyDesktopWindowStyles`) — окно никогда не активируется по клику, из-за
  чего дефолтная тема `ListBoxItem` (реагирующая на `IsSelectionActive`) рисовала бы выделение
  невидимым для неактивного окна.
- **Фича B — «мусорные» GUID-имена и отсутствие переименования.** `PanelItemStorageService` до этого
  изменения формировал физическое имя как `{name(≤40)}_{Guid:N}{ext}` — GUID жил **в имени файла**, и
  `FileItem.Name = Path.GetFileName(Path)` показывал это имя пользователю напрямую (`Отчёт_9f3c1a...e21.docx`).
  Переименования значка не было вообще — только переименование заголовка панели (Фаза 2 ROADMAP).

Обе фичи реализованы, покрыты тестами и прошли два раунда `code-quality-reviewer`/
`fences-ux-parity-reviewer` с фиксами. Ниже фиксируются 6 архитектурных решений — как расширение
[ADR-009](ADR-009-drag-drop-move-semantics.md) (managed storage + manifest) и
[ADR-012](ADR-012-performance-and-resilience-hardening.md) (авторитетный кеш манифеста, дисциплина
«manifest до деструктивного шага»).

**Важно:** решения ниже описывают **финальное состояние кода** после ревью, а не исходный замысел
плана — Решение 5 и форма контракта `RenameManaged` изменились по итогам `code-quality-reviewer`
относительно первоначального черновика плана (см. соответствующие разделы).

---

## Решения

### Решение 1: оба вида панели — `ListBox`; выделение через штатную модель контейнеров, независимо от `IsSelectionActive`

`WrapViewTemplate` переведён с `ItemsControl` на `ListBox` с `ItemsPanel = WrapPanel` (внешний
`ScrollViewer` удалён — вложенные скроллы ломали бы перенос строк). Оба вида (`ListViewTemplate` и
`WrapViewTemplate`) используют общий `PanelItemContainerStyle` (`TargetType=ListBoxItem`,
`Templates.xaml`) с собственным `ControlTemplate`: триггер реагирует **только** на `IsSelected`,
никогда на `IsSelectionActive` — иначе подсветка была бы постоянно невидимой у окна, которое из-за
`WS_EX_NOACTIVATE` никогда не становится активным.

**Деталь реализации:** рамка выделения (`SelectionBorder`) — отдельный `Border` с `BorderThickness="1"`
и компенсирующим `Margin="-1"`, а не увеличением `Padding` контейнера. Причина: `ListBoxItem`
измеряет площадь себя вместе с `Padding`, а wrap-раскладка квантуется к `GridMetrics.CellSize`;
`Padding` на контейнере расширил бы измеряемый размер каждого элемента и рассинхронизировал бы
фактический footprint `WrapPanel` с сеткой grid-snap-резайза. `Margin="-1"` на самом `Border` рисует
1px рамку поверх соседних элементов, не меняя измеряемый размер `ListBoxItem`.

**Почему так:** выделение, снятие выделения, клавиатурная навигация, `SelectedItem`-биндинг, hit-test
по контейнеру — всё уже есть в `Selector`/`ListBox`; ручная реализация на `ItemsControl` (флаг
`IsSelected` на `FileItem` + обработчики кликов) воспроизводила бы это хуже и дороже, а разные
шаблоны контейнера для списка и плитки не дали бы одинаковую подсветку (требование паритета: в Fences
подсветка не зависит от вида).

**Alternatives considered:**
- **`ItemsControl` + `bool IsSelected` на `FileItem` + ручные обработчики.** Отклонено: тянет
  UI-состояние в Core-модель, лишает клавиатурной навигации, дублирует логику «снять выделение».
- **`ListView`/`GridView`.** Отклонено: тяжелее, заточен под колонки, не даёт ничего сверх `ListBox`
  для wrap-раскладки.

**Trade-off:** `WrapPanel` не виртуализирует (`VirtualizingStackPanel.IsVirtualizing` фактически
`False` для этого шаблона) — для типового размера панели не проблема; панели на сотни иконок —
отдельный тикет, не в объёме этой фичи.

`BaseViewModel.SelectedFile` двусторонне привязан к `SelectedItem` в обоих шаблонах; клик по пустому
месту панели (`ChildWindow.xaml.cs`, `FileListHost_PreviewMouseLeftButtonDown`) снимает выделение,
если по цепочке визуальных предков нет `ListBoxItem`; ПКМ по элементу
(`FileListHost_PreviewMouseRightButtonDown`) выделяет его до показа `FileContextMenu` (Explorer-паттерн).
Выделение переживает перестройку `FileItems` — `BaseViewModel.ReplaceFileItems` запоминает
`SelectedFile?.Path` до `ReplaceAll` (который поднимает `Reset`, известный побочный эффект ADR-012) и
восстанавливает выделение по пути (регистронезависимо) после.

**Осознанно вне объёма:** multi-select — требует множественных команд контекстного меню и меняет
сигнатуры команд, отдельный тикет (см. «Сквозные задачи» ROADMAP).

---

### Решение 2: GUID перенесён из имени файла в имя каталога

`PanelItems\{name}_{guid}{ext}` → `PanelItems\{guid}\{originalName}{ext}` (для папок аналогично:
`PanelItems\{guid}\{OriginalFolderName}`). `PanelItemStorageService.BuildDestinationPath`/
`BuildDestinationDirectoryPath` создают per-item GUID-каталог, внутри которого файл/папка лежит под
своим **оригинальным** именем.

**Почему так:**
- Коллизионная безопасность сохраняется в той же форме («в один шаг», без racy check-then-create) —
  уникален теперь каталог, а не имя файла.
- `FileItem.Name`/`Path.GetFileName` сам по себе становится оригинальным именем — не нужно ни нового
  персистентного поля, ни второго источника правды, ни изменения схемы `ChildWindowModel.FilePaths :
  List<string>` (то, что [ADR-009](ADR-009-drag-drop-move-semantics.md) Решение 2 явно защищал).
- Переименование становится единообразным для managed и не-managed элементов: всегда физическое
  переименование, одна семантика.
- `IsManaged`, `Restore`, `Release`, весь manifest-контракт и [ADR-010](ADR-010-managed-storage-cleanup-on-panel-removal.md)
  (очистка при удалении панели) работают без изменений — они оперируют путями, а не раскладкой.
  `TryCleanupEmptyItemDirectory` дополнительно убирает осиротевший **пустой** per-item GUID-каталог
  после успешного `Restore`/`Release` (только прямой потомок storage root, только если пуст; сам root
  никогда не удаляется).

**Alternatives considered:**
- **Персистентный `DisplayName` на `FileItem`, независимый от диска.** Отклонено как основное решение:
  вводит второй источник правды для имени и требует изменения персистентной схемы (ровно то, чего
  ADR-009 Решение 2 избегал); `Restore` возвращал бы файл под **оригинальным** именем из manifest,
  молча теряя переименование пользователя; для портальных панелей персистентный `DisplayName` в
  принципе не живёт (список не персистится). Остаточно от этого варианта взят только необязательный,
  не персистируемый `displayName`-override в `FileItem` — см. Решение 3, для legacy-элементов.
- **Оставить плоскую раскладку, а имя для UI брать из manifest.** Отклонено: manifest стал бы
  обязательной зависимостью рендера (сейчас нужен только на Restore/Rename); при рассинхроне
  manifest/диска (ADR-009 Решение 3 прямо допускает такой сценарий) панель показывала бы GUID-имя
  обратно.
- **Explorer-подобный счётчик `name (2).ext` вместо GUID.** Отклонено: возвращает racy
  check-then-create цикл, от которого ADR-009 ушёл осознанно.

**Trade-off:** длина пути. Guard на `MaxDestinationPathLength = 240` символов — если итоговый полный
путь превышает лимит, усекается только **базовое** имя (сохраняя расширение, не разбивая UTF-16
суррогатную пару, обрезая финальные пробелы/точки); в обычном случае имя не усекается вовсе.

---

### Решение 3: legacy-элементы (старая плоская раскладка) — только косметическая деградация отображения, без миграции на старте

Уже лежащие в storage элементы вида `name_<32hex>.ext` не мигрируются на старте (никакого файлового
I/O на пути загрузки панелей — тот путь [ADR-012](ADR-012-performance-and-resilience-hardening.md)
Блок C специально защищал). Вместо этого:
- `FileItem` получил необязательный, **не персистируемый** параметр конструктора `displayName` и
  свойство `DisplayName => _displayName ?? Name`.
- `BaseViewModel` получил хук `protected virtual string? ResolveDisplayName(string path) => null`,
  вызываемый в `AddFilesCore` при конструировании каждого `FileItem`.
- `ChildWindowViewModel` переопределяет его: **только если** `_itemStorage.IsManaged(path)` —
  применяется чистый строковый хелпер `ManagedItemName.StripLegacyGuidSuffix(fileName)` (Core, без
  I/O, снимает ровно суффикс `_<32 hex>` перед расширением, иначе возвращает вход без изменений).
- При переименовании legacy-элемента (Решение 4/5) он физически переезжает в новую раскладку
  `PanelItems\{guid}\{новоеИмя}{ext}` (`PanelItemStorageService.RenameManaged`, ветка `isLegacyFlat`) —
  т.е. лечится окончательно, новым GUID-каталогом.

**Почему так:** миграция на старте — файловые операции над пользовательскими данными без запроса, на
самом чувствительном пути (ADR-010/ADR-012); косметическая деградация безопасна и обратима.

**Trade-off:** гипотетический ложноположительный strip у файла, реально названного
`report_0123456789abcdef0123456789abcdef.pdf`, — и то только для файлов **внутри managed storage**
(`IsManaged`-гард обязателен) и только на отображении, никогда на диске. Принято.

---

### Решение 4: переименование — всегда физическое, через отдельный `IFileItemRenameService`

Новая абстракция `IFileItemRenameService` (Services.Abstractions, `FileItemRenameService.Rename`
в Services/Tools) — единственная точка мутации имени панельного элемента. Она разводит два случая:
- `IPanelItemStorageService.IsManaged(path) == true` → делегирует
  `IPanelItemStorageService.RenameManaged(storedPath, newName)` (только сам storage-сервис вправе
  трогать свой storage root и manifest);
- иначе (обычный файл рабочего стола, или элемент внутри живой зеркалируемой директории Folder
  Portal) → обычный `File.Move`/`Directory.Move` в тот же родительский каталог.

Валидация нового имени (`FileNameValidation.IsValid` — Core, чистая строковая функция: непустое,
без символов из `Path.GetInvalidFileNameChars`, не `.`/`..`, без конечных пробела/точки, не
зарезервированное Windows-имя устройства с учётом расширения) выполняется **до** любого файлового
I/O, в `FileItemRenameService.Rename`.

**Почему так:** UI/ViewModel не должен делать мутирующее файловое I/O; storage-сервис не должен
переименовывать чужие (не свои) файлы — его `IsManaged`-гард существует именно для того, чтобы он
никогда не трогал то, чем не владеет. Новый сервис — тонкий диспетчер + валидация, полностью
тестируемый (`FileItemRenameServiceTests`, managed-путь тестируется через Moq).

**Alternatives considered:**
- **Расширить `IPanelItemStorageService.Rename(path, …)` на оба случая.** Отклонено: ломает инвариант
  «сервис владеет только своим storage root».
- **Расширить `IFileLauncher`.** Отклонено: `IFileLauncher` — про shell-запуск (read-only действия),
  добавление мутации туда размывает роль.
- **Rename прямо в `ChildWindowViewModel`.** Отклонено: не тестируется в изоляции, дублирует
  manifest-логику.

---

### Решение 5: переименование managed-элемента обновляет manifest — двухфазно (add-new-then-remove-old), не атомарным re-key

**Это решение по итогам `code-quality-reviewer` отличается от первоначального замысла плана** (план
описывал атомарный re-key: удалить старый ключ и записать новый одной записью на диск, до физического
`Move`). Финальная реализация (`PanelItemStorageService.RenameManaged` /
`TryRenameManifestEntry`) использует другую последовательность:

1. Вычисляется `newPath` — тот же per-item GUID-каталог + `newName` (для legacy плоского элемента —
   **новый** GUID-каталог, см. Решение 3).
2. `TryRenameManifestEntry` добавляет **новый** ключ манифеста (`newManagedPath → newOriginalPath`, где
   `newOriginalPath = Path.Combine(dirname(oldOriginalPath), newName)`), **оставляя старый ключ
   (`oldManagedPath → oldOriginalPath`) полностью нетронутым**. Оба ключа могут одновременно
   существовать в манифесте после этого шага.
3. Только затем выполняется физический `File.Move`/`Directory.Move`.
4. **Если физический `Move` успешен** — старый ключ удаляется (`RemoveManifestEntry(fullStored)`);
   элемент теперь полностью описан только новым ключом.
5. **Если физический `Move` провалился** — никакого отката/повторной записи манифеста не требуется:
   старый ключ как был, так и остался нетронутым (`Restore(oldManagedPath)` продолжает работать в
   точности как до вызова `RenameManaged`). Добавленный на шаге 2 новый ключ становится «мусором»
   (best-effort удаляется тем же вызовом, но даже если это удаление тоже не удастся — он безвреден:
   ссылается на путь, который физически никогда не был создан, и `Restore` вызывается только с уже
   существующим на диске managed-путём, так что этот мусорный ключ никогда не будет найден по
   валидному вызову).
6. Если запись **нового** ключа (шаг 2) не удалась (кеш «недоверенный» — см.
   [ADR-012](ADR-012-performance-and-resilience-hardening.md) Решение 4, или сам disk-write не
   удался) — `RenameManaged` абортирует **до** физического `Move`, файл остаётся нетронутым.

**Почему так изменили:** исходный «атомарный re-key» (удалить старый ключ + записать новый одной
записью, **до** `Move`) содержит дыру ровно на риск R2 плана: если после успешной перезаписи манифеста
физический `Move` проваливается (файл заблокирован, диска не хватило, гонка с антивирусом), файл
остаётся лежать по **старому** физическому пути, но манифест уже не содержит записи по старому
managed-пути — единственная запись указывает на **новый**, никогда не созданный путь. Восстановить
корректное состояние можно только best-effort откатом (записать старый ключ обратно), а если этот
откат **сам** проваливается (тот же класс сбоев диска, что уже уронил либо запись, либо `Move`) —
происходит именно то, чего вся дисциплина «manifest до деструктивного шага» (ADR-009/ADR-012) должна
не допускать: `originalPath` для реально существующего файла потерян, будущее «Убрать из панели»
превращается в безвозвратное удаление вместо restore.

Двухфазная схема «добавить новое, удалить старое только после подтверждённого успеха» устраняет эту
дыру структурно: в манифесте в любой момент времени существует валидная запись для **любого пути,
который сейчас реально существует на диске** — либо старая (Move ещё не начался/провалился), либо
новая (Move успешен) — без окна, где ни одна не действительна, и без необходимости в rollback-логике
вообще.

**Alternative considered (и отклонена именно по этой причине):** исходный атомарный re-key —
единственная запись, комбинирующая удаление старого ключа и добавление нового, **до** `Move`.
Отклонён после того, как `code-quality-reviewer` указал на риск двойного сбоя (успешный re-key +
проваленный `Move`, затем проваленный best-effort откат) как реальный сценарий потери
`originalPath`, а не гипотетический.

**Trade-off:** «оригинальный путь» перестаёт быть буквально оригинальным — меняется только имя файла
внутри него, каталог сохраняется (`newOriginalPath` берёт `dirname` старого `originalPath` и
подставляет новое имя). Это осознанное отступление от буквы ADR-009 в пользу ожидаемого UX: файл,
переименованный в панели, возвращается на рабочий стол под **новым** именем (коллизии на восстановлении
по-прежнему обрабатывает `ResolveCollisionFreeName`, Explorer-паттерн `name (2).ext`).

**Дополнительное аддитивное уточнение контракта (относительно плана):** план предполагал
`IPanelItemStorageService.RenameManaged` возвращающим `string?` (новый путь или `null`). Финальная
сигнатура — `RenameResult RenameManaged(string storedPath, string newName)`, переиспользующая тип
`RenameResult`/`RenameOutcome` из `IFileItemRenameService` (Services.Abstractions), а не изобретающая
собственный. Причина: DoD фичи требует, чтобы занятое имя (`NameTaken`) сообщалось пользователю
**отдельно** от несуществующего пути (`NotFound`), невалидного имени (`InvalidName`, defense-in-depth
на случай вызова в обход `FileItemRenameService`) и прочего сбоя (`Failed`) — одинарный `string?`
терял бы это различение на границе интерфейса storage-сервиса. `IFileItemRenameService.Rename` для
managed-пути просто пробрасывает `RenameResult` от `RenameManaged` без переупаковки.

---

### Решение 6: состояние inline-редактирования — на ViewModel, не на `FileItem`

`ChildWindowViewModel.EditingItem : FileItem?` (INPC, приватный сеттер) + `EditingName : string`
(двусторонне привязано к `TextBox`). Видимость `TextBlock`/`TextBox` в обоих шаблонах переключается
через `MultiBinding` + существующий `EqualityToBoolConverter` (`IMultiValueConverter`, сравнивающий
`DataContext` элемента и `DataContext.EditingItem` окна — конвертер уже использовался для страниц) +
`BooleanToVisibilityConverter`.

Отдельный класс `RenameEditBehavior` (`UI/Behaviors`, attached property `IsRenameEditor` на `TextBox`)
воспроизводит для inline-редактора файла ту же дисциплину commit/cancel, что `ChildWindow.xaml.cs`
уже использует для заголовка панели (`TitleEditBox_KeyDown`/`LostKeyboardFocus`): `Enter` —
`CommitRenameCommand`, `Escape` — `CancelRenameCommand`, `LostKeyboardFocus` — commit, фокус +
`SelectAll()` на `IsVisibleChanged`, отложенные на `DispatcherPriority.Input` (контейнер `ListBoxItem`
для только что появившегося `EditingItem` реализуется не сразу). Этот класс существует отдельно от
`ChildWindow.xaml.cs`, потому что `Templates.xaml` — `ResourceDictionary` без `x:Class`: у него нет
партиального класса, к которому можно приаттачить XAML-обработчики событий (`KeyDown="..."`), и нет
единого `x:Name` (файловый `TextBox` живёт внутри `DataTemplate`, инстанцируется по одному на
элемент, а не по одному на окно, как `TitleEditBox`).

`F2` (`ChildWindow.xaml.cs`, `PreviewKeyDown` на окне) вызывает `BeginRenameCommand.Execute(SelectedFile)`,
если ничего не редактируется в данный момент (ни заголовок, ни другой файл). Контекстное меню
`FileContextMenu` получило пункт «Переименовать» по существующему паттерну
`PlacementTarget.Tag.BeginRenameCommand`. Ошибка переименования сообщается через
`ChildWindowViewModel.RenameError : RenameOutcome?` — ненавязчивый хинт в UI, никогда `MessageBox`
(панель с `WS_EX_NOACTIVATE` не должна получать модальные диалоги — см. R1 ниже).

**Почему так:** `FileItem` — доменная модель, разделяемая порталами, загрузкой и фильтрами; флаг
«сейчас редактируется» — чистое UI-состояние одной панели. Владение `EditingItem` на ViewModel делает
«редактируется ровно один элемент» инвариантом по построению.

**Alternative:** `bool IsEditing` на `FileItem`. Отклонено: проще в XAML, но допускает несколько
одновременно редактируемых элементов и протекает UI-состоянием в Core.

---

### Решение 7: единое глобальное выделение на два типа desktop-окон — `IPanelSelectionCoordinator`

**Добавлено по итогам батча `polishing/ui-1`** ([docs/PLANS/ui-polishing-1.md](../docs/PLANS/ui-polishing-1.md)
§2, §5, Блоки A и D). До этого решения выделение (`BaseViewModel.SelectedFile`) было изолировано внутри
одной `ChildWindowViewModel` — выбор значка в одной fence-панели никак не влиял на другую панель, а
боковая панель (`SystemIconsPanel`) вообще не имела понятия выделения (`ItemsControl` без `IsSelected`).
Пользовательский запрос «выделение должно быть одно на весь рабочий стол, как в Explorer» потребовал
общего механизма поверх двух независимых типов desktop-окон, живущих в разных подсистемах UI-слоя.

**Решение:** новый UI-синглтон `IPanelSelectionCoordinator`/`PanelSelectionCoordinator`
(`src/DesktopOrganizer.UI/Services/`, регистрируется в `DiConfig` по образцу уже существующих
UI-синглтонов `IWindowManager`/`IPageManager`):

```csharp
public interface IPanelSelectionCoordinator
{
    void NotifySelected(object owner);
    void ClearAll();
    event EventHandler<object>? SelectionClaimed;
}
```

- `ChildWindowViewModel` (через переопределяемый хук `BaseViewModel.OnSelectedFileChanged()`, вызываемый
  из сеттера `SelectedFile` после смены значения) и `SystemIconsPanelViewModel` (через сеттер нового
  свойства `SelectedItem : SystemIconItemModel?`) — оба подписчика одного и того же координатора:
  каждый вызывает `NotifySelected(this)` при установке ненулевого значения и обнуляет своё выделение по
  `SelectionClaimed`, если владелец события — не он сам.
- Оба подписчика симметрично отписываются в `Dispose()` — без этого координатор удержал бы ссылки на уже
  закрытые окна (тот же класс утечки, что уже чинила Фаза 7, см. [ADR-012](ADR-012-performance-and-resilience-hardening.md)).
- Реентрантность закрыта тройной защитой: (а) `NotifySelected` вызывается только при ненулевом значении
  (сброс к `null` не «заявляет» выделение повторно); (б) `_suppressCoordinatorEcho`-флаг подавляет
  повторную заявку на время сброса, вызванного чужим `SelectionClaimed`; (в) сам
  `PanelSelectionCoordinator.NotifySelected` идемпотентен для повторной заявки того же владельца
  (не поднимает событие повторно, если владелец не изменился). Покрыто интеграционными тестами с
  реальными (не mock) экземплярами координатора между двумя `ChildWindowViewModel`, между
  `ChildWindowViewModel` и `SystemIconsPanelViewModel`, и между двумя `SystemIconsPanelViewModel`
  (`ChildWindowViewModelTests.TwoPanels_SharingCoordinator_*`,
  `SystemIconsPanelViewModelTests.SharingCoordinatorWithAnotherSidebarInstance_*`).

**Внутриоконный сброс (то же требование задачи 1, вне межпанельного координатора):** клик по заголовку
панели, по любой из трёх кнопок заголовка или начало resize обнуляют `SelectedFile` явным,
событийным (мышиным) обработчиком — не через фокус. Это согласовано с уже зафиксированным в этом ADR
инвариантом: панели несут `WS_EX_NOACTIVATE`, поэтому `Window.Deactivated`/`IsKeyboardFocusWithin`
непригодны как триггер сброса ни здесь, ни где-либо ещё в проекте.

**Миграция боковой панели на `ListBox` (Блок D).** `SystemIconsPanelWindow` мигрирован с `ItemsControl`
на `ListBox` + `ItemContainerStyle="{StaticResource PanelItemContainerStyle}"` — тот же стиль, что уже
описан в Решении 1 этого ADR для fence-панелей, без дублирования hex-токенов выделения/hover. Это же
переиспользование стало причиной жёсткой зависимости порядка выполнения от [ADR-015](ADR-015-panel-icon-grid-geometry.md):
Блок D **обязан** идти после Блока B, иначе унаследовал бы дефект среза рамки выделения (ADR-015,
Решение 2) в боковой панели тоже.

Миграция попутно закрыла предсуществующий дефект, не относящийся к выделению напрямую: обработчик
drag-reorder боковой панели искал предка `FindAncestorOrSelf<Button>`, но шаблон элемента с версии
«FIX 5» больше не содержит `Button` — drag-reorder в боковой панели был мёртвым кодом. Заменено на
`FindAncestorOrSelf<ListBoxItem>` (тот же паттерн, что уже используется в `ChildWindow.xaml.cs`),
закрывая недовыполненный пункт 11 из [ADR-007](ADR-007-system-icons-panel-window-type.md).

**Выделение боковой панели не персистируется** (не попадает в `PanelDto`) — как и выделение fence-панели
(Решение 1), это транзиентное UI-состояние, обнуляемое при исчезновении элемента из видимого набора
(`RebuildVisibleItems`) и сохраняемое при простом reorder.

**Почему единый координатор, а не двусторонняя ссылка между конкретными ViewModel-типами:** количество
одновременно открытых desktop-окон не фиксировано (несколько fence-панелей + одна боковая), и
`ChildWindowViewModel` не должна знать о существовании `SystemIconsPanelViewModel` (и наоборот) —
синглтон-координатор с единственным методом-«заявкой» на владение выделением развязывает эти два типа
ViewModel друг от друга так же, как `IWindowManager` развязывает управление окнами от конкретных
ViewModel-типов.

**Alternatives considered:**
- **Прямая подписка `ChildWindowViewModel` ↔ `SystemIconsPanelViewModel` через события.** Отклонено:
  N×M связей при нескольких fence-панелях, плюс `SystemIconsPanelViewModel` пришлось бы явно знать про
  тип `ChildWindowViewModel`.
- **Статическое событие на `BaseViewModel`.** Отклонено: `SystemIconsPanelViewModel` не наследует
  `BaseViewModel` (разные модели данных — `FileItem` vs `SystemIconItemModel`), статическое состояние
  усложнило бы unit-тестирование в изоляции (нельзя было бы поднять два независимых координатора в
  параллельных тестах).

---

## Последствия

- **Решение 7 (`polishing/ui-1`) добавило новый публичный контракт аддитивно:**
  `IPanelSelectionCoordinator`/`PanelSelectionCoordinator` (`DesktopOrganizer.UI.Services`, синглтон в
  DI). `BaseViewModel.SelectedFile`'s сеттер получил новый переопределяемый хук `OnSelectedFileChanged()`
  (по умолчанию no-op) — существующие переопределения/подписчики `SelectedFile` не пострадали.
  `SystemIconsPanelViewModel` получил новое свойство `SelectedItem : SystemIconItemModel?` и
  конструкторную зависимость `IPanelSelectionCoordinator`. `SystemIconsPanelWindow` мигрирован
  `ItemsControl → ListBox` (переиспользует `PanelItemContainerStyle` из Решения 1 без изменений в самом
  стиле). Персистентная схема (`PanelDto`, `ChildWindowsState`) не изменилась — выделение обеих панелей
  остаётся транзиентным UI-состоянием.
- **Публичные контракты, изменённые аддитивно:** `IPanelItemStorageService` получил
  `RenameResult RenameManaged(string storedPath, string newName)` (новый метод — существующие
  `MoveIn`/`MoveInDirectory`/`Restore`/`Release`/`IsManaged` не изменились). Новый интерфейс
  `IFileItemRenameService` (Services.Abstractions) с типами `RenameOutcome`/`RenameResult`,
  переиспользуемыми `RenameManaged`. `IWindowManager`/`IIconProvider` не затронуты. Схема JSON
  `ChildWindowsState` (`ChildWindowModel.FilePaths : List<string>`) **не изменилась** — полная
  обратная совместимость persistence.
- **Новые чистые (без I/O) типы в Core**, расширяющие прецедент `GridSnap`/`DesktopGestureStateMachine`
  (ADR-011/ADR-012): `ManagedItemName.StripLegacyGuidSuffix`, `FileNameValidation.IsValid` — оба
  покрыты юнит-тестами без файловой системы (`ManagedItemNameTests`, `FileNameValidationTests`).
- `FileItem` расширен методом `Relocate(newPath, icon)` — обновляет `Path`/`Icon`/сбрасывает
  `displayName`-override и поднимает `PropertyChanged` для `Path`/`Icon`/`Name`/`DisplayName`/`Type`/
  `IsShortcut`, сохраняя тот же инстанс (а не пересоздавая `FileItem`), чтобы связанное с ним UI-
  состояние (выделение, контейнер) не терялось при переименовании.
- `BaseViewModel` получил `_allItemPaths`-переиндексацию через `TryRenameItem` и хук
  `ResolveDisplayName` — оба используются `ChildWindowViewModel` для managed-элементов.
- Переименование элемента в **портальной** панели разрешено (в отличие от `RemoveFileCommand`,
  который блокирован для порталов) — это реальный файл в реальной зеркалируемой папке, паритет с
  Проводником; `ChildWindowViewModel` после успешного rename вызывает `OnSourceChanged()`
  (переприменяет `ContentFilter`, т.к. смена расширения может менять `IsShortcut`) и поднимает
  `FilesChanged` → `SaveWindowsState`.
- **Известный побочный эффект (не регрессия, унаследован от ADR-012):** `Reset`-уведомление
  `ObservableRangeCollection.ReplaceAll` по-прежнему требует явного восстановления выделения по пути
  после каждой перестройки `FileItems` — Решение 1 добавляет это восстановление в `ReplaceFileItems`,
  но точечный diff (Add/Remove вместо Reset) остаётся отдельным нереализованным тикетом (см. ROADMAP,
  Сквозные задачи).
- **Открытый критический риск, не верифицированный вживую (R1):** приходит ли клавиатурный ввод в
  inline-`TextBox` под окном с `WS_EX_NOACTIVATE` — то же открытое сомнение, что ROADMAP Фаза 2 уже
  фиксировала для переименования заголовка панели, и оно так же не было проверено вживую до этого
  момента. Если ручная проверка на Windows покажет, что `Focus()`/`Keyboard.Focus` из
  `RenameEditBehavior` не получают ввод у неактивного окна — эскалация на `win32-interop-specialist`:
  добавить `IDesktopIntegrationService.SetActivationAllowed(IntPtr hwnd, bool)` (временно снимать
  `WS_EX_NOACTIVATE` на время правки, возвращать на коммит/отмену) + `SetForegroundWindow`. Контекстное
  меню «Переименовать» этим риском не затронуто (клик по пункту меню не требует активации окна) — под
  риском только сам ввод текста в редакторе.

**Manual verify (Win10/Win11) — не выполнено, среда разработки ограничена `dotnet build`/`dotnet test`:**
см. `docs/ROADMAP.md`, Фаза 8, полный список пунктов (перенесён из плана §9). Наивысший приоритет —
пункт R1 выше (реальный ввод текста в `TextBox` под `WS_EX_NOACTIVATE`) и подсветка выделения на разных
`BackgroundColor`/`BackgroundOpacity`, поскольку оба поведения принципиально не воспроизводимы без
живого Windows-десктопа.
