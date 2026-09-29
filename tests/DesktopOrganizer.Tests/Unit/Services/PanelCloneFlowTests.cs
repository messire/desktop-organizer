using System.Windows;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Tests.Helpers;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="PanelCloneFlow"/> (H-1/LOW-3 fixes, code review, panel clone to page, backlog
/// 1.x wave 3 review) — the shared flow behind every "Copy to page" action, in isolation from both call
/// sites (<c>MainWindowViewModel.CopyPanelToPage</c>/<c>ChildWindowViewModel.CopyPanelToPage</c>, both
/// exercised through a mocked <see cref="IPanelCloneFlow"/> in their own test files).
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class PanelCloneFlowTests
{
    private readonly Mock<IWindowManager> _windowManagerMock = new();
    private readonly Mock<IConfirmationService> _confirmationMock = new();
    private readonly Mock<IBusyIndicator> _busyIndicatorMock = new();
    private readonly Mock<IDisposable> _busyScopeMock = new();

    // A real English LocalizationService — several tests below assert actual RESX text for the
    // Panel_Copy_* keys, same convention the rest of this backlog uses (docs/PLANS/localization.md R-5).
    private readonly ILocalizationService _localization = TestLocalization.English;

    /// <summary>Initializes a new instance of the <see cref="PanelCloneFlowTests"/> class.</summary>
    public PanelCloneFlowTests()
    {
        _busyIndicatorMock.Setup(b => b.Show()).Returns(_busyScopeMock.Object);
    }

    /// <summary>Delegates to <see cref="IWindowManager.ClonePanelToPage"/> with the exact source/destination given.</summary>
    [Fact]
    public void CopyToPage_CallsClonePanelToPage()
    {
        var sut = BuildSut();
        var source = new ChildWindowModel { PageId = "default" };
        _windowManagerMock
            .Setup(w => w.ClonePanelToPage(source, "work"))
            .Returns(new PanelCloneResult(PanelCloneOutcome.Cloned, new ChildWindowModel(), 0));

        sut.CopyToPage(source, "work", onUnknownPage: () => { });

        _windowManagerMock.Verify(w => w.ClonePanelToPage(source, "work"), Times.Once);
    }

    /// <summary>Shows the busy indication for the duration of the (synchronous) clone call, and always disposes it afterward.</summary>
    [Fact]
    public void CopyToPage_ShowsAndDisposesBusyIndicator()
    {
        var sut = BuildSut();
        var source = new ChildWindowModel { PageId = "default" };
        _windowManagerMock
            .Setup(w => w.ClonePanelToPage(source, "work"))
            .Returns(new PanelCloneResult(PanelCloneOutcome.Cloned, new ChildWindowModel(), 0));

        sut.CopyToPage(source, "work", onUnknownPage: () => { });

        _busyIndicatorMock.Verify(b => b.Show(), Times.Once);
        _busyScopeMock.Verify(s => s.Dispose(), Times.Once);
    }

    /// <summary>The busy indication is disposed even when <see cref="IWindowManager.ClonePanelToPage"/> itself throws.</summary>
    [Fact]
    public void CopyToPage_ClonePanelToPageThrows_StillDisposesBusyIndicator()
    {
        var sut = BuildSut();
        var source = new ChildWindowModel { PageId = "default" };
        _windowManagerMock
            .Setup(w => w.ClonePanelToPage(source, "work"))
            .Throws<InvalidOperationException>();

        Assert.Throws<InvalidOperationException>(() => sut.CopyToPage(source, "work", onUnknownPage: () => { }));

        _busyScopeMock.Verify(s => s.Dispose(), Times.Once);
    }

    /// <summary>A <see cref="PanelCloneOutcome.Cloned"/> result is returned as-is, and nothing is notified.</summary>
    [Fact]
    public void CopyToPage_Cloned_ReturnsResult_NeverNotifies()
    {
        var sut = BuildSut();
        var source = new ChildWindowModel { PageId = "default" };
        var clone = new ChildWindowModel { PageId = "work" };
        _windowManagerMock.Setup(w => w.ClonePanelToPage(source, "work")).Returns(new PanelCloneResult(PanelCloneOutcome.Cloned, clone, 0));

        var result = sut.CopyToPage(source, "work", onUnknownPage: () => { });

        Assert.Equal(PanelCloneOutcome.Cloned, result.Outcome);
        Assert.Same(clone, result.ClonedPanel);
        _confirmationMock.Verify(c => c.Notify(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MessageBoxImage>()), Times.Never);
    }

    /// <summary>
    /// A <see cref="PanelCloneOutcome.UnknownPage"/> outcome invokes <c>onUnknownPage</c> instead of
    /// notifying — mirroring <c>MainWindowViewModel.OnPagesChanged</c>'s own silent re-sync.
    /// </summary>
    [Fact]
    public void CopyToPage_UnknownPage_InvokesCallback_NeverNotifies()
    {
        var sut = BuildSut();
        var source = new ChildWindowModel { PageId = "default" };
        _windowManagerMock.Setup(w => w.ClonePanelToPage(source, "gone")).Returns(new PanelCloneResult(PanelCloneOutcome.UnknownPage, null, 0));
        var callbackInvoked = false;

        var result = sut.CopyToPage(source, "gone", onUnknownPage: () => callbackInvoked = true);

        Assert.True(callbackInvoked);
        Assert.Equal(PanelCloneOutcome.UnknownPage, result.Outcome);
        _confirmationMock.Verify(c => c.Notify(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MessageBoxImage>()), Times.Never);
    }

    /// <summary>A <see cref="PanelCloneOutcome.TooLarge"/> outcome notifies with the size-specific key.</summary>
    [Fact]
    public void CopyToPage_TooLarge_NotifiesTooLargeMessage() => AssertNotifiesForOutcome(PanelCloneOutcome.TooLarge, "Panel_Copy_TooLarge_Message");

    /// <summary>A <see cref="PanelCloneOutcome.InsufficientSpace"/> outcome notifies with the disk-space-specific key.</summary>
    [Fact]
    public void CopyToPage_InsufficientSpace_NotifiesNoSpaceMessage() => AssertNotifiesForOutcome(PanelCloneOutcome.InsufficientSpace, "Panel_Copy_NoSpace_Message");

    /// <summary>A <see cref="PanelCloneOutcome.CopyFailed"/> outcome notifies with the generic copy-failed key.</summary>
    [Fact]
    public void CopyToPage_CopyFailed_NotifiesGenericFailure() => AssertNotifiesForOutcome(PanelCloneOutcome.CopyFailed, "Panel_Copy_Failed_Message");

    /// <summary>Every failure outcome notifies with the same title and <see cref="MessageBoxImage.Warning"/> icon.</summary>
    [Fact]
    public void CopyToPage_Failed_NotifiesWithFailedTitleAndWarningIcon()
    {
        var sut = BuildSut();
        var source = new ChildWindowModel { PageId = "default" };
        _windowManagerMock.Setup(w => w.ClonePanelToPage(source, "work")).Returns(new PanelCloneResult(PanelCloneOutcome.CopyFailed, null, 0));

        sut.CopyToPage(source, "work", onUnknownPage: () => { });

        _confirmationMock.Verify(
            c => c.Notify(
                _localization.GetString("Panel_Copy_Failed_Message"),
                _localization.GetString("Panel_Copy_Failed_Title"),
                MessageBoxImage.Warning),
            Times.Once);
    }

    /// <summary>
    /// <see cref="PanelCloneResult.SkippedMissingItems"/> never changes a <c>Cloned</c> outcome into a
    /// notified one — it is expected, recoverable behavior (ADR-045 §8.4), only logged by
    /// <see cref="IWindowManager.ClonePanelToPage"/> itself.
    /// </summary>
    [Fact]
    public void CopyToPage_ClonedWithSkippedMissingItems_NeverNotifies()
    {
        var sut = BuildSut();
        var source = new ChildWindowModel { PageId = "default" };
        _windowManagerMock
            .Setup(w => w.ClonePanelToPage(source, "work"))
            .Returns(new PanelCloneResult(PanelCloneOutcome.Cloned, new ChildWindowModel(), SkippedMissingItems: 3));

        sut.CopyToPage(source, "work", onUnknownPage: () => { });

        _confirmationMock.Verify(c => c.Notify(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MessageBoxImage>()), Times.Never);
    }

    private PanelCloneFlow BuildSut() => new(_windowManagerMock.Object, _confirmationMock.Object, _localization, _busyIndicatorMock.Object);

    private void AssertNotifiesForOutcome(PanelCloneOutcome outcome, string expectedMessageKey)
    {
        var sut = BuildSut();
        var source = new ChildWindowModel { PageId = "default" };
        _windowManagerMock.Setup(w => w.ClonePanelToPage(source, "work")).Returns(new PanelCloneResult(outcome, null, 0));

        sut.CopyToPage(source, "work", onUnknownPage: () => { });

        _confirmationMock.Verify(
            c => c.Notify(
                _localization.GetString(expectedMessageKey),
                It.IsAny<string>(),
                It.IsAny<MessageBoxImage>()),
            Times.Once);
    }
}
