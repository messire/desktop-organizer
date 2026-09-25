# Prompt: tdd-test-engineer — Reference Panel Chrome tests

Добавь/обнови автоматические проверки для ADR-030.

Цель тестов не в pixel-perfect screenshot testing, а в защите архитектурных и XAML-инвариантов.

Проверь по возможности:

1. `PanelChrome` template содержит отдельные semantic layers для border/glow/highlight/marker.
2. Curved accent marker является vector geometry (`Path` или эквивалент), а не hard-coded bitmap.
3. Marker/glow/stroke получают accent из общей theme pipeline, а не category-specific literal color.
4. Decorative layers не перехватывают hit test (`IsHitTestVisible=False` там, где применимо).
5. State changes не используют Auto-sized chrome geometry, способную двигать content.
6. Existing interaction state tests остаются зелёными.
7. High Contrast resource path существует и decorative highlight/glow не являются обязательным
   единственным каналом различимости.
8. Нет изменений persistence contract:
   - schema version тот же;
   - нет новой migration;
   - DTO/state keys не добавлены;
   - `Settings.settings`/`IAppSettings` не расширены.
9. Не появился DWM/Acrylic/`SetWindowCompositionAttribute` код в рамках этой фичи.
10. `dotnet build` и `dotnet test` зелёные.

Если screenshot/UI automation в текущей среде невозможны, не симулируй результат. Оформи manual verify
список для Win10/Win11.

Отдельно оцени performance risk: если новый XAML создаёт `DropShadowEffect` per item или dynamic brush
allocation в hot path, пометь это как MAJOR и предложи исправление.
