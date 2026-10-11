# DesktopOrganizer

A Windows desktop organizer in the spirit of [Stardock Fences](https://www.stardock.com/products/fences) — group your desktop icons into movable, resizable panels ("fences"), keep multiple pages of them, and get your desktop back.

Windows 10/11 (x64), .NET 8, WPF — self-contained, nothing else to install. No account, no telemetry, no ads.

Version 1.0.0. Two ways to get it: the **Microsoft Store** (recommended) or the **classic installer**.

## Contents

- [What it is](#what-it-is)
- [Install](#install)
- [Features](#features)
- [Where your data lives](#where-your-data-lives)
- [Autostart and updates](#autostart-and-updates)
- [Diagnostics and logs](#diagnostics-and-logs)
- [Command-line options](#command-line-options)
- [Uninstall](#uninstall)
- [Known limitations](#known-limitations)
- [Support and license](#support-and-license)
- [Русская версия](#desktoporganizer-ru)

## What it is

DesktopOrganizer runs quietly in the tray and lets you drag desktop icons into panels that live directly
on the desktop layer — panels stay below your other windows, don't show up in Alt+Tab or the taskbar, and
move/resize like any other window. Files dropped into a panel are moved into the app's own per-user
storage so the desktop shortcut you had is gone, not duplicated; removing an item from a panel puts the
original file back where it came from.

## Install

### Microsoft Store (recommended)

1. Find **DesktopOrganizer** in the Microsoft Store app and click Install. Requires Windows 10 version 2004
   (build 19041) or later, x64. No administrator rights are needed and there is no SmartScreen warning.
2. Start it from the Start menu. On first run it creates one empty panel so you have something to start
   with, and shows a notification that the app lives in the notification area (it may be hidden under the
   "^" arrow). Drag desktop icons into the panel (and out again any time), right-click a panel's title bar
   for options, and right-click the tray icon for Settings and Exit.
3. Updates come through the Store.

### Classic installer

The classic installer has no public releases yet. If you were given a `DesktopOrganizer-Setup-<version>.exe`
directly (Windows 10/11, x64):

1. Run it. No administrator rights are requested — the app only ever touches your own user profile
   (`HKCU` and your desktop), so it installs per-user under `%LocalAppData%\Programs\DesktopOrganizer`.
2. On first run it creates one empty panel and shows the same notification-area notice as the Store build.
   Drag desktop icons into the panel (and out again any time), right-click a panel's title bar for options,
   and right-click the tray icon for global settings.
3. Windows SmartScreen may warn "Unknown publisher" the first time you run it — the installer is unsigned
   (no purchased code-signing certificate); click "More info" → "Run anyway" if you trust the person you
   received it from.

## Features

- Multiple movable, resizable panels ("fences") embedded directly into the desktop — hidden from
  Alt+Tab/taskbar, always below your other windows.
- Multiple pages of panels, switchable from the tray/settings, each with its own panel set and its own
  editable name (rename a page from Settings → Pages); the tray's right-click menu lists every page by
  name for quick switching, and double-clicking the tray icon cycles to the next one.
- Copy a panel to another page (including its own page) as a fully independent duplicate — its own copies
  of any managed files, so deleting one copy never touches the other.
- Grid view and list view per panel, with icon-size, sort mode (manual drag-to-reorder or by name),
  content filter, and roll-up (collapse to just the title bar).
- Move semantics on drag & drop: dropping a file/folder into a panel moves it into the app's managed
  storage (no duplicate desktop shortcut); removing it from the panel restores it to where it came from.
- Folder Portals: point a panel at a real folder and it mirrors that folder's contents live.
- Icons load in the background: panels stay responsive at startup and while you work even with a large
  Folder Portal or shortcuts pointing at a currently-unreachable network share — items show a placeholder,
  then their real icon, as soon as it's ready.
- Multi-monitor aware placement, magnetic grid snapping while dragging/resizing, and layout snapshots
  (save/restore a full arrangement of pages and panels by name).
- A dedicated "system icons" sidebar panel for the classic desktop icons (This PC, Recycle Bin, etc.),
  independent of your regular panels.
- Show/hide the underlying desktop icons with one toggle (tray or settings), independent of panel
  contents.
- Start with Windows (optional; where you turn it on depends on how you installed — see
  [Autostart and updates](#autostart-and-updates)).
- Full English and Russian UI, switchable live from Settings without restarting.
- A single, consistent light/dark interface that follows Windows' own app-mode setting live (no
  restart) across Settings, the "Customize panel…" window, every menu and dialog, and the tray — including
  High Contrast; a panel's own colors are unaffected, that's still yours to set.
- Classic installer only: installing or uninstalling over a running instance asks it to close gracefully
  first (state save, desktop icons restored) instead of just blocking.

## Where your data lives

Everything DesktopOrganizer stores is under your own user profile — nothing is written system-wide and
nothing leaves your machine. The folder depends on how you installed it:

| | Microsoft Store | Classic installer |
|---|---|---|
| Data folder | `%USERPROFILE%\DesktopOrganizer\` | `%LOCALAPPDATA%\DesktopOrganizer\` |
| Layout and state | `State\` — pages/panels/layout as JSON (versioned schema, automatic backups on save) | same, under its folder |
| Your files | `PanelItems\` — files/folders you dragged into a panel (moved here from their original location) | same, under its folder |
| Diagnostics logs | `Logs\` (see below) | same, under its folder |
| `README.txt` in the data folder | yes, written on first run (English and Russian) | no |
| After uninstall / Reset | the folder stays; your files are not deleted | the uninstaller returns your files (see [Uninstall](#uninstall)); `State\` and `Logs\` stay |

The two builds use separate folders and do not share panels.

A handful of small preferences (interface language, one-time hints, etc.) live in the standard per-user
`.NET` application settings store under `%LocalAppData%` in both builds (managed by the framework, not
something you need to manage by hand). In the Store build they are reset to defaults when the Store
updates the app — see [Known limitations](#known-limitations).

To get a file out of a panel: drag it onto the desktop or into any folder, or open Settings → Backup →
"Files without a panel" and press Return for files no panel uses any more. Pressing Delete on an item
returns the file and sends it to the Recycle Bin.

## Autostart and updates

| | Microsoft Store | Classic installer |
|---|---|---|
| Start with Windows | Windows Settings → Apps → Startup. Off by default; the entry appears there after the first launch. Settings → General has a button that opens that page. | A checkbox on the installer's last page, or the toggle in Settings → General — both control the same setting (`HKCU` Run entry) |
| Updates | Through the Microsoft Store; there is no Updates card in About | The "Check for updates" button in Settings → About (no public releases yet, so it normally reports none) |

## Diagnostics and logs

DesktopOrganizer always writes a small, rotating diagnostics log (`app-<timestamp>-<pid>.jsonl`, JSON
Lines, up to 10 files × 10 MB) to the `Logs` folder inside its data folder (see
[Where your data lives](#where-your-data-lives)), in English, with file paths and
your Windows username stripped out before anything is written — useful if you need to report a problem. In
everyday use the log only records errors, warnings, and a few startup/shutdown lifecycle events; it does
not record what you click or drag.

For a more detailed trace while reproducing a problem, set the environment variable
`DESKTOPORGANIZER_DIAG=1` before starting the app (works in both builds), or — classic installer only —
run it with the `--diag` command-line flag, to also capture verbose gesture/interaction events in the
same log folder.

If the app crashes, it also writes a `crash-<timestamp>-<pid>.log` file to the same folder (up to 10 kept)
describing the error (also scrubbed of paths/username) — attach it, together with the `.jsonl` log, to a
bug report.

## Command-line options

Available in the **classic installer** build only. In the Store build the executable lives in the protected
`WindowsApps` folder and cannot be started by hand with arguments; only the `DESKTOPORGANIZER_DIAG=1`
environment variable (see above) works there.

| Flag | What it does |
|---|---|
| `--diag` | Enables verbose diagnostics logging for this run (see above). |
| `--restore-state-backup` | Restores your layout from the most recent automatic backup instead of the live state file — use this if a previous run's state file was corrupted (the app already keeps rolling `.bak` copies made on every successful save). Prints what it did to the console and to the log, then exits; start the app normally afterwards. |
| `--restore-panel-items` | Returns every file/folder currently moved into the app's managed `PanelItems` storage to its original location (or the desktop, if that location is gone) — run automatically by the uninstaller, not meant to be typed by hand. |

## Uninstall

**Microsoft Store.** Uninstall from Windows Settings → Apps → Installed apps (or right-click the app in the
Start menu). Neither uninstalling nor "Reset" deletes `%USERPROFILE%\DesktopOrganizer`, so the files you
moved into panels are still there, in `PanelItems\` (the sub-folders have generated names). The Store build
does not return them automatically: before uninstalling, drag them out of their panels, or afterwards copy
them out of the folder yourself. Delete the folder yourself if you want a truly clean removal — but only
after taking your files back.

**Classic installer.** Uninstalling through "Apps & features" removes the installed program files. Before
that, it also automatically returns every file/folder you had dragged into a panel (moved into the app's
managed `PanelItems` storage under `%LocalAppData%\DesktopOrganizer`) back to its original location — or to
your desktop, if that original location is gone, on a different drive, or already occupied by something
else — so nothing is stranded in a hidden folder. Your layout/settings under `State\` and the diagnostics
log files under `Logs\` are **left in place** — reinstalling the same version later picks up right where
you left off. Delete `%LocalAppData%\DesktopOrganizer` yourself if you want a truly clean removal.

## Known limitations

- Per-monitor DPI depends on how Windows currently embeds panels into the desktop layer, which differs by
  OS version:
  - **Embedded mode** (Windows 10, Windows 11 up to 23H2): panels are children of Explorer's own desktop
    window and share **one** common scale (the scale of that desktop layer) rather than each rendering
    natively at its own monitor's DPI on a mixed-DPI multi-monitor setup — a known, documented constraint
    of this embedding approach, not a bug.
  - **Top-level mode** (Windows 11 24H2 and newer): panels are genuinely per-monitor — each one renders at
    its own monitor's real DPI, including on a mixed-DPI setup.
- If `explorer.exe` restarts or crashes while the app is running, panels, the sidebar, and desktop-icon
  visibility now recover automatically (usually within a few seconds); no manual action is normally needed.
- Deleting a panel or a page moves any files it holds into the **Recycle Bin** — it never deletes them
  outright. If you restore such a file from the Recycle Bin afterwards, it reappears under
  `PanelItems\<guid>\` in the data folder (its original per-item managed-storage folder), not back onto
  any panel — the panel it belonged to is gone by the time you restore it.
- Very long file paths (beyond `MAX_PATH`) are not supported by the underlying Recycle Bin/shell
  operations the app relies on for some file actions.
- Downgrading to 0.8.0 or older after running 0.9.0 or newer is not supported automatically:
  the older build sees a settings-file format it doesn't recognize and starts with an empty, read-only
  session, without any notice (your data is not deleted — the file itself is left untouched, and going
  back to 0.9.0+ picks it right back up). Manual recovery on an older version requires restoring the
  automatic pre-upgrade snapshot (`application-state.premigration-v6.json`) by hand — see the Upgrade
  notes in `CHANGELOG.md`.

Microsoft Store build:

- The preferences kept in `user.config` (interface language, one-time hints already shown, layout lock) are
  reset to their defaults every time the Store updates the app. Panels, pages and files are not affected.
  After an update the one-time "file was moved" hint shows again.
- Uninstalling the app while it is running leaves the desktop icons hidden if they were hidden at that
  moment; restart Explorer or sign out and in again to bring them back.
- Autostart can only be turned on in Windows Settings → Apps → Startup, and the entry appears there only
  after the app has been started once.
- There are no command-line options (see above), and no automatic return of your files on uninstall.
- Installing the Store build side by side with the classic installer is not supported: they use separate
  data folders, and starting one while the other is running just brings up the running one's settings.

## Support and license

Copyright (c) 2024-2026 khagen. Support: https://messire.github.io/desktop-organizer-site/, e.smirnov@protonmail.com. The Microsoft Store build is
licensed under the Standard Application License Terms.

---

# DesktopOrganizer (RU)

Органайзер рабочего стола для Windows в духе
[Stardock Fences](https://www.stardock.com/products/fences) — группируйте значки рабочего стола в
перемещаемые панели ("fences"), храните несколько страниц таких раскладок, и наконец наведите порядок на
столе.

Windows 10/11 (x64), .NET 8, WPF — приложение самодостаточное, ничего дополнительно ставить не нужно. Без
аккаунта, без телеметрии, без рекламы.

Версия 1.0.0. Два способа получить: **Microsoft Store** (рекомендуется) или **классический установщик**.

## Содержание

- [Что это](#что-это)
- [Установка](#установка)
- [Возможности](#возможности)
- [Где хранятся данные](#где-хранятся-данные)
- [Автозапуск и обновления](#автозапуск-и-обновления)
- [Диагностика и логи](#диагностика-и-логи)
- [Опции командной строки](#опции-командной-строки)
- [Удаление](#удаление)
- [Известные ограничения](#известные-ограничения)
- [Поддержка и лицензия](#поддержка-и-лицензия)
- [English version](#desktoporganizer)

## Что это

DesktopOrganizer тихо работает из трея и позволяет перетаскивать значки рабочего стола в панели, которые
живут прямо в слое рабочего стола: панели остаются под другими окнами, не показываются в Alt+Tab и на
панели задач, при этом двигаются и ресайзятся как обычное окно. Файлы, перетащенные в панель,
перемещаются в собственное хранилище приложения (в пределах вашего профиля) — исходный ярлык на столе не
дублируется, а исчезает; при удалении элемента из панели файл возвращается на исходное место.

## Установка

### Microsoft Store (рекомендуется)

1. Найди **DesktopOrganizer** в приложении Microsoft Store и нажми «Установить». Нужна Windows 10 версии
   2004 (сборка 19041) или новее, x64. Права администратора не требуются, предупреждения SmartScreen нет.
2. Запусти из меню «Пуск». При первом запуске создаётся одна пустая панель — чтобы было с чего начать, —
   и показывается уведомление о том, что приложение живёт в области уведомлений (значок может быть скрыт
   под стрелкой «^»). Перетащи в панель значки с рабочего стола (и обратно — в любой момент), правый клик
   по заголовку панели — опции панели, правый клик по значку в трее — Настройки и Выход.
3. Обновления приходят через Store.

### Классический установщик

Публичных релизов классического установщика пока нет. Если тебе напрямую передали
`DesktopOrganizer-Setup-<версия>.exe` (Windows 10/11, x64):

1. Запусти. Права администратора не требуются — приложение работает только с твоим профилем (`HKCU` и
   рабочим столом), поэтому ставится по пользователю в `%LocalAppData%\Programs\DesktopOrganizer`.
2. При первом запуске создаётся одна пустая панель и показывается то же уведомление об области
   уведомлений, что и в Store-сборке. Перетащи в неё значки с рабочего стола (и обратно — в любой
   момент), правый клик по заголовку панели — опции панели, правый клик по значку в трее — общие
   настройки.
3. Windows SmartScreen может показать предупреждение «Неизвестный издатель» при первом запуске — установщик
   не подписан (нет купленного сертификата подписи кода); нажми «Дополнительно» → «Выполнить в любом
   случае», если доверяешь тому, от кого его получил.

## Возможности

- Несколько перемещаемых, изменяемых по размеру панелей ("fences"), встроенных прямо в слой рабочего
  стола — скрыты из Alt+Tab/панели задач, всегда под другими окнами.
- Несколько страниц панелей, переключаемых из трея/настроек, каждая — со своим набором панелей и своим
  редактируемым именем (переименование — в Настройках → Страницы); меню трея показывает список страниц по
  именам для быстрого переключения, а двойной клик по иконке трея переключает на следующую страницу.
- Копирование панели на другую страницу (в том числе на ту же самую) как полностью независимой копии —
  со своими копиями всех перемещённых в неё файлов, удаление одной копии никогда не затрагивает другую.
- Вид «сетка» и вид «список» для каждой панели, размер значков, режим сортировки (ручной
  drag-to-reorder или по имени), фильтр содержимого, сворачивание в заголовок (roll-up).
- Move-семантика drag&drop: перетаскивание файла/папки в панель перемещает его в управляемое хранилище
  приложения (без дублирующего ярлыка на столе); удаление из панели возвращает файл туда, откуда он был
  взят.
- Folder Portals: панель может «смотреть» на реальную папку и живо отражать её содержимое.
- Иконки загружаются в фоне: панели остаются отзывчивыми при старте и в работе, даже с большим Folder
  Portal или ярлыками на временно недоступную сетевую шару — элемент сначала показывает плейсхолдер, затем
  свою настоящую иконку, как только она готова.
- Учёт нескольких мониторов при размещении, магнитное прилипание к сетке при перетаскивании/ресайзе, и
  снапшоты раскладки (сохранение/восстановление всей расстановки страниц и панелей по имени).
- Отдельная боковая панель «системные значки» для классических значков рабочего стола (Этот компьютер,
  Корзина и т.д.), независимая от обычных панелей.
- Скрытие/показ настоящих значков рабочего стола одним переключателем (трей или настройки), независимо
  от содержимого панелей.
- Запуск вместе с Windows (по желанию; где его включать, зависит от способа установки — см.
  [Автозапуск и обновления](#автозапуск-и-обновления)).
- Полный русский и английский интерфейс, переключается вживую в Настройках без перезапуска.
- Единый светлый/тёмный интерфейс, который следует системному режиму приложений Windows вживую (без
  перезапуска) — окно настроек, окно «Настроить панель…», все меню и диалоги, трей; поддерживается и режим
  высокой контрастности. Цвет самих панелей при этом не меняется — это по-прежнему твой выбор.
- Только классический установщик: установка/удаление поверх запущенного приложения сначала просит его
  штатно закрыться (сохранение состояния, восстановление значков рабочего стола), а не просто
  блокируется.

## Где хранятся данные

Всё, что хранит DesktopOrganizer, находится в пределах твоего профиля — ничего не пишется в систему
целиком и ничего не покидает компьютер. Папка зависит от способа установки:

| | Microsoft Store | Классический установщик |
|---|---|---|
| Папка данных | `%USERPROFILE%\DesktopOrganizer\` | `%LOCALAPPDATA%\DesktopOrganizer\` |
| Раскладка и состояние | `State\` — страницы/панели/раскладка в JSON (версионированная схема, автобэкапы при сохранении) | то же, в своей папке |
| Твои файлы | `PanelItems\` — файлы/папки, перетащенные в панель (перемещены сюда с исходного места) | то же, в своей папке |
| Файлы диагностического лога | `Logs\` (см. ниже) | то же, в своей папке |
| `README.txt` в папке данных | есть, создаётся при первом запуске (на русском и английском) | нет |
| После удаления / сброса | папка остаётся, файлы не удаляются | деинсталлятор возвращает файлы (см. [Удаление](#удаление)); `State\` и `Logs\` остаются |

Две сборки используют разные папки и не делят панели между собой.

Небольшой набор настроек (язык интерфейса, одноразовые подсказки и т.п.) в обеих сборках хранится в
стандартном пользовательском хранилище настроек `.NET` под `%LocalAppData%` (управляется фреймворком,
руками лезть туда не нужно). В Store-сборке они сбрасываются к значениям по умолчанию при обновлении
приложения из Store — см. [Известные ограничения](#известные-ограничения).

Чтобы достать файл из панели: перетащи его на рабочий стол или в любую папку, либо открой Настройки →
Резервные копии → «Файлы без панели» и нажми «Вернуть» для файлов, которые больше не используются ни одной
панелью. Клавиша Delete на элементе возвращает файл и отправляет его в Корзину.

## Автозапуск и обновления

| | Microsoft Store | Классический установщик |
|---|---|---|
| Запуск вместе с Windows | Параметры Windows → Приложения → Автозагрузка. По умолчанию выключен; запись появляется там после первого запуска. Кнопка в Настройки → Общие открывает эту страницу. | Галочка на последней странице установщика или переключатель в Настройки → Общие — обе стороны управляют одной и той же настройкой (запись `Run` в `HKCU`) |
| Обновления | Через Microsoft Store; карточки «Обновления» в «О программе» нет | Кнопка проверки обновлений в Настройки → О программе (публичных релизов пока нет, поэтому обычно сообщает, что их нет) |

## Диагностика и логи

DesktopOrganizer всегда пишет небольшой ротируемый диагностический лог (`app-<таймстамп>-<pid>.jsonl`,
JSON Lines, до 10 файлов по 10 МБ) в папку `Logs` внутри своей папки данных (см.
[Где хранятся данные](#где-хранятся-данные)), на английском языке, с
вырезанными путями к файлам и именем пользователя Windows — пригодится, если нужно сообщить о проблеме. В
обычном режиме в лог попадают только ошибки, предупреждения и несколько событий жизненного цикла
запуска/остановки; клики и перетаскивания не записываются.

Для более подробной трассировки при воспроизведении проблемы задай переменную окружения
`DESKTOPORGANIZER_DIAG=1` перед запуском (работает в обеих сборках) или — только в классическом
установщике — запусти приложение с флагом командной строки `--diag`, чтобы дополнительно писать в тот же
лог подробные события жестов/взаимодействия.

Если приложение падает, туда же пишется файл `crash-<таймстамп>-<pid>.log` (хранится до 10 штук) с описанием
ошибки (тоже без путей/имени пользователя) — приложи его вместе с `.jsonl`-логом к сообщению о проблеме.

## Опции командной строки

Доступны только в сборке **классического установщика**. В Store-сборке исполняемый файл лежит в защищённой
папке `WindowsApps` и не может быть запущен вручную с аргументами; там работает только переменная
окружения `DESKTOPORGANIZER_DIAG=1` (см. выше).

| Флаг | Что делает |
|---|---|
| `--diag` | Включает подробное диагностическое логирование для этого запуска (см. выше). |
| `--restore-state-backup` | Восстанавливает раскладку из последнего автоматического бэкапа вместо текущего файла состояния — используй, если файл состояния из прошлого запуска повреждён (приложение уже хранит цепочку `.bak`-копий, создаваемых при каждом успешном сохранении). Печатает результат в консоль и в лог, затем завершается; после этого запусти приложение как обычно. |
| `--restore-panel-items` | Возвращает все файлы/папки, перемещённые в управляемое хранилище `PanelItems`, обратно на исходное место (или на рабочий стол, если исходное место исчезло) — запускается автоматически деинсталлятором, вручную вводить не нужно. |

## Удаление

**Microsoft Store.** Удаляй через Параметры Windows → Приложения → Установленные приложения (или правым
кликом по приложению в меню «Пуск»). Ни удаление, ни «Сброс» не удаляют `%USERPROFILE%\DesktopOrganizer`,
так что файлы, перемещённые в панели, остаются в `PanelItems\` (подпапки имеют служебные имена).
Store-сборка не возвращает их автоматически: до удаления вытащи их из панелей, либо потом скопируй из
папки сам. Удали папку вручную, если нужна полностью чистая деинсталляция, — но только после того, как
заберёшь свои файлы.

**Классический установщик.** Удаление через «Программы и компоненты» убирает файлы самой программы. Перед
этим оно также автоматически возвращает все файлы/папки, перетащенные в панели (перемещённые в
управляемое хранилище `PanelItems` под `%LocalAppData%\DesktopOrganizer`), обратно на исходное место — или
на рабочий стол, если исходное место исчезло, находится на другом диске, или уже занято чем-то другим — так
что ничего не остаётся забытым в скрытой папке. Раскладка/настройки под `State\` и файлы диагностического
лога под `Logs\` **остаются на месте** — переустановка той же версии позже продолжит с того же места. Удали
`%LocalAppData%\DesktopOrganizer` вручную, если нужна полностью чистая деинсталляция.

## Известные ограничения

- Работа per-monitor DPI зависит от того, как Windows сейчас встраивает панели в слой рабочего стола, а
  это отличается по версии ОС:
  - **Встроенный режим** (Windows 10, Windows 11 до 23H2 включительно): панели — дочерние окна
    собственного окна рабочего стола Explorer и используют **один** общий масштаб (масштаб этого слоя), а
    не собственный DPI каждого монитора на конфигурации с разным DPI — задокументированное ограничение
    этого способа встраивания, не баг.
  - **Top-level режим** (Windows 11 24H2 и новее): панели по-настоящему per-monitor — каждая рендерится в
    реальном DPI своего монитора, в том числе на конфигурации с разным DPI.
- Если `explorer.exe` перезапускается или падает во время работы приложения, панели, боковая панель и
  видимость значков рабочего стола теперь восстанавливаются автоматически (обычно за несколько секунд);
  вручную ничего делать не нужно.
- Удаление панели или страницы перемещает её файлы в **Корзину** — они никогда не удаляются безвозвратно.
  Если потом восстановить такой файл из Корзины, он появится в
  `PanelItems\<guid>\` внутри папки данных (исходной папке управляемого хранилища для этого
  элемента), а не обратно на панель — панель, которой он принадлежал, к моменту восстановления уже удалена.
- Очень длинные пути (больше `MAX_PATH`) не поддерживаются операциями Корзины/shell, на которые опирается
  часть файловых действий приложения.
- Откат на 0.8.0 или старше после запуска 0.9.0 или новее не поддерживается автоматически: старая
  сборка видит незнакомый ей формат файла настроек и стартует с пустой, недоступной для записи сессией
  без какого-либо уведомления (данные не удаляются — сам файл остаётся нетронутым, и возврат на 0.9.0+
  сразу возвращает всё на место). Ручное восстановление на старой версии требует вручную восстановить
  автоматический снимок, сделанный перед апгрейдом (`application-state.premigration-v6.json`), — см.
  раздел Upgrade notes в `CHANGELOG.md`.

Сборка из Microsoft Store:

- Настройки из `user.config` (язык интерфейса, уже показанные одноразовые подсказки, блокировка раскладки)
  сбрасываются к значениям по умолчанию при каждом обновлении приложения из Store. Панели, страницы и файлы
  не затрагиваются. После обновления одноразовая подсказка «файл перемещён» показывается снова.
- Если удалить приложение, пока оно запущено, значки рабочего стола остаются скрытыми, если на тот момент
  были скрыты; перезапусти Explorer или выйди из системы и войди снова, чтобы вернуть их.
- Автозапуск включается только в Параметры Windows → Приложения → Автозагрузка, и запись там появляется
  только после первого запуска приложения.
- Опций командной строки нет (см. выше), и файлы при удалении автоматически не возвращаются.
- Установка Store-сборки одновременно с классическим установщиком не поддерживается: у них разные папки
  данных, а запуск одной при работающей другой просто открывает настройки уже запущенной.

## Поддержка и лицензия

Copyright (c) 2024-2026 khagen. Поддержка: https://messire.github.io/desktop-organizer-site/, e.smirnov@protonmail.com. Сборка из Microsoft Store
распространяется на условиях Standard Application License Terms.
