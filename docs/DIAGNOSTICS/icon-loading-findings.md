# Icon Loading Findings — P08 (icon pipeline diagnosis + fix)

**Ветка:** `bugfix/workspace-grid`, worktree `worktree-agent-a00bf3cc794067a5d`. **Вход:**
`docs/fix-prompts/08-icons.md`, P00 baseline (`docs/PLANS/workspace-grid-00-baseline.md`, часть C —
полный evidence trail эксперимента на реальном Windows 11 живёт там, этот документ — конспект +
результаты после фикса, не дублирует эксперимент целиком). **Дата:** 2026-09-22.

Симптом («некоторые ярлыки в панели без иконки, подпись есть») подтверждён **измерением**, не
предположением: см. P00 часть C, раздел «Эксперименты на Windows» — реальный `IconProvider` +
`NativeApi` на STA-потоке, реальный `application-state.json`/`.manifest.json` пользователя.

---

## TL;DR — что было и что исправлено

| # | Находка (P00, ИЗМЕРЕНО) | Фикс (P08) | Коммит |
|---|---|---|---|
| 1 | Placeholder для несуществующего пути **полностью прозрачен** (0/256 opaque px): `RenderTargetBitmap.Render()` вызывался до закрытия `DrawingContext` | Явный блочный `using` вокруг `RenderOpen()`/`DrawRectangle`, закрывающий контекст **до** `Render`; заодно placeholder увеличен с 16×16 до 32×32 (совпадает с `SHGFI_SHELLICONSIZE`, не мылится при апскейле) | `aa6760c` |
| 2 | Ошибки icon pipeline уходят в `Console.Error` процесса `WinExe` — теряются; в JSONL-логах (ADR-027) нет ни одной записи об иконках (0/698) | 4 события категории `icon` (`IconHandleFailed`/`IconDestroyFailed`/`IconRetrievalFailed`/`IconFreshnessCheckFailed`) через `IDiagnosticsLog`, privacy-safe payload (расширение + тип исключения, никогда путь/сообщение) | `6e23c48` |
| 3 | 20/26 реальных элементов указывают на устаревшие (pre-Move) исходные пути; 19/20 имеют запись original→managed в `.manifest.json`, но ничего не резолвило её в эту сторону | `IPanelItemStorageService.TryFindManagedPath` (read-only reverse lookup) + `ChildWindowViewModel.LoadFromModel` remap; оформлено как ADR-009 Amendment (2026-09-22) | `e758ddc`, `1ebf71f` |
| 4 | `FileItem.Icon` не перечитывается, когда путь становится валидным (`Refresh()`/`RefreshExistence` трогают только `Exists`/`IsDirectory`) | `FileItem.UpdateIcon` + `BaseViewModel.RefreshItemsLiveness()`, вызывается из уже существующего 3-секундного liveness-poll ровно на переходе missing→present | `021c8ab` |
| 5 | ≈2.8 мс/элемент синхронно на UI-потоке (200 некешированных ≈0.56 с блокировки) | **Закрыто 2026-09-30** — см. [ADR-046](../../adr/ADR-046-background-icon-loading.md) (бэклог 1.x, пункт 7): выделенная фоновая STA-очередь (`StaIconLoadQueue`), `GetIcon` больше не вызывается на UI-потоке. На момент этой находки (2026-09-22) решение было — не менять, задокументировать и отложить до отдельного ADR (см. «Отзывчивость» ниже, оставлено как есть — исторический контекст) | `feature/background-icon-loading` (см. ADR-046 для полного списка коммитов) |
| 6 | Слот иконки якобы мог схлопываться при `Icon == null` | **Проверено — уже исправлено ранее** (`Templates.xaml`, комментарий «ui-polishing-1 §3.6, task 6»): Grid/Image в обоих режимах (List 16×16, Grid `IconSize`) имеют явные `Width`/`Height`, слот не сжимается | — (без изменений) |

Тесты: `aa6760c` (2 новых), `6e23c48` (6 новых/адаптировано 2), `e758ddc` (6 новых),
`1ebf71f` (3 новых), `021c8ab` (5 новых), `1d45eab` (8 новых интеграционных, реальный shell).

---

## Root cause (2 независимых, действующих вместе фактора — из P00, перепроверено здесь)

1. **Данные.** 20 из 26 элементов сохранённого state указывают на несуществующие исходные пути
   (User Desktop ×8, Public Desktop ×12) — ярлыки давно перемещены в managed `PanelItems`
   (`IPanelItemStorageService`, ADR-009 Move-семантика). Вероятный источник (не доказано на 100%,
   см. P00 H2a/H2b): первый legacy-импорт из per-exe `user.config` установленной сборки, отражающий
   состояние рабочего стола ДО Move, либо restore старого снапшота.
2. **Код.** Для несуществующего пути `IconProvider.GetIcon` отдаёт placeholder
   (`IconProvider.cs:78-82`, до фикса — 6e23c48/aa6760c это не трогали), а placeholder был полностью
   прозрачным из-за порядка операций в `IconFactory.CreatePlaceHolder` (`IconFactory.cs:36/39` в P00
   нумерации). Итог: элемент есть, `Exists=False` (подпись приглушена `Opacity=0.45`), но иконки не
   видно вообще — пустой слот вместо серого placeholder'а.

