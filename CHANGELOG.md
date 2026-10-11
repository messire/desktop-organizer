# Changelog

All notable changes to this project are documented here. Format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); this project does not yet follow strict SemVer
pre-1.0 (a version bump is not guaranteed to mean a breaking change).

Built from the repository's own history: each `vX.Y.Z` entry summarizes the corresponding squash commit's
body (`git log --format='%h %ad %s%n%b' --date=short`) on `master`. Versions before `0.1.0` are not
tracked — `0.1.0` is the first tagged history point.

## [Unreleased]

Version 1.0.0: first Microsoft Store release. The Store (MSIX) build runs with package identity; the Inno Setup
installer build behaves as before except where noted. Settings and state storage did not change (ADR-024): the
schema, the settings and the file formats are the same in both builds.

- **Added: Microsoft Store build (MSIX).** A package-identity build is detected at startup; everything below that is
  described as "Store build" applies only to it.
- **Added (Store build): storage folder.** Panels, the files moved into them, state and logs live in
  `%USERPROFILE%\DesktopOrganizer` (Store packages do not virtualize that location, so uninstalling the app or resetting
  it in Windows Settings never deletes your moved files). The installer build keeps `%LOCALAPPDATA%\DesktopOrganizer`.
  On the first run the Store build writes a short `README.txt` (English and Russian) into that folder explaining what it
  holds and how to get files back.
- **Added (Store build): autostart through Windows Settings.** The Windows startup entry of a Store app can only be
  switched on by the user in Windows Settings, so General no longer shows the "Start with Windows" toggle there; it
  explains this and has an "Open Windows startup settings" button that opens Windows Settings → Apps → Startup. The app no
  longer touches the registry `Run` key in that build. The entry is off by default.
- **Added: first-run tray balloon.** On the very first run (both builds) a notification says that the app lives in the
  notification area (possibly under the "^" overflow arrow) and that the icon's right-click menu has Settings and Exit. It
  is shown only when no saved state existed yet, and nothing is stored to remember it.
- **Changed (Store build): no update check.** The Store delivers updates, so the "Updates" card in About is hidden and the
  app makes no update request there.
- **Changed: the move hint names the folder.** The one-time hint shown when a dropped file is moved into a panel now says
  where the file is kept, and stays up for 8 seconds instead of 4 because of its length; in the Store build it also says
  that the file stays there if the app is uninstalled (the installer build's uninstaller returns the files to the desktop,
  so it does not say that).
- **Changed: "Report a problem" and "Project page"** both open the author's public page (one shared address), instead of
  the source repository's issue tracker.
- **Changed: plainer help texts.** The diagnostics note in About no longer mentions a command-line switch and no longer
  says to attach the file to a report, and the description of "Reset one-time hints" no longer uses an internal term.
- **Fixed:** a failure to show the first-run balloon (for example an empty text) is logged and no longer propagates from
  the tray icon manager.

Known limitations of the Microsoft Store build:

- Settings kept in the app's `user.config` (the interface language, the one-time hints already shown, the layout lock)
  are reset to their defaults each time the Store updates the package. Panels, pages, files and everything else stored in
  the state document are not affected. As a consequence, the one-time hint about moving a file into a panel is shown again
  after a package update.
- If the package is uninstalled while the app is running, the desktop icons stay hidden if they were hidden at that
  moment; restart Explorer or sign out and in again to bring them back.
- The installer build and the Store build use separate storage folders and do not share panels; installing both side by
  side is not supported.
- Autostart can be enabled only in Windows Settings → Apps → Startup, and the entry appears there only after the app has
  been started once.

## [0.9.0] — 2026-10-10

ADR audit fixes, 2026-10-09 (a review of all 47 ADRs against the code; each item below is documented in the ADR it
names):

- **Fixed (Windows 11 24H2+):** panels no longer pop up on top of your application windows. A newly created panel, a
  panel re-created after Explorer restarts, and the panels of a page you switch to now stay behind application windows,
  in the desktop layer, without a flash; clicking a panel still neither activates it nor raises it above your windows
  (ADR-049, ADR-034, ADR-020). Windows 10 and the system icons sidebar behave as before.
