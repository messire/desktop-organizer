# Prompt: parallel review — code-quality-reviewer + fences-ux-parity-reviewer

Проведите параллельное ревью реализации ADR-030.

## code-quality-reviewer

Сфокусируйся на:

- корректности WPF template/resource lookup;
- отсутствии layout jitter;
- отсутствии лишних converters/DP/state duplication;
- frozen/shared brushes там, где это имеет смысл;
- стоимости `DropShadowEffect` и invalidation;
- отсутствии effects на item-level;
- clipping/bounds;
- hit testing;
- High Contrast;
- persistence freeze;
- build/test quality.

CRITICAL/MAJOR замечания должны содержать конкретный файл/узел и минимальный фикс.

## fences-ux-parity-reviewer

Сравни итоговый визуальный язык с `docs/Ref image.png`, но с учётом ADR-025/030: это не pixel-perfect
копия Fences/reference.

Проверь:

- поверхность остаётся нейтральной;
- accent sparse, а не сплошная заливка;
- marker действительно имеет мягкий верхний изгиб;
- glow заметен, но не превращается в neon halo;
- border помогает читать геометрию на разных обоях;
- header/content hierarchy стала ближе к reference;
- interaction states не делают панель визуально шумной;
- разные accent colors сохраняют одинаковый perceived weight;
- dark/light wallpapers;
- small/large panels;
- DPI scaling risk;
- High Contrast fallback.

## Fix cycle

После параллельного review исправить все CRITICAL/MAJOR. Затем повторить review максимум ещё 2 раза.
MINOR не должен раздувать scope без пользовательской пользы.

Финальный критерий: визуально ближе к reference, но не ценой деградации drag/resize, accessibility или
архитектуры.
