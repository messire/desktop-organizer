# Changelog

All notable changes to this project are documented here. Format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); this project does not yet follow strict SemVer
pre-1.0 (a version bump is not guaranteed to mean a breaking change).

Built from the repository's own history: each `vX.Y.Z` entry summarizes the corresponding squash commit's
body (`git log --format='%h %ad %s%n%b' --date=short`) on `master`. Versions before `0.1.0` are not
tracked — `0.1.0` is the first tagged history point.

## [Unreleased]

Release hardening 1.0 (ADR-033–ADR-038): crash safety, automatic Explorer-restart recovery, panel/page
deletion through the Recycle Bin, drag-out of panel items, PerMonitorV2, and a release-engineering pass.
Version intentionally stays 0.6.1 for this round.

- Diagnostics (B): an always-on (Release-default) structured app log (`IAppLog`, `app-*.jsonl`) alongside
  the existing verbose gesture-diagnostics log, with a path/username-scrubbing filter shared by both.
- Crash safety (A): unhandled exceptions on the UI thread, a background thread, or from the tray icon now
  restore desktop icons, write a scrubbed `crash-*.log`, save application state (UI-thread crashes only),
  and show a short dialog before exiting (exit code 5) instead of the app just disappearing; a single
  idempotent shutdown sequence now backs `OnExit` and both session-ending signals.
- Explorer restart recovery (C): panels, the sidebar, desktop-icon visibility, and the virtual-desktop
  cache now recover automatically within a few seconds of `explorer.exe` restarting or crashing, on both
  the classic (embedded) and the Windows 11 24H2+ (top-level) desktop layouts.
- Panel/page deletion → Recycle Bin (D, amends ADR-010): deleting a panel or page with managed files now
  always moves them to the Recycle Bin instead of deleting them outright; a failure (locked file, user
  declines the shell's oversized-item warning) keeps the panel/page in place rather than losing data.
- Drag-out of panel items (E, amends ADR-009/ADR-014): an item can now be dragged out of a panel to the
  desktop, Explorer, or a different panel, matching Stardock Fences' own drag-out behavior; reordering
  within a panel is unchanged.
- PerMonitorV2 (F, amends ADR-004/ADR-022/ADR-028): the app now declares itself PerMonitorV2-aware; panels
  render per-monitor DPI on Windows 11 24H2+ (where they are top-level), and keep the previous single-scale
  behavior on Windows 10/Windows 11 ≤23H2 (where they are still embedded into the desktop layer) — a
  documented, unchanged limitation on that OS range. No persisted-geometry migration; single-scale setups
  are numerically unchanged from 0.6.1.
- Release engineering (G1): installer publisher/support links, optional code-signing hook for
  `build.ps1`/the `.iss`, root `README.md`/`LICENSE`/`CHANGELOG.md`, CI workflow.
- Manual update check (G2): a "Check for updates" button in Settings → About queries GitHub's releases API
  on demand (no automatic/background check, nothing persisted), plus links to the project page, issue
  tracker, and logs folder.

## [0.6.1] — 2026-09-27

UI localization (ADR-032): full English/Russian interface, switchable live from Settings without
restarting. WPF markup extensions (`{loc:Loc}`, `{loc:LocBinding}`, `{loc:CultureBinding}`) and
`CultureSync` drive every window, converter, and tray label; a `Language` card was added to Settings.
Localization consistency gates (RESX parity between languages, dead-key detection, no hardcoded XAML
literals) run as part of the test suite. Installer's Russian "close app" message was synced with the
localized tray label.

## [0.6.0] — 2026-09-26

Settings window redesign (ADR-031): replaced the old expander/tab-strip layout with a sidebar-navigation
shell (General, Layout & snapping, Panels, New panel defaults, Pages, System icons, Backup, About), plus a
new modeless "Customize panel…" dialog for per-panel appearance/content/layout. Numerous fix rounds closed
review findings: live-reactive dialogs, panel subtitle/item-count accuracy, order-preserving panel lists,
accessibility names across all interactive controls, and dead-code removal.

## [0.5.1] — 2026-09-25

Added a custom overlay-style scrollbar (minimal track/thumb, no arrow buttons) for panel and sidebar
lists, matching the panel chrome's visual language. Documentation-only follow-up: corrected several
inaccurate code comments found during review, and enabled full XML documentation generation for the
codebase (closes all outstanding StyleCop/documentation warnings).

## [0.5.0] — 2026-09-25

Installer: graceful shutdown of a running instance via a named event (ADR-023) instead of Inno Setup's
blocking `AppMutex=` — install/uninstall now ask the running app to exit cleanly (state save, desktop
icons restored) and retry if it doesn't. Panel chrome visual overhaul (ADR-030): layered rounded-rect
inset with neutral/accent stroke rings, an accent-colored glow effect with a shader-free fallback for
software/RDP rendering, a curved accent marker tracing the panel's corner radius, and a subtle
non-semantic surface highlight — all collapsing correctly in High Contrast mode.

## [0.4.0] — 2026-09-25

Workspace grid (ADR-028/029): per-monitor workspace grid with `GridAligned`/`LegacyFreeform` panel
geometry modes, fixed-gap icon layout, fit-to-content sizing, size presets, and both native and WPF-driven
resize across grid spans. Icon pipeline fixes (transparent placeholder, stale managed paths, added
diagnostics). Visual polish pass ("Ambient Accent"): corner radius + clip, hover contrast, accessibility,
header-height accent strip. Persistence schema bumped to v5 with a pre-migration backup safeguard.

## [0.3.0] — 2026-09-05

Fixed a spurious vertical scrollbar on single-row grid-mode panels (edge-padding math). Two rounds of
native-layer hardening: bounded the low-level mouse hook's `SendMessageW` call with a timeout (a hung
`explorer.exe` could otherwise stall all mouse input system-wide) and consolidated every P/Invoke
declaration behind the single `INativeApi` interface. Refactoring pass: decomposed
`MainWindowViewModel`/`ChildWindow.xaml.cs` into focused sub-ViewModels and behaviors (drag-move, resize,
roll-up, inline rename, drag-reorder) with no intended behavior change. Wired up `StyleCop.Analyzers`
project-wide, surfacing ~450 pre-existing style warnings for later cleanup. `docs/`, `adr/`, and
`.claude/` were brought under version control for the first time.

