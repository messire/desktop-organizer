# Desktop Manager — Custom WPF Scrollbar Prompt Pack

Этот пакет предназначен для поэтапной реализации кастомного вертикального scrollbar в Desktop Manager.

Цель визуала:
- overlay-style scrollbar;
- без стрелок;
- без glow / trail / bloom;
- узкий rounded thumb-capsule;
- нейтральный graphite / frosted gray;
- track почти невидимый;
- максимально простая и нативная реализация через WPF ControlTemplate.

Рекомендуемый порядок:
1. `01-analysis.md`
2. `02-implementation.md`
3. `03-review.md`
4. `04-design-tokens.md` использовать как визуальную спецификацию на всех этапах.

Главный принцип: не писать собственную механику прокрутки. Сохраняем стандартные WPF `ScrollViewer -> ScrollBar -> Track -> Thumb/RepeatButton` и меняем только визуальное представление.
