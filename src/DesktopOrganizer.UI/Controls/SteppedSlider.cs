using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.UI.Controls;

/// <summary>
/// A <see cref="Slider"/> whose every input path — keyboard, mouse-drag, track click, and (focused-only)
/// mouse wheel — lands on a multiple of <see cref="Step"/> via <see cref="SteppedValue"/>
/// (docs/PLANS/color-presets-and-size-step.md §5.2), instead of WPF's own continuous
/// <see cref="RangeBase.SmallChange"/>/<see cref="RangeBase.LargeChange"/> stepping. Direction is taken
/// from WHICH mechanism fired (a key, a thumb drag, a track click, a wheel tick) — never inferred from a
/// <see cref="RangeBase.ValueChanged"/> delta, which would not know which of two adjacent multiples the
/// user actually meant.
/// </summary>
/// <remarks>
/// <see cref="Slider.AutoToolTipPlacement"/> and <see cref="Slider.IsMoveToPointEnabled"/> are UNSUPPORTED
/// (MINOR-5, review round): this class's own <see cref="OnThumbDragDelta"/> override computes its snapped
/// target purely from the drag delta relative to <c>_dragStart</c> (§5.1's <see cref="SteppedValue.SnapForDrag"/>),
/// never from where the pointer landed on the track — so a click-to-jump-to-point gesture
/// (<see cref="Slider.IsMoveToPointEnabled"/>) would land the thumb at an unsnapped, continuous position
/// this class does not otherwise produce, and <see cref="Slider.AutoToolTipPlacement"/>'s built-in tooltip
/// shows the raw (potentially unsnapped, mid-drag) <see cref="RangeBase.Value"/> rather than a
/// step-accurate one. Both stay at their <see cref="Slider"/> base-class defaults (<c>None</c>/<c>false</c>)
/// today; a future consumer that wants either would need this class to special-case them explicitly rather
/// than assuming they already compose correctly.
/// </remarks>
public class SteppedSlider : Slider
{
    /// <summary>Identifies the <see cref="Step"/> dependency property.</summary>
    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(
        nameof(Step),
        typeof(int),
        typeof(SteppedSlider),
        new PropertyMetadata(SizeStepPolicy.Step, OnStepChanged),
        ValidateStep);

    private Track? _track;
    private double? _dragStart;

    /// <summary>
    /// Initializes a new instance of the <see cref="SteppedSlider"/> class. Disables WPF's own tick-snap
    /// (every step decision below routes through <see cref="SteppedValue"/> instead) and ties
    /// <see cref="RangeBase.SmallChange"/>/<see cref="RangeBase.LargeChange"/>/<see cref="Slider.TickFrequency"/>
    /// to <see cref="Step"/> purely for the visual tick marks and any external UIA
    /// <c>RangeValuePattern.SmallChange</c>/<c>LargeChange</c> readers — this class's own overrides below
    /// never consult them.
    /// </summary>
    public SteppedSlider()
    {
        IsSnapToTickEnabled = false;
        TickFrequency = Step;
        SmallChange = Step;
        LargeChange = Step;
    }

    /// <summary>
    /// The fixed step every keyboard/drag/track-click/wheel interaction below snaps to. Defaults to
    /// <see cref="SizeStepPolicy.Step"/>. Must be at least 1.
    /// </summary>
    public int Step
    {
        get => (int)GetValue(StepProperty);
        set => SetValue(StepProperty, value);
    }

