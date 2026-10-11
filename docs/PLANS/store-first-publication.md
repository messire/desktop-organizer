# Готовность к первой публикации в Microsoft Store — анализ и план

Дата: 2026-10-10. База: `rc/0.9.0` (138cd49), версия 0.9.0. Статус: **v4 — решения владельца получены
2026-10-10 (§7); реализация кода завершена на ветке `feature/store-publication` (пакеты 1–4, см.
[store-publication-implementation.md](store-publication-implementation.md)), решение зафиксировано в
[ADR-050](../../adr/ADR-050-microsoft-store-channel-msix.md).** Остаются процессные пункты AP1–AP4 (владелец) и
roadmap C1–C14 (см. `docs/ROADMAP.md`, «Публикация в Microsoft Store»); установка и запуск под identity проверяются
вручную на Windows 11. Расхождения реализации с этим планом — ADR-050 (D-1…D-8) и «Фактическая реализация» в конце
документа. История анализа — §9. Источник фактов: инспекция кода оркестратором и тремя агентами-аналитиками, три независимых
критика, документация Microsoft (ссылки по тексту). Проверено локально: `dotnet build -c Release` — 0 errors /
0 warnings; `dotnet test` — 6426 passed / 0 failed.

> Это **физическая** оценка готовности (можно ли это физически опубликовать), не сверка с ROADMAP/ADR.
> Критерий «первая публикация»: приложение не обязано быть идеальным. **Блокер** = потеря данных пользователя,
> тихо сломанная ключевая функция, провал сертификации, вводящий в заблуждение UI или «нечего отправлять».

## 0. Вердикт

> **Статус на 2026-10-11 (версия продукта 1.0.0).** Вердикт ниже — исходный анализ, написанный до реализации; код по нему
> выполнен на `feature/store-publication`: A1–A4 и B2/B4/B5/B9 реализованы (корень хранилища и identity-probe, `startupTask`,
> скрытие Updates, MSIX-упаковка `installer/build-msix.ps1`), пользовательская документация (README, `installer/README.md`,
> `docs/store/*`, CHANGELOG) приведена в соответствие (C12 закрыт). Открыто только за владельцем: AP1–AP4 (аккаунт и имя,
> публичная страница с политикой и поддержкой, сертификационные заметки/листинг в Partner Center) и подстановка
> https://messire.github.io/desktop-organizer-site/ / e.smirnov@protonmail.com (README, `docs/store/*`, B6 — `UpdateEndpoints.PublicSiteUrl`); установка и запуск под
> identity проверяются вручную на Windows 11. Утверждения «упаковки нет», «публиковать как есть нельзя» и оценки объёма в §0–§1
> описывают состояние до реализации; фактические отличия — §10 и [ADR-050](../../adr/ADR-050-microsoft-store-channel-msix.md).

**Полировать приложение не нужно. Нужно упаковать его в MSIX и исправить две несовместимости с MSIX.**
Публиковать «как есть» нельзя — но не из-за качества: как Win32-продукт приложение готово (см. §6).
Единственный реалистичный путь в Store без сертификата подписи — MSIX (Store подписывает пакет сам), а в
MSIX-песочнице у этого приложения ломаются две вещи, которые являются его сутью:

1. **Файлы, перемещённые в панели, уходят в виртуализированный AppData** — их не видит Explorer и другие
   программы, и они **удаляются вместе с приложением** (Uninstall / Reset). Это потеря данных.
2. **«Запускать при старте Windows» ничего не делает** (запись в HKCU\Run виртуализируется), а переключатель
   показывает «включено».

Упаковки MSIX в репозитории нет вообще (манифест, ассеты, листинг, privacy policy, аккаунт).

**Решение владельца 2026-10-10:** D-1 = A (отдельный корень хранилища для Store-сборки — явное разрешение на
изменение замороженного контура persistence, только для сборки с package identity), остальные решения — §7.
**Код остаётся закрытым** (репозиторий приватный: `https://github.com/messire/desktop-organizer.git`); Store
исходники не запрашивает и не проверяет — публичной должна быть только страница с политикой конфиденциальности
и контактом поддержки (AP2).

