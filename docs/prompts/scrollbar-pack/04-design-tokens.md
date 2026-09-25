# Prompt / Spec 04 — Design tokens

Используй эти значения как стартовую визуальную спецификацию.

```text
ScrollBar width:      14
Thumb width:           8
Thumb min height:     36
Thumb corner radius:   4

Track background:   #00000000

Thumb normal:       #66788088
Thumb border:       #407F8A90
Thumb hover:        #80919BA0
Thumb pressed:      #A0A8B0B4

Disabled opacity:    0.35
```

## Notes

Это стартовые значения, не абсолютная догма.

Допустимо слегка подстроить их под существующий `PanelChrome`, если:
- hue остаётся нейтральным graphite/frosted gray;
- saturation остаётся низкой;
- thumb остаётся заметным, но не ярким;
- scrollbar не начинает светиться;
- hover/pressed остаются сдержанными;
- контраст не превращает scrollbar в отдельный акцентный элемент.

## Recommended WPF implementation

Предпочтительно:
- `Border` для Thumb;
- `CornerRadius=4` при ширине thumb `8`;
- `Background` через `SolidColorBrush` или очень простой `LinearGradientBrush`;
- `BorderBrush` с alpha;
- `ControlTemplate.Triggers` или `VisualStateManager` для hover/dragging;
- прозрачные `RepeatButton`;
- прозрачный Track background.

Не использовать:
- `DropShadowEffect`;
- `BlurEffect`;
- `ShaderEffect`;
- DWM/Acrylic;
- custom drawing;
- custom scroll math;
- timers;
- code-behind ради hover/pressed.
