# План: Folder Portals (ROADMAP Фаза 5)

> Источник: `docs/ROADMAP.md` Фаза 5, п.1. Автор плана: `fences-feature-planner`.
> Скилл `architecture-designer` в текущей среде недоступен — план оформлен по его принципам
> (ADR-style решение, явные trade-off, декомпозиция по слоям) вручную.

---

## 1. Контекст

Панель-портал уже существует как **одноразовый снимок** папки:

- `ChildWindowModel.SourceFolderPath` (`src/DesktopOrganizer.UI/Models/ChildWindowModel.cs:117`) —
  если задан, панель отражает содержимое папки, а `FilePaths` не персистится.
- `ChildWindowViewModel.GetSourceFiles` (`...ViewModels/ChildWindowViewModel.cs:147`) один раз читает
  `Directory.GetFileSystemEntries(SourceFolderPath)` при `LoadFromModel` и больше не следит за папкой.
- `App.CaptureDesktop` (`...UI/App.xaml.cs:109`) создаёт портал на папку Desktop + прячет реальные иконки.
- `ChildWindow.SyncToModel` (`...Views/ChildWindow.xaml.cs:83`) уже корректно НЕ персистит список портала.

**Чего не хватает (суть фичи):** панель не обновляется вживую. Добавил/удалил/переименовал файл в папке —
панель этого не видит до перезапуска. Folder Portals = подключить `FileSystemWatcher`, чтобы панель
live-отражала папку (паритет с Fences "Folder Portal").

**Важные находки по коду (влияют на решение):**

1. **Биндится `BaseViewModel.FileItems`**, а не `ChildWindowModel.FileItems`. Последняя коллекция
   (`ChildWindowModel.cs:128`) — мёртвый код: нигде не читается из UI. Watcher должен менять
   `BaseViewModel.FileItems` (через VM-методы), чтобы UI реагировал.
2. `BaseViewModel.AddFiles` (`BaseViewModel.cs:46`) дедуплицирует по `Path`, но **нет** метода
   удаления по пути (есть только `RemoveFile(FileItem)`). Понадобится `RemoveFileByPath` / ресинк.
3. `FileItem` — `record` (`Core/Models/FileItem.cs`), `Exists` вычисляется на лету. Переименование =
   delete старого + add нового (имя — часть пути).
4. Все сервисы — singletons (`Services/Configuration/DiConfig.cs`). `IDisposable`-сервисы освобождаются
   через `_serviceProvider.Dispose()` в `App.OnExit`. Watcher же **пер-панельный** → его жизненный цикл
   привязан к окну/VM, не к контейнеру; нужен явный `Dispose` при закрытии панели.
5. `FileSystemWatcher` поднимает события в потоке пула — обязателен маршалинг в UI-поток
   (`Dispatcher`), как уже сделано для жестов в `App.InitDesktopGestures`.

## 2. Решение (что и как)

### Что

Ввести managed-сервис наблюдения за папкой `IFolderWatcher` (обёртка над `FileSystemWatcher`) и
подключить его к панели-порталу. При событиях ФС панель инкрементально обновляет свой список файлов;
при потере событий (буфер переполнен / `Error`) — делает полную пересинхронизацию со снимком папки.

### Как (по слоям)

**Services.Abstractions** — контракт:

```csharp
public interface IFolderWatcher : IDisposable
{
    event EventHandler<FolderChangeEventArgs>? Changed;     // created / deleted / renamed
    event EventHandler? ResyncRequired;                     // overflow / error → full rescan
    void Start(string folderPath);
    void Stop();
}
```

- `FolderChangeEventArgs` (новый record в Abstractions): `ChangeKind` (`Created/Deleted/Renamed`),
  `FullPath`, `OldFullPath?`.
- `IFolderWatcherFactory` — фабрика, т.к. наблюдателей много (по одному на портал), а singleton тут
  не подходит. `Func<IFolderWatcher>` или явная фабрика, зарегистрированная в DI.

**Services (реализация, `Tools/`)** — `FolderWatcher : IFolderWatcher`:

- Внутри `System.IO.FileSystemWatcher` с `NotifyFilters = FileName | DirectoryName`,
  `IncludeSubdirectories = false` (портал = одна папка, как в Fences).
