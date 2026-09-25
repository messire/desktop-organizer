# Задача: довести визуал панелей до reference-style layered chrome

Нужно реализовать визуальное обновление панелей DesktopOrganizer по `docs/Ref image.png`.

Перед началом обязательно прочитай:

- `CLAUDE.md`;
- `adr/ADR-025-ambient-accent-visual-language.md`;
- новый ADR из этого пакета: `adr/ADR-030-reference-panel-chrome.md`;
- `src/DesktopOrganizer.UI/Resources/Styles/Panels.xaml`;
- `src/DesktopOrganizer.UI/Resources/Styles/Brushes.xaml`;
- `src/DesktopOrganizer.UI/Controls/PanelChrome.cs`;
- `src/DesktopOrganizer.UI/Models/PanelMetrics.cs`;
- `src/DesktopOrganizer.UI/Views/ChildWindow.xaml`;
- `src/DesktopOrganizer.UI/Theming/InteractionVisualState.cs`;
- `src/DesktopOrganizer.UI/Theming/PanelTheme.cs`;
- `src/DesktopOrganizer.UI/Theming/SurfaceComposer.cs`;
- `src/DesktopOrganizer.UI/Behaviors/PanelVisualStateBehavior.cs`.

## Цель

Сохранить текущий Ambient Accent дизайн, но добавить depth/chrome, близкий к референсу:

- тонкая нейтральная рамка;
- слабый accent-colored border/stroke;
- мягкий accent glow;
- очень слабый surface highlight/gradient;
- яркая accent-линия слева у заголовка, которая сверху слегка загибается вправо;
- аккуратное усиление этих эффектов в существующих interaction states;
- никаких новых полей persistence.

Референс НЕ нужно копировать пиксель-в-пиксель. Нужна та же визуальная иерархия: тёмная нейтральная
поверхность + тонкая геометрия + локальный цветной свет + чистый контент.

## Жёсткие ограничения

1. Persistence заморожен. Нельзя менять `schemaVersion`, migrations, DTO, `IAppSettings`,
   `Settings.settings`, `user.config`, defaults и семантику сохранения.
2. Нельзя внедрять настоящий DWM/Acrylic/backdrop blur. `Glass` остаётся тем, чем является сейчас.
3. Нельзя менять WorkerW/Progman hosting без абсолютно необходимой причины.
4. Нельзя создавать дополнительные top-level shadow windows.
5. Нельзя заводить вторую state machine. Используй текущие `InteractionVisualState`, Edit Layout и
   design tokens.
6. Нельзя допустить layout jitter: декоративные изменения opacity/thickness не должны двигать content.
7. Decorative layers не должны перехватывать hit testing.
8. Не превращай дизайн в сильный neon/RGB. Glow должен быть слабым.

## Ожидаемая структура реализации

Предпочтительно расширить `PanelChrome` template примерно так:

```text
PanelChrome
├─ glow layer
├─ accent stroke
├─ neutral stroke
├─ PART_Chrome / surface
│  ├─ subtle surface highlight
│  ├─ header
│  │  ├─ curved accent marker (Path)
│  │  ├─ title
│  │  └─ header actions
│  └─ content
└─ PART_InteractionOverlay
```

Точная XAML-структура может отличаться, если есть более чистое решение.

### Curved marker

Не имитируй загиб несколькими прямоугольниками. Используй `Path`/`Geometry` с rounded caps/joins или
другую vector geometry. Marker должен продолжать существующую accent semantics и emphasis.

### Glow

Сначала попробуй controlled `DropShadowEffect` на отдельном accent/glow слое:

- `ShadowDepth=0`;
- консервативный `BlurRadius`;
- низкая opacity;
- цвет из `AccentBrush`;
- glow рисуется внутри дополнительного прозрачного inset Window/Control bounds, чтобы его не clip'нуло.

Если это заметно бьёт по drag/resize performance, переходи на более дешёвый faux-glow через несколько
полупрозрачных strokes/gradients. Не применяй shader/effect к item grid.

### Tokens

Все константы визуала вынеси в существующую систему ресурсов/метрик: radius, thickness, glow radius,
glow opacity, border opacity, highlight opacity/inset. Не разбрасывай magic numbers по XAML.

Не добавляй настройки пользователя. Это code-level design tokens.

## Оркестрация

Выполни весь объём за один заход по правилам `CLAUDE.md`:

1. `fences-feature-planner` — план и dependency/risk map;
2. `wpf-ui-builder` — реализация;
3. `tdd-test-engineer` — tests/static gates;
4. `debug-healer` — только если build/tests красные;
5. параллельно `code-quality-reviewer` + `fences-ux-parity-reviewer`;
6. исправить MAJOR/CRITICAL замечания;
7. повторить review максимум 2–3 цикла;
8. `architecture-documenter` — финализировать ADR/документацию.

## Что обязательно проверить тестами/статическими гейтами

- новый template использует существующие accent/surface sources;
- persistence files/contracts не изменены;
- curved marker не содержит hard-coded category color;
- template parts/brushes корректно разрешаются;
- decorative layers `IsHitTestVisible=False` там, где это требуется;
- High Contrast branch не получает обязательный glow/highlight;
- build/tests зелёные.

## Manual verify Win10/Win11

В финале отдельно перечисли результаты/пункты ручной проверки:

- 1, 3, 5+ панелей одновременно;
- drag панели;
- resize с разных граней/углов;
- Edit Layout;
- Grid/List/Folder Portal/System Icons panel, где применимо;
- светлые/тёмные обои;
- разные accent colors;
- 100%, 125%, 150%, 200% DPI;
- High Contrast;
- отсутствие clipping glow на углах;
- отсутствие мерцания/geometry jitter;
- визуальная близость к `docs/Ref image.png`;
- субъективная отзывчивость drag/resize после glow.

## Финальный отчёт

Коротко и предметно:

- какие файлы изменены;
- что именно реализовано;
- какие токены добавлены;
- результаты build/test;
- замечания review и что исправлено;
- что осталось только manual verify;
- явно подтвердить: persistence/schema не менялись, Acrylic/DWM blur не добавлялся.
