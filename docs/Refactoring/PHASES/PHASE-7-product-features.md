# Phase 7 — Product Capability Expansion

## Приоритет 1: Peek / Overlay mode

Hotkey временно поднимает panels поверх приложений. Повторный hotkey возвращает desktop-level placement.

DoD:
- не меняет persisted Z-order mode;
- корректно возвращается после focus loss;
- multi-monitor проверен.

## Приоритет 2: Stacked panels / Tabs

Несколько logical panels занимают одну geometry и переключаются tabs.

DoD:
- independent content/settings;
- deterministic active tab restore;
- drag-in/out tab behavior specified.

## Приоритет 3: Smart panels

Dynamic query panels:
- recent downloads;
- modified today/week;
- extension/category filters;
- known folders.

DoD:
- query model versioned;
- watcher load bounded;
- empty/error states designed.

## Приоритет 4: Rules engine

Rule examples:
- screenshots -> Screenshots panel;
- `.pdf` -> Docs;
- new `.lnk` -> Apps.

DoD:
- dry-run/preview;
- deterministic priority;
- loop protection;
- audit log.

## Приоритет 5: Layout profiles

Work / Personal / Gaming / Presentation, optionally bound to virtual desktop.

DoD:
- atomic switch;
- rollback if a monitor is missing;
- no item loss.