- Подписка на `Created`, `Deleted`, `Renamed`, `Error`.
- **Debounce**: события ФС идут «пачками» (текстовый редактор, копирование). Коалесцировать через
  таймер (`System.Threading.Timer` или `DispatcherTimer` на стороне VM) с окном ~150–300 мс:
  накапливать изменения, по тику отдавать одним батчем. На переполнении/ошибке — не батчить,
  сразу `ResyncRequired`.
- **Error / переполнение буфера**: `FileSystemWatcher.Error` (часто `InternalBufferOverflowException`)
  → поднять `ResyncRequired`. Опционально увеличить `InternalBufferSize` (но не как замену ресинку).
  При `Error` watcher может «умереть» — пере-инициализировать `EnableRaisingEvents` или пересоздать FSW.
- **Битый/исчезнувший путь**: если папка не существует при `Start` — не падать, watcher неактивен;
  если папка удаляется в рантайме — `Error`/`Deleted` корневой → перейти в неактивное состояние,
  панель показывает последний снимок серым (через `FileItem.Exists`). При повторном появлении папки —
  возможность повторного `Start` (опционально, через периодическую попытку или ручной рефреш).
- **Dispose**: `EnableRaisingEvents = false`, отписать обработчики, `Dispose()` FSW и таймера.
  Идемпотентность (повторный Dispose безопасен).

**UI** — интеграция с панелью:

- `ChildWindowViewModel` получает `IFolderWatcherFactory` через конструктор (DI).
- В `LoadFromModel`: если `IsPortal` (есть валидный `SourceFolderPath`) — создать watcher, подписаться,
  `Start(SourceFolderPath)`.
- Маршалинг: обработчики `Changed`/`ResyncRequired` приходят из пула → `Application.Current.Dispatcher`
  (`BeginInvoke`) перед изменением `FileItems` (как в `App.InitDesktopGestures`). Альтернатива —
  `DispatcherTimer` в VM для debounce уже работает в UI-потоке (предпочтительно: и debounce, и
  маршалинг одним механизмом).
- Инкрементальное применение:
  - `Created` → `AddFiles([path])` (дедуп уже есть).
  - `Deleted` → новый `RemoveFileByPath(path)` в `BaseViewModel`.
  - `Renamed` → `RemoveFileByPath(old)` + `AddFiles([new])`.
  - `ResyncRequired` → очистить `FileItems` и перезалить из `Directory.GetFileSystemEntries`
    (вынести снимок из `GetSourceFiles` в переиспользуемый метод).
- `ChildWindowViewModel` становится `IDisposable`; диспоуз вызывается при закрытии окна
  (`ChildWindow.OnClosing` / `RemoveChildWindow`). `WindowManager.RemoveChildWindow` уже закрывает окно —
  добавить туда диспоуз watcher через VM.
- Persistence: **без изменений по формату** — `SyncToModel` уже не пишет `FilePaths` для портала.
  Восстановление при старте: `LoadWindowsState` → `CreateChildWindow` → `Initialize` → `LoadFromModel`
  поднимет watcher автоматически (после правки VM). Дополнительно ничего сохранять не нужно.

**App** — `CaptureDesktop` менять не обязательно: портал уже создаётся с `SourceFolderPath`; watcher
подключится сам через VM. Опционально добавить UI «создать портал из произвольной папки» (пункт трея /
кнопка в настройках) — вынесено в зависимый план, не входит в DoD ядра.

### Trade-offs (ADR-style)

| Решение | Альтернатива | Почему так |
|---|---|---|
| Один `FileSystemWatcher` на портал (`IncludeSubdirectories=false`) | Рекурсивный watcher | Fences-портал = одна папка; рекурсия = больше шума, больше переполнений буфера. |
| Debounce + батч на стороне VM (`DispatcherTimer`) | Debounce в сервисе (`System.Threading.Timer`) | VM-таймер = debounce и маршалинг в один механизм; сервис остаётся тонким и тестируемым по событиям. (Решается реализующим агентом; обе валидны — зафиксировать в коде.) |
| Полная ресинхронизация при `Error`/overflow | Попытка точечно «довосстановить» | FSW при overflow не сообщает ЧТО потеряно → единственный надёжный путь — пересчитать снимок. |
| Watcher вне DI-контейнера (фабрика, owned by VM) | Singleton в DI | Наблюдателей N (по числу порталов), время жизни = время жизни панели; контейнерный Dispose не подходит. |
| Мёртвую `ChildWindowModel.FileItems` удалить | Оставить | Убирает путаницу «какая коллекция биндится»; снижает риск багов синхронизации. (Опционально, см. артефакты.) |

