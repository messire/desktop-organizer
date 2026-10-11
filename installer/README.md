# Сборка инсталлятора и MSIX-пакета

Приложение распространяется двумя каналами (версия 1.0.0, [ADR-050](../adr/ADR-050-microsoft-store-channel-msix.md)):

| Канал | Артефакт | Статус | Раздел |
|---|---|---|---|
| **Microsoft Store** (основной для пользователей) | `installer\Output\DesktopOrganizer-<версия>.msix` (не подписывается, подпись ставит Store) | отправляется в Partner Center владельцем | [MSIX для Microsoft Store](#msix-для-microsoft-store) |
| **Классический установщик** | `installer\Output\DesktopOrganizer-Setup-<версия>.exe` (Inno Setup, неподписанный, SmartScreen) | публичных релизов пока нет | остальные разделы |

Каналы используют разные корни хранилища (`%USERPROFILE%\DesktopOrganizer` у Store, `%LOCALAPPDATA%\DesktopOrganizer` у Inno)
и не делят данные. Код закрытый: ни один канал не обещает пользователю исходники.

Инсталлятор собирается через [Inno Setup](https://jrsoftware.org/isdl.php) (бесплатный, ~10 МБ).

## Разово

1. Установи Inno Setup 6: https://jrsoftware.org/isdl.php (можно на русском языке интерфейса — есть в комплекте).
2. Убедись, что установлен .NET 8 SDK (`dotnet --version` должен показать 8.x).

## Каждый релиз

1. Подними версию **один раз** — в `Directory.Build.props` (`<Version>`) в корне репозитория. `installer/DesktopOrganizer.iss` ничего не хранит сам: `MyAppVersion` читается из FILEVERSION-ресурса уже опубликованного `DesktopOrganizer.UI.exe` (`GetVersionNumbersString`), поэтому редактировать версию прямо в `.iss` не нужно и не имеет смысла — она возьмётся из свежего publish-выхода на следующем шаге.
2. Запусти:
   ```powershell
   .\installer\build.ps1
   ```
   Скрипт делает оба шага сразу: сначала `dotnet publish` (кладёт свежий self-contained билд в `installer\publish\win-x64`), затем сам находит `ISCC.exe` и собирает инсталлятор.
3. Готовый `DesktopOrganizer-Setup-<версия>.exe` появится в `installer\Output\`.

Если нужно только опубликовать билд без сборки инсталлятора: `.\installer\build.ps1 -SkipInstaller`.

### ⚠️ Самая частая ошибка: "пересобрал — а изменений нет"

Если открыть `installer\DesktopOrganizer.iss` в Inno Setup Compiler и просто нажать Build **без** предварительного запуска `build.ps1` (или после правки кода, но без повторной публикации) — ISCC молча упакует **старые** файлы из `installer\publish\win-x64`, которые остались с прошлого раза. Никакой ошибки при этом не будет: компиляция пройдёт успешно, инсталлятор соберётся, но внутри окажется прежний код.

Правило: **перед каждой сборкой инсталлятора сначала публикация**. Проще всего — всегда запускать `installer\build.ps1` целиком (он теперь сам вызывает ISCC в конце), а не компилировать `.iss` отдельно вручную.

Если приложение всё равно "не обновилось" после переустановки — проверь ещё дату модификации
`installer\publish\win-x64\DesktopOrganizer.UI.exe` — она должна быть свежее, чем последняя правка кода.
(Закрывать старый процесс в трее вручную перед установкой больше не нужно — см. следующий раздел.)

## Установка/удаление поверх запущенного приложения

Инсталлятор (и деинсталлятор) сам обнаруживает запущенный DesktopOrganizer и предлагает закрыть его,
вместо того чтобы просто блокироваться со своим собственным «закройте приложение и нажмите OK»:

1. Если приложение запущено, перед копированием файлов (или перед удалением) появляется диалог
   «DesktopOrganizer сейчас запущен. Закрыть его и продолжить установку/удаление?».
2. При согласии инсталлятор сигналит приложению штатно завершиться (тот же путь, что и «Выход» из
   меню трея: сохранение состояния, восстановление иконок рабочего стола, корректное закрытие) и ждёт,
   пока процесс и файл `DesktopOrganizer.UI.exe` действительно освободятся.
3. Если закрыть автоматически не получилось (например, установлена версия старее этой фичи, у которой
   нет такого слушателя) — показывается диалог «Не удалось закрыть автоматически… закройте вручную
   (значок в трее → Выход), затем нажмите Повторить», с кнопками Retry/Cancel.
4. Отказ на любом из диалогов отменяет установку/удаление без изменений на диске.
5. Restart Manager (`CloseApplicationsFilter=*.exe,*.dll` в `[Setup]`) остаётся как запасной механизм —
   на случай, если файл держит открытым что-то, кроме самого приложения.

**Автоматические/тихие обновления.** Диалоги выше показываются и в `/VERYSILENT`, если не передан
`/SUPPRESSMSGBOXES` — это документированное поведение Inno Setup (`SuppressibleMsgBox` показывает
диалог, если явно не подавлен). Для полностью безголового запуска (скрипт автообновления и т.п.)
передавай оба ключа: `/VERYSILENT /SUPPRESSMSGBOXES` — тогда «закрыть?» автоматически отвечается
«да», а «не удалось закрыть» — автоматически «отмена», без единого окна. Если закрытие запущенного
приложения тихим апдейтом действительно произошло, инсталлятор сам перезапускает его после установки
(обычный `[Run]`-пункт с `skipifsilent` в тихом режиме не срабатывает — без этого пользователь остался
бы без запущенного приложения после «фонового» апдейта).

## Что делает инсталлятор

- Ставит приложение в `%LocalAppData%\Programs\DesktopOrganizer` — **без запроса прав администратора** (UAC), т.к. само приложение работает только с HKCU и рабочим столом пользователя.
- Создаёт ярлык в меню Пуск и (опционально, чекбокс на экране установки) на рабочем столе.
- Регистрирует деинсталлятор в "Приложения и возможности" Windows, со ссылками "Support"/"Publisher"
  (`AppSupportURL`/`AppPublisherURL` в `.iss`) на `https://github.com/messire` (временное значение: заменяется на адрес
  публичной страницы поддержки, когда владелец его даст — шаг AP2 в `docs/ROADMAP.md`; контракт закреплён
  `InstallerMetadataContractTests`).
- При удалении подчищает папку установки.
- На финальной странице — две галочки: «Запустить DesktopOrganizer» и «Запускать при старте Windows».
  Вторая — **та же самая настройка**, что и чекбокс автозапуска в окне настроек приложения: обе стороны
  читают и пишут одно и то же значение `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\DesktopOrganizer`
  (`AutostartService` в приложении, секция `[Code]` в `.iss`), никакой копии флага в JSON нет.
  - Чистая установка: галочка включена по умолчанию.
  - Обновление: галочка показывает текущее состояние (если автозапуск выключили в приложении — останется выключенной).
  - Нажатие Finish применяет выбор: включено → значение записывается с путём к новому exe, снято → значение удаляется (автозапуск отключается).
  - Тихая установка (`/SILENT`, `/VERYSILENT`): финальной страницы нет, текущий выбор пользователя не меняется; существующее значение лишь перенацеливается на свежеустановленный exe.
  - Деинсталляция удаляет значение автозапуска.
  - Совпадение пути ключа и имени значения между `.iss` и `AutostartService` закреплено тестом `InstallerAutostartContractTests`.

## Раздача

Публичной раздачи классического установщика пока нет (репозиторий закрытый, GitHub Releases для пользователей не публикуются).
Если `DesktopOrganizer-Setup-<версия>.exe` передаётся напрямую, это единственный файл, который нужен получателю: он ничего не требует
предустановленным (Runtime зашит внутрь, ~60–100 МБ на выходе). Windows SmartScreen может показать предупреждение
"Неизвестный издатель" при первом запуске — это ожидаемо для неподписанных инсталляторов (см. следующий раздел).
Основной путь к пользователям — Microsoft Store (раздел ниже).

Различия каналов, видимые пользователю (подробно — `README.md` в корне):

| | Microsoft Store (MSIX) | Классический установщик |
|---|---|---|
| Хранилище | `%USERPROFILE%\DesktopOrganizer\{State,PanelItems,Logs}` + `README.txt`; переживает удаление и Reset | `%LOCALAPPDATA%\DesktopOrganizer\…`; деинсталлятор возвращает файлы на место |
| Автозапуск | Параметры Windows → Приложения → Автозагрузка (`startupTask`, выключен по умолчанию) | чекбокс установщика / переключатель в Settings → General (`HKCU\…\Run`) |
| Обновления | через Store, карточки Updates в About нет | кнопка в About |
| CLI-ключи | недоступны (exe в `WindowsApps`); `DESKTOPORGANIZER_DIAG=1` работает | `--diag`, `--restore-state-backup`, `--restore-panel-items` |
| Требования | Windows 10 2004 (19041)+ x64 | Windows 10/11 x64 |

## Подпись кода (опционально)

По умолчанию сборка **неподписана** — `build.ps1` печатает явное предупреждение об этом и ничего не
меняет в самом процессе сборки. Чтобы подписать опубликованные `DesktopOrganizer*.exe`/`*.dll`, а заодно
и сам `Setup.exe` (и деинсталлятор), задай переменные окружения перед запуском `build.ps1`:

| Переменная | Обязательна | Значение |
|---|---|---|
| `DESKTOPORGANIZER_SIGN_CERT` | да (включает подпись) | путь к `.pfx`-файлу **или** SHA1-отпечаток сертификата из локального хранилища сертификатов (**предпочтительный вариант** — см. ниже) |
| `DESKTOPORGANIZER_SIGN_PASSWORD` | только для `.pfx` | пароль к контейнеру `.pfx` |
| `DESKTOPORGANIZER_SIGN_TIMESTAMP` | нет | URL RFC3161 timestamp-сервера (по умолчанию `http://timestamp.digicert.com`) |
| `SIGNTOOL_PATH` | нет | явный путь к `signtool.exe`, если он не находится через Windows SDK/PATH автоматически |

**Предпочтительный вариант — отпечаток сертификата из хранилища** (пароль вообще не участвует):

```powershell
$env:DESKTOPORGANIZER_SIGN_CERT = "AABBCCDDEEFF00112233445566778899AABBCCDD"  # SHA1-отпечаток
.\installer\build.ps1
```

**Вариант с `.pfx`-файлом** (пароль используется только для прямой подписи publish-выхода — см. ниже):

```powershell
$env:DESKTOPORGANIZER_SIGN_CERT = "C:\secrets\desktoporganizer.pfx"
$env:DESKTOPORGANIZER_SIGN_PASSWORD = "..."
.\installer\build.ps1
```

Скрипт сам находит `signtool.exe`, подписывает publish-выход (`/fd sha256 /tr <timestamp> /td sha256`),
затем передаёт ISCC собственный sign tool (`/DSignEnabled /Sdosign=...`), которым Inno Setup подписывает
итоговый `Setup.exe` и деинсталлятор (`SignedUninstaller=yes`, `.iss`). Пароль сертификата никогда не
попадает в консольный вывод/лог сборки **и никогда — в строку, которую видит ISCC**: для ISCC скрипт
ВСЕГДА подписывает по отпечатку сертификата (`/sha1`), импортируя `.pfx` во временный сертификат текущего
пользователя (`Cert:\CurrentUser\My`), если был передан файл, а не отпечаток, и удаляя его перед выходом
из скрипта (в `finally`, независимо от результата сборки). Пароль (`/p`) используется только в прямом
вызове `signtool` для подписи publish-выхода (exe/dll) — том самом, что маскируется в выводе этого
скрипта, а не передаётся другому процессу. Без заданных переменных — прежнее поведение, инсталлятор
собирается неподписанным.

CI (`.github/workflows/ci.yml`) подписывает релизный `Setup.exe` только если те же переменные заданы как
GitHub Actions secrets — их отсутствие не останавливает сборку/публикацию тега, просто артефакт остаётся
неподписанным. В CI секрет `.pfx` импортируется в хранилище сертификатов ДО запуска `build.ps1` в
отдельном шаге, который передаёт скрипту уже готовый отпечаток (а не файл/пароль), и удаляет
сертификат из хранилища в шаге с `if: always()` сразу после сборки — так что весь путь `.pfx`+пароль в
CI используется только один раз, для самого импорта, и никогда не доходит ни до `build.ps1`, ни тем
более до строки, которую видит ISCC.

## MSIX для Microsoft Store

Store-канал (`docs/PLANS/store-publication-implementation.md`, Пакет 4) собирается **без Visual Studio и без
`.wapproj`**: ручной манифест `installer/msix/AppxManifest.xml` + `makepri.exe` + `makeappx.exe` из Windows SDK
(скрипт сам находит новейший `Windows Kits\10\bin\10.0.*\x64`; нужен установленный Windows 10/11 SDK).
Inno-инсталлятор выше не затрагивается, это отдельный канал.

### Как собрать

```powershell
.\installer\build-msix.ps1
```

Скрипт: публикует приложение через `build.ps1 -SkipInstaller` (self-contained win-x64), собирает layout
`installer\msix\layout` (publish-выход без `*.pdb` и XML-документации — `*.xml` рядом с одноимённой `.dll`/`.exe`,
остальные XML остаются — + `installer\msix\Assets`), подставляет в манифест версию, Identity и DisplayName через
XML DOM, строит `resources.pri` (`makepri`, по отдельной staging-папке только с манифестом и Assets, чтобы папку
`ru\` с .NET-сателлитами не приняли за языковой ресурс пакета) и упаковывает
`installer\Output\DesktopOrganizer-<версия>.msix` (`makeappx pack`).

Версия берётся из FILEVERSION `DesktopOrganizer.UI.exe` (как в `.iss`, источник один — `<Version>` в
`Directory.Build.props`): Store резервирует четвёртую секцию, поэтому ревизия обязана быть `0` (иначе скрипт
останавливается с ошибкой, а не обнуляет её молча), а major должен быть >= 1.

Скрипты `build-msix.ps1` и `msix\Generate-Assets.ps1` работают под `Set-StrictMode -Version Latest`
(вложенный `build.ps1` вызывается с выключенным strict mode — он на него не рассчитан).

#### Отклонения от плана `store-publication-implementation.md` (Пакет 4)

- **Имена параметров и переменных окружения** — `-IdentityName` / `-Publisher` / `-PublisherDisplayName` / `-DisplayName`
  ↔ `DESKTOPORGANIZER_MSIX_IDENTITY_NAME` / `_PUBLISHER` / `_PUBLISHER_DISPLAY_NAME` / `_DISPLAY_NAME` (префикс как у
  `DESKTOPORGANIZER_SIGN_*`); параметры сильнее переменных.
- **Имя выхода** — `installer\Output\DesktopOrganizer-<major.minor.build>.msix` (рядом с Inno-инсталлятором; в имени файла —
  трёхсекционная версия).
- **Условие CI** — job `msix` запускается на тегах `v*` и вручную (`workflow_dispatch`), `needs: build-and-test`; артефакт
  `DesktopOrganizer-msix` хранится 30 дней. При dev-заглушках Identity на раннере скрипт выводит аннотацию `::warning::`.
- **`_altform-lightunplated`** — к наборам `Square44x44Logo.targetsize-N` и `_altform-unplated` добавлены копии
  `_altform-lightunplated`, для светлой темы панели задач/Пуска (без плитки-подложки).
- **`makepri createconfig /dq en-US_ru-RU`** — языки пакета (`en-US`, `ru-RU`) совпадают с `<Resources>` манифеста; в
  сгенерированном `priconfig.xml` удаляется секция `<packaging>` (иначе `makepri new` режет ресурсы на
  `resources.scale-*.pri`, а в layout попадал бы только `resources.pri` — на HiDPI плитки брались бы из scale-100).
  После `makepri new` стоит guard: появление `resources.*.pri` в staging останавливает сборку.
- **Нет `.wapproj`** — упаковка только скриптами (закреплено тестом `MsixManifestContractTests`).

### Что подставить из Partner Center

Без параметров скрипт собирает пакет с **зарезервированной Store-идентичностью** (Partner Center, 2026-10-11):
`khagendev.DesktopOrganizer` / `CN=96A4E2BB-2519-41C4-9349-F738CF8EB8FB` / `khagendev` — это и есть файл для загрузки
в Partner Center. Ключ `-DevIdentity` переключает на dev-заглушки (`khagendev.DesktopOrganizer.Dev` /
`CN=DesktopOrganizer Dev`) для локального sideload-теста (`-DevIdentity -SignForSideload`); такой пакет Store отклонит,
скрипт печатает предупреждение. Значения из Partner Center (Product management → Product identity) должны совпадать
**символ в символ**; переопределение — параметрами или переменными окружения:

| Параметр | Переменная окружения | Поле в Partner Center |
|---|---|---|
| `-IdentityName` | `DESKTOPORGANIZER_MSIX_IDENTITY_NAME` | Package/Identity/Name |
| `-Publisher` | `DESKTOPORGANIZER_MSIX_PUBLISHER` | Package/Identity/Publisher (`CN=…`) |
| `-PublisherDisplayName` | `DESKTOPORGANIZER_MSIX_PUBLISHER_DISPLAY_NAME` | Package/Properties/PublisherDisplayName |
| `-DisplayName` | `DESKTOPORGANIZER_MSIX_DISPLAY_NAME` | Package/Properties/DisplayName (имя продукта; дефолт `DesktopOrganizer`) |

`DisplayName` подставляется в трёх местах манифеста (`Properties/DisplayName`, `uap:VisualElements/@DisplayName`,
`desktop:StartupTask/@DisplayName`) — это пятый токен `$DISPLAY_NAME$` шаблона.

```powershell
.\installer\build-msix.ps1 -IdentityName "<Name>" -Publisher "CN=<GUID>" -PublisherDisplayName "<имя издателя>" -DisplayName "<имя продукта>"
```

- На **каждый язык** из `<Resources>` манифеста (`en-US`, `ru-RU`) в Partner Center нужен свой Store listing.
- Для capability `runFullTrust` при сабмите требуется обоснование — текст в `docs/store/notes-for-certification.md`
  (лежит в основном дереве репозитория).

Store-пакет **не подписывается** — подпись ставит сам Store; в Partner Center загружается `.msix` напрямую
(`.msixupload` не нужен). В CI job `msix` (`.github/workflows/ci.yml`, на тегах `v*` и вручную) значения берутся из
repository variables с теми же именами, что и переменные окружения выше; артефакт `DesktopOrganizer-msix`.

### Sideload и WACK

Проверка пакета на своей машине (сначала удали Inno-сборку, иначе у приложения будет два корня хранилища):

```powershell
.\installer\build-msix.ps1 -SignForSideload
# затем из ПОВЫШЕННОГО PowerShell (скрипт сертификат сам не импортирует):
Import-Certificate -FilePath .\installer\Output\DesktopOrganizer-sideload.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
Add-AppxPackage -Path .\installer\Output\DesktopOrganizer-<версия>.msix
```

`-SignForSideload` создаёт (или переиспользует) self-signed сертификат в `Cert:\CurrentUser\My` с Subject =
`-Publisher` и подписывает им пакет через `signtool`. Это только для разработки: подписанный так пакет раздавать нельзя.
Сертификат: FriendlyName `DesktopOrganizer sideload`, EKU Code Signing (`1.3.6.1.5.5.7.3.3`), ключ `NonExportable`,
срок 1 год. Переиспользуется только сертификат с тем же FriendlyName, тем же Subject (сравнение как
distinguished name, а не строки), EKU Code Signing и действующим сроком. С не-dev `-Publisher` (то есть с реальным
Subject из Partner Center) скрипт откажется подписывать и остановится; обойти можно только явным `-Force`.

#### Как удалить sideload-сертификат

После проверки пакета сертификат стоит убрать (доверенный self-signed сертификат в `TrustedPeople` позволяет ставить
любые пакеты, подписанные им):

```powershell
# пакет
Get-AppxPackage <IdentityName> | Remove-AppxPackage

# доверие (ПОВЫШЕННЫЙ PowerShell) - по отпечатку из .cer
$thumbprint = (Get-PfxCertificate -FilePath .\installer\Output\DesktopOrganizer-sideload.cer).Thumbprint
Remove-Item Cert:\LocalMachine\TrustedPeople\$thumbprint

# сам сертификат с приватным ключом
Get-ChildItem Cert:\CurrentUser\My | Where-Object FriendlyName -eq 'DesktopOrganizer sideload' | Remove-Item
```

Windows App Certification Kit: `.\installer\build-msix.ps1 -SignForSideload -RunWack` из повышенной оболочки
(WACK устанавливает пакет, поэтому сертификат должен быть уже доверенным). Отчёт —
`installer\Output\wack-report.xml`, ищем `OVERALL_RESULT="PASS"`. Если `appcert.exe` не найден — предупреждение, не ошибка.

### Ассеты

PNG-набор плиток и логотипов лежит в `installer\msix\Assets` и **закоммичен**; сборка его не пересоздаёт.
После смены `src\DesktopOrganizer.UI\Resources\app.ico` перегенерируй и закоммить результат:

```powershell
.\installer\msix\Generate-Assets.ps1
```

Скрипт берёт PNG-кадр 256×256 из `app.ico` и масштабирует его через System.Drawing (HighQualityBicubic,
прозрачный фон). Контракт набора (имена, размеры, лимит 200 КБ) закреплён тестом `MsixManifestContractTests`.
