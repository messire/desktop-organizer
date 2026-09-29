using System.Windows;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Tests.Helpers;
using DesktopOrganizer.UI.Services;

namespace DesktopOrganizer.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="InputDialogService"/> (design-system-1x.md §2.8, ADR-047 D-9, T-10) — through
/// a fake <see cref="IDialogPresenter"/> so no real <see cref="System.Windows.Window"/> is ever shown.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class InputDialogServiceTests
{
    private readonly ILocalizationService _localization = TestLocalization.English;

    /// <summary>Builds an input-mode request with the given title/prompt/default text, no severity icon, and "OK"/"Cancel" buttons.</summary>
    [Fact]
    public void PromptText_BuildsInputModeRequest()
    {
        var presenter = new FakeDialogPresenter(new AppDialogResult(AppDialogButton.Secondary));
        var sut = new InputDialogService(presenter, _localization);

        sut.PromptText("Save snapshot as…", "Snapshot name:", "2026-09-29 14-00");

        Assert.NotNull(presenter.LastRequest);
        Assert.Equal("Save snapshot as…", presenter.LastRequest!.Title);
        Assert.Equal("Snapshot name:", presenter.LastRequest.Message);
        Assert.Equal(MessageBoxImage.None, presenter.LastRequest.Icon);
        Assert.Equal("2026-09-29 14-00", presenter.LastRequest.InputText);
        Assert.Equal("OK", presenter.LastRequest.PrimaryText);
        Assert.Equal("Cancel", presenter.LastRequest.SecondaryText);
        Assert.True(presenter.LastRequest.PrimaryIsDefault);
        Assert.NotNull(presenter.LastRequest.ValidateInput);
    }

    /// <summary>The request's <c>ValidateInput</c> delegate rejects empty/whitespace-only text — same "OK disabled while empty" behavior the pre-6.3 InputDialog had.</summary>
    /// <param name="text">The blank/whitespace text under test.</param>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void PromptText_ValidateInput_RejectsBlankText(string text)
    {
        var presenter = new FakeDialogPresenter(new AppDialogResult(AppDialogButton.Secondary));
        var sut = new InputDialogService(presenter, _localization);

        sut.PromptText("Title", "Prompt", "seed");

        Assert.NotNull(presenter.LastRequest!.ValidateInput!(text));
    }

    /// <summary>The request's <c>ValidateInput</c> delegate accepts non-blank text.</summary>
    [Fact]
    public void PromptText_ValidateInput_AcceptsNonBlankText()
    {
        var presenter = new FakeDialogPresenter(new AppDialogResult(AppDialogButton.Secondary));
        var sut = new InputDialogService(presenter, _localization);

        sut.PromptText("Title", "Prompt", "seed");

        Assert.Null(presenter.LastRequest!.ValidateInput!("My snapshot"));
    }

    /// <summary>A primary ("OK") result returns the entered text.</summary>
    [Fact]
    public void PromptText_PrimaryResult_ReturnsInputText()
    {
        var presenter = new FakeDialogPresenter(new AppDialogResult(AppDialogButton.Primary, "My snapshot"));
        var sut = new InputDialogService(presenter, _localization);

        var result = sut.PromptText("Title", "Prompt", "seed");

        Assert.Equal("My snapshot", result);
    }

    /// <summary>Escape, the Cancel button, or closing the window all map to a secondary result, which returns <see langword="null"/>.</summary>
    [Fact]
    public void PromptText_SecondaryResult_ReturnsNull()
    {
        var presenter = new FakeDialogPresenter(new AppDialogResult(AppDialogButton.Secondary));
        var sut = new InputDialogService(presenter, _localization);

        var result = sut.PromptText("Title", "Prompt", "seed");

        Assert.Null(result);
    }

    private sealed class FakeDialogPresenter(AppDialogResult result) : IDialogPresenter
    {
        public AppDialogRequest? LastRequest { get; private set; }

        public AppDialogResult Show(AppDialogRequest request)
        {
            LastRequest = request;
            return result;
        }
    }
}