Ни один из факторов сам по себе не объясняет весь симптом: фикс только placeholder'а (без remap)
сделал бы 20 сломанных ярлыков видимыми, но серыми/безликими; фикс только remap (без placeholder'а)
не помог бы 1 пути без записи в manifest и не чинил бы будущие «настоящие» broken paths (пользователь
руками удалил файл). Оба фикса реализованы.

---

## Изменённый pipeline (после фикса)

```
saved path (state / snapshot)
  → ChildWindowViewModel.LoadFromModel
      → GetSourceFiles(model)                      [портал: live listing, иначе model.FilePaths]
      → ResolveManagedReplacements(paths)           [NEW, P08] — missing path + live manifest entry → managed path
      → ReplaceAll → BaseViewModel.AddFilesCore → IIconProvider.GetIcon(path)
          → IconProvider.GetIcon
              missing (после remap — редко) → PlaceholderIcon (IconFactory, теперь непрозрачный, 32×32)
              exists  → IconHandler.GetIconHandle → SHGetFileInfoW
                  failure → IDiagnosticsLog.Write("icon", "IconHandleFailed", ...)  [NEW]
                  success → IconFactory.CreateBitmap(hIcon) → Freeze() → LRU cache

… 3s liveness poll (ChildWindowViewModel.RefreshExistence)
      → BaseViewModel.RefreshItemsLiveness()          [NEW, P08]
          foreach item: wasMissing = !item.Exists; item.Refresh();
          if (wasMissing && item.Exists) item.UpdateIcon(iconProvider.GetIcon(item.Path));
```

Не изменено (и не должно было быть, per scope этого этапа): математика/геометрия icon-грида,
`AdaptiveIconGridPanel`, XAML-раскладка панели (кроме уже-существующей fixed-size проверки, см. ниже),
COM/STA-контракт (ADR-008) — весь icon pipeline по-прежнему синхронно вызывается на UI STA-потоке.

---

## Решения и обоснование

### 1. Placeholder transparency + size (commit `aa6760c`)

`IconFactory.CreatePlaceHolder` рендерил `RenderTargetBitmap` **до** закрытия `DrawingContext`
(`using var`, область видимости — весь метод). `DrawingVisual` фиксирует нарисованное только при
закрытии контекста — рендер «пустого» визуала давал 0/256 непрозрачных пикселей (подтверждено P00
экспериментом и regression-тестом `CreateBitmap_Placeholder_IsNotFullyTransparent`). Фикс — явный
блочный `using`, закрывающий контекст перед `bitmap.Render(...)`.

Заодно placeholder увеличен с 16×16 до 32×32 (совпадает с `SHGFI_SHELLICONSIZE` — реальным размером
всех остальных иконок в pipeline), чтобы не апскейлиться/мылиться в Grid-слоте (`IconSize` по
умолчанию ≈32). Это качественное улучшение, не часть исходного дефекта, но нулевой риск (константа
внутри `IconFactory`, не трогает XAML/геометрию) и явно упомянуто в fix-prompt как «Лучше».

**Рассмотрено и отклонено:** документированный OS-fallback (`SHGetStockIconInfo(SIID_DOCNOASSOC)`
или generic-иконка по расширению через `SHGFI_USEFILEATTRIBUTES` для несуществующего пути) — P00
предлагал проверить, работает ли это без реального файла. Отклонено для этого цикла: добавляет новый
P/Invoke-путь ради визуального полирования, когда исправление порядка операций уже даёт видимый,
корректно расположенный (правильный размер слота) placeholder, удовлетворяющий DoD этапа
(«отсутствующий target не ломает layout», «нет silent exception»). Кандидат на будущий отдельный тикет
UX-полировки, не блокирует P08.

### 2. Диагностика вместо `Console.Error` (commit `6e23c48`)

Заменены 4 точки: `IconHandler.GetIconHandle` (нулевой handle), `IconHandler.DestroyIconHandle`
(неудачный `DestroyIcon`), `IconProvider.TryRetrieveIcon` (исключение при извлечении),
`IconProvider.TryGetLastWriteTimeUtc` (исключение при чтении `LastWriteTimeUtc`). Новая категория
`icon` в схеме `docs/Refactoring/Testing/diagnostics-log-events.md` (обновлена в этом же PR).

**Privacy-инвариант (ADR-027) соблюдён и расширен документированно:** payload — только `extension`
(санитизированное, ≤16 символов, никогда полный путь/имя файла), `attributes`/`errorType` (закрытые
enum-имена/тип исключения). Никогда `Exception.Message` (в этом pipeline он почти всегда содержит
путь — `IconHandler`/`IconProvider` формировали сообщения вида `"...for '{filePath}'"`). Проверено
тестами (`IconProviderTests.GetIcon_WritesDiagnosticsEvent_WhenIconHandlerThrows` явно ассертит, что
ни путь, ни сырое сообщение исключения не попадают ни в одно строковое значение payload).

**API логгера сверен по факту, не предположен:** `IDiagnosticsLog.Write(category, name, scope,
params KeyValuePair<string, object?>[] payload)`, `IsEnabled`-гейтинг перед построением payload —
тот же контракт, что уже использует `GestureDiagnostics` (UI-слой). `IconProvider`/`IconHandler`
берут `IDiagnosticsLog` напрямую через конструктор (как `GestureDiagnostics`), не через
`Func<IDiagnosticsLog>` — паттерн отложенного резолва (ADR-023 R-12) в этом файле не нужен: оба
класса — обычные DI-синглтоны, резолвящиеся уже после построения хоста, а не в раннем
`Program.cs`-контексте, где `Func<IDiagnosticsLog>` используется для single-instance координатора.

**Известный тестовый пробел (задокументирован, не скрыт):** `IconFreshnessCheckFailed` (ветка
исключения в `TryGetLastWriteTimeUtc`) не покрыта отдельным unit-тестом с реальным I/O — путь
достижим только когда `File.Exists`/`Directory.Exists` уже вернули `true`, но последующий
`GetLastWriteTimeUtc` всё равно бросает (гонка: файл исчез между проверками, либо доступ запрещён) —
детерминированно воспроизвести такую гонку в юнит-тесте без хрупких трюков (эксклюзивные хендлы,
ACL-манипуляции) не оправдано по цене/риску для этой ветки; сам механизм записи в диагностику
идентичен уже покрытому `IconRetrievalFailed` (тот же `_diagnosticsLog`, тот же гейтинг), так что
основной риск (немой `Console.Error`) закрыт и подтверждён кодовым обзором.

### 3. Stale path → managed copy resolution (commits `e758ddc`, `1ebf71f`)

См. **ADR-009, раздел «Amendment (2026-09-22, P08 icon loading fix cycle)»** — полное обоснование,
альтернативы и trade-off'ы задокументированы там (не дублируется здесь). Кратко: добавлен
`IPanelItemStorageService.TryFindManagedPath(originalPath, out managedPath)` — чистое чтение уже
закешированного манифеста (ADR-012 Решение 4) в обратном направлении, с проверкой, что managed-путь
реально существует на диске. Вызывается из `ChildWindowViewModel.LoadFromModel` до построения
`FileItem`, так что и иконка, и запуск сразу используют реальный путь. Самоисцеление становится
постоянным на следующем обычном сохранении панели (никакой принудительной записи здесь).

**Решение принято, не отложено как «просто документированное объяснение»**, потому что: (а) риск
минимален (чистое чтение, никаких новых записей); (б) DoD этапа требует не просто fallback-визуал, а
реальную иконку там, где она физически доступна; (в) прямое соответствие тому, что P00 уже
предполагал как «минимальное read-time разрешение» вариант.

### 4. File-based invalidation (commit `021c8ab`)

`FileItem.Refresh()` не трогал `Icon` (только `Exists`/`IsDirectory`), а `IconProvider` намеренно не
кеширует placeholder для missing-пути (значит повторный `GetIcon` сработал бы правильно — но его
никто не вызывал). Добавлены `FileItem.UpdateIcon(icon)` (Core, обновляет `Icon` без побочных
эффектов на `Path`/`Name`/`DisplayName`, в отличие от `Relocate`) и
`BaseViewModel.RefreshItemsLiveness()` (UI), вызываемый из уже существующего 3-секундного
`ChildWindowViewModel._existenceTimer`.

**Bounded backoff vs существующий poll:** fix-prompt предлагал «bounded backoff или FileSystemWatcher,
если он уже есть для панели». Проверено: FileSystemWatcher существует только для Folder Portal
источников (`IFolderWatcherFactory`/`AttachWatcher`) и относится к списку файлов портала, а не к
liveness отдельных элементов вне-портальной панели — не тот механизм. Уже существующий
3-секундный `System.Threading.Timer` (`ExistenceCheckIntervalMs`) — это и есть bounded periodic
recheck; переиспользован, а не задублирован новым механизмом.

**Сознательно НЕ сделано:** перечитывание иконки для элементов, которые остаются существующими на
каждый tick (например, чтобы подхватить изменение `LastWriteTime` уже видимого `.exe`). Это
реинтродуцировало бы именно ту синхронную UI-thread стоимость (≈2.8 мс/элемент), которую пункт 5 ниже
документирует как проблему — для 200-элементной панели это ≈0.56 с блокировки каждые 3 секунды,
неприемлемо. Задокументировано в XML-doc `RefreshItemsLiveness` как осознанный trade-off.

### 5. Отзывчивость при 200+ элементах — задокументировано, не изменено

> **Статус: закрыто 2026-09-30, см. [ADR-046](../../adr/ADR-046-background-icon-loading.md).** Раздел
> ниже — исторический (2026-09-22), описывает состояние ДО ADR-046 и объясняет, почему тогда было решено
> не чинить это внутри фикса иконок. ADR-046 реализует ровно то, что предвиделось в «Что было бы нужно
> для будущего ADR» ниже: выделенную фоновую STA-очередь (`StaIconLoadQueue`, две полосы Local/Remote,
> watchdog, circuit breaker по сетевому корню), отмену/дедупликацию через поколения группы и
> потокобезопасный LRU-кеш `IconProvider`. `GetIcon` больше не вызывается на UI-потоке ни для одного из
> путей, перечисленных в TL;DR выше.

Измерено в P00 (эксперимент на реальном Windows 11): 200 некешированных `.exe` через `IconProvider.
GetIcon` на STA-потоке — **557 мс, 2.79 мс/элемент**. Вся загрузка синхронна на UI-потоке
(`BaseViewModel.AddFilesCore` вызывает `_iconProvider.GetIcon` в цикле `foreach`).

**Почему не сделано async в этом цикле:** `SHGetFileInfoW` (обёрнутый в `IconHandler`/`NativeApi`)
требует инициализированного COM apartment (см. эксперимент — тест явно создаёт STA-поток) — тот же
класс ограничения, что ADR-008 задокументировал для shell context-меню. ADR-008 явно рассмотрел и
**отклонил** «выделенный STA-поток для shell-операций» (Альтернатива A) как избыточное усложнение для
контекстного меню; распространение того же паттерна на icon loading — это новое архитектурное
решение (не просто использование уже принятого), которое, по правилу CLAUDE.md/агентов, требует
отдельного ADR и профилирования, а не тихой правки внутри фикса иконок. Fix-prompt явно разрешает
такой исход: «иначе задокументируй замер и оставь как есть».

**Что было бы нужно для будущего ADR:** (а) выделенный долгоживущий STA-поток-воркер для icon
extraction, с очередью запросов и маршалингом результата обратно на UI-поток (`Dispatcher.Invoke`);
(б) отмена/дедупликация запросов при быстрой перезагрузке панели; (в) пере-baseline
`NativeResizeEquivalenceTests`-подобных guard-тестов не требуется (не тот код), но нужны новые тесты
на потокобезопасность `IconProvider`'s LRU-кеша при конкурентном доступе (сейчас `lock`, рассчитан на
однопоточный UI-вызов). Не реализовано в этом цикле.