- **Fixed:** when the app starts in a read-only session (the saved state was written by a newer version, or another
  instance holds the storage lock), it now tells you so — a one-time notice in Settings, a "(read-only)" suffix on the tray
  tooltip (truncated to the 127-character limit of a tray tooltip), and a hint on the panel when you drop a file. In that session a dropped file is no longer moved off your desktop
  into hidden storage (it stays where it is and is shown as a copy), because the panel could not be saved anyway; restoring
  a layout snapshot does not move files either, and its confirmation does not promise to. The notice names the actual
  reason (the saved state was written by a newer version, or another instance holds the storage lock), and when such a
  session starts with no panels on screen, Settings opens by itself once so the notice is actually seen. Nothing about how
  settings or state are stored changed (ADR-024).
- **Fixed:** restoring a saved layout no longer leaves files stranded. Panels that are not part of the snapshot are
  closed and their files go back to their original location (not the Recycle Bin); if a file cannot be returned, it is
  not lost — it stays visible in a panel and an error message with the count is shown, and a success message with the count
  is shown otherwise. A panel that fails to be re-created no longer interrupts the rest of the restore, and restoring the
  same snapshot again works too (ADR-004, ADR-048).
- **Fixed:** dropping files onto the Recycle Bin icon of the sidebar now asks before permanently deleting something that
  is too large for the Recycle Bin (as the panel deletion already did), no longer freezes Explorer / 7-Zip / Total
  Commander while the question is shown, and never lets the dragging program delete the originals before you answer. Panel
  items for files that have left the disk are removed afterwards (ADR-035).
- **Fixed:** uninstalling no longer deletes the managed storage folder when one of its sub-folders could not even be read;
  the unreadable location is counted as a file that was not restored, the folder is kept, and the uninstaller reports a
  partial failure (exit code 7) with a message pointing to the diagnostics log (ADR-041).
- **Fixed:** changing the Normal-mode layout gestures setting (which gestures are allowed outside Edit Layout mode) now takes effect on already open panels immediately, not only
  after the next Edit-layout toggle or a restart (ADR-026).
- **Fixed:** with "Snap panel position to grid" on, panel width is no longer pulled back onto the icon grid after changing
  the icon size or view mode, and the "Snap panel size to icon grid" switch is disabled in that state, with its description
  explaining why (ADR-011, ADR-015, ADR-018, ADR-029).
- **Fixed (multi-monitor, different DPI):** auto-fit to content, native resizing and roll-up/roll-down read the panel
  position through the same per-monitor conversion as the rest of the layout code (ADR-037).
- **Improved:** the always-on application log now also records icon-loading failures (a stuck icon extraction, a disabled
  loading lane, a quarantined network share, a failed existence probe) that used to appear only with `--diag`; repeated
  identical errors are capped (first five, then one "suppressed" notice) so the log stays readable, and Z-order problems
  are logged once per session (ADR-033, ADR-046, ADR-049).
- **Changed (internal, no user-visible effect):** the desktop-band Z-order mechanism has three new native helpers
  (`GetWindowProcessId`, `TryReadWindowPosZOrder`, `WriteWindowPosZOrder`) behind `INativeApi`.

Decisions of 2026-10-10 (each is documented in the ADR it names):

- **Changed (settings file format, schema version 7):** the internal state file (`application-state.json`) now carries a
  second, additive version counter, so a later build can add an optional field without making an earlier build of the
  same major version unable to open the file. A build that meets fields or values it does not know (for example a panel
  surface style added by a newer version) keeps them and writes them back unchanged instead of dropping them or treating
  the whole file as damaged. Nothing visible changes today — no new field is stored yet (ADR-024).
- **Added:** dragging a file from a OneDrive folder (including a Desktop that OneDrive backs up) into a panel now shows
  a one-time-per-session hint that the file was moved out of the synced folder into panel storage and is no longer synced
  — to return it, drag it from the panel back into a folder. Dropping still moves the file as before (ADR-009).
- **Added:** Settings → Backup has a "Files without a panel" card. "Find files without a panel" lists files that are still
  in panel storage but that no panel (on any page, or in any saved layout) uses any more — for example after deleting a
  panel in a read-only session. For each you can "Return" it to where it came from or "Open folder"; nothing is deleted or
  moved automatically, and the card is unavailable in a read-only session (ADR-009, ADR-048).
