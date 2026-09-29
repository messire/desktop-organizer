using System.Windows;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Tests.Helpers;
using DesktopOrganizer.UI.Services;

namespace DesktopOrganizer.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="ConfirmationService"/> (design-system-1x.md §2.8, ADR-047 D-9, T-10) — through
/// a fake <see cref="IDialogPresenter"/> so no real <see cref="System.Windows.Window"/> is ever shown.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class ConfirmationServiceTests
{
    private readonly ILocalizationService _localization = TestLocalization.English;

    /// <summary>The visual primary button is always "Yes" — regardless of icon — and the request carries the caller's message/title/icon verbatim.</summary>
    /// <param name="icon">The icon passed to <see cref="IConfirmationService.Confirm"/>.</param>
    [Theory]
    [InlineData(MessageBoxImage.Question)]
    [InlineData(MessageBoxImage.Warning)]
    public void Confirm_BuildsRequest_WithYesAsPrimaryAndNoAsSecondary(MessageBoxImage icon)
    {
        var presenter = new FakeDialogPresenter(AppDialogButton.Secondary);
        var sut = new ConfirmationService(presenter, _localization);

        sut.Confirm("Remove this panel?", "Remove panel", icon);

        Assert.NotNull(presenter.LastRequest);
        Assert.Equal("Remove this panel?", presenter.LastRequest!.Message);
        Assert.Equal("Remove panel", presenter.LastRequest.Title);
        Assert.Equal(icon, presenter.LastRequest.Icon);
        Assert.Equal("Yes", presenter.LastRequest.PrimaryText);
        Assert.Equal("No", presenter.LastRequest.SecondaryText);
    }

    /// <summary><see cref="MessageBoxImage.Question"/> defaults Enter to the primary ("Yes") button.</summary>
    [Fact]
    public void Confirm_Question_DefaultsEnterToPrimary()
    {
        var presenter = new FakeDialogPresenter(AppDialogButton.Primary);
        var sut = new ConfirmationService(presenter, _localization);

        sut.Confirm("Align all panels to the grid?", "Align all", MessageBoxImage.Question);

        Assert.True(presenter.LastRequest!.PrimaryIsDefault);
    }

    /// <summary>
    /// ADR-047 D-9/R-12: <see cref="MessageBoxImage.Warning"/> — an irreversible action — defaults Enter
    /// to the SECONDARY ("Нет") button instead, even though "Да" remains the visually primary button.
    /// </summary>
    [Fact]
    public void Confirm_Warning_DefaultsEnterToSecondary()
    {
        var presenter = new FakeDialogPresenter(AppDialogButton.Secondary);
        var sut = new ConfirmationService(presenter, _localization);

        sut.Confirm("This cannot be undone.", "Remove panel", MessageBoxImage.Warning);

        Assert.False(presenter.LastRequest!.PrimaryIsDefault);
    }

    /// <summary>The primary ("Yes") result maps to <see langword="true"/>.</summary>
    [Fact]
    public void Confirm_PrimaryResult_ReturnsTrue()
    {
        var presenter = new FakeDialogPresenter(AppDialogButton.Primary);
        var sut = new ConfirmationService(presenter, _localization);

        Assert.True(sut.Confirm("Remove this panel?"));
    }

    /// <summary>The secondary ("No")/cancelled result maps to <see langword="false"/>.</summary>
    [Fact]
    public void Confirm_SecondaryResult_ReturnsFalse()
    {
        var presenter = new FakeDialogPresenter(AppDialogButton.Secondary);
        var sut = new ConfirmationService(presenter, _localization);

        Assert.False(sut.Confirm("Remove this panel?"));
    }

    /// <summary><see cref="IConfirmationService.Notify"/> shows a single "OK" button — no secondary text.</summary>
    [Fact]
    public void Notify_BuildsRequest_WithOkAsPrimaryAndNoSecondary()
    {
        var presenter = new FakeDialogPresenter(AppDialogButton.Primary);
        var sut = new ConfirmationService(presenter, _localization);

        sut.Notify("Some items could not be moved to the Recycle Bin.", "Recycle Bin", MessageBoxImage.Warning);

        Assert.Equal("Some items could not be moved to the Recycle Bin.", presenter.LastRequest!.Message);
        Assert.Equal("Recycle Bin", presenter.LastRequest.Title);
        Assert.Equal(MessageBoxImage.Warning, presenter.LastRequest.Icon);
        Assert.Equal("OK", presenter.LastRequest.PrimaryText);
        Assert.Null(presenter.LastRequest.SecondaryText);
        Assert.True(presenter.LastRequest.PrimaryIsDefault);
    }

    private sealed class FakeDialogPresenter(AppDialogButton resultButton) : IDialogPresenter
    {
        public AppDialogRequest? LastRequest { get; private set; }

        public AppDialogResult Show(AppDialogRequest request)
        {
            LastRequest = request;
            return new AppDialogResult(resultButton);
        }
    }
}