### 6. Icon slot fixed size — проверено, не требует изменений

`src/DesktopOrganizer.UI/Resources/Templates.xaml`: List-режим — `Image Width="16" Height="16"`
(строка ~172, явные фиксированные размеры, слот не сжимается без `Source`). Grid-режим — `Grid
Height="{Binding ...IconSize}"` обёртка вокруг `Image Width="{...IconSize}" MaxHeight="{...IconSize}"
VerticalAlignment="Top"` (строки ~241-249), с явным комментарием в коде «ui-polishing-1 §3.6, task 6»
подтверждающим, что это уже было сознательно исправлено раньше именно для случая `Icon == null`.
Никаких правок не потребовалось — DoD-пункт «отсутствие картинки не сжимает слот» уже выполнен.

---

## Тесты (сводка)

| Файл | Новых/изменённых тестов | Что покрывает |
|---|---|---|
| `Unit/Tools/IconFactoryTests.cs` | +2 | Placeholder непрозрачен, 32×32 |
| `Unit/Services/IconProviderTests.cs` | +2 новых, 1 переписан (Console→Diagnostics) | `IconRetrievalFailed`/`IconFreshnessCheckFailed`-путь через diagnostics, privacy-safe payload, disabled-путь не пишет |
| `Unit/Services/IconHandlerTests.cs` | +3 новых, 1 переписан | `IconHandleFailed`/`IconDestroyFailed` через diagnostics, no-op на успехе |
| `Unit/Tools/PanelItemStorageServiceTests.cs` | +6 | `TryFindManagedPath`: live copy, case-insensitive, no entry, dangling entry, read-only (файл манифеста не меняется), пустой/whitespace путь |
| `Unit/ViewModels/ChildWindowViewModelTests.cs` | +6 | `LoadFromModel` remap (resolves/keeps/never-consults-when-existing), `RefreshItemsLiveness` (re-fetch on reappear / no-op when still-missing / no-op when still-present) |
| `Unit/Models/FileItemTests.cs` | +2 | `UpdateIcon`: разный инстанс поднимает `PropertyChanged`, тот же инстанс — no-op |
| `Integration/Services/IconExtractionIntegrationTests.cs` (**новый файл**) | +8 | Реальный `NativeApi`→`IconHandler`→`IconProvider` на `.exe` (`C:\Windows\notepad.exe`), реальной папке, `.lnk`→валидная цель, `.lnk`→битая цель (OS fallback, не наш placeholder), `.url`, отсутствующий путь (наш placeholder, теперь непрозрачный), кеш (реальная иконка и placeholder — тот же инстанс) |