## [0.2.1] — 2026-08-16

Fixed the sidebar's "show on all virtual desktops" checkbox, which was a no-op because the sidebar was a
`WorkerW` child (a single layer shared by every virtual desktop); it is now a plain top-level window that
Windows' virtual-desktop manager can actually track. Icon-grid-aware minimum panel width replacing a flat
120px floor; adaptive icon-grid layout with per-row gap justification (`AdaptiveIconGridPanel`). Added
cross-panel selection coordination, drag anti-overlap between panels, and a "show correct folder icon"
fix. UX pass: title-bar sort toggle, drag-to-reorder, icon selection highlight, inline rename, hidden
`.lnk` extensions, simplified tray menu, initial English localization pass.

## [0.2.0] — 2026-06-28

Phase 7 performance/resilience hardening: closed all 11 findings from the internal performance audit —
bounded/frozen brush caches, an exception-safe desktop-gesture hook with its state machine extracted for
testability, debounced window-state saves with a synchronous flush on structural changes, an LRU-bounded
icon cache, and a write-through manifest cache in the panel item storage service. Refined manual panel
drag with live magnetic grid snapping (replacing the built-in `DragMove`). Added a one-time move hint,
safe managed-copy cleanup when a panel is deleted, monitor re-clamp on work-area shrink, and native shell
context-menu integration with drag-and-drop for the system icons panel.

## [0.1.0] — 2026-06-13

First tracked release. Multi-page layouts, monitor-aware placement, and layout persistence; desktop
gesture handling (double-click/lasso) and desktop icon show/hide; per-panel appearance settings; roll-up
and inline title rename for panels; file launching and a file context menu; grid snapping; desktop-layer
embedding via the `WorkerW` technique; a dynamic settings window with add/remove panel; centralized NuGet
package version management.

[Unreleased]: https://github.com/messire/DesktopOrganizer/compare/v0.6.1...HEAD
[0.6.1]: https://github.com/messire/DesktopOrganizer/releases/tag/v0.6.1
[0.6.0]: https://github.com/messire/DesktopOrganizer/releases/tag/v0.6.0
[0.5.1]: https://github.com/messire/DesktopOrganizer/releases/tag/v0.5.1
[0.5.0]: https://github.com/messire/DesktopOrganizer/releases/tag/v0.5.0
[0.4.0]: https://github.com/messire/DesktopOrganizer/releases/tag/v0.4.0
[0.3.0]: https://github.com/messire/DesktopOrganizer/releases/tag/v0.3.0
[0.2.1]: https://github.com/messire/DesktopOrganizer/releases/tag/v0.2.1
[0.2.0]: https://github.com/messire/DesktopOrganizer/releases/tag/v0.2.0
[0.1.0]: https://github.com/messire/DesktopOrganizer/releases/tag/v0.1.0
