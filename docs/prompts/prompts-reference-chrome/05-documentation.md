# Prompt: architecture-documenter — finalize ADR-030

После реализации и review финализируй `ADR-030-reference-panel-chrome.md`.

Обнови Status с `Proposed` на `Accepted`, если реализация закрыта.

Документ должен зафиксировать фактическую, а не предполагаемую реализацию:

- какие template layers реально добавлены;
- какие design tokens/metrics добавлены;
- как реализован curved accent marker;
- какой glow mechanism в итоге выбран (`DropShadowEffect` или faux-glow fallback);
- как решён clipping/bounds;
- как High Contrast отключает/заменяет decorative effects;
- какие interaction/edit states влияют на chrome;
- performance trade-offs;
- какие manual verify пункты остаются;
- результаты review/fix cycles;
- отдельным явным пунктом: persistence/schema не менялись;
- отдельным явным пунктом: DWM/Acrylic/backdrop blur не реализован.

При необходимости добавь ссылку на ADR-030 в `adr/README.md`, но не переписывай unrelated ADR.
