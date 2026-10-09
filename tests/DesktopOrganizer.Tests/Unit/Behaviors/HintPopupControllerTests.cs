using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Behaviors;
using DesktopOrganizer.UI.Localization;

namespace DesktopOrganizer.Tests.Unit.Behaviors;

/// <summary>
/// Unit tests for <see cref="HintPopupController"/>'s rename-error text (docs/PLANS/localization.md
/// §2.4/L-5): the controller reads <see cref="LocalizationSource.Current"/> directly (a static
/// helper, not a DI-injected service — see that type's remarks), so these tests pin the default,
/// English text <see cref="LocalizationSource.Current"/> resolves to before <c>App.StartApp</c> ever
/// runs (R-5).
/// </summary>
/// <remarks>
/// Runs on a dedicated STA thread (<see cref="StaTestRunner"/>) — <see cref="Popup"/>/<see cref="TextBlock"/>
/// are WPF <see cref="DependencyObject"/>s, matching every other WPF-fixture test in this project.
/// </remarks>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class HintPopupControllerTests
{
    /// <summary>
    /// Verifies that <see cref="HintPopupController.ShowRenameError"/> sets the rename-error popup's
    /// text to the localized <c>Hint_Rename_*</c> string for each <see cref="RenameOutcome"/>.
    /// </summary>
    /// <param name="outcome">The rename failure reason under test.</param>
    /// <param name="expectedText">The expected English text (<see cref="LocalizationSource.Current"/>'s default culture).</param>
    [Theory]
    [InlineData(RenameOutcome.InvalidName, "Invalid file name.")]
    [InlineData(RenameOutcome.NameTaken, "A file or folder with that name already exists.")]
    [InlineData(RenameOutcome.NotFound, "File not found — it may have been deleted or moved.")]
    [InlineData(RenameOutcome.Failed, "Failed to rename the file.")]
    public void ShowRenameError_SetsLocalizedText(RenameOutcome outcome, string expectedText)
    {
        StaTestRunner.Run(() =>
        {
            var sut = BuildSut(out var renameErrorText, out _);

            sut.ShowRenameError(outcome);

            Assert.Equal(expectedText, renameErrorText.Text);
        });
    }

    /// <summary>
    /// F5: the read-only drop hint and the Move-semantics hint share one popup; each show sets its own
    /// localized text (the English defaults here), so a read-only hint never leaks into a later Move hint.
    /// </summary>
    [Fact]
    public void ShowReadOnlyDropHint_ThenShowMoveHint_SetsEachLocalizedText()
    {
        StaTestRunner.Run(() =>
        {
            var moveHintText = new TextBlock();
            var sut = BuildSut(out _, out _, moveHintText);

            sut.ShowReadOnlyDropHint();
            Assert.Equal("Read-only mode: the file stayed in place and the panel will not be saved.", moveHintText.Text);

            sut.ShowMoveHint();
            Assert.Equal("The file was moved into the panel — the original was removed from the desktop.", moveHintText.Text);

            sut.StopTimers();
        });
    }

    /// <summary>
    /// ADR-009 amendment 2026-10-10: the cloud-folder hint joins its message and action lines into the shared
    /// popup text (English defaults here).
    /// </summary>
    [Fact]
    public void ShowCloudDropHint_SetsMessageAndActionLines()
    {
        StaTestRunner.Run(() =>
        {
            var moveHintText = new TextBlock();
            var sut = BuildSut(out _, out _, moveHintText);

            sut.ShowCloudDropHint();

            Assert.Equal(
                "A file or folder from OneDrive was moved into panel storage and is no longer synced. To return it, drag it out of the panel back into its folder.",
                moveHintText.Text);

            sut.StopTimers();
        });
    }

    private static HintPopupController BuildSut(out TextBlock renameErrorText, out Popup renameErrorPopup, TextBlock? moveHintText = null)
    {
        var moveHintPopup = new Popup();
        renameErrorPopup = new Popup();
        renameErrorText = new TextBlock();
        var searchRoot = new Grid();

        return new HintPopupController(
            Dispatcher.CurrentDispatcher,
            moveHintPopup,
            renameErrorPopup,
            renameErrorText,
            searchRoot,
            getEditingItem: () => null,
            moveHintText: moveHintText);
    }
}