Интеграционные тесты помечены `[Trait(IntegrationTests, nameof(Services))]` и лежат в
`tests/DesktopOrganizer.Tests/Integration/Services/`, как и существующий `NativeApiTests.cs`
(единственная существующая конвенция для тестов, трогающих реальный shell/native API в этом
проекте). `.lnk`-фикстуры создаются через late-bound `WScript.Shell` COM (reflection, без
`Microsoft.CSharp`/`dynamic`, без нового P/Invoke) — тот же механизм, которым P00-аудит уже
подтверждённо пользовался (там — через PowerShell) для построения тестовых `.lnk` в scratchpad.

Финальные числа — см. блок ниже («Baseline прогона после фикса»).

---

## Manual verify (Win10/Win11)

Автоматические тесты покрывают всё, что не требует реального GUI-запуска приложения (которое
встраивается в рабочий стол пользователя — см. CLAUDE.md, приложение не запускалось в этой сессии).
Следующее требует ручной проверки живым пользователем:

1. **Startup с реальным «грязным» state.** Запустить приложение с состоянием, где часть элементов
   указывает на устаревшие (pre-Move) пути рабочего стола (как в P00 — 20/26 в реальном примере).
   Ожидается: элементы с managed-копией показывают **реальную** иконку сразу при старте (не
   placeholder), а не только после ручного пересоздания.