- **Changed:** panels now always resize through the Windows sizing loop. The "Native resize (experimental)" setting is
  removed, together with the old resize path it switched off and its `--native-sizing` / `--no-native-sizing` command-line
  switches and `DESKTOPORGANIZER_NATIVESIZING` environment variable; a settings file saved by an earlier version that still
  contains this entry is read without errors (the leftover entry is simply ignored, and everything else in the file is
  kept). Resizing a panel across a monitor with a different display scale no longer interrupts the drag; pressing Esc
  during a resize returns the panel to its previous size without saving and without switching an auto-fit panel to manual
  height; only a click on a resize edge leaves the active window alone — a click on the rest of the panel behaves as it did
  before; double-clicking a top or bottom edge no longer maximizes the panel vertically; and the accent resize grips of
  Edit Layout mode are drawn (ADR-022, ADR-021, ADR-026, ADR-037). Known limits: resizing is verified with a mouse only
  (touch and pen are not), and a panel is not highlighted while the pointer is over its resize edge.
- **Changed (documentation only):** the rules for what happens to a file dragged into a panel (when it is returned to its
  original place, sent to the Recycle Bin, or kept) are now consolidated in one document, ADR-048; no behavior change.
- **Changed (internal, release process):** every release candidate's squash commit `vX.Y.Z` now gets an annotated tag
  `vX.Y.Z`; CI also runs on `rc/**` branches (ADR-038). No effect on the app.
- **Upgrade notes:** after the first launch of this version the state file has schema version 7 (the automatic
  pre-upgrade snapshot is saved as `%LOCALAPPDATA%\DesktopOrganizer\State\application-state.premigration-v6.json`).
  **After the upgrade, every launch of 0.8.0 or earlier on this file opens an empty, read-only session without any
  notice** — 0.8.0 cannot yet recognize this state (your panels are not deleted and the file is not touched). It stays
  that way until you run this or a newer version again, or close the app and manually restore
  `application-state.premigration-v6.json` over `application-state.json` (it holds everything as it was right before the
  upgrade, not anything changed afterwards). The "once" here is the format change itself, not a single session: this is
  the last format change of this kind — moving back and forth between revisions of the same major version (7.x) no longer
  produces a read-only session.

## [0.8.0] — 2026-09-30

Backlog 1.x, unified design system (ADR-047), 2026-09-30:

