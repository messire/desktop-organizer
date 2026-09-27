using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using DesktopOrganizer.Tests.Helpers;
using DesktopOrganizer.UI.Controls;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.Tests.Unit.Controls;

/// <summary>
/// Integration-level unit tests for <see cref="SteppedValueEditor"/>'s own anti-loop wiring
/// (docs/PLANS/color-presets-and-size-step.md §5.3, WP-C T-13) — <see cref="SteppedSlider"/>'s stepping
/// decisions are already exhaustively covered by <c>SteppedSliderTests</c> and <see cref="IntegerInputBox"/>'s
/// parsing/commit rules by <c>IntegerInputBoxTests</c>; this class only pins that
/// <see cref="SteppedValueEditor"/> itself (a) never writes a bound <see cref="SteppedValueEditor.Value"/>
/// back through a real <see cref="Binding"/> on construction/<c>Loaded</c>/layout — even for an
/// out-of-step or out-of-range starting value — and (b) forwards exactly one write per user edit through
/// either child control, via its single <c>CommitFromUser</c> path.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class SteppedValueEditorTests
{
    /// <summary>
    /// Verifies that binding <see cref="SteppedValueEditor.Value"/> (TwoWay) to a source already holding
    /// 34 (in-step), 32.0101 (a legacy fractional, out-of-step value), or 200 (out of the icon-size range)
    /// never writes back to that source — not on construction, not once the editor is shown
    /// (<c>Loaded</c>), and not after a full layout pass (§5.3's "no write-back on load" rule).
    /// </summary>
    /// <param name="startingValue">The bound source's starting value.</param>
    [Theory]
    [InlineData(34.0)]
    [InlineData(32.0101)]
    [InlineData(200.0)]
    public void BindingToOutOfStepOrOutOfRangeValue_NeverWritesBackOnLoadOrLayout(double startingValue)
    {
        StaTestRunner.Run(() =>
        {
            WpfApplicationHost.EnsureApplication();
            var source = new RecordingSource(startingValue);
            var editor = BuildIconSizeEditor();
            Bind(editor, source);

            var window = BuildOffscreenWindow(editor);
            window.Show();
            window.UpdateLayout();

            Assert.Equal(0, source.WriteCount);

            window.Close();
        });
    }

    /// <summary>
    /// Verifies that the inner <see cref="SteppedSlider"/> stepping up from 34 (icon-size range) writes
    /// the bound source exactly once, to 40 — not once per intermediate <see cref="RangeBase.ValueChanged"/>
    /// tick, and not the slider's own raw, unsnapped value.
    /// </summary>
    [Fact]
    public void SliderIncreaseSmallFromThirtyFour_WritesSourceExactlyOnce_ToForty()
    {
        StaTestRunner.Run(() =>
        {
            WpfApplicationHost.EnsureApplication();
            var source = new RecordingSource(34);
            var editor = BuildIconSizeEditor();
            Bind(editor, source);

            InvokeProtected(editor.ValueSlider, "OnIncreaseSmall");

            Assert.Equal(40, source.Value);
            Assert.Equal(1, source.WriteCount);
        });
    }

    /// <summary>
    /// Verifies that typing "36" into the inner <see cref="IntegerInputBox"/> and flushing it via
    /// <see cref="SteppedValueEditor.CommitPendingEdit"/> (the same call <c>PanelCustomizeWindow.OnClosing</c>
    /// makes) writes the bound source to 36.
    /// </summary>
    [Fact]
    public void TypedBoxEditFlushedByCommitPendingEdit_WritesSourceToTypedValue()
    {
        StaTestRunner.Run(() =>
        {
            WpfApplicationHost.EnsureApplication();
            var source = new RecordingSource(34);
            var editor = BuildIconSizeEditor();
            Bind(editor, source);

            var window = BuildOffscreenWindow(editor);
            window.Show();

            TypeText(editor.ValueBox, "36");
            editor.CommitPendingEdit();

            Assert.Equal(36, source.Value);

            window.Close();
        });
    }

    /// <summary>
    /// Verifies that an external ("UIA <c>RangeValuePattern.SetValue</c>") write straight to the inner
    /// slider's own <see cref="RangeBase.Value"/> — bypassing both the slider's stepping overrides and the
    /// box entirely, exactly like a Narrator/UI-Automation client would — still rounds to the nearest
    /// integer and commits through to the bound source.
    /// </summary>
    [Fact]
    public void ExternalRangeValueWriteToInnerSlider_RoundsAndCommitsToSource()
    {
        StaTestRunner.Run(() =>
        {
            WpfApplicationHost.EnsureApplication();
            var source = new RecordingSource(34);
            var editor = BuildIconSizeEditor();
            Bind(editor, source);

            editor.ValueSlider.Value = 37.4;

            Assert.Equal(37, source.Value);
        });
    }

    /// <summary>
    /// Verifies that re-committing the exact value <see cref="SteppedValueEditor.Value"/> already holds —
    /// the guard <c>CommitFromUser</c> itself applies, independent of either child control's own
    /// unchanged-value guard — never writes the bound source again.
    /// </summary>
    [Fact]
    public void RecommittingTheSameValue_NeverWritesSourceAgain()
    {
        StaTestRunner.Run(() =>
        {
            WpfApplicationHost.EnsureApplication();
            var source = new RecordingSource(34);
            var editor = BuildIconSizeEditor();
            Bind(editor, source);

            InvokeProtected(editor.ValueSlider, "OnIncreaseSmall");
            Assert.Equal(1, source.WriteCount); // sanity: the first edit above did write once, to 40.

            InvokeCommitFromUser(editor, 40); // re-commit the SAME value the editor already holds.

            Assert.Equal(1, source.WriteCount);
        });
    }

    /// <summary>
    /// MINOR-1 fix (review round): verifies that narrowing <see cref="SteppedValueEditor.Maximum"/> AFTER
    /// <see cref="SteppedValueEditor.Value"/> is already set to something now outside the new range never
    /// writes the bound source — <see cref="System.Windows.Controls.Primitives.RangeBase"/>'s own coercion
    /// of the inner <see cref="SteppedSlider"/>'s <c>Value</c> (34 -> clamped to the new Maximum, 20) used
    /// to fire <see cref="System.Windows.Controls.Primitives.RangeBase.ValueChanged"/> OUTSIDE the
    /// <c>_syncing</c> guard, which <see cref="SteppedValueEditor"/>'s own <c>OnSliderValueChanged</c> would
    /// then treat as a real user edit and commit back through to the bound source.
    /// </summary>
    [Fact]
    public void ChangingMaximumAfterValueIsSet_NeverWritesSource()
    {
        StaTestRunner.Run(() =>
        {
            WpfApplicationHost.EnsureApplication();
            var source = new RecordingSource(34);
            var editor = BuildIconSizeEditor();
            Bind(editor, source);

            // Narrow the range so the slider's own Value (34) would fall OUTSIDE it if the Minimum/Maximum
            // assignments in OnRangeChanged were not guarded by _syncing.
            editor.Maximum = 20;

            Assert.Equal(0, source.WriteCount);
            Assert.Equal(34, source.Value);
            Assert.Equal(34, editor.Value);
        });
    }

    private static SteppedValueEditor BuildIconSizeEditor() => new()
    {
        Minimum = SizeStepPolicy.IconSizeMinimum,
        Maximum = SizeStepPolicy.IconSizeMaximum,
    };

    private static void Bind(SteppedValueEditor editor, RecordingSource source) =>
        BindingOperations.SetBinding(
            editor,
            SteppedValueEditor.ValueProperty,
            new Binding(nameof(RecordingSource.Value)) { Source = source, Mode = BindingMode.TwoWay });

    /// <summary>
    /// Hosts <paramref name="editor"/> in a real, off-screen, non-activating <see cref="Window"/> — same
    /// precaution as <c>PanelCustomizeWindowClosingTests</c>' own harness — so nothing visibly flashes or
    /// steals focus during a <c>dotnet test</c> run.
    /// </summary>
    private static Window BuildOffscreenWindow(UIElement editor) => new()
    {
        Content = editor,
        Left = -5000,
        Top = -5000,
        Width = 300,
        Height = 80,
        ShowActivated = false,
    };

    /// <summary>
    /// Invokes a protected, parameterless instance method by reflection — the same technique
    /// <c>SteppedSliderTests</c>' own <c>TestableSteppedSlider</c> wrapper exposes via public methods,
    /// used directly here since <see cref="SteppedValueEditor.ValueSlider"/> is a plain, XAML-instantiated
    /// <see cref="SteppedSlider"/> this test cannot subclass.
    /// </summary>
    private static void InvokeProtected(object target, string methodName)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingMethodException(target.GetType().FullName, methodName);
        method.Invoke(target, null);
    }

    /// <summary>
    /// Invokes <see cref="SteppedValueEditor"/>'s own private <c>CommitFromUser(int)</c> directly — the
    /// single write-back path both child controls funnel through (§5.3) — since neither child control has
    /// a public, no-op-triggering way to re-request the SAME value the editor already holds (both
    /// <see cref="IntegerInputBox.Commit"/> and a same-valued <see cref="RangeBase.Value"/> assignment are
    /// themselves silently no-ops before ever reaching this method, per their own dedicated test classes).
    /// </summary>
    private static void InvokeCommitFromUser(SteppedValueEditor editor, int value)
    {
        var method = typeof(SteppedValueEditor).GetMethod("CommitFromUser", BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(editor, [value]);
    }

    /// <summary>
    /// Simulates typing <paramref name="text"/> into <paramref name="box"/> via a real
    /// <see cref="System.Windows.Input.TextCompositionEventArgs"/> — same technique as
    /// <c>IntegerInputBoxTests.TypeText</c> (duplicated here rather than shared across test classes, per
    /// this codebase's existing per-fixture convention for small STA input helpers).
    /// </summary>
    private static void TypeText(TextBox box, string text)
    {
        box.ApplyTemplate();
        box.SelectAll();

        var composition = new System.Windows.Input.TextComposition(System.Windows.Input.InputManager.Current, box, text);

        var previewArgs = new System.Windows.Input.TextCompositionEventArgs(System.Windows.Input.InputManager.Current.PrimaryKeyboardDevice, composition)
        {
            RoutedEvent = UIElement.PreviewTextInputEvent,
        };
        box.RaiseEvent(previewArgs);
        if (previewArgs.Handled) return;

        var bubbleArgs = new System.Windows.Input.TextCompositionEventArgs(System.Windows.Input.InputManager.Current.PrimaryKeyboardDevice, composition)
        {
            RoutedEvent = UIElement.TextInputEvent,
        };
        box.RaiseEvent(bubbleArgs);
    }

    /// <summary>A minimal <see cref="INotifyPropertyChanged"/> binding source that counts every write.</summary>
    private sealed class RecordingSource : INotifyPropertyChanged
    {
        private double _value;

        /// <summary>
        /// Initializes a new instance of the <see cref="RecordingSource"/> class, seeding the field
        /// DIRECTLY rather than through <see cref="Value"/>'s own setter — so seeding the starting value
        /// this test's binding will pull from is never itself mistaken for a "write-back"
        /// (<see cref="WriteCount"/> starts at exactly 0, counting only edits made AFTER construction).
        /// </summary>
        /// <param name="initialValue">The value this source starts out holding.</param>
        public RecordingSource(double initialValue) => _value = initialValue;

        /// <inheritdoc />
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>The bound value — every SET (a write-back from the editor) increments <see cref="WriteCount"/>.</summary>
        public double Value
        {
            get => _value;
            set
            {
                _value = value;
                WriteCount++;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
            }
        }

        /// <summary>How many times <see cref="Value"/> has been written since this instance was created.</summary>
        public int WriteCount { get; private set; }
    }
}