    /// <inheritdoc />
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _track = GetTemplateChild("PART_Track") as Track;
    }

    /// <summary>Right-arrow/up-arrow/mouse-wheel-up: step to the nearest multiple strictly above the current value.</summary>
    protected override void OnIncreaseSmall() => MoveTo(SteppedValue.Next(Value, Step, StepDirection.Up, (int)Minimum, (int)Maximum));

    /// <summary>Left-arrow/down-arrow/mouse-wheel-down: step to the nearest multiple strictly below the current value.</summary>
    protected override void OnDecreaseSmall() => MoveTo(SteppedValue.Next(Value, Step, StepDirection.Down, (int)Minimum, (int)Maximum));

    /// <summary>Page-up / track click above the thumb: same target as <see cref="OnIncreaseSmall"/> — one step up, never a jump.</summary>
    protected override void OnIncreaseLarge() => MoveTo(SteppedValue.Next(Value, Step, StepDirection.Up, (int)Minimum, (int)Maximum));

    /// <summary>Page-down / track click below the thumb: same target as <see cref="OnDecreaseSmall"/> — one step down, never a jump.</summary>
    protected override void OnDecreaseLarge() => MoveTo(SteppedValue.Next(Value, Step, StepDirection.Down, (int)Minimum, (int)Maximum));

    /// <inheritdoc />
    protected override void OnMinimizeValue() => MoveTo((int)Minimum);

    /// <inheritdoc />
    protected override void OnMaximizeValue() => MoveTo((int)Maximum);

    /// <summary>Captures the drag-start value so <see cref="OnThumbDragDelta"/> can snap relative to it (§5.1).</summary>
    /// <param name="e">Event data — forwarded to the base implementation unchanged.</param>
    protected override void OnThumbDragStarted(DragStartedEventArgs e)
    {
        _dragStart = Value;
        base.OnThumbDragStarted(e);
    }

    /// <summary>
    /// Computes the raw (unsnapped) value implied by the current drag position and snaps it via
    /// <see cref="SteppedValue.SnapForDrag"/> relative to the value the drag started from — deliberately
    /// does NOT call <c>base.OnThumbDragDelta</c>, which would otherwise also write the raw, unsnapped
    /// value through <see cref="RangeBase.Value"/>. A missing <c>PART_Track</c> (an unrelated custom theme)
    /// degrades to the base, unsnapped behaviour rather than throwing.
    /// </summary>
    /// <param name="e">Event data supplying the drag delta since the last event.</param>
    protected override void OnThumbDragDelta(DragDeltaEventArgs e)
    {
        // MINOR-5 fix (review round): defensive — WPF's own Slider wires this override to fire only for
        // PART_Track's own Thumb, so e.OriginalSource should always already be _track.Thumb; guarded
        // anyway so a future custom ControlTemplate that somehow routes a DIFFERENT Thumb's DragDelta here
        // degrades to the base, unsnapped behaviour instead of snapping a drag that was never this
        // control's own thumb.
        if (_track is null || e.OriginalSource != _track.Thumb)
        {
            base.OnThumbDragDelta(e);
            return;
        }

        var raw = Value + _track.ValueFromDistance(e.HorizontalChange, e.VerticalChange);
        var start = _dragStart ?? Value;
        MoveTo(SteppedValue.SnapForDrag(raw, start, Step, (int)Minimum, (int)Maximum));
    }

    /// <inheritdoc />
    protected override void OnThumbDragCompleted(DragCompletedEventArgs e)
    {
        _dragStart = null;
        base.OnThumbDragCompleted(e);
    }

    /// <summary>
    /// Steps via <see cref="SteppedValue"/> only while this slider has keyboard focus (§5.2) — an
    /// unfocused slider must let the wheel event bubble up to whatever <see cref="ScrollViewer"/> it sits
    /// inside, the same way every other WPF control that doesn't own the wheel already does.
    /// </summary>
    /// <param name="e">Event data — <see cref="System.Windows.Input.MouseWheelEventArgs.Delta"/> decides the step direction.</param>
    protected override void OnPreviewMouseWheel(System.Windows.Input.MouseWheelEventArgs e)
    {
        if (!IsKeyboardFocusWithin)
        {
            base.OnPreviewMouseWheel(e);
            return;
        }

        MoveTo(SteppedValue.Next(Value, Step, e.Delta > 0 ? StepDirection.Up : StepDirection.Down, (int)Minimum, (int)Maximum));
        e.Handled = true;
    }

    private static bool ValidateStep(object value) => (int)value >= 1;

    private static void OnStepChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not SteppedSlider slider) return;

        var step = (int)e.NewValue;
        slider.TickFrequency = step;
        slider.SmallChange = step;
        slider.LargeChange = step;
    }

    private void MoveTo(int value) => SetCurrentValue(ValueProperty, (double)value);
}