2. **После фикса placeholder'а.** Элемент без managed-копии (или без manifest-записи) показывает
   видимый серый placeholder правильного размера (совпадает со слотом `IconSize`), подпись не
   прыгает, соседние элементы с реальными иконками не сдвигаются.
3. **Самоисцеление персистентности.** После сценария 1 закрыть приложение (обычный выход — триггерит
   `SaveWindowsStateNow`) и снова открыть `application-state.json` (или `user.config`, если legacy) —
   пути ранее сломанных элементов должны быть уже managed-путями, не исходными.
4. **File-based invalidation.** Удалить managed-файл элемента в Explorer → элемент тускнеет с
   placeholder (в течение ≤3 с, следующий liveness-tick). Вернуть файл на то же место (не через
   приложение) → в течение ≤3 с элемент должен и стать непрозрачным (`Exists=true`), и получить
   **реальную** иконку, а не остаться на placeholder.
5. **200+ items.** Панель/портал с 200+ файлами — старт не должен ощутимо подвешивать UI дольше
   измеренных ≈0.5-0.6 с (см. п.5 выше); если ощутимо хуже — профилировать заново, это не было
   изменено в этом цикле.
6. **Win10 UFA-флаг.** `SHGFI_USEFILEATTRIBUTES`-эквивалентность с/без флага проверена P00 только на
   Win11 26200 (совпадение результатов для всех кейсов). На Win10 документация Microsoft предполагает
   иное поведение (файл не читается вовсе) — сверить на реальной Win10-машине через
   `IconExtractionIntegrationTests` (те же тесты, тот же ассерт «непрозрачный/frozen»).
7. **List-режим + скролл** — 1 элемент и 200+ элементов, включая элементы с battled/placeholder
   иконками, чтобы визуально подтвердить фиксированный 16×16 слот при `Icon == null` (пункт 6 выше
   проверен только по коду/комментарию, не на живом рендере).
8. **`.url`-элемент** реального размещения (не только временная фикстура теста) — визуально сверить,
   что иконка Internet Shortcut отображается, а не generic/placeholder.

---

## Fix cycle 1 (2026-09-22) — reviewer findings closed

Second-pass review (`docs/fix-prompts/08-icons.md` fix cycle, iteration 1 of max 2) found defects
introduced or left open by the fix above. Branch: `bugfix/workspace-grid`, worktree
`agent-a540e188fe261afc9`. Status table — see the fix-cycle final report (commit history / PR) for the
authoritative finding→status table; summary below.

| # | Finding | Fix |
|---|---|---|
| CRITICAL C1 | `MainWindowViewModel.RemovePanel`/`PageManagerService.RemovePage` computed `hasManagedFiles` off a panel's stale, pre-remap `Model.FilePaths` (remap only landed on the ViewModel, not the model, until some unrelated future save) — showed the neutral "nothing lost" confirm dialog right before `RemoveChildWindow`'s own `SyncToModel()`+`Release()` silently deleted the by-then-managed file past the Recycle Bin | (a) Both call sites now call `IWindowManager.SyncAllWindowsToModels()` before the check; `RemovePage` also gained the same warning-vs-neutral confirm gate `RemovePanel` already had (it previously had none at all). (b) `ChildWindowViewModel.LoadFromModel` now writes a remap onto `Model.FilePaths` immediately, in memory, and requests a *debounced* save (`FilesChanged` → `SaveWindowsState()`), not just leaving it to some later unrelated `SyncToModel()` — see ADR-009 Amendment 2 |
| MAJOR M1 | `TryFindManagedPath` picked the first live manifest candidate by dictionary order; no check for a candidate already owned by a different panel | `TryFindManagedPath` now requires exactly one live candidate (else refuses); new `FindManagedCandidates` (plural) + `WindowManager.ResolveStalePanelPaths` centralize cross-panel-aware remap before any panel is constructed — see ADR-009 Amendment 2 |
| MAJOR M2 | `IconDiagnostics.SafeExtension` let `Path.GetExtension`'s "extension" for a dotted directory/file name (e.g. `ivan.petrov` → `.petrov`) leak into the privacy-restricted diagnostics payload | Directories → always `string.Empty`; otherwise only `^\.[a-z0-9]{1,8}$` passes through verbatim, else the closed literal `"other"`. Dead `catch (ArgumentException)` removed. See ADR-027 Amendment |
| MAJOR M3 | `RefreshItemsLiveness` only re-fetched an icon on missing→present transition — a transient `SHGetFileInfoW` failure (e.g. shell not ready yet during autostart) left an *existing* item stuck on the placeholder for the whole session | New `IIconProvider.IsPlaceholder`; `BaseViewModel` retries a still-placeholder-but-existing item, bounded to 3 attempts with a growing per-tick backoff (1, then 2 ticks) |
| MAJOR M4 | `IconExtractionIntegrationTests` asserted only `opaquePixelCount > 0`, which also holds for our own (now-opaque) placeholder — didn't actually prove "not a placeholder"/"different icons for different files" | Added `Assert.NotSame(IconFactory().CreateBitmap(), icon)` to every real-icon test (constructed directly, bypassing the shell, for a deterministic reference); new pixel-content comparison test (`notepad.exe` vs `explorer.exe`); shared `GetPbgra32Pixels` helper |
| UX P1 | A missing panel item always showed the uniform gray placeholder, even when the OS could resolve a real, type-specific icon via `SHGFI_USEFILEATTRIBUTES` | `IconProvider.GetIcon`'s missing-path branch now attempts the same shell lookup (extension present → file attributes; none → directory attributes, best-effort heuristic since a missing path's directory-ness can't be checked on disk) before falling back to the placeholder |
| UX P2 | `ChildWindowViewModel.RefreshExistence`'s `catch` still wrote to `Console.Error` (unread in a `WinExe` process) | New `IGestureDiagnostics.ExistenceRefreshFailed(panelId, exceptionType)` (`panel` category), privacy-safe (`errorType` only) |
| NIT | `IconFactory.CreatePlaceHolder`'s 1px border pen straddled the render surface edge (half the stroke clipped) | Inset to `Rect(0.5, 0.5, 31, 31)` |
| NIT | SA1202 ordering: a private helper (`GetPayloadValue`) sat between public `[Fact]` methods in `IconProviderTests`/`IconHandlerTests` | Moved to the private-members section at the bottom of each file |

