# Prompt 03 — Review and polish

Проведи review реализованного custom scrollbar в Desktop Manager.

## Visual
Проверь:
- scrollbar не похож на стандартный Windows control;
- нет arrow buttons;
- нет белого системного track;
- нет glow;
- нет trail;
- нет bloom;
- thumb выглядит как тонкая capsule;
- цвет соответствует `PanelChrome`;
- hover/pressed отличаются, но очень деликатно;
- scrollbar не становится главным визуальным элементом панели;
- на тёмном фоне thumb остаётся заметным;
- scrollbar не создаёт лишний визуальный шум.

## WPF correctness
Проверь:
- используется стандартный `Track`;
- `PART_Track` присутствует там, где требуется;
- thumb корректно двигается;
- dragging работает;
- mouse wheel работает;
- touchpad scrolling не сломан;
- page scrolling работает;
- scrollbar корректно скрывается, когда scrolling не нужен;
- minimum thumb size адекватный;
- Vertical/Horizontal orientation не перепутаны;
- Horizontal scrollbar либо имеет отдельный корректный template, либо явно не затронут;
- binding между `ScrollViewer` и `ScrollBar` не сломан;
- нет ручной синхронизации offsets.

## Performance
Проверь:
- нет `BlurEffect`;
- нет `DropShadowEffect`;
- нет `ShaderEffect`;
- нет лишних animations на каждом frame;
- hover/pressed сделаны через triggers или `VisualStateManager`;
- никаких таймеров и code-behind для внешнего вида.

## Consistency
Проверь:
- цвета вынесены в resources;
- размеры вынесены в constants/resources, если проект уже использует такой подход;
- стиль можно переиспользовать в других `ScrollViewer`;
- решение не привязано жёстко к одному `ChildWindow`;
- design tokens имеют понятные имена и не конфликтуют с существующими ресурсами.

Если найдёшь проблемы, исправь их.

В конце дай:
1. короткий changelog;
2. итоговый список design tokens;
3. список файлов, где лежат scrollbar styles/templates;
4. короткое подтверждение, что scrolling behavior остался стандартным WPF.