## 3. Scope

**M–L** (ближе к L). Чистый managed-код (нет P/Invoke), но затрагивает 3 слоя
(Abstractions + Services + UI), persistence-инвариант, конкурентность (маршалинг/debounce) и
жизненный цикл (Dispose). Нативного слоя нет → не XL.

## 4. Затрагиваемые слои и файлы

**Новые:**
- `src/DesktopOrganizer.Services.Abstractions/IFolderWatcher.cs`
- `src/DesktopOrganizer.Services.Abstractions/IFolderWatcherFactory.cs`
- `src/DesktopOrganizer.Services.Abstractions/FolderChangeEventArgs.cs` (+ enum `FolderChangeKind`)
- `src/DesktopOrganizer.Services/Tools/FolderWatcher.cs`
- `src/DesktopOrganizer.Services/Tools/FolderWatcherFactory.cs`

**Изменяемые:**
- `src/DesktopOrganizer.Services/Configuration/DiConfig.cs` — регистрация `IFolderWatcherFactory`.
- `src/DesktopOrganizer.UI/ViewModels/ChildWindowViewModel.cs` — внедрить фабрику, поднять/диспоузить
  watcher, обработчики событий, вынести снимок папки в отдельный метод, реализовать `IDisposable`.
- `src/DesktopOrganizer.UI/ViewModels/BaseViewModel.cs` — добавить `RemoveFileByPath(string)` и/или
  `ReplaceAll(string[])` для ресинка.
- `src/DesktopOrganizer.UI/Views/ChildWindow.xaml.cs` — диспоуз VM в `OnClosing`.
- `src/DesktopOrganizer.UI/Services/WindowManager.cs` — диспоуз watcher/VM в `RemoveChildWindow`.
- (Опц.) `src/DesktopOrganizer.UI/Models/ChildWindowModel.cs` — удалить неиспользуемую `FileItems`.

**Тесты:**
- `tests/DesktopOrganizer.Tests/Unit/Services/FolderWatcherTests.cs`
- `tests/DesktopOrganizer.Tests/Unit/ViewModels/ChildWindowViewModelPortalTests.cs`
- (Опц.) `tests/.../Unit/ViewModels/BaseViewModelTests.cs` — `RemoveFileByPath`/`ReplaceAll`.

## 5. Definition of Done (измеримые критерии)

1. Создание файла в папке-портале → новый `FileItem` появляется в панели **без перезапуска**
   (verify: unit на VM с мок-`IFolderWatcher`; manual Win).
2. Удаление файла → элемент исчезает из панели.
3. Переименование файла → старое имя исчезает, новое появляется (один логический элемент).
4. Серия быстрых изменений (≥10 за <300 мс) коалесцируется в ≤2 обновления UI (debounce работает) —
   проверяется на сервисе/таймере unit-тестом или счётчиком обновлений.
5. `Error`/переполнение буфера → панель делает полную пересинхронизацию и её содержимое совпадает
   с фактическим содержимым папки (unit: эмуляция `ResyncRequired` → состояние == снимок).
6. Все ФС-события применяются в UI-потоке (нет `InvalidOperationException` от cross-thread доступа к
   `ObservableCollection`) — manual Win + код-ревью на наличие `Dispatcher`-маршалинга.
7. Закрытие/удаление панели-портала освобождает `FileSystemWatcher` (нет утечки хендлов/потоков):
   `Dispose` вызван, повторный `Dispose` безопасен (unit на идемпотентность).
8. Битый/несуществующий `SourceFolderPath` не валит загрузку панели; панель показывает последний
   снимок (серым через `Exists`) и не бросает исключений (unit + manual).
9. Персистентность не сломана: после перезапуска портал восстанавливается и снова live-следит за
   папкой; `FilePaths` портала по-прежнему не пишется в JSON (verify: `SyncToModel`/round-trip тест).
10. `dotnet build` + `dotnet test` зелёные; StyleCop/`linter.ruleset` без новых нарушений.

## 6. Список артефактов

