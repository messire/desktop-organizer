using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Localization;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.UI.Controls;

/// <summary>
/// A <see cref="TextBox"/> restricted to a plain non-negative whole number in <c>[Minimum, Maximum]</c>
/// (docs/PLANS/color-presets-and-size-step.md §5.3) — the manual-entry half of <c>SteppedValueEditor</c>.
/// Every parsing/formatting decision routes through the pure, WPF-free <see cref="IntegerInputParser"/> so
/// keyboard filtering, paste filtering, and commit/format all agree on the exact same rules. Commits on
/// <c>Enter</c>/<c>LostKeyboardFocus</c>; <c>Escape</c> reverts the pending edit (only when dirty — MINOR-6
/// fix, review round); an invalid or unchanged commit never writes <see cref="Value"/> and never throws.
/// </summary>
/// <remarks>
/// MAJOR-1 fix (review round): dirtiness (<see cref="IsDirty"/>) is now a pure TEXT COMPARISON —
/// <c>Text != _syncedText</c>, where <see cref="RefreshText(double)"/> is the only place that updates
/// <c>_syncedText</c> — instead of a flag set only from <see cref="OnPreviewTextInput"/>/paste. The old
/// flag-based approach missed every edit that changes <see cref="TextBox.Text"/> WITHOUT going through
/// <see cref="OnPreviewTextInput"/> (Backspace, Delete, Ctrl+X, Ctrl+Z, and IME composition) — e.g. typing
/// <c>128</c>, pressing Backspace down to <c>12</c>, then Enter, used to silently keep committing <c>128</c>.
/// A text-comparison-based <see cref="IsDirty"/> catches every one of those paths uniformly, because it
/// does not care WHICH mechanism changed <see cref="TextBox.Text"/> — only whether it now differs from the
/// last synchronized value. IME composition is also disabled outright (<see cref="InputMethod.SetIsInputMethodEnabled(DependencyObject, bool)"/>)
/// in the constructor: this is a plain ASCII-digit control, so a candidate window has nothing valid to offer.
/// </remarks>
public class IntegerInputBox : TextBox
{
    /// <summary>Identifies the <see cref="Value"/> dependency property.</summary>
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value),
        typeof(double),
        typeof(IntegerInputBox),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

    /// <summary>Identifies the <see cref="Minimum"/> dependency property.</summary>
    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum),
        typeof(int),
        typeof(IntegerInputBox),
        new PropertyMetadata(0, OnRangeChanged));

    /// <summary>Identifies the <see cref="Maximum"/> dependency property.</summary>
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum),
        typeof(int),
        typeof(IntegerInputBox),
        new PropertyMetadata(int.MaxValue, OnRangeChanged));

    /// <summary>
    /// Raised after a valid, in-range, actually-changed value is committed (Enter/LostFocus) — the only
    /// path <c>SteppedValueEditor</c> reacts to; an invalid/unchanged/reverted edit never raises this.
    /// </summary>
    public event EventHandler<int>? ValueCommitted;

    private string _syncedText = string.Empty;
    private ILocalizationService? _subscribedLocalization;

    /// <summary>
    /// Initializes a new instance of the <see cref="IntegerInputBox"/> class.
    /// </summary>
    public IntegerInputBox()
    {
        MaxLength = 3;
        HorizontalContentAlignment = HorizontalAlignment.Right;
        InputScope = BuildNumberInputScope();

        // MAJOR-1 fix: this is a plain ASCII-digit control — an IME candidate window has nothing valid to
        // compose here, and disabling it removes a whole class of composition edits IsDirty would
        // otherwise need to track indirectly.
        InputMethod.SetIsInputMethodEnabled(this, false);

        DataObject.AddPastingHandler(this, OnPaste);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        RefreshText();
        RefreshHelpText();
    }

    /// <summary>The current value. Read-back into the text unless the user has an in-progress, uncommitted edit.</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>Inclusive lower bound accepted by <see cref="IntegerInputParser.TryParse"/>.</summary>
    public int Minimum
    {
        get => (int)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    /// <summary>Inclusive upper bound accepted by <see cref="IntegerInputParser.TryParse"/>.</summary>
    public int Maximum
    {
        get => (int)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    /// <summary>
    /// Whether <see cref="TextBox.Text"/> has changed since the last time it was synchronized from
    /// <see cref="Value"/> (<see cref="RefreshText(double)"/>) — MAJOR-1 fix: a pure text comparison, so
    /// EVERY mechanism that can change <see cref="TextBox.Text"/> (typed input, Backspace/Delete, Ctrl+X,
    /// Ctrl+Z, paste, IME) is caught uniformly, not just the ones this control explicitly instruments.
    /// </summary>
    private bool IsDirty => Text != _syncedText;

    /// <summary>
    /// Commits the current text as <see cref="Value"/> (raising <see cref="ValueCommitted"/>) when it
    /// parses to a valid, in-range, actually-new integer. A no-op when the text was never touched since
    /// the last synchronization (<see cref="IsDirty"/>) — so a displayed legacy fractional value (e.g.
    /// <c>32.0101</c> shown as <c>"32"</c>) is never rewritten just because focus passed through. Any
    /// parse failure reverts the text to <see cref="Value"/>'s own formatted form, without throwing or
    /// changing <see cref="Value"/>.
    /// </summary>
    public void Commit()
    {
        if (!IsDirty) return;

        if (IntegerInputParser.TryParse(Text, Minimum, Maximum, out var parsed) != IntegerInputError.None)
        {
            RefreshText();
            return;
        }

        RefreshText(parsed);

        if (Math.Abs(parsed - Value) < 0.5) return;

        ValueCommitted?.Invoke(this, parsed);
    }

    /// <inheritdoc />
    protected override void OnPreviewTextInput(TextCompositionEventArgs e)
    {
        if (!IntegerInputParser.IsAllowedInputChunk(e.Text))
        {
            e.Handled = true;
            return;
        }

        base.OnPreviewTextInput(e);
    }

    /// <inheritdoc />
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space:
                // WPF never routes Space through PreviewTextInput for a TextBox — it must be rejected here.
                e.Handled = true;
                return;
            case Key.Enter:
                Commit();
                SelectAll();
                e.Handled = true;
                return;
            case Key.Escape:
                // MINOR-6 fix (review round): only handle Escape when there is a pending edit to revert —
                // otherwise let it bubble normally (e.g. to close a hosting dialog) instead of always
                // swallowing the key.
                if (!IsDirty) break;

                RevertPendingEdit();
                e.Handled = true;
                return;
        }

        base.OnPreviewKeyDown(e);
    }

    /// <inheritdoc />
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        Commit();
        base.OnLostKeyboardFocus(e);
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not IntegerInputBox box) return;

        // Never overwrite an in-progress, uncommitted edit (§5.3) — the box's own Commit()/RevertPendingEdit()
        // are the only paths that resynchronize _syncedText (clearing IsDirty) while focused.
        if (box.IsKeyboardFocusWithin && box.IsDirty) return;

        box.RefreshText();
    }

    private static void OnRangeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is IntegerInputBox box) box.RefreshHelpText();
    }

    private static InputScope BuildNumberInputScope()
    {
        var scope = new InputScope();
        scope.Names.Add(new InputScopeName(InputScopeNameValue.Number));
        return scope;
    }

    private void RevertPendingEdit() => RefreshText();

    private void OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        // MINOR-6 fix (review round): trim before checking/pasting, so e.g. "40\r\n" (a common clipboard
        // artifact from copying a single line out of another app) is accepted instead of rejected outright.
        var text = (e.DataObject.GetData(DataFormats.Text) as string)?.Trim();
        if (!IntegerInputParser.IsAllowedInputChunk(text))
        {
            e.CancelCommand();
            return;
        }

        e.DataObject = new DataObject(DataFormats.Text, text);
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        RefreshHelpText();

        // LocalizationSource.Current is the same process-wide ambient instance every {loc:...} markup
        // extension already resolves against (Localization/LocalizationSource.cs) — this control has no
        // DI-injected dependency of its own (it is constructed by the WPF XAML parser, not the
        // container), so it reads the same ambient singleton rather than inventing a second source of
        // truth for "the current UI culture". Subscribed/unsubscribed in Loaded/Unloaded so this control
        // never outlives its own visual tree presence as a stale subscriber.
        _subscribedLocalization = LocalizationSource.Current;
        _subscribedLocalization.CultureChanged += OnCultureChanged;
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        if (_subscribedLocalization is not null)
        {
            _subscribedLocalization.CultureChanged -= OnCultureChanged;
            _subscribedLocalization = null;
        }
    }

    private void OnCultureChanged(object? sender, CultureChangedEventArgs e) => RefreshHelpText();

    private void RefreshHelpText()
    {
        var text = LocalizationSource.Current.Format("Common_IntegerRange_Format", Minimum, Maximum);

        ToolTip = text;
        AutomationProperties.SetHelpText(this, text);
    }

    private void RefreshText() => RefreshText(Value);

    private void RefreshText(double value)
    {
        var formatted = IntegerInputParser.FormatForDisplay(value);
        _syncedText = formatted;
        if (Text != formatted) SetCurrentValue(TextProperty, formatted);
    }
}
