using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Tests.Helpers;
using DesktopOrganizer.UI.Services;
using DesktopOrganizer.UI.Theming;
using DesktopOrganizer.UI.Views.Dialogs;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Views.Dialogs;

/// <summary>
/// STA unit tests for <see cref="AppDialogWindow"/> (design-system-1x.md §2.8, ADR-047 D-9, T-10).
/// </summary>
/// <remarks>
/// Deliberately never calls <see cref="Window.ShowDialog"/>: WPF only allows setting
/// <see cref="Window.DialogResult"/> (what <c>PrimaryButton_Click</c>/<c>SecondaryButton_Click</c> do)
/// while inside a real, blocking <c>ShowDialog()</c> call, which a synchronous unit test cannot drive
/// without a second thread. These tests instead verify the WIRING that drives Enter/Escape/Click
/// behavior — <see cref="System.Windows.Controls.Button.IsDefault"/>/<see cref="System.Windows.Controls.Button.IsCancel"/>,
/// content, visibility — which is what <see cref="AppDialogWindow"/>'s own constructor is actually
/// responsible for computing from an <see cref="AppDialogRequest"/>; <c>DialogPresenter</c> (untested here)
/// owns the real modal <c>ShowDialog()</c> call, and <c>ConfirmationServiceTests</c>/<c>InputDialogServiceTests</c>
/// already cover the request-building logic end-to-end through a fake <see cref="IDialogPresenter"/>.
/// </remarks>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class AppDialogWindowTests
{
    private static readonly Mock<IAppThemeService> AppThemeServiceMock = BuildAppThemeServiceMock();
    private static readonly Mock<IWindowFrameTheming> WindowFrameThemingMock = BuildWindowFrameThemingMock();
    private static readonly ILocalizationService Localization = TestLocalization.English;

    /// <summary><see cref="MessageBoxImage.Question"/> defaults Enter to the primary button; the secondary button carries <c>IsCancel</c>.</summary>
    [Fact]
    public void Confirm_Question_PrimaryIsDefault_SecondaryIsCancel()
    {
        StaTestRunner.Run(() =>
        {
            WpfApplicationHost.EnsureApplication();
            var window = Build(ConfirmRequest(MessageBoxImage.Question, primaryIsDefault: true));

            try
            {
                Assert.True(window.PrimaryButton.IsDefault);
                Assert.False(window.SecondaryButton.IsDefault);
                Assert.True(window.SecondaryButton.IsCancel);
                Assert.Equal("Yes", window.PrimaryButton.Content);
                Assert.Equal("No", window.SecondaryButton.Content);
                Assert.Equal(Visibility.Visible, window.SecondaryButton.Visibility);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// ADR-047 D-9/R-12: <see cref="MessageBoxImage.Warning"/> defaults Enter to the SECONDARY ("Нет")
    /// button — the visual primary ("Да") stays exactly where it was, only which one Enter targets flips.
    /// </summary>
    [Fact]
    public void Confirm_Warning_SecondaryIsDefault_PrimaryStaysVisuallyPrimary()
    {
        StaTestRunner.Run(() =>
        {
            WpfApplicationHost.EnsureApplication();
            var window = Build(ConfirmRequest(MessageBoxImage.Warning, primaryIsDefault: false));

            try
            {
                Assert.False(window.PrimaryButton.IsDefault);
                Assert.True(window.SecondaryButton.IsDefault);
                Assert.True(window.SecondaryButton.IsCancel);
                Assert.Equal("Yes", window.PrimaryButton.Content); // still the visual primary — only the Enter target changed
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>A single-button (Notify) request collapses the secondary button and makes the primary one double as <c>IsCancel</c> too.</summary>
    [Fact]
    public void Notify_SingleButton_SecondaryCollapsed_PrimaryIsAlsoCancel()
    {
        StaTestRunner.Run(() =>
        {
            WpfApplicationHost.EnsureApplication();
            var request = new AppDialogRequest("Recycle Bin", "Some items could not be moved.", MessageBoxImage.Warning, "OK", null, true);
            var window = Build(request);

            try
            {
                Assert.Equal(Visibility.Collapsed, window.SecondaryButton.Visibility);
                Assert.True(window.PrimaryButton.IsDefault);
                Assert.True(window.PrimaryButton.IsCancel);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>Each severity resolves to its own glyph and accessible name; <see cref="MessageBoxImage.None"/> hides the glyph entirely.</summary>
    /// <param name="icon">The severity under test.</param>
    /// <param name="expectedName">The accessible name <see cref="AppDialogWindow"/> should resolve for it.</param>
    [Theory]
    [InlineData(MessageBoxImage.Question, "Question")]
    [InlineData(MessageBoxImage.Warning, "Warning")]
    [InlineData(MessageBoxImage.Error, "Error")]
    [InlineData(MessageBoxImage.Information, "Information")]
    public void Severity_SetsGlyphAndAccessibleName(MessageBoxImage icon, string expectedName)
    {
        StaTestRunner.Run(() =>
        {
            WpfApplicationHost.EnsureApplication();
            var window = Build(ConfirmRequest(icon, primaryIsDefault: true));

            try
            {
                Assert.Equal(Visibility.Visible, window.SeverityGlyphText.Visibility);
                Assert.False(string.IsNullOrEmpty(window.SeverityGlyphText.Text));
                Assert.Equal(expectedName, AutomationProperties.GetName(window.SeverityGlyphText));
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary><see cref="MessageBoxImage.None"/> (the input-mode prompt) collapses the severity glyph entirely.</summary>
    [Fact]
    public void NoIcon_CollapsesGlyph()
    {
        StaTestRunner.Run(() =>
        {
            WpfApplicationHost.EnsureApplication();
            var request = new AppDialogRequest("Save snapshot as…", "Snapshot name:", MessageBoxImage.None, "OK", "Cancel", true, InputText: "seed");
            var window = Build(request);

            try
            {
                Assert.Equal(Visibility.Collapsed, window.SeverityGlyphText.Visibility);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>Input mode shows the <see cref="System.Windows.Controls.TextBox"/> pre-filled and pre-selected with the seed text.</summary>
    [Fact]
    public void InputMode_ShowsInputRow_PreFilledAndSelected()
    {
        StaTestRunner.Run(() =>
        {
            WpfApplicationHost.EnsureApplication();
            var request = new AppDialogRequest("Title", "Prompt", MessageBoxImage.None, "OK", "Cancel", true, InputText: "seed-name");
            var window = Build(request);

            try
            {
                Assert.Equal(Visibility.Visible, window.InputRow.Visibility);
                Assert.Equal("seed-name", window.InputBox.Text);
                Assert.Equal("seed-name", window.InputBox.SelectedText); // SelectAll() in the constructor
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>A non-input-mode request (Confirm/Notify) leaves the input row Collapsed.</summary>
    [Fact]
    public void NonInputMode_InputRowCollapsed()
    {
        StaTestRunner.Run(() =>
        {
            WpfApplicationHost.EnsureApplication();
            var window = Build(ConfirmRequest(MessageBoxImage.Question, primaryIsDefault: true));

            try
            {
                Assert.Equal(Visibility.Collapsed, window.InputRow.Visibility);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>Blank input disables the primary button with no inline message (mirrors the pre-6.3 InputDialog's own "OK disabled while empty" behavior).</summary>
    [Fact]
    public void InputMode_BlankText_DisablesPrimary_NoInlineMessage()
    {
        StaTestRunner.Run(() =>
        {
            WpfApplicationHost.EnsureApplication();
            var request = new AppDialogRequest(
                "Title",
                "Prompt",
                MessageBoxImage.None,
                "OK",
                "Cancel",
                true,
                InputText: "seed",
                ValidateInput: text => string.IsNullOrWhiteSpace(text) ? string.Empty : null);
            var window = Build(request);

            try
            {
                window.InputBox.Text = "   ";

                Assert.False(window.PrimaryButton.IsEnabled);
                Assert.Equal(Visibility.Collapsed, window.ErrorText.Visibility);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>A validation failure WITH a message shows it inline and disables the primary button.</summary>
    [Fact]
    public void InputMode_InvalidText_WithMessage_ShowsInlineErrorAndDisablesPrimary()
    {
        StaTestRunner.Run(() =>
        {
            WpfApplicationHost.EnsureApplication();
            var request = new AppDialogRequest(
                "Title",
                "Prompt",
                MessageBoxImage.None,
                "OK",
                "Cancel",
                true,
                InputText: "seed",
                ValidateInput: text => text.Contains('/') ? "Name cannot contain '/'." : null);
            var window = Build(request);

            try
            {
                window.InputBox.Text = "bad/name";

                Assert.False(window.PrimaryButton.IsEnabled);
                Assert.Equal(Visibility.Visible, window.ErrorText.Visibility);
                Assert.Equal("Name cannot contain '/'.", window.ErrorText.Text);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>Valid text re-enables the primary button and hides the inline error.</summary>
    [Fact]
    public void InputMode_ValidText_EnablesPrimary_HidesInlineError()
    {
        StaTestRunner.Run(() =>
        {
            WpfApplicationHost.EnsureApplication();
            var request = new AppDialogRequest(
                "Title",
                "Prompt",
                MessageBoxImage.None,
                "OK",
                "Cancel",
                true,
                InputText: "seed",
                ValidateInput: text => string.IsNullOrWhiteSpace(text) ? string.Empty : null);
            var window = Build(request);

            try
            {
                window.InputBox.Text = string.Empty;
                Assert.False(window.PrimaryButton.IsEnabled);

                window.InputBox.Text = "a real name";

                Assert.True(window.PrimaryButton.IsEnabled);
                Assert.Equal(Visibility.Collapsed, window.ErrorText.Visibility);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary><see cref="AppDialogWindow.ResultButton"/> defaults to <see cref="AppDialogButton.Secondary"/> — the safe default for an unshown/just-closed window (Escape/closing without a click).</summary>
    [Fact]
    public void ResultButton_DefaultsToSecondary()
    {
        StaTestRunner.Run(() =>
        {
            WpfApplicationHost.EnsureApplication();
            var window = Build(ConfirmRequest(MessageBoxImage.Question, primaryIsDefault: true));

            try
            {
                Assert.Equal(AppDialogButton.Secondary, window.ResultButton);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// H-1 fix (review round): a long ru-locale title must WRAP within the Grid's own title column
    /// (narrower than the window's full 440 DIP content width, since the severity glyph's Auto column sits
    /// beside it) instead of behaving as though it had unbounded width — the horizontal-StackPanel bug this
    /// replaces gave the Orientation-axis child infinite available width during Measure, so
    /// TextWrapping="Wrap" never had anything to wrap against and the title silently overran the window.
    /// </summary>
    [Fact]
    public void LongRussianTitle_WrapsWithinItsOwnColumn_NotClippedOrOverflowing()
    {
        StaTestRunner.Run(() =>
        {
            WpfApplicationHost.EnsureApplication();
            const string longTitle = "Применение расположения значков ко всем панелям";
            var request = new AppDialogRequest(longTitle, "Это действие нельзя отменить.", MessageBoxImage.Warning, "Да", "Нет", PrimaryIsDefault: false);
            var window = Build(request);

            try
            {
                window.Show();
                window.UpdateLayout();

                // A single line of ANY text, at TitleText's own font settings — the reference this test
                // compares against to detect that wrapping (not just a tall single line) actually happened.
                var singleLineReference = new TextBlock
                {
                    Text = "X",
                    FontSize = window.TitleText.FontSize,
                    FontWeight = window.TitleText.FontWeight,
                };
                singleLineReference.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

                // MessageText spans the FULL row width (no sibling glyph column) — TitleText's own column
                // is that same width minus the glyph's Auto column, so it must never be WIDER than
                // MessageText. Under the old StackPanel bug, TitleText's unconstrained desired width for
                // this long a string would have been far larger than MessageText's actual (wrapped) width.
                Assert.True(
                    window.TitleText.ActualWidth <= window.MessageText.ActualWidth,
                    $"expected TitleText's Grid column to constrain it to <= MessageText's width ({window.MessageText.ActualWidth}); got {window.TitleText.ActualWidth}.");
                Assert.True(
                    window.TitleText.ActualHeight > singleLineReference.DesiredSize.Height * 1.5,
                    $"expected TitleText to wrap onto more than one line; got ActualHeight={window.TitleText.ActualHeight}, single-line reference={singleLineReference.DesiredSize.Height}.");
                Assert.Equal(longTitle, window.TitleText.Text); // unchanged — only its LAYOUT was ever the bug.
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>M-1 (a11y): the window and both buttons all expose the SAME message text as their <see cref="AutomationProperties.HelpTextProperty"/>.</summary>
    [Fact]
    public void HelpText_SetOnWindowAndBothButtons_MatchesMessage()
    {
        StaTestRunner.Run(() =>
        {
            WpfApplicationHost.EnsureApplication();
            var window = Build(ConfirmRequest(MessageBoxImage.Question, primaryIsDefault: true));

            try
            {
                Assert.Equal("Remove this panel?", AutomationProperties.GetHelpText(window));
                Assert.Equal("Remove this panel?", AutomationProperties.GetHelpText(window.PrimaryButton));
                Assert.Equal("Remove this panel?", AutomationProperties.GetHelpText(window.SecondaryButton));
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>M-1 (a11y): a blank title collapses <see cref="AppDialogWindow.TitleText"/> entirely rather than announcing/showing an empty heading.</summary>
    [Fact]
    public void BlankTitle_CollapsesTitleText()
    {
        StaTestRunner.Run(() =>
        {
            WpfApplicationHost.EnsureApplication();
            var request = new AppDialogRequest(string.Empty, "Snapshot name:", MessageBoxImage.None, "OK", "Cancel", true, InputText: "seed");
            var window = Build(request);

            try
            {
                Assert.Equal(Visibility.Collapsed, window.TitleText.Visibility);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>M-1 (a11y): a non-blank title stays Visible (the common case — guards the blank-title branch against a regression that always collapses it).</summary>
    [Fact]
    public void NonBlankTitle_TitleTextStaysVisible()
    {
        StaTestRunner.Run(() =>
        {
            WpfApplicationHost.EnsureApplication();
            var window = Build(ConfirmRequest(MessageBoxImage.Question, primaryIsDefault: true));

            try
            {
                Assert.Equal(Visibility.Visible, window.TitleText.Visibility);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>M-1 (a11y): <see cref="AppDialogWindow.InputBox"/> is labelled by <see cref="AppDialogWindow.MessageText"/> (the prompt text), so a screen reader announces something other than a bare, unnamed edit box.</summary>
    [Fact]
    public void InputMode_InputBoxIsLabeledByMessageText()
    {
        StaTestRunner.Run(() =>
        {
            WpfApplicationHost.EnsureApplication();
            var request = new AppDialogRequest("Save snapshot as…", "Snapshot name:", MessageBoxImage.None, "OK", "Cancel", true, InputText: "seed");
            var window = Build(request);

            try
            {
                Assert.Same(window.MessageText, AutomationProperties.GetLabeledBy(window.InputBox));
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static AppDialogWindow Build(AppDialogRequest request)
    {
        var window = new AppDialogWindow(request, AppThemeServiceMock.Object, WindowFrameThemingMock.Object, Localization);
        window.ShowActivated = false;
        window.Left = -5000;
        window.Top = -5000;
        return window;
    }

    private static AppDialogRequest ConfirmRequest(MessageBoxImage icon, bool primaryIsDefault) =>
        new("Remove panel", "Remove this panel?", icon, "Yes", "No", primaryIsDefault);

    private static Mock<IAppThemeService> BuildAppThemeServiceMock()
    {
        var mock = new Mock<IAppThemeService>();
        mock.SetupGet(s => s.Current).Returns(AppTheme.Light);
        return mock;
    }

    private static Mock<IWindowFrameTheming> BuildWindowFrameThemingMock()
    {
        var mock = new Mock<IWindowFrameTheming>();
        mock.Setup(w => w.TryApplyImmersiveDarkMode(It.IsAny<IntPtr>(), It.IsAny<bool>())).Returns(true);
        return mock;
    }
}