**Интерфейсы:** `IFolderWatcher`, `IFolderWatcherFactory`.
**Классы/типы:** `FolderWatcher`, `FolderWatcherFactory`, `FolderChangeEventArgs`,
`FolderChangeKind` (enum). Правки: `ChildWindowViewModel`, `BaseViewModel`, `ChildWindow.xaml.cs`,
`WindowManager`, `DiConfig` (Services).
**Тесты:** `FolderWatcherTests`, `ChildWindowViewModelPortalTests`, (опц.) `BaseViewModelTests`.

## 7. Риски

### Непроверяемые вне Windows (manual verify Win10/Win11)
- `FileSystemWatcher` на сетевых дисках/SMB ведёт себя иначе (часто не шлёт события или шлёт реже) —
  проверить локальная папка vs сетевая.
- Поведение при переполнении буфера воспроизводимо только под нагрузкой на реальной ФС.
- Корректность маршалинга в UI-поток и отсутствие cross-thread исключений у `ObservableCollection`
  проверяется только на запущенном WPF.
- Реакция при удалении/пересоздании самой папки-портала в рантайме.
- DPI/смена обоев/виртуальные рабочие столы — не относятся напрямую, но регресс панели проверить.

> ⚠️ Нативного P/Invoke фича НЕ добавляет (managed `FileSystemWatcher`), поэтому `win32-interop-specialist`
> не требуется. Это снижает риск vs Фазе 4.

### Технические
- **Дребезг/штормы событий**: без debounce редактор/архиватор устроит сотни обновлений → лаги UI и
  переполнение. Митигировано debounce + батч (DoD#4).
- **Гонки при ресинке**: батч-обновление и ресинк не должны накладываться — сериализовать через
  UI-поток (один `DispatcherTimer`).
- **Утечки**: незакрытый `FileSystemWatcher` держит поток/хендл. Митигировано DoD#7 (Dispose в
  `OnClosing` + `RemoveChildWindow`).
- **Дублирование коллекций**: пока `ChildWindowModel.FileItems` существует параллельно с
  `BaseViewModel.FileItems`, есть риск, что кто-то начнёт писать «не туда». Рекомендуется удалить
  мёртвую коллекцию в рамках этой задачи.

## 8. Зависимости

- **Опирается на готовое**: `SourceFolderPath`, `GetSourceFiles`, `SyncToModel`-инвариант (не писать
  `FilePaths` портала), `IIconProvider` (иконки новых файлов), паттерн `Dispatcher.BeginInvoke` из
  `App.InitDesktopGestures`.
- **Разблокирует / связано**: «создать портал из произвольной папки» (UI-вход, отдельный мелкий план),
  Фаза 5 «Автоправила сортировки» (может переиспользовать события watcher).
- **Не зависит от**: нативного слоя, multi-monitor, снапшотов.

---

## 🔄 Recommended Next Actions

1. 🚨 **CRITICAL: dotnet-services-engineer**
   Reason: ядро фичи — managed-сервис `IFolderWatcher`/`FolderWatcher` (FileSystemWatcher, debounce,
   обработка Error/overflow, Dispose) + регистрация в DI. Затронут слой Services/Abstractions.
   Command: Task tool, subagent_type "dotnet-services-engineer". Параметры: этот план §2,§4 (Services-часть).

2. 🚨 **CRITICAL: wpf-ui-builder**
   Reason: интеграция в `ChildWindowViewModel`/`BaseViewModel`/`ChildWindow`/`WindowManager` —
   маршалинг в UI-поток, инкрементальное обновление `FileItems`, `IDisposable` панели.
   Command: Task tool, subagent_type "wpf-ui-builder". Параметры: этот план §2,§4 (UI-часть).
   (Запускать параллельно с dotnet-services-engineer — независимые слои, контракт уже зафиксирован в §2.)

3. ⚠️ **RECOMMENDED: tdd-test-engineer**
   Reason: спланировать/написать тесты до или вместе с реализацией (debounce, ресинк, Dispose-идемпотентность,
   битый путь, round-trip persistence) — DoD#1–9.
   Condition: всегда для нетривиальной фичи. Command: Task tool, subagent_type "tdd-test-engineer".

4. 💡 **OPTIONAL: architecture-documenter**
   Reason: вводится новый сервисный контракт и паттерн «owned-by-VM disposable». Если решено фиксировать
   архитектурно — создать ADR-004 (Folder Portals / live folder mirroring).
   Condition: только если реализующие агенты подтвердят отклонение от текущих инвариантов или ввод нового
   слоя времени жизни сервисов.
