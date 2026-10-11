# Store listing — черновик полей (Partner Center → Store listings)

Заполняется владельцем; `<…>` — плейсхолдеры. Имя приложения резервируется отдельно (AP1): **DesktopOrganizer**.

## Общие поля

| Поле | Значение |
|---|---|
| Category | Utilities & tools |
| Subcategory | Personalization (если доступно) |
| Pricing | Free |
| Markets | все |
| Age rating (IARC) | анкета: утилита, нет контента/покупок/общения → ожидаемо «3+ / Everyone» |
| Privacy policy URL | `<PUBLIC-URL>/privacy` (текст — `docs/store/privacy-policy.md`) |
| Website | `<PUBLIC-URL>` |
| Support contact | `<SUPPORT-EMAIL>` |
| License terms | оставить Standard Application License Terms (код закрытый, MIT в листинг не выносить) |
| Package | `.msix`, x64, версия `1.0.0.0`, capability `runFullTrust`, расширение `windows.startupTask` (Enabled=false) |
| Languages | en-US (обязательно), ru-RU |

## Описание (en-US), ≤10 000 знаков

```
DesktopOrganizer groups your desktop icons into movable, resizable panels ("fences") that live directly on
the desktop — behind your other windows, out of Alt+Tab and the taskbar. Drag icons in, drag them out, roll a
panel up to its title bar, keep several pages of panels and switch between them from the tray.

Features
• Panels embedded in the desktop layer: always behind your windows, never in the taskbar.
• Grid or list view per panel, icon size, sort order (manual or by name), content filter, roll-up.
• Multiple pages of panels with their own names; switch from the tray menu.
• Folder Portals: point a panel at a folder and it mirrors that folder live.
• Layout snapshots: save and restore a complete arrangement by name.
• A separate panel for the system icons (This PC, Recycle Bin, …).
• Hide or show the standard desktop icons with a double-click on the desktop or from the tray.
• Multi-monitor and high-DPI aware; magnetic grid snapping while dragging and resizing.
• Light/dark theme follows Windows, including High Contrast. English and Russian interface.
• No account, no telemetry, no ads, no network access.

Good to know
• The app runs in the tray and has no main window. Right-click the tray icon for Settings and Exit.
• Files you drop into a panel are MOVED off the desktop into C:\Users\<you>\DesktopOrganizer\PanelItems and
  come back when you drag them out. That folder is yours and is kept if you uninstall or reset the app, so
  take your files back first if you want them on the desktop. Settings > Backup > "Files without a panel"
  lists files no panel uses any more and returns them.
• Double-clicking an empty area of the desktop hides the standard desktop icons; double-click again to bring
  them back. Icons are restored automatically when the app exits.
• To start DesktopOrganizer with Windows, enable it in Settings > Apps > Startup after the first launch.
• Requires Windows 10 version 2004 (build 19041) or later, x64.
```

## Описание (ru-RU)

```
DesktopOrganizer собирает значки рабочего стола в перемещаемые панели («заборы»), которые живут прямо на
рабочем столе — позади остальных окон, вне Alt+Tab и панели задач. Перетаскивайте значки в панель и обратно,
сворачивайте панель до заголовка, держите несколько страниц панелей и переключайте их из трея.

Возможности
• Панели встроены в слой рабочего стола: всегда позади окон, никогда не в панели задач.
• Сетка или список, размер значков, сортировка (ручная или по имени), фильтр содержимого, сворачивание.
• Несколько страниц панелей со своими именами; переключение из меню трея.
• Папки-порталы: панель показывает содержимое выбранной папки в реальном времени.
• Снимки раскладки: сохранение и восстановление всей компоновки по имени.
• Отдельная панель для системных значков (Этот компьютер, Корзина, …).
• Скрытие и показ стандартных значков рабочего стола двойным кликом по столу или из трея.
• Поддержка нескольких мониторов и высокого DPI; магнитная привязка к сетке при перетаскивании.
• Светлая/тёмная тема следует за Windows, включая высокую контрастность. Интерфейс на русском и английском.
• Без учётной записи, телеметрии, рекламы и доступа в сеть.

Полезно знать
• Приложение работает в трее и не имеет главного окна. Настройки и Выход — в меню значка трея.
• Файлы, перетащенные в панель, ПЕРЕМЕЩАЮТСЯ с рабочего стола в C:\Users\<вы>\DesktopOrganizer\PanelItems и
  возвращаются, когда вы перетаскиваете их обратно. Папка остаётся при удалении и сбросе приложения, поэтому
  заберите файлы заранее, если хотите видеть их на рабочем столе. Параметры > Резервные копии > «Файлы без
  панели» показывает файлы, которые больше не использует ни одна панель, и возвращает их.
• Двойной клик по пустому месту рабочего стола скрывает стандартные значки; повторный — возвращает.
• Автозапуск включается в Параметры > Приложения > Автозагрузка после первого запуска.
• Требуется Windows 10 версии 2004 (сборка 19041) и новее, x64.
```

## Скриншоты (≥1, PNG, ≥1366×768, ≤10 шт.)

1. Рабочий стол с 3–4 панелями разного вида (сетка/список/свёрнутая) на обоях Windows 11.
2. Окно Settings → Panels.
3. «Customize panel…» с пресетами цвета.
4. Меню трея со страницами.
5. (опционально) Folder Portal и панель системных значков.

Снимать на чистой учётной записи, без личных файлов в именах. Store logo 1:1 300×300 — из `app.svg`.

## Что ещё заполнить

- Notes for certification — `docs/store/notes-for-certification.md`.
- Restricted capability justification (`runFullTrust`) — тот же абзац из notes.
- Search terms (до 7): fences, desktop, organizer, icons, panels, declutter, workspace.
