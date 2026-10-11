# Notes for certification (Partner Center → Submission options)

Лимит поля — 2000 знаков. Ниже английский текст для вставки (≈1850 знаков) и пояснение, зачем каждый абзац.

```
DesktopOrganizer is a tray-resident desktop utility (a "fences"-style organizer). It has NO main window: after
launch you will see (1) a tray icon and (2) one empty translucent panel titled "New Panel" placed on the
desktop, behind all other windows. This is the expected first-run state, not a crash.

How to test:
1. Create a few files or shortcuts on the desktop (a clean VM may have none).
2. Drag a desktop icon into the panel. The file is MOVED into
   %USERPROFILE%\DesktopOrganizer\PanelItems and shown inside the panel; a one-time hint explains this.
   Drag it back out onto the desktop to return it.
3. Right-click the panel title bar for panel options (rename, customize, roll up, layout).
4. Right-click the tray icon for Settings, Edit layout, Show/hide desktop icons, pages and Exit.
5. Right-drag on an empty desktop area to draw a new panel.
6. Double-click an empty desktop area to hide the standard desktop icons; double-click again (or use the
   tray menu) to show them. Icons are also restored automatically when the app exits.
7. "Start with Windows" is an opt-in Windows startup task (disabled by default); it can be enabled in
   Windows Settings > Apps > Startup after the app has run once.

runFullTrust justification: the app is a classic WPF/.NET desktop application. It needs full trust to
(a) parent its panel windows into the desktop layer (Progman/WorkerW) so they stay behind other windows,
(b) install a low-level mouse hook used ONLY to detect desktop-background gestures (double-click, right-drag,
clicks on panels); no keystrokes are captured and nothing is logged, (c) call shell APIs for icons and the
native file context menu. No network access, no telemetry, no services, no drivers, no elevation.

User data lives in %USERPROFILE%\DesktopOrganizer (not in the package's AppData) so that files moved into
panels survive uninstall and are visible to Explorer.
```

Зачем:

- Абзац 1 — главный риск отказа «app appears incomplete / nothing happens»: тестер должен знать, что окна нет.
- Шаги 1–7 — воспроизводимый сценарий ключевых функций на чистой VM; шаг 1 нужен, потому что на VM тестера может
  не быть ни одной иконки.
- Шаг 6 — явно описывает жест, меняющий «Windows experience» (policy 10.2.8), и как его откатить.
- Абзац runFullTrust — обязательное обоснование restricted capability; перечисляет именно те механизмы,
  которые сканер/тестер увидит в бинарнике (SetWindowsHookEx, SetParent в WorkerW, shell COM).
- Последний абзац объясняет, почему данные не в LocalCache пакета (A1 в плане).
