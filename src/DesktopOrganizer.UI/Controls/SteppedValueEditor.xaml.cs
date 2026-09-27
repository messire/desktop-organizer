using System.Windows;
using System.Windows.Controls;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.UI.Controls;

/// <summary>
/// Composes <see cref="SteppedSlider"/> + <see cref="IntegerInputBox"/> + a unit label behind one
/// <see cref="Value"/> dependency property (docs/PLANS/color-presets-and-size-step.md §5.3). The inner
/// slider/box are never bound to <see cref="Value"/> directly — this class synchronizes them imperatively,
/// under <see cref="_syncing"/>, so writing <see cref="Value"/> (e.g. on window open, from a bound
/// ViewModel property) never triggers a write back through either child control, and a user edit through
/// either child commits through exactly one path (<see cref="CommitFromUser"/>).
/// </summary>
public partial class SteppedValueEditor : UserControl
{
    /// <summary>Identifies the <see cref="Value"/> dependency property. Two-way by default.</summary>
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value),
        typeof(double),
        typeof(SteppedValueEditor),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

    /// <summary>Identifies the <see cref="Minimum"/> dependency property.</summary>
    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum),
        typeof(int),
        typeof(SteppedValueEditor),
        new PropertyMetadata(0, OnRangeChanged));

    /// <summary>Identifies the <see cref="Maximum"/> dependency property.</summary>
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum),
        typeof(int),
        typeof(SteppedValueEditor),
        new PropertyMetadata(100, OnRangeChanged));

    /// <summary>Identifies the <see cref="Step"/> dependency property.</summary>
    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(
        nameof(Step),
        typeof(int),
        typeof(SteppedValueEditor),
        new PropertyMetadata(SizeStepPolicy.Step, OnStepChanged));

    /// <summary>Identifies the <see cref="AutomationName"/> dependency property.</summary>
    public static readonly DependencyProperty AutomationNameProperty = DependencyProperty.Register(
        nameof(AutomationName),
        typeof(string),
        typeof(SteppedValueEditor),
        new PropertyMetadata(string.Empty));

    private bool _syncing;

    /// <summary>
    /// Initializes a new instance of the <see cref="SteppedValueEditor"/> class.
    /// </summary>
    public SteppedValueEditor()
    {
        InitializeComponent();

        ValueSlider.Minimum = Minimum;
        ValueSlider.Maximum = Maximum;
        ValueSlider.Step = Step;
        ValueBox.Minimum = Minimum;
        ValueBox.Maximum = Maximum;

        SyncChildrenFromValue();

        ValueSlider.ValueChanged += OnSliderValueChanged;
        ValueBox.ValueCommitted += OnBoxValueCommitted;
    }

    /// <summary>The value this editor shows/edits. Two-way bindable; never written back on load (§5.3).</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>Inclusive lower bound, forwarded to both child controls.</summary>
    public int Minimum
    {
        get => (int)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    /// <summary>Inclusive upper bound, forwarded to both child controls.</summary>
    public int Maximum
    {
        get => (int)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    /// <summary>The fixed step, forwarded to the inner <see cref="SteppedSlider"/>.</summary>
    public int Step
    {
        get => (int)GetValue(StepProperty);
        set => SetValue(StepProperty, value);
    }

    /// <summary>Accessible name applied to both the inner slider and the inner integer box.</summary>
    public string AutomationName
    {
        get => (string)GetValue(AutomationNameProperty);
        set => SetValue(AutomationNameProperty, value);
    }

    /// <summary>
    /// Flushes the inner <see cref="IntegerInputBox"/>'s pending (uncommitted) edit — for a caller (e.g.
    /// <c>PanelCustomizeWindow.OnClosing</c>) that needs a typed-but-not-yet-blurred value applied before
    /// the window actually closes.
    /// </summary>
    public void CommitPendingEdit() => ValueBox.Commit();

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SteppedValueEditor editor) editor.SyncChildrenFromValue();
    }

    /// <summary>
    /// MINOR-1 fix (review round): the Minimum/Maximum assignments below are wrapped in
    /// <see cref="_syncing"/> — <see cref="System.Windows.Controls.Primitives.RangeBase.Minimum"/>/
    /// <see cref="System.Windows.Controls.Primitives.RangeBase.Maximum"/> coerce
    /// <see cref="System.Windows.Controls.Primitives.RangeBase.Value"/> into the new range as a side
    /// effect, which (without this guard) fires <see cref="OnSliderValueChanged"/> and would write a
    /// coercion side effect back through <see cref="CommitFromUser"/> as if the user had edited the value —
    /// exactly the kind of accidental write-on-open §5.3 exists to prevent, latent here only because
    /// today's 3 call sites (<c>PanelCustomizeWindow.xaml</c>/<c>NewPanelDefaultsSection.xaml</c>/
    /// <c>LayoutSnappingSection.xaml</c>) all set <c>Minimum</c>/<c>Maximum</c> as fixed XAML attributes
    /// before <c>Value</c>'s own binding is applied. <see cref="SyncChildrenFromValue"/> afterward restores
    /// both children from the authoritative <see cref="Value"/>, undoing any coercion the range change
    /// itself caused.
    /// </summary>
    private static void OnRangeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not SteppedValueEditor editor) return;

        editor._syncing = true;
        try
        {
            editor.ValueSlider.Minimum = editor.Minimum;
            editor.ValueSlider.Maximum = editor.Maximum;
            editor.ValueBox.Minimum = editor.Minimum;
            editor.ValueBox.Maximum = editor.Maximum;
        }
        finally
        {
            editor._syncing = false;
        }

        editor.SyncChildrenFromValue();
    }

    private static void OnStepChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SteppedValueEditor editor) editor.ValueSlider.Step = editor.Step;
    }

    private void OnSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing) return;

        CommitFromUser(SteppedValue.RoundAndClamp(e.NewValue, Minimum, Maximum));
    }

    private void OnBoxValueCommitted(object? sender, int value) => CommitFromUser(value);

    /// <summary>
    /// The single write-back path for both child controls (§5.3): ignores a "commit" that doesn't
    /// actually change <see cref="Value"/> by at least 0.5 (the sub-integer no-op guard the plan
    /// specifies), then writes through <see cref="Value"/>'s own dependency property — via
    /// <see cref="DependencyObject.SetCurrentValue(DependencyProperty, object)"/> so an existing TwoWay
    /// binding still propagates to the bound ViewModel property without this class needing to touch that
    /// binding directly. The resulting <see cref="OnValueChanged"/> callback then re-syncs both children
    /// under <see cref="_syncing"/>, closing the loop without feeding back into this method again.
    /// </summary>
    private void CommitFromUser(int value)
    {
        if (Math.Abs(value - Value) < 0.5) return;

        SetCurrentValue(ValueProperty, (double)value);
    }

    private void SyncChildrenFromValue()
    {
        _syncing = true;
        try
        {
            ValueSlider.Value = Value;
            ValueBox.Value = Value;
        }
        finally
        {
            _syncing = false;
        }
    }
}
