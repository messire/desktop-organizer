# Architecture Decision Records

Здесь хранятся архитектурные решения проекта DesktopOrganizer.

Формат: `ADR-NNN-краткое-название.md`

Статусы: `Proposed` → `Accepted` → `Deprecated` / `Superseded by ADR-NNN`

## Индекс

| ADR | Название | Статус |
|-----|----------|--------|
| [ADR-001](ADR-001-desktop-integration.md) | Desktop Integration — Progman/WorkerW подход | Accepted |
| [ADR-002](ADR-002-main-window-as-settings.md) | MainWindow как Settings/Config окно | Accepted |
| [ADR-003](ADR-003-desktop-gestures-and-capture.md) | Десктоп-жесты и захват иконок + доп. 2026-09-05 (`SendMessageTimeout`-бюджет 50мс в `LVM_HITTEST`-проверке внутри хука, консолидация P/Invoke в `NativeApi`) | Accepted |
| [ADR-004](ADR-004-phase5-pages-snapshots-multimonitor.md) | Архитектура Фазы 5: Pages, Snapshots, Multi-Monitor | Accepted |
| [ADR-005](ADR-005-panel-ux-improvements.md) | UX-улучшения панели: ресайз по сетке, фильтр содержимого, roll-up кнопка | Accepted |
| [ADR-006](ADR-006-hide-files-only-lvm-position.md) | Избирательное скрытие иконок рабочего стола — идентификация по метке и LVM_SETITEMPOSITION | Rejected (2026-07-26) — оставлен только простой toggle показать/скрыть всё |
| [ADR-007](ADR-007-system-icons-panel-window-type.md) | SystemIconsPanel — второй тип независимого desktop-window | Accepted |
| [ADR-008](ADR-008-shell-com-sta-threading-and-pidl-marshaling.md) | Shell COM — STA-поток и маршалинг PIDL-массивов | Accepted |
| [ADR-009](ADR-009-drag-drop-move-semantics.md) | Drag&drop в панель — Move-семантика через managed storage + JSON-manifest | Accepted |
| [ADR-010](ADR-010-managed-storage-cleanup-on-panel-removal.md) | Очистка managed-копий при удалении панели — разделение «закрыть» vs «удалить навсегда» | Accepted |
| [ADR-011](ADR-011-manual-drag-with-magnetic-grid-snap.md) | Ручной драг панели вместо `Window.DragMove()` — покадровый магнитный снаппинг к сетке | Accepted |
| [ADR-012](ADR-012-performance-and-resilience-hardening.md) | Фаза 7 — Persistence-дебаунс, вынос машины состояний хука, Freeze()-инвариант, авторитетный кеш манифеста | Accepted |
| [ADR-013](ADR-013-panel-item-selection-and-rename.md) | Выделение и переименование значков панели — `ListBox`-контейнеры, GUID в имени каталога, физическое переименование через `IFileItemRenameService` | Accepted |
| [ADR-014](ADR-014-panel-item-sorting-and-manual-order.md) | Сортировка и ручной порядок значков панели — `PanelSortMode` (Manual/Name), natural-order компаратор, drag-to-reorder с якорением к видимому соседу | Accepted |
| [ADR-015](ADR-015-panel-icon-grid-geometry.md) | Модель геометрии сетки значков панели — разделение сетки позиции окна (`GridSize`) и сетки раскладки элементов (`GridMetrics`), фикс среза рамки выделения (B-F1), top-align иконки | Accepted |
| [ADR-016](ADR-016-virtual-desktop-visibility.md) | Видимость desktop-окон на виртуальных рабочих столах — `IVirtualDesktopManager` («Follow-me»), тихая деградация, отступление от инварианта P/Invoke-за-`INativeApi` для COM | **Superseded by ADR-019** (2026-09-02) |
| [ADR-017](ADR-017-panel-anti-overlap-on-drag.md) | Блокировка перекрытия панелей при перетаскивании — жёсткая «твёрдая стена» (AABB collision response), раздельные оси + swept-тест, правило R1 (пред-существующие пересечения не чинятся), без настроек/обходов | Accepted |
| [ADR-018](ADR-018-adaptive-icon-grid-layout.md) | Адаптивная раскладка иконок в панели (Fences parity) — `AdaptiveGridLayout`/`AdaptiveIconGridPanel` заменяют `WrapPanel`, растяжение зазоров полного ряда vs прижатый неполный ряд, вертикальные зазоры никогда не растягиваются, `GridMetrics.Quantize` удалён, MAJOR-баг M-1 (потеря колонки при недостаче <1px) исправлен, MAJOR-trade-off M-2 (скачок колонок на пороге появления скроллбара) принят как есть; обновлено — `DefaultMinGap` 0→4 (зазор на обеих осях), явный `MinPanelWidth` только в Grid-режиме; финализировано — рассинхрон трёх копий `120` устранён единым `GridMetrics.MinPanelSize` + публичным тестируемым `GridMetrics.MinPanelWidth` (фикс скачка правого края при ресайзе на малых `IconSize`) | Accepted |
| [ADR-019](ADR-019-sidebar-top-level-virtual-desktop.md) | Боковая панель как top-level окно вместо WorkerW-child — устраняет первопричину нерабочего чекбокса «Show on all virtual desktops» (`WorkerW` — единый слой на все столы); `ApplyDesktopWindowStyles` строго после `Show()` (spike R0: стили до `Show()` навсегда исключают окно из учёта менеджера столов); ON — poll-«follow-me» ADR-016 переиспользован на реально отслеживаемом окне; OFF — anchor на «Desktop 1» через `ResolveAnchorDesktopId` (systemDefault ?? persisted ?? panelCurrent) с read-only реестровым зондом R2 (кросс-валидация, тихий откат на эвристику R1); fence-панели не затронуты; supersedes ADR-016; **Trade-off `Topmost="True"` пересмотрен, см. ADR-020** | Accepted |
| [ADR-020](ADR-020-sidebar-zorder-back-without-topmost.md) | Боковая панель без `Topmost` — периодический (300 мс) `DispatcherTimer` + `IDesktopIntegrationService.SendToBackOfZOrder` возвращает панель на один слот выше `Progman` (явный якорь `GetWindow(GW_HWNDPREV)`, fallback `HWND_BOTTOM`), `SWP_NOOWNERZORDER` обязателен (иначе off-by-one/бесконечный churn со скрытым owner-окном WPF); частично пересматривает отклонение «Варианта 2» из ADR-001 (2026-06-16) — смягчает его явным якорем, не устраняет полностью (Windows 10 и визуальный рендеринг не проверены); реактивный `SetWinEventHook` отклонён (второй глобальный хук) | Accepted |