**Deferred/skipped (documented, not silently dropped):**
- **m1 (integration test hygiene)** — `Environment.GetFolderPath(SpecialFolder.Windows)` instead of a
  hardcoded `C:\Windows`; skip-not-fail when prerequisites are missing; running
  `IconExtractionIntegrationTests` on a dedicated STA thread (mirroring `ResourceDictionaryMergeTests`).
  **Skipped this cycle**: the existing integration suite (commit `1d45eab`) already runs — and, per this
  document's own "baseline after fix" numbers, already passed — without STA on real Windows 11; MTA
  `SHGetFileInfoW` calls for these flag combinations (no PIDL/context-menu COM marshaling, unlike
  ADR-008's actual STA-requiring scenario) have not been observed to fail. Retrofitting STA execution
  into an already-green, pre-existing integration suite carries real regression risk with no observed
  defect to fix — `ResourceDictionaryMergeTests`' own remarks document a genuinely nasty
  hang class of bug from a very similar STA retrofit. Low priority (MINOR) + real risk + no reproduced
  failure = deferred to a future cycle with a dedicated repro, not attempted blind.
- **m2 (perf: lazy original→managed reverse index)** — `FindManagedCandidates`/`TryFindManagedPath`
  still do a full manifest scan per lookup (same complexity as before this cycle, not worsened — M1's
  fix changed *correctness*, not *complexity*). A proper fix (an inverted index, invalidated on manifest
  write) is a larger, independent perf refactor of `PanelItemStorageService`'s internal representation;
  out of scope for a bounded fix-cycle iteration. Not currently measured as a bottleneck (manifest size
  is bounded by how many items a user has ever moved into a panel, not by panel content size).
- **m4 (diagnostics: remap/untrusted-manifest counters)** — no new diagnostics event added for "N stale
  paths remapped" / "manifest untrusted this session". `ResolveStalePanelPaths`/`TryFindManagedPath`
  remain pure reads with no `IDiagnosticsLog` dependency threaded through (`PanelItemStorageService`
  already has one internal test-only counter, `ManifestDiskReadCount`, but that's disk-read count, not
  remap count). Deferred: would need `IDiagnosticsLog`/`IGestureDiagnostics` threaded into
  `PanelItemStorageService`/`WindowManager.ResolveStalePanelPaths`, a real dependency-injection change
  for a nice-to-have signal, not a correctness fix.
- **`IconDestroyFailed` Win32 error code** — `IconHandler.DestroyIconHandle`'s diagnostics event still
  carries no error code (`GetLastError()`); `DestroyIcon`'s own failure modes are narrow (invalid/already-
  destroyed handle) and the existing payload (bare event name, no path) already identifies the failure
  class. Deferred as a low-value addition given the same M4-style effort/risk tradeoff as m2/m4 above.

## Fix cycle 2 (2026-09-22, ПОСЛЕДНИЙ) — reviewer findings closed

Третий-по-счёту (второй fix-cycle) ревью-проход (`docs/fix-prompts/08-icons.md`, iteration 2 of max 2,
последняя разрешённая итерация). Ветка `bugfix/workspace-grid`, worktree
`agent-ad09ddc1ae114a1c1`. Diff cycle 1: `git diff 11485a2 309f3cf -- src tests`.