- **Changed:** the app now follows Windows' own light/dark app mode for its own chrome — Settings, the
  "Customize panel…" window, every right-click menu (a panel's own header menu, the tray), confirmation and
  input dialogs, and window title bars all switch between light and dark together with Windows, live,
  without restarting. Individual panels' own colours (set per-panel or as defaults for new panels) are
  unchanged — this only affects the app's own interface chrome. High Contrast themes are also supported,
  built directly from Windows' own high-contrast colours.
- **Changed:** a single, consistent visual style now applies everywhere across the interface — Settings and
  its eight sections, the "Customize panel…" window, every context menu (including a panel's own
  right-click menu, previously always dark regardless of Windows' theme), tooltips, and the confirmation/
  input dialogs described below. Text size, spacing, colours, and hover/pressed/disabled/selected states are
  now the same system throughout, and text is never smaller than Windows' own system text size.
- **Changed:** confirmation dialogs (deleting a panel or page, "Align all panels to grid", "Apply to all
  panels…") and the "Save snapshot as…" text prompt now use a new, themed dialog window instead of a plain
  system message box; for a warning about something that can't be undone, pressing Enter now safely
  cancels (answers "No") instead of confirming — the visually highlighted button is still the one that
  confirms, so mouse users see no change. Successful "move/copy panel to page" no longer interrupts you
  with a pop-up window at all — it's now a small, non-blocking status message inside the Settings window
  that fades on its own.
- **Changed:** the "Panels" list in Settings replaces its row of five buttons (which used to wrap to two
  lines in Russian) with three: Add, Customize…, and a "More" button holding Move/Copy to page and Remove.
  The tray's own right-click menu now follows Windows' light/dark app mode too, drawn by the app itself
  instead of the previous fixed light look.
- **Fixed:** a submenu inside a panel's own right-click menu ("Move to page ▸", etc.) used to render with
  barely-readable light-on-light text; several other low-contrast spots (the selected item in the Settings
  sidebar, a toggle's "off" track, warning text on a couple of Settings pages) are also corrected.
- **Added:** a panel's own shortcut-icon right-click menu, and a window's system menu, can now also render
  in Windows' dark classic menu style when the app itself is in dark mode (Windows 10 1903+/Windows 11);
  this is the one native, Explorer-owned menu surface the app could reach — the "Open with"/"Send to"/
  third-party entries inside it are unaffected, and menus drawn by a shell extension itself may still be
  light, the same limitation Explorer's own equivalent menu has.
- Known, accepted gap for this release: a handful of longer help/description texts have not yet been
  shortened to the new, shorter length guideline, and a few settings cards have not yet been moved onto the
  new shared card style — tracked as follow-up, not a blocker.

Backlog 1.x, background icon loading (ADR-046), 2026-09-30:

- **Changed:** panel icons now load in the background instead of on the UI thread — panels and the tray
  stay responsive immediately, even at startup with a large Folder Portal or a panel full of shortcuts
  pointing at an unreachable network share. Each item shows a generic placeholder, then (usually within a
  frame or two) a type icon, then its real icon once fetched; nothing about how icons look or are cached
  changed.
- **Fixed:** a shortcut whose target lives on an unreachable network share, a disconnected mapped drive
  letter, or a slow third-party shell extension (archiver, cloud-sync client) no longer stalls the whole
  panel or the app's startup — only that network location is temporarily skipped, and it recovers
  automatically once reachable again.
- **Changed:** the background "does this file still exist" check (every ~3 seconds) now runs off the UI
  thread too, so it no longer causes a brief stutter on a panel with many items or a slow network location.
- **Fixed:** two startup/edit paths that could briefly touch a network share while resolving panel item
  paths (loading a panel, reconciling after a shell command) now check the app's own managed-file list
  first and only touch the network when genuinely necessary.

Backlog 1.x, UX polish wave 1 (ADR-043/044/045, ADR-042 amendments), 2026-09-29:

- **Added:** pages now have their own editable display name, separate from their internal id (Settings →
  Pages: rename via button, double-click, or F2). An unnamed default page still shows a localized name
  that follows the interface language; a renamed page keeps its own name regardless of language.
- **Added:** a "Pages" submenu at the top of the tray's right-click menu, listing every page by its
  display name with a checkmark on the current one, plus a "Next page" entry; it stays in sync as pages
  are added, removed, renamed, or the interface language changes.
- **Added:** "Copy to page" next to "Move to page" (in both a panel's own header menu and the Settings
  Panels list) — creates a fully independent duplicate of a panel, including its own copies of any files
  you had dragged into it, on any page (including its own page, offset so it doesn't land exactly on top
  of the original). Deleting one copy never affects the other.
- **Changed:** double-clicking the tray icon now cycles to the next page (wrapping back to the first)
  instead of opening/closing the Settings window; Settings still opens from the tray's "Settings" menu
  item.
- **Changed:** a colour preset now sets the panel's text colour together with its background colour —
  each of the 10 coloured presets carries its own readable text tint instead of the same white for all of
  them; clicking a preset again re-applies its own text colour even if it had drifted.
- **Changed:** the panel colour editor (Customize panel… and Settings → New panel defaults) is simplified
  to a colour-preset grid plus a light/dark switch as the main, one-click path; custom hex colours,
  surface style, opacity, and reset are still there, just tucked under a collapsed "More options" section.
- **Upgrade notes:** this release raises the internal settings file format to schema version 6 (adds
  per-page display names). **Downgrading to 0.7.0 or earlier after running this version is not supported
  without manual recovery** — an older build will start with an empty, read-only session instead of your
  panels (nothing is deleted; the file itself is left untouched). To go back: close the app, and manually
  restore the automatic pre-upgrade snapshot at `%LOCALAPPDATA%\DesktopOrganizer\State\application-state.premigration-v5.json`
  over `application-state.json` (this captures everything as it was right before upgrading to this
  version, not anything changed afterwards).

Release polish, colour and size controls (ADR-042, redesigned same day), 2026-09-28:

- The panel Customize window and Settings → New panel defaults now use an explicit Light/Dark mode switch,
  a 12-colour accent palette (spanning the spectrum, from Red through Violet/Magenta to two neutrals), and a
  redesigned, Windows 11 Personalization-style layout: a live preview card, a Mode segmented control, a
  12-swatch accent grid with a 13th "Custom…" cell, and a collapsible Advanced section for raw hex input and
  Reset — replacing the earlier flat 10-preset grid plus two hex boxes.
  - Switching Light/Dark re-derives the panel's text colour for its current accent (each of the 12 presets
    carries its own light- and dark-mode text colour) instead of the accent alone deciding readability; light
    panels now render on their own light surface so their (dark) text stays reliably readable, instead of the
    text-contrast problem some light presets had on the app's original always-dark surface.
  - A "Custom…" swatch (rather than a separate button) opens the system colour picker (RGB, custom-colour
    slots pre-filled from the presets) for full RGB control and shows the resulting colour as selected when it
    doesn't match any of the 12 presets; the hex boxes remain available as a third, advanced way to enter a
    colour, and surface style (Solid/Ambient/Glass) plus opacity — with an honestly-enforced opacity floor —
    moved into the same card layout.
- Icon-size and grid-step sliders now move in fixed steps of 8 (icon size up to 128, up from 96) with an
  adjacent numeric box for typing an exact value; opening a settings window no longer silently rewrites an
  existing size value.
- The "Customize panel…" window now fits on a 1366×768 display at 125% scaling instead of being clipped off
  the bottom of the screen.
- The grid-step control in Layout & snapping now has a visible "Grid step" caption (it previously only had a
  screen-reader name).
- Known gap, tracked for follow-up: in High Contrast mode, the active Mode/Surface segment and the selected
  accent swatch are not yet tied to the system highlight colour (only a thin hairline currently marks them).
- Product metadata: Company is now khagendev; existing settings are migrated automatically on first start.

Release polish (ADR-039–ADR-041, plus an ADR-038 amendment), 2026-09-27:

- Real, high-resolution panel icons: shortcuts and custom-icon folders now show their actual icon instead of
  a generic type icon, and icons render at the panel's own size instead of an upscaled 32px bitmap
  (`IShellItemImageFactory`, with the previous attribute-only lookup kept as a fallback and the only path for
  a missing file).
- Panel items now show the real Windows shell context menu (the same one Explorer shows, including "Open
  with", "Send to", and third-party shell extensions) instead of a bespoke four-item menu. "Remove from
  panel" is gone as a menu command — removing an item from a panel is now a drag-out gesture (unchanged from
  the previous round); deleting a managed item through the shell menu restores it to a visible location
  first, then recycles it.
- First run now creates one empty panel instead of showing a blank desktop with no entry point.
- Uninstalling the app now returns any file you had dragged into a panel to its original location (or the
  desktop, if that location is gone) instead of deleting it along with the app's data folder.
- Task Manager and Explorer's file Properties now show "DesktopOrganizer" as the process/file name; the
  copyright holder is unified as "khagen" across the app, the installer, and `LICENSE`.
- The installer now defaults to English, refuses to run on anything older than Windows 10, and no longer
  bundles debug symbols/XML doc comments.
- Documentation: the Release hardening 1.0 manual-verify items were closed after testing on Windows 11 24H2;
  panel item sorting (`Manual`/`Name`) was finalized as-is.

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

[Unreleased]: https://github.com/messire/DesktopOrganizer/compare/v0.9.0...HEAD
[0.9.0]: https://github.com/messire/DesktopOrganizer/releases/tag/v0.9.0
[0.8.0]: https://github.com/messire/DesktopOrganizer/releases/tag/v0.8.0
[0.6.1]: https://github.com/messire/DesktopOrganizer/releases/tag/v0.6.1
[0.6.0]: https://github.com/messire/DesktopOrganizer/releases/tag/v0.6.0
[0.5.1]: https://github.com/messire/DesktopOrganizer/releases/tag/v0.5.1
[0.5.0]: https://github.com/messire/DesktopOrganizer/releases/tag/v0.5.0
[0.4.0]: https://github.com/messire/DesktopOrganizer/releases/tag/v0.4.0
[0.3.0]: https://github.com/messire/DesktopOrganizer/releases/tag/v0.3.0
[0.2.1]: https://github.com/messire/DesktopOrganizer/releases/tag/v0.2.1
[0.2.0]: https://github.com/messire/DesktopOrganizer/releases/tag/v0.2.0
[0.1.0]: https://github.com/messire/DesktopOrganizer/releases/tag/v0.1.0
