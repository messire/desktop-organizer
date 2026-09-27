using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using DesktopOrganizer.Tests.Helpers;
using DesktopOrganizer.UI.Controls;

namespace DesktopOrganizer.Tests.Unit.Controls;

/// <summary>
/// Focused unit tests for <see cref="IntegerInputBox"/>'s commit/revert behaviour
/// (docs/PLANS/color-presets-and-size-step.md §5.3, WP-B) — parsing itself is exhaustively covered by
/// <c>IntegerInputParserTests</c>; this class only pins that the control wires typed input/paste/Commit()
/// to the right outcome (raise <see cref="IntegerInputBox.ValueCommitted"/> exactly once, revert the
/// displayed text, or do neither) without ever throwing. Runs on an STA thread (project-wide convention for
/// a live <see cref="System.Windows.DependencyObject"/>). Typed input is simulated by raising a REAL
/// <see cref="TextCompositionEventArgs"/> on <see cref="UIElement.PreviewTextInputEvent"/> — this reaches
/// <see cref="IntegerInputBox"/>'s own protected override (the class-handler mechanism WPF wires up for
/// every <c>UIElement.OnXxx</c> virtual, the same way <see cref="SteppedSliderTests"/>'s subclass reaches
/// protected overrides directly) rather than a plain <c>TextBox.SetCurrentValue(TextBox.TextProperty, ...)</c>,
/// which would bypass the control's own <c>_isDirty</c> tracking entirely and make every commit below a
/// silent no-op.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class IntegerInputBoxTests
{
    /// <summary>A typed, in-range value committed via <see cref="IntegerInputBox.Commit"/> raises <see cref="IntegerInputBox.ValueCommitted"/> exactly once, with the parsed value.</summary>
    [Fact]
    public void Commit_ValidInRangeEdit_RaisesValueCommittedOnce()
    {
        StaTestRunner.Run(() =>
        {
            var box = new IntegerInputBox { Minimum = 16, Maximum = 128, Value = 34 };
            var raised = new List<int>();
            box.ValueCommitted += (_, v) => raised.Add(v);

            TypeText(box, "36");
            box.Commit();

            Assert.Equal([36], raised);
        });
    }

    /// <summary>An out-of-range commit reverts the displayed text to <see cref="IntegerInputBox.Value"/>'s own formatted form and never raises <see cref="IntegerInputBox.ValueCommitted"/> or throws.</summary>
    [Fact]
    public void Commit_OutOfRangeEdit_RevertsTextAndDoesNotRaiseOrThrow()
    {
        StaTestRunner.Run(() =>
        {
            var box = new IntegerInputBox { Minimum = 16, Maximum = 128, Value = 34 };
            var raised = false;
            box.ValueCommitted += (_, _) => raised = true;

            TypeText(box, "200");
            var exception = Record.Exception(box.Commit);

            Assert.Null(exception);
            Assert.False(raised);
            Assert.Equal("34", box.Text);
        });
    }

    /// <summary>Re-committing the exact same value the box already holds does not re-raise <see cref="IntegerInputBox.ValueCommitted"/>.</summary>
    [Fact]
    public void Commit_SameValueRetyped_DoesNotRaise()
    {
        StaTestRunner.Run(() =>
        {
            var box = new IntegerInputBox { Minimum = 16, Maximum = 128, Value = 34 };
            var raiseCount = 0;
            box.ValueCommitted += (_, _) => raiseCount++;

            TypeText(box, "34");
            box.Commit();

            Assert.Equal(0, raiseCount);
        });
    }

    /// <summary>A never-touched box's Commit() is a no-op — the legacy-fractional-value guard (§5.3): a displayed <c>32.0101 -> "32"</c> value must never be rewritten just because Commit() ran.</summary>
    [Fact]
    public void Commit_NeverTouched_IsNoOp()
    {
        StaTestRunner.Run(() =>
        {
            var box = new IntegerInputBox { Minimum = 16, Maximum = 128, Value = 32.0101 };
            var raised = false;
            box.ValueCommitted += (_, _) => raised = true;

            Assert.Equal("32", box.Text);

            box.Commit();

            Assert.False(raised);
            Assert.Equal("32", box.Text);
        });
    }

    /// <summary>A non-digit paste is rejected (cancelled) and never marks the box dirty.</summary>
    [Fact]
    public void Paste_NonDigitText_IsRejectedAndNeverRaisesOnCommit()
    {
        StaTestRunner.Run(() =>
        {
            var box = new IntegerInputBox { Minimum = 16, Maximum = 128, Value = 34 };
            var raised = false;
            box.ValueCommitted += (_, _) => raised = true;

            var dataObject = new DataObject(DataFormats.Text, "12a");
            var args = new DataObjectPastingEventArgs(dataObject, false, DataFormats.Text)
            {
                RoutedEvent = DataObject.PastingEvent,
                Source = box,
            };
            box.RaiseEvent(args);

            Assert.True(args.CommandCancelled);

            box.Commit();

            Assert.False(raised);
        });
    }

    /// <summary>A non-digit keystroke never reaches <see cref="TextBox.Text"/> and never marks the box dirty.</summary>
    [Fact]
    public void TypedNonDigitCharacter_IsRejectedAndNeverRaisesOnCommit()
    {
        StaTestRunner.Run(() =>
        {
            var box = new IntegerInputBox { Minimum = 16, Maximum = 128, Value = 34 };
            var raised = false;
            box.ValueCommitted += (_, _) => raised = true;

            TypeText(box, "a");
            box.Commit();

            Assert.False(raised);
            Assert.Equal("34", box.Text);
        });
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    // MAJOR-1 fix (review round): Backspace/Delete/Ctrl+X/Ctrl+Z never went through OnPreviewTextInput,
    // so the old flag-based _isDirty was never set for them — "128" -> Backspace -> "12" -> Enter used to
    // silently keep committing 128. IsDirty is now a pure Text-vs-_syncedText comparison, so it no longer
    // matters which WPF mechanism changed Text. EditingCommands.Backspace is executed directly (the
    // routed-command WPF itself binds Backspace/Delete to on a real TextBox), the same "reach the real
    // editing pipeline, not a bypassing SetCurrentValue" spirit TypeText's own remarks document above.
    // ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Typed "128", Backspace once -> "12", Enter commits 12 (MAJOR-1's own motivating example).</summary>
    [Fact]
    public void Commit_BackspaceOnly_CommitsNewValue()
    {
        StaTestRunner.Run(() =>
        {
            var box = new IntegerInputBox { Minimum = 0, Maximum = 999, Value = 128 };
            box.ApplyTemplate();
            var raised = new List<int>();
            box.ValueCommitted += (_, v) => raised.Add(v);

            box.CaretIndex = box.Text.Length;
            EditingCommands.Backspace.Execute(null, box);

            Assert.Equal("12", box.Text);

            box.Commit();

            Assert.Equal([12], raised);
        });
    }

    /// <summary>Select-all + Backspace empties the text; Enter reverts to the last committed value (128) instead of throwing or silently keeping stale text.</summary>
    [Fact]
    public void Commit_SelectAllBackspace_RevertsToLastValid()
    {
        StaTestRunner.Run(() =>
        {
            var box = new IntegerInputBox { Minimum = 0, Maximum = 999, Value = 128 };
            box.ApplyTemplate();
            var raised = false;
            box.ValueCommitted += (_, _) => raised = true;

            box.SelectAll();
            EditingCommands.Backspace.Execute(null, box);

            Assert.Equal(string.Empty, box.Text);

            var exception = Record.Exception(box.Commit);

            Assert.Null(exception);
            Assert.False(raised);
            Assert.Equal("128", box.Text);
        });
    }

    // --- MINOR-6 fix (review round): Escape only handled when dirty; paste is trimmed first -------------

    /// <summary>Escape on an untouched (never-dirtied) box is NOT handled — it must bubble normally (e.g. to close a hosting dialog) instead of always being swallowed.</summary>
    [Fact]
    public void Escape_WhenNotDirty_IsNotHandled()
    {
        StaTestRunner.Run(() =>
        {
            var box = new TestableIntegerInputBox { Minimum = 16, Maximum = 128, Value = 34 };

            var args = BuildKeyEventArgs(Key.Escape);
            box.InvokeOnPreviewKeyDown(args);

            Assert.False(args.Handled);
            Assert.Equal("34", box.Text);
        });
    }

    /// <summary>Escape on a dirtied box IS handled and reverts the pending edit — the pre-existing behaviour for an actual pending edit is unchanged.</summary>
    [Fact]
    public void Escape_WhenDirty_RevertsPendingEditAndIsHandled()
    {
        StaTestRunner.Run(() =>
        {
            var box = new TestableIntegerInputBox { Minimum = 16, Maximum = 128, Value = 34 };
            TypeText(box, "99");
            Assert.Equal("99", box.Text);

            var args = BuildKeyEventArgs(Key.Escape);
            box.InvokeOnPreviewKeyDown(args);

            Assert.True(args.Handled);
            Assert.Equal("34", box.Text);
        });
    }

    /// <summary>A pasted fragment with trailing whitespace (a common clipboard artifact, e.g. copying a single line out of another app) is trimmed and accepted, not rejected outright.</summary>
    [Fact]
    public void Paste_TrailingWhitespace_IsTrimmedAndAccepted()
    {
        StaTestRunner.Run(() =>
        {
            var box = new IntegerInputBox { Minimum = 16, Maximum = 128, Value = 34 };

            var dataObject = new DataObject(DataFormats.Text, "40\r\n");
            var args = new DataObjectPastingEventArgs(dataObject, false, DataFormats.Text)
            {
                RoutedEvent = DataObject.PastingEvent,
                Source = box,
            };
            box.RaiseEvent(args);

            Assert.False(args.CommandCancelled);
            Assert.Equal("40", args.DataObject.GetData(DataFormats.Text));
        });
    }

    /// <summary>Constructs a real <see cref="KeyEventArgs"/> for <paramref name="key"/> — needs a <see cref="PresentationSource"/> instance (the constructor throws on <see langword="null"/>), satisfied here by <see cref="FakePresentationSource"/> since <see cref="IntegerInputBox.OnPreviewKeyDown"/> never reads it.</summary>
    /// <param name="key">The key to build event args for.</param>
    private static KeyEventArgs BuildKeyEventArgs(Key key) =>
        new(InputManager.Current.PrimaryKeyboardDevice, new FakePresentationSource(), 0, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        };

    /// <summary>Exposes <see cref="IntegerInputBox.OnPreviewKeyDown"/> for direct invocation — same "testable subclass opens protected methods" convention <see cref="SteppedSliderTests"/> already uses.</summary>
    private sealed class TestableIntegerInputBox : IntegerInputBox
    {
        public void InvokeOnPreviewKeyDown(KeyEventArgs e) => OnPreviewKeyDown(e);
    }

    /// <summary>Minimal <see cref="PresentationSource"/> stub — only exists to satisfy <see cref="KeyEventArgs"/>'s constructor; none of its members are ever read by the code under test.</summary>
    private sealed class FakePresentationSource : PresentationSource
    {
        public override Visual RootVisual { get; set; } = null!;

        public override bool IsDisposed => false;

        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }

    /// <summary>
    /// Simulates typing <paramref name="text"/> into <paramref name="box"/> via a real
    /// <see cref="TextCompositionEventArgs"/> on <see cref="UIElement.PreviewTextInputEvent"/> — reaches
    /// <see cref="IntegerInputBox.OnPreviewTextInput"/>'s protected override (which marks the box dirty
    /// and, for an allowed chunk, calls <c>base.OnPreviewTextInput</c> to actually insert the text), unlike
    /// a plain <c>TextBox.SetCurrentValue(TextBox.TextProperty, ...)</c>, which would leave
    /// <c>_isDirty</c> <see langword="false"/>.
    /// </summary>
    private static void TypeText(TextBox box, string text)
    {
        // ApplyTemplate materializes the TextBox's default ControlTemplate — without it, a never-measured/
        // arranged TextBox has no internal TextBoxView/TextEditor yet, and SelectAll()/typed-input handling
        // below silently no-ops. Select-all first so the composition REPLACES the box's current text
        // (matching how a user would retype a value) instead of inserting at whatever caret position an
        // untouched TextBox happens to default to.
        box.ApplyTemplate();
        box.SelectAll();

        var composition = new TextComposition(InputManager.Current, box, text);

        var previewArgs = new TextCompositionEventArgs(InputManager.Current.PrimaryKeyboardDevice, composition)
        {
            RoutedEvent = UIElement.PreviewTextInputEvent,
        };
        box.RaiseEvent(previewArgs);
        if (previewArgs.Handled) return;

        // WPF's real input pipeline promotes an unhandled Preview (tunnel) event to its bubble
        // counterpart itself (InputManager's own post-processing); a synthetic RaiseEvent call on just
        // the Preview event does not — TextEditor's actual insertion logic is wired to the BUBBLING
        // TextInputEvent, not the tunnelling PreviewTextInputEvent this control's own override observes.
        var bubbleArgs = new TextCompositionEventArgs(InputManager.Current.PrimaryKeyboardDevice, composition)
        {
            RoutedEvent = UIElement.TextInputEvent,
        };
        box.RaiseEvent(bubbleArgs);
    }
}
