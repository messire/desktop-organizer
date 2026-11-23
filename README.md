# DesktopOrganizer

A Windows desktop organizer: group your desktop icons into movable, resizable panels, keep multiple pages of them, and get your desktop back.

Windows 10/11 (x64), .NET 8, WPF — self-contained, nothing else to install. No account, no telemetry, no ads.

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

Windows 10/11, x64. Build the installer from source on Windows (needs the .NET 8 SDK and
[Inno Setup 6](https://jrsoftware.org/isdl.php)):

```powershell
.\installer\build.ps1
```

The result is `installer\Output\DesktopOrganizer-Setup-<version>.exe`; see [installer/README.md](installer/README.md).

1. Run it. No administrator rights are requested — the app only ever touches your own user profile
   (`HKCU` and your desktop), so it installs per-user under `%LocalAppData%\Programs\DesktopOrganizer`.
2. On first run it creates one empty panel so you have something to start with, and shows a notification
   that the app lives in the notification area (it may be hidden under the "^" arrow). Drag desktop icons
   into the panel (and out again any time), right-click a panel's title bar for options, and right-click
   the tray icon for Settings and Exit.
3. Windows SmartScreen may warn "Unknown publisher" the first time you run it — the installer is unsigned;
   click "More info" → "Run anyway".

## Features

- Multiple movable, resizable panels embedded directly into the desktop — hidden from
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
- Start with Windows (optional; see [Autostart and updates](#autostart-and-updates)).
- Full English and Russian UI, switchable live from Settings without restarting.
- A single, consistent light/dark interface that follows Windows' own app-mode setting live (no
  restart) across Settings, the "Customize panel…" window, every menu and dialog, and the tray — including
  High Contrast; a panel's own colors are unaffected, that's still yours to set.
- Installing or uninstalling over a running instance asks it to close gracefully
  first (state save, desktop icons restored) instead of just blocking.

## Where your data lives

Everything DesktopOrganizer stores is under your own user profile — nothing is written system-wide and
nothing leaves your machine. The data folder is `%LOCALAPPDATA%\DesktopOrganizer\`:

| Folder | What is there |
|---|---|
| `State\` | pages/panels/layout as JSON (versioned schema, automatic backups on save) |
| `PanelItems\` | files/folders you dragged into a panel (moved here from their original location) |
| `Logs\` | diagnostics logs (see below) |

On uninstall your files are returned (see [Uninstall](#uninstall)); `State\` and `Logs\` stay.

A handful of small preferences (interface language, one-time hints, etc.) live in the standard per-user
`.NET` application settings store under `%LocalAppData%` (managed by the framework, not something you need
to manage by hand).

To get a file out of a panel: drag it onto the desktop or into any folder, or open Settings → Backup →
"Files without a panel" and press Return for files no panel uses any more. Pressing Delete on an item
returns the file and sends it to the Recycle Bin.

## Autostart and updates

- **Start with Windows:** a checkbox on the installer's last page, or the toggle in Settings → General —
  both control the same setting (`HKCU` Run entry).
- **Updates:** the "Check for updates" button in Settings → About (no public releases yet, so it normally
  reports none).

## Diagnostics and logs

DesktopOrganizer always writes a small, rotating diagnostics log (`app-<timestamp>-<pid>.jsonl`, JSON
Lines, up to 10 files × 10 MB) to the `Logs` folder inside its data folder (see
[Where your data lives](#where-your-data-lives)), in English, with file paths and
your Windows username stripped out before anything is written — useful if you need to report a problem. In
everyday use the log only records errors, warnings, and a few startup/shutdown lifecycle events; it does
not record what you click or drag.

For a more detailed trace while reproducing a problem, set the environment variable
`DESKTOPORGANIZER_DIAG=1` before starting the app, or
run it with the `--diag` command-line flag, to also capture verbose gesture/interaction events in the
same log folder.

If the app crashes, it also writes a `crash-<timestamp>-<pid>.log` file to the same folder (up to 10 kept)
describing the error (also scrubbed of paths/username) — attach it, together with the `.jsonl` log, to a
bug report.

## Command-line options

| Flag | What it does |
|---|---|
| `--diag` | Enables verbose diagnostics logging for this run (see above). |
| `--restore-state-backup` | Restores your layout from the most recent automatic backup instead of the live state file — use this if a previous run's state file was corrupted (the app already keeps rolling `.bak` copies made on every successful save). Prints what it did to the console and to the log, then exits; start the app normally afterwards. |
| `--restore-panel-items` | Returns every file/folder currently moved into the app's managed `PanelItems` storage to its original location (or the desktop, if that location is gone) — run automatically by the uninstaller, not meant to be typed by hand. |

## Uninstall

Uninstalling through "Apps & features" removes the installed program files. Before
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

## Support and license

Copyright (c) 2024-2026 khagen. Support: https://messire.github.io/desktop-organizer-site/, e.smirnov@protonmail.com.
Released under the [MIT License](LICENSE).

---

# DesktopOrganizer (RU)

Органайзер рабочего стола для Windows: группируйте значки рабочего стола в
перемещаемые панели, храните несколько страниц таких раскладок, и наконец наведите порядок на
столе.

Windows 10/11 (x64), .NET 8, WPF — приложение самодостаточное, ничего дополнительно ставить не нужно. Без
аккаунта, без телеметрии, без рекламы.

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

Windows 10/11, x64. Установщик собирается из исходников на Windows (нужны .NET 8 SDK и
[Inno Setup 6](https://jrsoftware.org/isdl.php)):

```powershell
.\installer\build.ps1
```

Результат — `installer\Output\DesktopOrganizer-Setup-<версия>.exe`; подробности в [installer/README.md](installer/README.md).

1. Запусти. Права администратора не требуются — приложение работает только с твоим профилем (`HKCU` и
   рабочим столом), поэтому ставится по пользователю в `%LocalAppData%\Programs\DesktopOrganizer`.
2. При первом запуске создаётся одна пустая панель — чтобы было с чего начать, — и показывается
   уведомление о том, что приложение живёт в области уведомлений (значок может быть скрыт под стрелкой
   «^»). Перетащи в панель значки с рабочего стола (и обратно — в любой момент), правый клик по заголовку
   панели — опции панели, правый клик по значку в трее — Настройки и Выход.
3. Windows SmartScreen может показать предупреждение «Неизвестный издатель» при первом запуске — установщик
   не подписан; нажми «Дополнительно» → «Выполнить в любом случае».

## Возможности

- Несколько перемещаемых, изменяемых по размеру панелей, встроенных прямо в слой рабочего
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
- Запуск вместе с Windows (по желанию; см. [Автозапуск и обновления](#автозапуск-и-обновления)).
- Полный русский и английский интерфейс, переключается вживую в Настройках без перезапуска.
- Единый светлый/тёмный интерфейс, который следует системному режиму приложений Windows вживую (без
  перезапуска) — окно настроек, окно «Настроить панель…», все меню и диалоги, трей; поддерживается и режим
  высокой контрастности. Цвет самих панелей при этом не меняется — это по-прежнему твой выбор.
- Установка/удаление поверх запущенного приложения сначала просит его
  штатно закрыться (сохранение состояния, восстановление значков рабочего стола), а не просто
  блокируется.

## Где хранятся данные

Всё, что хранит DesktopOrganizer, находится в пределах твоего профиля — ничего не пишется в систему
целиком и ничего не покидает компьютер. Папка данных — `%LOCALAPPDATA%\DesktopOrganizer\`:

| Папка | Что там |
|---|---|
| `State\` | страницы/панели/раскладка в JSON (версионированная схема, автобэкапы при сохранении) |
| `PanelItems\` | файлы/папки, перетащенные в панель (перемещены сюда с исходного места) |
| `Logs\` | файлы диагностического лога (см. ниже) |

При удалении файлы возвращаются (см. [Удаление](#удаление)); `State\` и `Logs\` остаются.

Небольшой набор настроек (язык интерфейса, одноразовые подсказки и т.п.) хранится в стандартном
пользовательском хранилище настроек `.NET` под `%LocalAppData%` (управляется фреймворком, руками лезть туда
не нужно).

Чтобы достать файл из панели: перетащи его на рабочий стол или в любую папку, либо открой Настройки →
Резервные копии → «Файлы без панели» и нажми «Вернуть» для файлов, которые больше не используются ни одной
панелью. Клавиша Delete на элементе возвращает файл и отправляет его в Корзину.

## Автозапуск и обновления

- **Запуск вместе с Windows:** галочка на последней странице установщика или переключатель в Настройки →
  Общие — обе управляют одной и той же настройкой (запись `Run` в `HKCU`).
- **Обновления:** кнопка проверки обновлений в Настройки → О программе (публичных релизов пока нет, поэтому
  обычно сообщает, что их нет).

## Диагностика и логи

DesktopOrganizer всегда пишет небольшой ротируемый диагностический лог (`app-<таймстамп>-<pid>.jsonl`,
JSON Lines, до 10 файлов по 10 МБ) в папку `Logs` внутри своей папки данных (см.
[Где хранятся данные](#где-хранятся-данные)), на английском языке, с
вырезанными путями к файлам и именем пользователя Windows — пригодится, если нужно сообщить о проблеме. В
обычном режиме в лог попадают только ошибки, предупреждения и несколько событий жизненного цикла
запуска/остановки; клики и перетаскивания не записываются.

Для более подробной трассировки при воспроизведении проблемы задай переменную окружения
`DESKTOPORGANIZER_DIAG=1` перед запуском или запусти приложение с флагом командной строки `--diag`, чтобы дополнительно писать в тот же
лог подробные события жестов/взаимодействия.

Если приложение падает, туда же пишется файл `crash-<таймстамп>-<pid>.log` (хранится до 10 штук) с описанием
ошибки (тоже без путей/имени пользователя) — приложи его вместе с `.jsonl`-логом к сообщению о проблеме.

## Опции командной строки

| Флаг | Что делает |
|---|---|
| `--diag` | Включает подробное диагностическое логирование для этого запуска (см. выше). |
| `--restore-state-backup` | Восстанавливает раскладку из последнего автоматического бэкапа вместо текущего файла состояния — используй, если файл состояния из прошлого запуска повреждён (приложение уже хранит цепочку `.bak`-копий, создаваемых при каждом успешном сохранении). Печатает результат в консоль и в лог, затем завершается; после этого запусти приложение как обычно. |
| `--restore-panel-items` | Возвращает все файлы/папки, перемещённые в управляемое хранилище `PanelItems`, обратно на исходное место (или на рабочий стол, если исходное место исчезло) — запускается автоматически деинсталлятором, вручную вводить не нужно. |

## Удаление

Удаление через «Программы и компоненты» убирает файлы самой программы. Перед
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

## Поддержка и лицензия

Copyright (c) 2024-2026 khagen. Поддержка: https://messire.github.io/desktop-organizer-site/, e.smirnov@protonmail.com.
Распространяется по [лицензии MIT](LICENSE).
