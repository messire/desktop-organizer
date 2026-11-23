# Changelog

Versions follow SemVer: patch for fixes only, minor for backward-compatible features, major for breaking changes such as a `schemaVersion` bump (rule: [CLAUDE.md → Версии](CLAUDE.md)).

## [Unreleased]
- Released under the MIT License; source published on GitHub.
- Autostart honours the Task Manager "StartupApproved" flag; Debug builds no longer register autostart.

## [1.0.0] — 2026-10-11
- Microsoft Store build (MSIX) with its own storage folder and autostart through Windows Settings.
- First-run tray notification; the move hint names the folder where files are kept.
- Public support page and contact.

## [0.9.0] — 2026-10-10
- Windows 11 24H2+: panels no longer pop up over application windows.
- Fixes for read-only sessions, layout restore, Recycle Bin drops and multi-monitor DPI.
- "Files without a panel" in Settings → Backup; OneDrive warning on drop.
- Panels always resize through the Windows sizing loop. State schema v7.

## [0.8.0] — 2026-09-30
- Unified design system that follows the Windows light/dark mode.
- Page display names, "Pages" in the tray menu, double-click on the tray cycles pages.
- "Copy to page" for panels; colour presets set text and background together.
- Icons load in the background; network shares no longer block the UI. State schema v6.

## [0.7.0] — 2026-09-27
- Crash safety: icons restored, state saved, scrubbed crash log.
- Automatic recovery after an Explorer restart.
- Panel and page deletion goes to the Recycle Bin; drag items out of a panel.
- PerMonitorV2 DPI, real high-resolution shell icons, native shell context menu.
- First run creates one panel; uninstall returns files to their original place.
- Manual update check, installer and CI.

## [0.6.1] — 2026-09-27
- Full English/Russian interface, switchable without restart.

## [0.6.0] — 2026-09-26
- Settings window with sidebar navigation; "Customize panel…" dialog.

## [0.5.1] — 2026-09-25
- Overlay scrollbar for panels and the sidebar.

## [0.5.0] — 2026-09-25
- Installer closes a running app gracefully.
- New panel chrome: rounded inset, accent glow, High Contrast support.

## [0.4.0] — 2026-09-25
- Per-monitor workspace grid, fit-to-content sizing and size presets. State schema v5 with backup.

## [0.3.0] — 2026-09-05
- Mouse hook bounded by a timeout; all P/Invoke behind one interface.
- ViewModels and window code split into focused parts; StyleCop enabled.

## [0.2.1] — 2026-08-16
- Sidebar on all virtual desktops; adaptive icon grid; anti-overlap on drag.
- Sorting, drag-to-reorder, selection and inline rename of icons.

## [0.2.0] — 2026-06-28
- Performance and resilience fixes from the internal audit.
- Magnetic grid snap while dragging; system icons panel with shell menu.

## [0.1.0] — 2026-06-13
- Panels on the desktop layer, multiple pages, layout persistence.
- Desktop gestures and icon show/hide, roll-up, rename, grid snapping.