| # | Finding | Fix |
|---|---|---|
| CRITICAL-1 | `MainWindowViewModel.RemovePage` called `_pageManager.RemovePage(pageId)` (`void`) then **unconditionally** `Pages.Remove(pageId)` — `PageManagerService.RemovePage`'s own confirm gate (ADR-010 Decision 2 / ADR-009 Amendment 2, added fix cycle 1) can decline (user clicks Cancel), at which point nothing was actually removed, yet the page (and every panel on it, still fully alive) vanished from this ViewModel's `Pages` collection — and therefore the page-switcher UI bound to it — until the app restarted | `IPageManager.RemovePage` now returns `bool` (removed vs. declined/no-op); `PageManagerService.RemovePage` returns that outcome instead of `void`; `MainWindowViewModel.RemovePage` calls `SyncPages()` (re-reading the authoritative `IPageManager.Pages`) only when it returns `true`, and does nothing at all on `false` |
| MAJOR-1 | `LayoutSnapshotService.Restore` deserialized-then-immediately-`CreateChildWindow`'d one panel at a time — only per-panel `ChildWindowViewModel.LoadFromModel`/`TryFindManagedPath` resolution applied, never the cross-panel-aware `WindowManager.ResolveStalePanelPaths` (M1, fix cycle 1) — a documented, sanctioned gap in Amendment 2 at the time. Two snapshot panels with different stale originals unambiguously (per single-panel rules) resolving to the SAME managed copy would both have been handed it | `Restore` now deserializes every panel model into an in-memory list FIRST (fresh `Id` assigned to each), calls `WindowManager.ResolveStalePanelPaths(models, storage)` once across the whole list, THEN creates each panel — mirroring `LoadWindowsState`'s own pre-construction pass. New `IPanelItemStorageService` constructor dependency (already registered in DI) |
| MINOR-1 | `IconProvider.GetIcon`'s UX P1 missing-path shell fallback (fix cycle 1) was `StoreInCache`'d under `lastWriteTimeUtc = default` — the SAME freshness key MOST extensions (anything not in `FreshnessSensitiveExtensions`: not `.exe`/`.dll`/`.ico`/`.lnk`/`.cur`/`.url`) keep computing forever, including once the path becomes a real file, since `NeedsFreshnessCheck` is skipped whenever the path didn't exist. `RefreshItemsLiveness`'s missing→present re-fetch would still hit this now-wrong cached entry | `TryRetrieveIcon`/`TryCreateBitmapSource` gained a `cacheable` parameter, threaded from `GetIcon` as `exists` — a path that did not exist at retrieval time is never `StoreInCache`'d at all. Trade-off: a still-missing path now re-resolves (cheap, no-disk-I/O `SHGFI_USEFILEATTRIBUTES`) on every call instead of serving a (possibly soon-to-be-wrong) cached instance |
| MINOR-2 | `WindowManager.ResolveStalePanelPaths` (M1, fix cycle 1) let two DISTINCT stale entries resolving to the SAME single unclaimed candidate be decided by iteration order — "first panel processed wins", an unexplained coin-flip contradicting the codebase's explicit-refusal-over-guessing principle. Fix cycle 1's own regression test (`..._OnlyTheFirstWins`) had encoded this as the intended contract | Two-pass rewrite: a discovery pass tallies how many distinct stale entries resolve to each candidate WITHOUT mutating anything; an apply pass only remaps entries whose candidate's tally is exactly 1. A contested candidate is claimed by NEITHER — both stay stale (visible placeholder/generic icon, never silently wrong). ADR-009 Amendment 3 documents the rule; the old test renamed/rewritten (`..._BothRefuseTheAmbiguousClaim`) plus a new regression proving a contested candidate doesn't collaterally block an unrelated, genuinely unambiguous remap in the same batch |
| NIT-1 | `IconProvider.IsPlaceholder` compared against the `PlaceholderIcon` PROPERTY, which lazily allocates a 32×32 `RenderTargetBitmap` on first access — calling it for every REAL, already-resolved icon forced that allocation on the UI thread even in a session where no placeholder had ever actually been needed | `IsPlaceholder` now compares against the backing `_placeholderIcon` FIELD directly: `icon is not null && _placeholderIcon is not null && ReferenceEquals(...)` — a real icon check when `_placeholderIcon` is still `null` short-circuits without ever calling `CreateBitmap()` |
| NIT-2 | `ChildWindowViewModel`'s constructor XML-doc for its `diagnostics` parameter claimed it was "the same transient, per-panel `IGestureDiagnostics` instance `ChildWindow` already uses" — incorrect: `DiConfig.cs` registers `IGestureDiagnostics` `AddTransient`, so each DI resolution is a NEW instance, not a shared one | Comment corrected to describe the shared REGISTRATION (transient), not a shared INSTANCE — no code change, doc-only |
| NIT-3 | `IconDiagnostics.SafeExtension` called `Directory.Exists(path)` (disk I/O) on every single icon-pipeline diagnostics event, even for a path with no dotted segment at all, where the result is knowably `string.Empty` regardless | Reordered: `Path.GetExtension` is computed first; `Directory.Exists` is only reached when it actually returned something non-empty. Same output for every input (verified by the existing `IconDiagnosticsTests` suite, unchanged) — pure I/O-avoidance reorder |

