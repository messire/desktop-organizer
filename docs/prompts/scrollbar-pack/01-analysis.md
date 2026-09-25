# Prompt 01 — Analyze current scrollbar implementation

Проанализируй текущую реализацию прокрутки в Desktop Manager.

Цель:
заменить стандартный Windows/WPF scrollbar внутри container window на кастомный минималистичный scrollbar в стиле текущего UI.

Визуальная цель:
- вертикальный scrollbar;
- без стрелок вверх/вниз;
- без светящегося шлейфа;
- без bloom/glow вокруг thumb;
- очень узкий;
- thumb в виде вертикальной capsule/pill;
- скругление максимально близкое к половине ширины thumb;
- цвет thumb нейтральный серо-графитовый, визуально связанный с цветом PanelChrome;
- допустим очень слабый внутренний highlight;
- track практически невидимый;
- scrollbar должен выглядеть как overlay control, а не как стандартный Windows scrollbar.

Важно:
- сначала найди, где именно сейчас создаётся `ScrollViewer` / `ScrollBar` и какой `ControlTemplate` используется;
- найди существующие `ResourceDictionary` и design tokens, которые логично переиспользовать;
- не меняй код;
- не предлагай новый UI framework;
- не используй сторонние библиотеки;
- решение должно быть чистым WPF/XAML.

На выходе дай:
1. какие файлы нужно менять;
2. текущую структуру `ScrollViewer` / `ScrollBar`;
3. какие существующие brushes/tokens можно переиспользовать;
4. минимальный план реализации;
5. потенциальные проблемы с nested `ScrollViewer`, mouse wheel, touchpad, DPI и visibility scrollbar;
6. отдельно укажи, есть ли сейчас Horizontal ScrollBar и нужно ли его стилизовать или оставить нетронутым.
