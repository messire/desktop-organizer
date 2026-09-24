# Phase 5 — Ambient Accent Visual System

## Цель

Перейти от разноцветных полупрозрачных прямоугольников к системному visual language:

> neutral surface + semantic accent + interaction feedback

## Visual principles

- 90% neutral surface, ~10% accent.
- Accent не заливает всю панель.
- Idle chrome приглушён.
- Controls становятся явными при hover/editing.
- Typography/spacing/radius централизованы.
- Text labels максимум 2 строки + ellipsis + tooltip.

## Работы

### 5.1 Resource dictionary split

```text
Styles/
  Colors.xaml
  Brushes.xaml
  Typography.xaml
  Spacing.xaml
  Controls.xaml
  Panels.xaml
  Icons.xaml
```

### 5.2 Appearance model

Добавить:

```text
SurfaceStyle: Solid | Ambient | Glass
AccentColor
SurfaceOpacity
```

Background и accent больше не одно и то же.

### 5.3 Panel chrome

Создать reusable style/control:

```text
PanelChrome
  Header
  AccentIndicator
  ContentPresenter
  InteractionOverlay
```

### 5.4 Header

- 32-36px;
- title + optional semantic icon;
- actions muted in idle;
- hover feedback;
- overflow menu;
- title retains rename behavior.

### 5.5 Icon tiles

- fixed logical slot;
- two-line label;
- ellipsis;
- tooltip full name;
- neutral hover surface;
- selected uses accent at low alpha.

### 5.6 Rolled-up state

`Accent | Title | optional item count | expand control`.

## Артефакты

- `ADR/ADR-025-ambient-accent-visual-language.md`
- `UI/Colors.xaml`
- `UI/Brushes.xaml`
- `UI/Typography.xaml`
- `UI/Spacing.xaml`
- `UI/Panels.xaml`
- `UI/Controls.xaml`
- reference concept image in `Reference/`

## DoD

Статус на 2026-09-21 (HEAD `c477c59`, после 3 полных раундов `code-quality-reviewer` ∥
`fences-ux-parity-reviewer` + финального узкого фикс-цикла). Полная трассировка — план
`docs/PLANS/phase-5-ambient-accent-ui.md` §5/§7, архитектурные решения и явно принятые trade-off'ы —
[ADR-025](../../../adr/ADR-025-ambient-accent-visual-language.md) (Accepted).

- [x] Ни одного нового hardcoded color в `ChildWindow.xaml`. Проверено grep-гейтом
      (`rg -n "#[0-9A-Fa-f]{3,8}" src/DesktopOrganizer.UI/Views/ChildWindow.xaml` → 0 совпадений);
      попутно закрыт DO-003 (`docs/Refactoring/BACKLOG.md`) — то же для `MainWindow.xaml`.
- [x] Panel appearance выглядит одинаково для Grid/List/Portal/System Icons. Доставляется attached DP
      `Theming/PanelTheme.cs` (наследуемый, не биндинг на конкретный `DataContext`) — код-уровень
      проверен build + `PanelThemeMetadataTests`; визуальное сравнение (V-5..V-8) — manual verify.
- [x] Idle/Hover/Editing/Dragging/Resizing визуально различимы. `InteractionVisualStateTests` —
      `BorderOpacityFor` строго монотонна по всем пяти состояниям; после финального фикс-цикла
      Dragging/Resizing дополнительно различимы по ширине акцент-индикатора (3.0 → 4.5 → 5.1px) —
      см. ADR-025 «Dragging vs Resizing различимость». Живой рендеринг (V-9..V-13) — manual verify.
- [x] Accent используется локально, не как full-panel fill по умолчанию. `SurfaceComposer.ComposeSurface(...,
      Solid, o)` не зависит от `accent` (unit-тест-инвариант); нейтральная поверхность + локальный
      акцент — сознательное расхождение с буквальным дефолтом Fences, зафиксировано в ADR-025.
- [x] Labels не ломаются на 3+ строки. Фиксированная `GridMetrics.LabelHeight` + `TextTrimming` (без
      изменений типографики, см. план R-4); длинные имена (V-4) — manual verify.
- [~] **Known issue, задокументировано в ADR-025** — не «не сделано»: 100/125/150/200% DPI visual smoke
      (V-15) принципиально не воспроизводим в этой среде (нет живого Windows GUI) — код-часть (токены,
      `Freeze()`-кисти) завершена, сам визуальный smoke не выполнен ни разу.
- [~] **Known issue, задокументировано в ADR-025** — не «не сделано»: High-contrast/readability
      механизм реализован (`Colors.HighContrast.xaml`, `MinSurfaceOpacity`-floor, unit-тест
      `alpha == 255`), но с тремя явными ограничениями, зафиксированными в ADR-025 «Известные
      проблемы»: `Color.Overlay.Hover` может совпасть с фоном в отдельных HC-темах; активная вкладка
      настроек в HC различается только цветом фона; смена HC-темы подхватывается только при старте
      приложения (DO-015). Живая проверка на нескольких HC-темах (V-14) — manual verify.