Объём: пункты A1–A4 и B2–B9 делаются до публикации (**≈3–5 рабочих дней кода** с тестами и 0 warnings,
плюс ≈1 день процессной работы); C1–C13 и P1 — roadmap. Календарно с резервацией имени (1–3 дня) и
сертификацией (до 3 рабочих дней, возможен повторный круг) — **≈1,5–2 недели**.

Путь «EXE/MSI-инсталлятор в Store» (policy 10.2.9) отвергнут: требует подписи инсталлятора и всех PE
сертификатом из Microsoft Trusted Root Program; Store такие пакеты не переподписывает
([msi/app-package-requirements](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msi/app-package-requirements)).

## 1. Блокеры в коде (делать до первой публикации)

### A1. Хранилище в `%LOCALAPPDATA%` несовместимо с виртуализацией MSIX — БЛОКЕР (потеря данных)

**Факты.** `AppStorageRoot.cs:62` → `%LOCALAPPDATA%\DesktopOrganizer` (`State\`, `PanelItems\`, `Logs\`,
`.storage-lock`). Перетащенные файлы **перемещаются** в `PanelItems\` (ADR-009); пути в состоянии абсолютные
(`PanelContentDto.FilePaths`, ключи `.manifest.json`). Единственная защита при удалении — Inno
`[UninstallRun] --restore-panel-items` (`installer/DesktopOrganizer.iss:161-167`), которой в MSIX нет.

**Что происходит в MSIX.** Новые файлы под `AppData\Local` пишутся в
`%LOCALAPPDATA%\Packages\<PFN>\LocalCache\Local\…` и видны только процессам пакета
([behind-the-scenes](https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-behind-the-scenes)):

- **Uninstall** и **Settings → Apps → Reset** удаляют LocalCache → файлы пользователя уничтожаются
  ([reset-and-repair](https://learn.microsoft.com/en-us/windows/msix/desktop/managing-your-msix-reset-and-repair);
  Repair данные сохраняет).
- **Процессы без identity не видят виртуальный путь**: drag-out из панели в Explorer
  (`PanelItemDragDataFactory.cs:54` кладёт виртуальный путь в FileDrop), уже запущенный Word/Photos,
  получающий путь по DDE/COM, «Открыть папку», out-of-proc shell-глаголы → «файл не найден». Наследование
  identity дочерними процессами официально не документировано — считать невидимым. Ключевая функция
  ломается с первого перетаскивания.
- Отключение виртуализации (`unvirtualizedResources`) — restricted capability, по документации
  «designed for certain types of desktop PC games … published by Microsoft and our partners … not intended
  to be used for other scenarios»
  ([capability declarations](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/app-capability-declarations)).
  Не путь для первого релиза.

**Решение (требует явного «да» владельца — путь `%LOCALAPPDATA%\DesktopOrganizer` входит в замороженный
контур CLAUDE.md).** Store-сборка выбирает **невиртуализируемый корень** при наличии package identity:
рекомендуется `%USERPROFILE%\DesktopOrganizer` для **всего корня** (State, PanelItems, Logs, lock).
Не Documents/Desktop: Controlled Folder Access защищает Documents/Pictures/Videos
([controlled-folders](https://learn.microsoft.com/defender-endpoint/controlled-folders)), OneDrive KFM
переносит Desktop/Documents/Pictures ([redirect-known-folders](https://learn.microsoft.com/en-us/sharepoint/redirect-known-folders));
корень профиля не трогают ни тот, ни другой, `CloudPathClassifier` остаётся корректным.

- Схема не меняется, миграции нет, переписывания путей нет: Store-пользователей ещё не существует
  (у репозитория 0 remote, публичных релизов нет), Inno-корень не трогается.
- Техника: `AppStorageRoot.Default` — статика, вызывается до DI (`Program.cs:365-367`,
  `App.CrashLogDirectory`) → identity-probe (`GetCurrentPackageFullName`, `APPMODEL_ERROR_NO_PACKAGE` = нет
  identity) делать как `Lazy<bool>`-статику с обёрткой в `INativeApi` для остального кода (явное,
  задокументированное исключение из инварианта «P/Invoke только за INativeApi»). `AppStorageRootTests.cs:54-64`
  переписать на резолвер с инжектированной probe; предусмотреть вариант для `DebugFolderName`. ~15
  XML-doc/ADR-комментариев упоминают `%LOCALAPPDATA%\DesktopOrganizer` — правит `architecture-documenter`.
- В корень положить `README.txt` («что это за папка, как вернуть файлы»).
- Побочный эффект: закрывает B7 (неверные папки в «Открыть папку логов/хранилища») бесплатно.

Оценка: **M** (1–1,5 дня: probe + резолвер + тесты + amendment к ADR-009/024/041 + комментарии).

### A2. «Запускать при старте Windows» в MSIX — ложь UI — БЛОКЕР

**Факты.** `AutostartService.cs:59-81` пишет `HKCU\…\Run` с `Environment.ProcessPath` и читает обратно.
В MSIX запись виртуализируется («All writes under HKCU are copied on write to a private per-user, per-app
location»), Explorer её не видит, путь указывает в `WindowsApps`. Переключатель показывает «вкл», ничего не
запускается (10.1.1 — вводящий в заблуждение UI).

**Минимальное решение (S).** В манифесте
`<desktop:Extension Category="windows.startupTask"><desktop:StartupTask TaskId="…" Enabled="false" DisplayName="DesktopOrganizer"/>`
([desktop-to-uwp-extensions](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/desktop-to-uwp-extensions):
задача регистрируется при первом запуске приложения; отключённую пользователем задачу программно включить
нельзя). Под package identity переключатель заменить текстом + ссылкой `ms-settings:startupapps`
([launch-settings](https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-settings)).
`Enabled="false"` — чтобы автозапуск не включался без согласия (сегодня это чекбокс инсталлятора; у Fences
автозапуск тоже ставит инсталлятор — паритет не страдает). Зависит от identity-probe из A1.

Полная интеграция `Windows.ApplicationModel.StartupTask` (нужен TFM `net8.0-windows10.0.19041.0` на весь
solution) — roadmap (C1).

### A3. Упаковки MSIX не существует — БЛОКЕР («нечего отправлять»)

Нужно создать:

- **Проект упаковки.** Вариант 1: `DesktopOrganizer.Package.wapproj` (Windows Application Packaging
  Project) со ссылкой на `DesktopOrganizer.UI`. `RuntimeIdentifier=win-x64` + `SelfContained=true` задавать
  **в `.wapproj`/publish-профиле, не в `UI.csproj`** (иначе задевается тестовый проект, который ссылается на
  UI; в .NET 8 RID больше не подразумевает self-contained —
  [breaking change](https://learn.microsoft.com/en-ca/dotnet/Core/compatibility/sdk/8.0/runtimespecific-app-default)).
  `.wapproj` не собирается `dotnet build` (нужны DesktopBridge-таргеты msbuild/VS) → **не добавлять в
  `DesktopOrganizer.sln`** (CI гоняет `dotnet build … -p:TreatWarningsAsErrors=true` по всему sln); держать в
  отдельном `.sln`/`.slnf` с job на `setup-msbuild`. Локально VS 2022 есть. Вариант 2 без VS: ручной
  `AppxManifest.xml` + `MakeAppx.exe pack` поверх `installer/build.ps1`-публикации (SDK 10.0.26100 установлен).
  Self-contained обязателен: Store не ставит .NET Desktop Runtime как зависимость
  ([dotnet/runtime#100880](https://github.com/dotnet/runtime/discussions/100880)); `PublishSingleFile` не
  использовать (уже `false` в `installer/build.ps1:172`).
- **Манифест:** `Identity Name/Publisher/PublisherDisplayName` — скопировать посимвольно из Partner Center
  (зависит от AP1); `Version` пакета — **первая секция ≠ 0, четвёртая = 0** → минимум `1.0.0.0`
  ([msix/app-package-requirements](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/app-package-requirements));
  `TargetDeviceFamily Windows.Desktop MinVersion 10.0.19041.0`; capability `runFullTrust` (обычный путь
  Desktop Bridge, одобряется по текстовому обоснованию); расширение `startupTask` (манифест-часть A2);
  x64 only (на Win11 ARM работает через эмуляцию).
- **Ассеты** (растеризация из `Resources/app.svg`; локально нет magick/inkscape — либо владелец даёт PNG,
  либо `dotnet-services-engineer` пишет скрипт в `tools/`, например на Svg.Skia): `StoreLogo`,
  `Square150x150Logo` (мин. scale-100), `Square44x44Logo` + targetsize 16/24/32/48/256 incl.
  `altform-unplated`, `Wide310x150Logo` (рекомендуется); каждый < 200 КБ; дефолтные VS-картинки проваливают
  Branding validation
  ([app-icon-construction](https://learn.microsoft.com/en-us/windows/apps/design/iconography/app-icon-construction)).
  Отсюда же логотип 300×300 и скриншоты для AP4.
- **CI:** отдельный job, собирающий `.msixupload` (`UapAppxPackageBuildMode=StoreUpload`,
  `AppxPackageSigningEnabled=false`). Первый `.msixupload` собирается локально (remote у репозитория пока
  нет), отправка — вручную в Partner Center. Локальный WACK (`appcert.exe test -appxpackagepath …`) —
  перед загрузкой; Release-сборка, `asInvoker` (есть).

Оценка: **M–L** (1,5–2,5 дня: первый `.wapproj`/MakeAppx, ассеты, CI, прогон WACK и правки по нему).

### A4. Проверка обновлений в Store-сборке — до публикации (S)

`UpdateEndpoints.cs:14-23`, `GitHubReleaseUpdateChecker`, карточка Updates в About: Store-сборка ходит в
`api.github.com` и ведёт на GitHub Releases (которых не существует — репозиторий приватный, карточка уже
сегодня показывает «No releases»). Под package identity карточку скрыть (обновления идут через Store).
Иначе — сетевой вызов, тянущий обязанности по privacy policy, и ссылка на внешний инсталлятор. Зависит от
identity-probe из A1.

## 2. Блокеры процесса (без кода)

| # | Что | Почему блокер | Оценка |
|---|---|---|---|
| AP1 | **Резервация имени** в Partner Center в день 1 (индивидуальный аккаунт бесплатный через storedeveloper.microsoft.com, верификация по документу). «DesktopOrganizer» — родовое название, может быть занято/отклонено → иметь запасной бренд. Identity-значения манифеста берутся из резервации | Без имени нет Identity, нет пакета | 1–3 дня ожидания |
| AP2 | **Публичная страница** (не репозиторий): privacy policy URL обязателен для Desktop Bridge (policy 10.5.1 «Desktop Bridge and Win32 products must always have privacy policies»; [store-policies](https://learn.microsoft.com/en-us/windows/apps/publish/store-policies)); ссылки «Project page»/«Report a problem» в приложении и support URL в листинге сегодня ведут на 404. Код закрытый → нужна одна публичная страница (GitHub Pages из отдельного публичного репозитория только с сайтом, любой статический хостинг) с текстом политики и контактом поддержки (e-mail или форма). Черновик политики — `docs/store/privacy-policy.md` | Поле обязательно при `runFullTrust` | 0,5 дня |
| AP3 | **Notes for certification** (≤2000 знаков): приложение живёт в трее, главного окна нет; шаги проверки (создать файл на рабочем столе → перетащить в панель → ПКМ по заголовку → ПКМ по трею → Settings/Exit); двойной клик по рабочему столу скрывает/показывает иконки; обоснование `runFullTrust` (WH_MOUSE_LL, SetParent в WorkerW, shell COM) | «Apps that appear incomplete may fail» | 1 час |
| AP4 | **Листинг**: описание (ценность + ограничения по 10.1.1: встраивание в слой рабочего стола, глобальный хук мыши, скрытие иконок жестом, файлы перемещаются в `%USERPROFILE%\DesktopOrganizer`), ≥1 скриншот PNG ≥1366×768, логотип 300×300, категория Utilities & tools, IARC-анкета, License terms = ссылка на MIT (иначе Standard Application License Terms), языки en-US (+ru-RU) | Обязательные поля отправки | 2–3 часа |

### Следствия закрытого кода (решение владельца 2026-10-10)

Репозиторий остаётся приватным. Store это не мешает (сертификация работает только с пакетом), но три места в
репозитории заявляют «open-source» и должны быть исправлены до публикации, иначе листинг и метаданные вводят в
заблуждение (10.1.1):

1. `Directory.Build.props` `<Description>` — «free, open-source» → «free».
2. `README.md:3` — та же формулировка.
3. `LICENSE` (MIT) остаётся в репозитории (лицензия действует только на то, что распространяется как исходники);
   в листинге поле License terms **не** заполнять ссылкой на MIT — оставить Standard Application License Terms.

Замечание: self-contained .NET-пакет декомпилируется в читаемый C#; приватность репозитория защищает от
копирования кода лишь отчасти, защиту даёт обфускация при публикации — отдельное решение, в объём первой
публикации не входит (→ C14).

## 3. Стоит сделать до первой публикации (дёшево и заметно)

| # | Что | Зависит от | Оценка | Вердикт |
|---|---|---|---|---|
| B2 | Одноразовый balloon при первом запуске: «DesktopOrganizer работает в трее — ПКМ по значку: Настройки/Выход» (`NotifyIconAdapter.ShowBalloonTip` есть, не используется). Тестер на чистой VM видит пустую панель и ничего больше | — | S | до публикации |
| B4 | Текст `Hint_Move_Message` (resx:516): назвать папку, куда перемещён файл, и что он там остаётся после удаления приложения | A1 (имя папки) | S | до публикации |
| B5 | Жаргон в UI: `Strings.resx:632` («--diag»), `:745` («Move-semantics») — переформулировать en/ru | — | S | до публикации |
| B6 | «Project page» (`UpdateEndpoints.cs:23`) и «Report a problem» ведут на несуществующие публичные страницы → перенацелить на публичную страницу из AP2, когда её URL известен | AP2 | S | **сделано 2026-10-11**: `PublicSiteUrl` = https://messire.github.io/desktop-organizer-site/, `PrivacyPolicyUrl` = …/privacy |
| B7 | «Открыть папку хранилища/логов», crash-диалог, `ManagedStorageOrphansViewModel.cs:357` (`OpenFolder` по `ManagedPath`) — открывают реальный `%LOCALAPPDATA%` (пустой в MSIX). **Исчезает автоматически с A1**; отдельной работы нет | A1 | — | проверить после A1 |
| B9 | Known limitations в CHANGELOG/листинге: P1 (сброс настроек при обновлении), иконки остаются скрытыми, если пакет удалили при работающем приложении (лечится перезапуском Explorer / входом в систему) | — | S | до публикации |

B1 переведён в A4; B3 (placeholder пустой панели) и B8 («Вернуть все файлы») сняты критикой → C9/C10.

## 4. Новая находка, затрагивающая замороженный контур — P1

**`user.config` сбрасывается при каждом обновлении из Store.** Путь user.config содержит `_Url_<hash>` от
пути exe (нет strong name); MSIX ставит каждую версию в новую папку
`WindowsApps\<Name>_<Version>_x64__<id>` → при каждом обновлении теряются все `IAppSettings`: язык,
`IsLayoutLocked`, жесты, сетка, одноразовые подсказки (`HasShownMoveHint` покажется снова). Панели/страницы
(JSON) **не** затрагиваются
([application-settings-overview](https://learn.microsoft.com/en-us/dotnet/desktop/winforms/advanced/application-settings-overview),
[package-identity-overview](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/package-identity-overview)).
`Upgrade()` не поможет (другой evidence-хэш). Любое исправление (strong-name только Store-сборки, custom
`SettingsProvider`, перенос в JSON) меняет контур `IAppSettings`/`user.config` → отдельное явное «да».
**Вердикт: DEFER** (= C13) с known-limitation; первая публикация проблему не показывает, первое обновление —
покажет.

## 5. Roadmap (отложить)

| # | Что | Почему можно отложить |
|---|---|---|
| C1 | Полная интеграция StartupTask API (WinRT TFM), отображение `DisabledByUser` | A2-минимум достаточен |
| C2 | Side-by-side Inno + Store: детект uninstall-ключа, миграция данных. Имена mutex/pipe общие → второй экземпляр активирует первый (открывает его настройки) | Внешних Inno-пользователей нет; разработчику — удалить Inno-сборку перед тестом Store-сборки |
| C3 | `appExecutionAlias` для CLI-ключей | `--diag` есть через env var; возврат файлов — UI Backup/Orphans |
| C4 | `RegisterApplicationRestart` / self-heal иконок после принудительного завершения пакета | Обновление MSIX при работающем приложении по умолчанию отвергается, kill — только с ForceTargetAppShutdown |
| C5 | Пакет arm64 | x64 эмулируется на Win11 ARM |
| C6 | msstore CLI / автопубликация из GitHub Actions | Первая отправка всё равно ручная |
| C7 | Страница лицензий/третьих сторон в приложении | Все зависимости — MIT Microsoft, атрибуция не требуется |
| C8 | Настройка отключения жеста «двойной клик скрывает иконки»; toast при первом скрытии | Жест описан в Notes for certification |
| C9 | Placeholder «Перетащите значки сюда» в пустой панели | nice-to-have; тестер следует шагам AP3 |
| C10 | Команда «Вернуть все файлы на рабочий стол» в Settings → Backup | После A1 файлы переживают удаление; orphan-UI уже есть |
| C11 | Тесты-контракты на манифест (аналог `Installer*ContractTests`) | Пока Inno — основной канал |
| C12 | README: раздел для Store / разделение инструкций установки | Документация |
| C13 | P1 (сброс настроек при обновлении) | См. §4 |
| C14 | Обфускация сборок в Store-пакете (защита закрытого кода от декомпиляции) | Не влияет на публикацию; отдельное решение владельца |

## 6. Проверено и в порядке

Хук `WH_MOUSE_LL`, `SetParent` в WorkerW, `TaskbarCreated`, нативное shell-меню (ADR-040), `ShellExecute`
URL, чтения HKCU (тема, HideIcons, виртуальные столы), tray icon, `SessionEnding`, нет файловых ассоциаций и
`Global\`-объектов, нет записи в папку установки, паритет resx en/ru (343/343), нет захардкоженного текста в
XAML, crash-диалог без путей/стеков, подтверждения удаления панели/страницы/снимка, безопасный drop в
Корзину, нет сети при старте, нет Console/Debugger-остатков в Release-UI, манифест PerMonitorV2,
`asInvoker` (тест WACK UAC), покрытие `AutomationProperties`, per-user модель совпадает с MSIX, сборка/тесты
зелёные.

## 7. Решения владельца (получены 2026-10-10)

| # | Вопрос | Решение |
|---|---|---|
| D-1 | A1: отдельный корень хранилища для Store-сборки (замороженный контур) | **A** — весь корень в `%USERPROFILE%\DesktopOrganizer` при наличии package identity; Inno-корень без изменений. Это явное разрешение на изменение контура persistence ровно в этом объёме: новых persisted-полей, миграций, смены схемы нет |
| D-2 | Поднять продуктовую `<Version>` до 1.0.0 | **да** |
| D-3 | Имя для резервации | **DesktopOrganizer** (запасной бренд не задан) |
| D-4 | P1 (сброс user.config при обновлении) | Ответ не дан (вместо него указан URL репозитория — добавлен как remote `origin`) → действует рекомендация: **отложить**, known limitation (C13) |
| D-5 | `startupTask Enabled` по умолчанию | **false** (выключен) |
| D-6 | Открытость кода | **закрытый**; публичной будет только страница политики/поддержки (AP2) |

**Закрыто 2026-10-11:** URL публичной страницы (политика + поддержка) для B6/AP2 и e-mail поддержки для
листинга. До его получения ссылки в приложении остаются прежними.

## 8. План выполнения (после решений)

Порядок и зависимости; каждый пакет — через конвейер CLAUDE.md (планировщик → реализация → тесты →
ревью → документация), сборка 0 warnings.

1. **День 0 (процесс, параллельно):** AP1 резервация имени; AP2 публичный репозиторий/страница с privacy
   policy и support; черновик AP3/AP4; владелец решает D-1…D-5.
2. **Пакет 1 — A1 корень хранилища + identity-probe** (`win32-interop-specialist` для probe,
   `dotnet-services-engineer` для резолвера): `Lazy`-probe, резолвер корня, `README.txt`, тесты
   (`AppStorageRootTests` на резолвер), amendment к ADR-009/024/041 и ~15 doc-комментариев
   (`architecture-documenter`). Зависит от D-1. **Все остальные кодовые пакеты зависят от probe этого пакета.**
3. **Пакет 2 — A2 автозапуск** (`wpf-ui-builder`): под identity — текст + `ms-settings:startupapps`,
   Inno-путь без изменений. Зависит от Пакета 1 и D-5.
4. **Пакет 3 — A4/B2/B4/B5/B6/B9** (`wpf-ui-builder`, resx en/ru): скрыть Updates под identity (A4),
   balloon (B2), тексты (B4 зависит от A1, B5), ссылка (B6 зависит от AP2), CHANGELOG known limitations (B9).
   Зависит от Пакета 1.
5. **Пакет 4 — A3 упаковка** (`dotnet-services-engineer`/CI): `.wapproj` в отдельном `.sln`/`.slnf` или
   MakeAppx-скрипт, манифест (Identity из AP1, startupTask из A2), ассеты из `app.svg` (скрипт в `tools/`
   или PNG от владельца), версия пакета по D-2, отдельный CI-job `.msixupload`, локальный WACK. Жёстко
   зависит от AP1; слабо — от Пакета 2 (TaskId).
6. **Ручная проверка на Windows 11** (владелец): удалить Inno-сборку; установить `.msix` локально
   (sideload с тестовым сертификатом, либо `Add-AppxPackage -Register` по распакованному layout при
   включённом Developer Mode); пройти сценарий AP3; проверить: файлы в
   `%USERPROFILE%\DesktopOrganizer\PanelItems` открываются из Explorer и перетаскиваются обратно;
   Uninstall и Settings → Apps → **Reset** не трогают эту папку; автозапуск появляется в Task Manager →
   Startup после включения в Параметрах; «Открыть папку логов/хранилища», crash-диалог и «Открыть папку» в
   Orphans открывают правильную папку (B7); карточка Updates скрыта; переключатель автозапуска заменён
   ссылкой.
7. **Отправка** в Partner Center: пакет, листинг, IARC, Notes for certification, privacy URL. Сертификация
   — до 3 рабочих дней.

## 9. История кругов анализа

- **v1 (3 аналитика):** 4 блокера кода, 9 should-fix, 9 roadmap.
- **Критика 1 (код + объём):** A1 усилен (невидимость файлов внешними процессами — не только удаление);
  «неверные папки» понижены до B7 и закрываются A1; side-by-side и alias → roadmap (C2/C3: 0 remote, 404,
  внешних пользователей нет); имя приложения и публичный URL подняты в процессные блокеры (AP1/AP2);
  найдена P1 (сброс user.config при обновлении); цена A1 снижена (миграции нет).
- **Критика 2 (сходимость):** Repair вычеркнут (данные сохраняет), остаются Uninstall + Reset; startupTask
  `Enabled="false"`; добавлены RID/SelfContained; требование версии ≥1.0.0.0 подтверждено документацией;
  скрытие Updates стало обязательным (A4); второй toast, placeholder и «Вернуть все» → roadmap (C8–C10);
  D-1 дополнен вариантом D.
- **Критика 3 (редакционная):** ссылки на код (10 шт.) подтверждены; оценки усилий подняты (A1 → M,
  A3 → M–L, итого 3–5 дней); RID/SelfContained перенесены из `UI.csproj` в упаковочный проект (тестовый
  проект ссылается на UI); `.wapproj` вынесен из основного sln (ломает `dotnet build` в CI); зависимости
  пакетов 2–4 от probe Пакета 1 проставлены; D-2 переформулирован (версия пакета — факт, продуктовая
  версия — решение); чеклист ручной проверки расширен (Reset, crash-диалог, Orphans).
- **v4 (2026-10-10, решения владельца):** D-1…D-6 зафиксированы (§7); AP2 переформулирована под закрытый код;
  добавлены «Следствия закрытого кода» и C14; реализация начата на `feature/store-publication`.
- **v3:** расхождений по классификации между критиками не осталось; дальнейшие круги без новых данных
  (решений владельца, реальной MSIX-сборки, прогона WACK) дадут только редакционные правки.
- **Реализация (2026-10-10):** пакеты 1–4 выполнены, ADR-050 + amendments ADR-009/024/027/038/041, ROADMAP обновлён.

## 10. Фактическая реализация (расхождения с планом)

Что реализовано иначе, чем описано в §1–§8 и в [store-publication-implementation.md](store-publication-implementation.md)
(источник истины — код и [ADR-050](../../adr/ADR-050-microsoft-store-channel-msix.md)):

- **Упаковка (A3):** вариант 2 из §1 (ручной манифест + `makeappx`), без `.wapproj` и без `.msixupload` — в Partner Center
  загружается `.msix` напрямую. Скрипт — `installer/build-msix.ps1`, артефакт — `installer/Output/DesktopOrganizer-X.Y.Z.msix`
  (не `installer/msix/Output/`). Идентичность задаётся параметрами/env `DESKTOPORGANIZER_MSIX_*`, а не файлами
  `identity.dev.psd1`/`identity.store.psd1`; переключателя `-Identity Dev|Store` нет; подпись для sideload — `-SignForSideload`
  (а не `-Sign`); скрипт ассетов — `installer/msix/Generate-Assets.ps1` (а не `New-MsixAssets.ps1`), 32 PNG.
- **CI:** job `msix` запускается на тегах `v*` и `workflow_dispatch` (а не «на всё, кроме pull_request»).
- **README (B-часть A1):** пишется только при `packaged && AppStorageRoot.IsPackagedLocation` (в плане — только `isPackaged`);
  при откате корня на `%LOCALAPPDATA%` (пустой профиль) README не создаётся, чтобы не обещать сохранность.
- **Move-hint (B4):** ключ формата — `Hint_Move_Message_Format` (в плане `Hint_Move_Message`); `MoveHintText.Compose` принимает
  `ILocalizationService`; фраза о сохранности зависит от `AppStorageRoot.IsPackagedLocation`, а не от identity.
- **Событие `PackageIdentity`:** поля `packaged`, `underUserProfile`, `probeResultCode`, `storageRoot` (в плане — `packaged`, `rc`);
  сырой код пробы идёт через pre-DI фасад `AppStorageRoot.PackageProbeResultCode` (дополнительное разрешённое место
  `PackageProbeUsageGuardTests`).
- **B6:** `PublicSiteUrl` — временная константа `https://github.com/messire`; настоящий URL страницы (AP2) от владельца не получен.
