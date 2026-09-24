# DesktopOrganizer — Refactoring & Product Improvement Package

Дата: 2026-09-17

Этот пакет предназначен как рабочая база для следующего цикла развития DesktopOrganizer. Он объединяет:

- стабилизацию текущей реализации;
- устранение известных архитектурных долгов;
- переход от WPF `Thumb`-resize к надёжному native resize path;
- формализацию interaction state;
- single-instance + IPC;
- пересмотр persistence;
- обновление UI к Ambient Accent visual language;
- подготовку native boundary для C/C++ модулей;
- тестовую и rollout-стратегию.

Пакет основан на фактической структуре загруженного проекта и текущих ADR/планах. Ключевые исходные точки:

- `src/DesktopOrganizer.UI/Behaviors/PanelResizeController.cs`
- `src/DesktopOrganizer.UI/Behaviors/PanelDragMoveBehavior.cs`
- `src/DesktopOrganizer.UI/Views/ChildWindow.xaml`
- `src/DesktopOrganizer.UI/App.xaml.cs`
- `src/DesktopOrganizer.UI/Services/AppSettings.cs`
- `src/DesktopOrganizer.Services/Tools/DesktopIntegrationService.cs`
- `src/DesktopOrganizer.Services/Tools/VirtualDesktopService.cs`
- `docs/REFACTORING_PLAN.md`
- ADR-001..ADR-020

## Главные решения

1. Не смешивать визуальный редизайн и системную стабилизацию в один PR.
2. Native boundary вводить точечно: desktop host, resize/move lifecycle, shell integration и потенциально thumbnails/icon cache.
3. Ambient Accent принять как целевой visual language: нейтральная поверхность + семантический accent + сильные interaction states.
4. `ApplicationSettingsBase` оставить только как legacy/простые user preferences; структурное состояние панелей мигрировать в versioned JSON store.
5. Interaction mode сделать явной частью UI architecture.
6. Resize сначала стабилизировать, затем заменить WPF Thumb path на Win32 non-client sizing path.

## Фазы

- Phase 0 — Baseline, diagnostics, safety net
- Phase 1 — Resize stabilization + interaction state
- Phase 2 — Native resize/desktop-host boundary
- Phase 3 — Single instance + IPC + lifecycle hardening
- Phase 4 — Storage migration to versioned JSON
- Phase 5 — Ambient Accent visual system
- Phase 6 — UX interaction polish / Edit Layout mode
- Phase 7 — Product capability expansion
- Phase 8 — Native acceleration and shell enhancements
- Phase 9 — Hardening, performance, release readiness

Подробности см. `ROADMAP.md` и каталог `PHASES/`.
