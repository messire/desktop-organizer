# DesktopOrganizer

A free, open-source Windows desktop organizer in the spirit of [Stardock Fences](https://www.stardock.com/products/fences) — group your desktop icons into movable, resizable panels ("fences"), keep multiple pages of them, and get your desktop back.

Windows 10/11, .NET 8, WPF. No account, no telemetry, no ads.

## Contents

- [What it is](#what-it-is)
- [Install](#install)
- [Features](#features)
- [Where your data lives](#where-your-data-lives)
- [Diagnostics and logs](#diagnostics-and-logs)
- [Command-line options](#command-line-options)
- [Uninstall](#uninstall)
- [Known limitations](#known-limitations)
- [License](#license)
- [Русская версия](#desktoporganizer-ru)

## What it is

DesktopOrganizer runs quietly in the tray and lets you drag desktop icons into panels that live directly
on the desktop layer — panels stay below your other windows, don't show up in Alt+Tab or the taskbar, and
move/resize like any other window. Files dropped into a panel are moved into the app's own per-user
storage so the desktop shortcut you had is gone, not duplicated; removing an item from a panel puts the
original file back where it came from.

## Install

1. Download the latest `DesktopOrganizer-Setup-<version>.exe` from the
   [Releases](https://github.com/messire/DesktopOrganizer) page.
2. Run it. No administrator rights are requested — the app only ever touches your own user profile
   (`HKCU` and your desktop), so it installs per-user under `%LocalAppData%\Programs\DesktopOrganizer`.
3. On first run it creates one empty panel so you have something to start with. Drag desktop icons into
   it (and out again any time), right-click a panel's title bar for options, and right-click the tray
   icon for global settings.
4. Windows SmartScreen may warn "Unknown publisher" the first time you run an unsigned build — this is
   expected for an installer without a purchased code-signing certificate; click "More info" → "Run
   anyway" if you trust the source you downloaded it from.

## Features

- Multiple movable, resizable panels ("fences") embedded directly into the desktop — hidden from
  Alt+Tab/taskbar, always below your other windows.
- Multiple pages of panels, switchable from the tray/settings, each with its own panel set.
- Grid view and list view per panel, with icon-size, sort mode (manual drag-to-reorder or by name),
  content filter, and roll-up (collapse to just the title bar).
- Move semantics on drag & drop: dropping a file/folder into a panel moves it into the app's managed
  storage (no duplicate desktop shortcut); removing it from the panel restores it to where it came from.
- Folder Portals: point a panel at a real folder and it mirrors that folder's contents live.
- Multi-monitor aware placement, magnetic grid snapping while dragging/resizing, and layout snapshots
  (save/restore a full arrangement of pages and panels by name).
- A dedicated "system icons" sidebar panel for the classic desktop icons (This PC, Recycle Bin, etc.),
  independent of your regular panels.
- Show/hide the underlying desktop icons with one toggle (tray or settings), independent of panel
  contents.
- Start with Windows (optional, toggle in the installer's finish page or in Settings — both control the
  exact same setting).
- Full English and Russian UI, switchable live from Settings without restarting.
- Installing or uninstalling over a running instance asks it to close gracefully first (state save,
  desktop icons restored) instead of just blocking.

## Where your data lives

Everything DesktopOrganizer stores is under your own user profile — nothing is written system-wide and
nothing leaves your machine:

```
%LOCALAPPDATA%\DesktopOrganizer\
  State\        — your pages/panels/layout as JSON (versioned schema, automatic backups on save)
  PanelItems\   — files/folders you dragged into a panel (moved here from their original location)
  Logs\         — diagnostics log files (see below)
```

A handful of small preferences (interface language, one-time hints, etc.) live in the standard per-user
`.NET` application settings store under `%LocalAppData%` (managed by the framework, not something you
need to manage by hand).

## Diagnostics and logs

DesktopOrganizer always writes a small, rotating diagnostics log (`app-<timestamp>-<pid>.jsonl`, JSON
Lines, up to 10 files × 10 MB) to `%LOCALAPPDATA%\DesktopOrganizer\Logs`, in English, with file paths and
your Windows username stripped out before anything is written — useful if you need to report a problem. In
everyday use the log only records errors, warnings, and a few startup/shutdown lifecycle events; it does
not record what you click or drag.

For a more detailed trace while reproducing a problem, run the app with the `--diag` command-line flag
(or set the environment variable `DESKTOPORGANIZER_DIAG=1` before starting it) to also capture verbose
gesture/interaction events in the same log folder.

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

Uninstalling through "Apps & features" removes the installed program files. Before that, it also
automatically returns every file/folder you had dragged into a panel (moved into the app's managed
`PanelItems` storage under `%LocalAppData%\DesktopOrganizer`) back to its original location — or to your
desktop, if that original location is gone, on a different drive, or already occupied by something else
— so nothing is stranded in a hidden folder. Your layout/settings under `State\` and the diagnostics log
files under `Logs\` are **left in place** — reinstalling the same version later picks up right where you
left off. Delete `%LocalAppData%\DesktopOrganizer` yourself if you want a truly clean removal.

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
  `%LocalAppData%\DesktopOrganizer\PanelItems\<guid>\` (its original per-item managed-storage folder), not
  back onto any panel — the panel it belonged to is gone by the time you restore it.
- Very long file paths (beyond `MAX_PATH`) are not supported by the underlying Recycle Bin/shell
  operations the app relies on for some file actions.

## License

[MIT](LICENSE) — Copyright (c) 2024-2026 khagen. Support/source: <https://github.com/messire>.

---

# DesktopOrganizer (RU)

Бесплатный опенсорсный органайзер рабочего стола для Windows в духе
[Stardock Fences](https://www.stardock.com/products/fences) — группируйте значки рабочего стола в
перемещаемые панели ("fences"), храните несколько страниц таких раскладок, и наконец наведите порядок на
столе.

Windows 10/11, .NET 8, WPF. Без аккаунта, без телеметрии, без рекламы.

## Что это

DesktopOrganizer тихо работает из трея и позволяет перетаскивать значки рабочего стола в панели, которые
живут прямо в слое рабочего стола: панели остаются под другими окнами, не показываются в Alt+Tab и на
панели задач, при этом двигаются и ресайзятся как обычное окно. Файлы, перетащенные в панель,
перемещаются в собственное хранилище приложения (в пределах вашего профиля) — исходный ярлык на столе не
дублируется, а исчезает; при удалении элемента из панели файл возвращается на исходное место.

## Установка

1. Скачай последний `DesktopOrganizer-Setup-<версия>.exe` со страницы
   [Releases](https://github.com/messire/DesktopOrganizer).
2. Запусти. Права администратора не требуются — приложение работает только с твоим профилем (`HKCU` и
   рабочим столом), поэтому ставится по пользователю в `%LocalAppData%\Programs\DesktopOrganizer`.
3. При первом запуске создаётся одна пустая панель — чтобы было с чего начать. Перетащи в неё значки
   с рабочего стола (и обратно — в любой момент), правый клик по заголовку панели — опции панели, правый
   клик по значку в трее — общие настройки.
4. Windows SmartScreen может показать предупреждение «Неизвестный издатель» при первом запуске
   неподписанной сборки — это ожидаемо для инсталлятора без купленного сертификата подписи кода; нажми
   «Дополнительно» → «Выполнить в любом случае», если доверяешь источнику, откуда скачал.

## Возможности

- Несколько перемещаемых, изменяемых по размеру панелей ("fences"), встроенных прямо в слой рабочего
  стола — скрыты из Alt+Tab/панели задач, всегда под другими окнами.
- Несколько страниц панелей, переключаемых из трея/настроек, каждая — со своим набором панелей.
- Вид «сетка» и вид «список» для каждой панели, размер значков, режим сортировки (ручной
  drag-to-reorder или по имени), фильтр содержимого, сворачивание в заголовок (roll-up).
- Move-семантика drag&drop: перетаскивание файла/папки в панель перемещает его в управляемое хранилище
  приложения (без дублирующего ярлыка на столе); удаление из панели возвращает файл туда, откуда он был
  взят.
- Folder Portals: панель может «смотреть» на реальную папку и живо отражать её содержимое.
- Учёт нескольких мониторов при размещении, магнитное прилипание к сетке при перетаскивании/ресайзе, и
  снапшоты раскладки (сохранение/восстановление всей расстановки страниц и панелей по имени).
- Отдельная боковая панель «системные значки» для классических значков рабочего стола (Этот компьютер,
  Корзина и т.д.), независимая от обычных панелей.
- Скрытие/показ настоящих значков рабочего стола одним переключателем (трей или настройки), независимо
  от содержимого панелей.
- Запуск вместе с Windows (по желанию, галочка на финальной странице инсталлятора или в Настройках —
  обе стороны управляют ровно одной и той же настройкой).
- Полный русский и английский интерфейс, переключается вживую в Настройках без перезапуска.
- Установка/удаление поверх запущенного приложения сначала просит его штатно закрыться (сохранение
  состояния, восстановление значков рабочего стола), а не просто блокируется.

## Где хранятся данные

Всё, что хранит DesktopOrganizer, находится в пределах твоего профиля — ничего не пишется в систему
целиком и ничего не покидает компьютер:

```
%LOCALAPPDATA%\DesktopOrganizer\
  State\        — твои страницы/панели/раскладка в JSON (версионированная схема, автобэкапы при сохранении)
  PanelItems\   — файлы/папки, перетащенные в панель (перемещены сюда с исходного места)
  Logs\         — файлы диагностического лога (см. ниже)
```

Небольшой набор настроек (язык интерфейса, одноразовые подсказки и т.п.) хранится в стандартном
пользовательском хранилище настроек `.NET` под `%LocalAppData%` (управляется фреймворком, руками лезть
туда не нужно).

## Диагностика и логи

DesktopOrganizer всегда пишет небольшой ротируемый диагностический лог (`app-<таймстамп>-<pid>.jsonl`,
JSON Lines, до 10 файлов по 10 МБ) в `%LOCALAPPDATA%\DesktopOrganizer\Logs`, на английском языке, с
вырезанными путями к файлам и именем пользователя Windows — пригодится, если нужно сообщить о проблеме. В
обычном режиме в лог попадают только ошибки, предупреждения и несколько событий жизненного цикла
запуска/остановки; клики и перетаскивания не записываются.

Для более подробной трассировки при воспроизведении проблемы запусти приложение с флагом командной строки
`--diag` (или задай переменную окружения `DESKTOPORGANIZER_DIAG=1` перед запуском), чтобы дополнительно
писать в тот же лог подробные события жестов/взаимодействия.

Если приложение падает, туда же пишется файл `crash-<таймстамп>-<pid>.log` (хранится до 10 штук) с описанием
ошибки (тоже без путей/имени пользователя) — приложи его вместе с `.jsonl`-логом к сообщению о проблеме.

## Опции командной строки

| Флаг | Что делает |
|---|---|
| `--diag` | Включает подробное диагностическое логирование для этого запуска (см. выше). |
| `--restore-state-backup` | Восстанавливает раскладку из последнего автоматического бэкапа вместо текущего файла состояния — используй, если файл состояния из прошлого запуска повреждён (приложение уже хранит цепочку `.bak`-копий, создаваемых при каждом успешном сохранении). Печатает результат в консоль и в лог, затем завершается; после этого запусти приложение как обычно. |
| `--restore-panel-items` | Возвращает все файлы/папки, перемещённые в управляемое хранилище `PanelItems`, обратно на исходное место (или на рабочий стол, если исходное место исчезло) — запускается автоматически деинсталлятором, вручную вводить не нужно. |

## Удаление

Удаление через «Программы и компоненты» убирает файлы самой программы. Перед этим оно также
автоматически возвращает все файлы/папки, перетащенные в панели (перемещённые в управляемое хранилище
`PanelItems` под `%LocalAppData%\DesktopOrganizer`), обратно на исходное место — или на рабочий стол,
если исходное место исчезло, находится на другом диске, или уже занято чем-то другим — так что ничего не
остаётся забытым в скрытой папке. Раскладка/настройки под `State\` и файлы диагностического лога под
`Logs\` **остаются на месте** — переустановка той же версии позже продолжит с того же места. Удали
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
  `%LocalAppData%\DesktopOrganizer\PanelItems\<guid>\` (исходной папке управляемого хранилища для этого
  элемента), а не обратно на панель — панель, которой он принадлежал, к моменту восстановления уже удалена.
- Очень длинные пути (больше `MAX_PATH`) не поддерживаются операциями Корзины/shell, на которые опирается
  часть файловых действий приложения.

## Лицензия

[MIT](LICENSE) — Copyright (c) 2024-2026 khagen. Поддержка/исходники: <https://github.com/messire>.