**Tests:** `MainWindowViewModelTests` (+3: `RemovePage_PageManagerRemoves_ResyncsPagesFromPageManager`,
`RemovePage_PageManagerDeclines_LeavesPagesUntouched`, `RemovePage_DefaultPageId_NeverCallsPageManager`),
`PageManagerServiceTests` (+1 new `RemovePage_UnknownPageId_ReturnsFalse`, 3 existing tests now also
assert the `bool` return value), `WindowManagerTests` (1 renamed/rewritten + 1 new, see MINOR-2 above),
`LayoutSnapshotServiceTests` (+3: single-panel baseline resolution, the MAJOR-1 two-panel
tie-break-contested scenario, cross-panel-already-referenced-by-sibling), `IconProviderTests` (+3:
missing→present no-longer-stale-cached, missing-called-twice re-resolves each time, `IsPlaceholder`
doesn't allocate for a real icon), `IconExtractionIntegrationTests` (1 renamed/rewritten — see below).

**Pre-existing test corrected, not just extended:** `IconExtractionIntegrationTests.GetIcon_MissingPathTwice_ReturnsSamePlaceholderInstance`
asserted `Assert.Same(first, second)` for two `GetIcon` calls against the same still-missing `.lnk` path
— on real Windows this resolves via the UX P1 shell fallback to a real generic shortcut icon (not our
own `PlaceholderIcon` singleton, despite the test's name), and the "same instance" assertion was only
true BECAUSE of the MINOR-1 caching defect this cycle fixes. Renamed to
`GetIcon_MissingPathTwice_NeverCachesTheFallback_BothCallsStillValid` and rewritten to assert
`Assert.NotSame` (still valid, frozen, non-transparent icons either way) — the previous name/assertion
encoded the defect as the intended contract, the same class of stale-regression-test risk MINOR-2's own
old test (`..._OnlyTheFirstWins`) had.

**Deferred/skipped:** none new this cycle — all five real findings (1 CRITICAL, 1 MAJOR, 2 MINOR) plus
all three NITs were fixed. This was the last allowed fix-cycle iteration (2 of max 2) for P08.

## Открытые риски / не закрыто в этом цикле

- **Async icon loading** — задокументировано (п.5 выше), не реализовано; требует отдельного ADR и
  профилирования по правилам CLAUDE.md, если Fences-паритет по отзывчивости при очень больших
  портал-папках (сотни-тысячи файлов) станет приоритетом.
- **1 из 20 путей без manifest-записи** (Public Desktop) остаётся неразрешённым по конструкции —
  никакой эвристики «угадать managed-копию по имени» намеренно не добавлено (тот же принцип, что
  ADR-009 Решение 3 уже применяет к `RestoreOutcome.OriginalPathUnknown`: явный отказ лучше
  вероятностного угадывания).
- **`IconFreshnessCheckFailed` unit-тест** — задокументированный пробел (см. раздел «Диагностика»
  выше), не блокирует DoD (ветка функционально идентична уже покрытой `IconRetrievalFailed`).
- **Восходящий (managed→original, `Restore`) манифест по-прежнему без единого держателя между
  процессами** (ADR-012 Решение 4, известный открытый риск с прошлой фазы) — `TryFindManagedPath`
  наследует то же ограничение (кеш загружается один раз за сессию сервиса); не в scope этого этапа.
- **Соответствие конкретному скриншоту из задания** (какие именно элементы были без иконок) не
  сверялось 1:1 — как и в P00, GUI не запускался в этой сессии.

---

## Ссылки

- `docs/PLANS/workspace-grid-00-baseline.md` — часть C (полный evidence trail эксперимента), сводка
  F-9/F-10.
- `adr/ADR-008-shell-com-sta-threading-and-pidl-marshaling.md` — STA-контракт для shell-вызовов,
  отклонённая Alternative A (выделенный STA-поток).
- `adr/ADR-009-drag-drop-move-semantics.md` — Amendment (2026-09-22) для `TryFindManagedPath`; Amendment 2
  (2026-09-22, fix cycle 1) для C1 (sync-before-check, immediate in-memory remap) и M1 (ambiguity-safe
  `TryFindManagedPath`, `FindManagedCandidates`, `WindowManager.ResolveStalePanelPaths`); Amendment 3
  (2026-09-22, fix cycle 2) для MINOR-2 (contested-candidate tie-break — neither claimant wins) и
  MAJOR-1 (`LayoutSnapshotService.Restore` now also runs `ResolveStalePanelPaths`).
- `adr/ADR-010-managed-storage-cleanup-on-panel-removal.md` — Решение 2 (confirm-диалог), теперь также
  применённое к `PageManagerService.RemovePage` (fix cycle 1, C1; its `bool`-returning contract fixed
  in fix cycle 2, CRITICAL-1, on the `MainWindowViewModel` caller side).
- `adr/ADR-012-performance-and-resilience-hardening.md` — Решение 3 (`Freeze()`-инвариант), Решение 4
  (авторитетный кеш манифеста), Решение 5 (bounded LRU icon cache, freshness-check; MINOR-1, fix cycle 2,
  refines it further — a missing-path result is never stored in this cache at all).
- `adr/ADR-027-diagnostics-logging.md` — `IDiagnosticsLog`-контракт, privacy-инвариант; Amendment
  (2026-09-22, fix cycle 1, M2) для `IconDiagnostics.SafeExtension`'s allow-list.
- `docs/Refactoring/Testing/diagnostics-log-events.md` — схема событий, включая категорию `icon`
  (обновлённый privacy note для `extension`) и `panel.ExistenceRefreshFailed` (fix cycle 1, UX P2);
  `SafeExtension`'s check order updated (NIT-3, fix cycle 2 — output unchanged, I/O-avoidance only).
