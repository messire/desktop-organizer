# Prompt 02 — Implement custom WPF scrollbar

Реализуй кастомный scrollbar для Desktop Manager по следующей спецификации.

## Stack
- WPF
- XAML
- без сторонних UI-библиотек

## Visual style
- только vertical scrollbar;
- ширина области scrollbar примерно 12–16 px;
- сам thumb примерно 8–10 px;
- thumb = rounded pill/capsule;
- `CornerRadius` визуально равен половине ширины;
- нейтральный graphite / frosted gray цвет;
- thumb должен быть немного светлее `PanelChrome`;
- допускается `LinearGradientBrush` из 2–3 очень близких серых оттенков;
- допускается тонкий `BorderBrush` с alpha;
- glow отсутствует;
- trail отсутствует;
- bloom отсутствует;
- track практически невидим;
- никаких стандартных Windows arrow buttons;
- scrollbar должен визуально растворяться в панели и не перетягивать внимание;
- hover: thumb немного светлее;
- dragging/pressed: thumb ещё немного светлее или чуть контрастнее;
- disabled: сниженная opacity;
- если прокрутка не нужна, scrollbar не показывать.

## Behavior
- сохранить стандартную механику WPF `ScrollBar` / `Track` / `Thumb`;
- сохранить mouse wheel;
- сохранить dragging thumb;
- сохранить page scrolling semantics через `Track`;
- не ломать `ScrollViewer`;
- не писать кастомную механику вычисления offset вручную;
- не использовать code-behind без необходимости;
- не использовать `ShaderEffect`;
- не использовать `DropShadowEffect`;
- не использовать DWM/Acrylic/Win32 для scrollbar.

## Architecture
- вынести стиль в общий `ResourceDictionary`;
- создать reusable style для `ScrollBar`;
- отдельно создать style/control template для `Thumb`;
- использовать `DynamicResource` для цветов, где это соответствует текущей системе темы;
- по возможности переиспользовать текущие design tokens `PanelChrome`;
- не хардкодить значения в `ChildWindow`, если их можно вынести в resources.

## Desired structure

```text
ScrollBar
└── Track
    ├── DecreaseRepeatButton — прозрачный, без visual chrome
    ├── Thumb — capsule
    └── IncreaseRepeatButton — прозрачный, без visual chrome
```

Для vertical orientation:
- `RepeatButton` должны оставаться функциональными для Track/page scrolling;
- визуально они должны быть полностью прозрачными;
- `Thumb` должен находиться на прозрачном track;
- никаких glyph/arrow внутри RepeatButton быть не должно.

Также:
- добавь `MinHeight` для `Thumb`, чтобы при длинном списке он не становился микроскопическим;
- не делай thumb слишком широким;
- track не должен иметь белую или системную рамку;
- scrollbar не должен добавлять визуальный padding больше необходимого;
- не менять ScrollViewer behavior ради внешнего вида;
- убедиться, что системный шаблон Windows нигде не просачивается.

После изменений:
1. покажи список изменённых файлов;
2. покажи итоговый XAML styles/templates;
3. объясни, какие значения отвечают за Width, Thumb width, CornerRadius, Hover и Pressed состояния;
4. проверь, что стандартный Windows scrollbar больше нигде визуально не просачивается;
5. укажи, как стиль применяется к нужному `ScrollViewer`.
