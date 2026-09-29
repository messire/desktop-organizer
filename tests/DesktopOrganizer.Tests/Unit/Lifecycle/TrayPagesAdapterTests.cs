using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Lifecycle;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Lifecycle;

/// <summary>
/// Unit tests for <see cref="TrayPagesAdapter"/> — the UI-side half of tray page navigation
/// (backlog-1x-master-plan.md §1/§2, ADR-044): the tray icon's double-click round-robin page switch, and
/// the tray context menu's "Pages" submenu. <see cref="ITrayIconManager"/> and <see cref="IPageManager"/>
/// are both mocked; a real <see cref="ILocalizationService"/> (<see cref="TestLocalization"/>) backs
/// <c>PageDisplayNames.Resolve</c> so culture-switch tests can drive it with genuine
/// <see cref="ILocalizationService.ApplyLanguage"/> calls instead of hand-raising a mocked event. The
/// UI-thread deferrals are replaced with synchronous test-only overloads (same seam as
/// <c>SingleInstanceActivationHandlerTests</c>/<c>PanelCustomizeServiceTests</c>), so none of these tests
/// needs a live WPF <see cref="System.Windows.Threading.Dispatcher"/>.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.UI))]
public class TrayPagesAdapterTests
{
    private readonly Mock<ITrayIconManager> _trayIconManagerMock = new();
    private readonly Mock<IPageManager> _pageManagerMock = new(MockBehavior.Strict);
    private readonly ILocalizationService _localization = TestLocalization.English;
    private int _deferCallCount;
    private int _deferPagesRefreshCallCount;
    private IReadOnlyList<TrayPageEntry>? _lastSetPages;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrayPagesAdapterTests"/> class, capturing every
    /// <see cref="ITrayIconManager.SetPages"/> snapshot into <c>_lastSetPages</c>.
    /// </summary>
    public TrayPagesAdapterTests()
    {
        _trayIconManagerMock
            .Setup(m => m.SetPages(It.IsAny<IReadOnlyList<TrayPageEntry>>()))
            .Callback<IReadOnlyList<TrayPageEntry>>(pages => _lastSetPages = pages);
    }

    /// <summary>
    /// Verifies that <see cref="ITrayIconManager.OnNextPageRequested"/>, after <see cref="TrayPagesAdapter.Start"/>,
    /// computes the next page via <see cref="PageCycling.Next"/> and calls <see cref="IPageManager.SwitchTo"/>
    /// with it — and that the switch is marshalled through the injected defer, not run in-place.
    /// </summary>
    [Fact]
    public void OnNextPageRequested_MultiplePages_CallsSwitchToWithNextPage_ThroughDefer()
    {
        SetupPages(("a", null), ("b", null), ("c", null));
        _pageManagerMock.SetupGet(p => p.Pages).Returns(["a", "b", "c"]);
        _pageManagerMock.SetupGet(p => p.CurrentPage).Returns("a");
        _pageManagerMock.Setup(p => p.SwitchTo("b"));
        _ = CreateStarted();
        var deferCountBeforeClick = _deferCallCount;

        _trayIconManagerMock.Raise(m => m.OnNextPageRequested += null, _trayIconManagerMock.Object, EventArgs.Empty);

        _pageManagerMock.Verify(p => p.SwitchTo("b"), Times.Once);
        Assert.Equal(deferCountBeforeClick + 1, _deferCallCount);
    }

    /// <summary>
    /// A single page is a no-op — <see cref="PageCycling.Next"/> returns <see langword="null"/> and
    /// <see cref="IPageManager.SwitchTo"/> is never called (ROADMAP: "при единственной странице — no-op").
    /// </summary>
    [Fact]
    public void OnNextPageRequested_SinglePage_DoesNotCallSwitchTo()
    {
        SetupPages(("default", null));
        _pageManagerMock.SetupGet(p => p.Pages).Returns(["default"]);
        _pageManagerMock.SetupGet(p => p.CurrentPage).Returns("default");
        _ = CreateStarted();

        _trayIconManagerMock.Raise(m => m.OnNextPageRequested += null, _trayIconManagerMock.Object, EventArgs.Empty);

        _pageManagerMock.Verify(p => p.SwitchTo(It.IsAny<string>()), Times.Never);
    }

    /// <summary>Before <see cref="TrayPagesAdapter.Start"/> is called, no event is wired at all.</summary>
    [Fact]
    public void OnNextPageRequested_BeforeStart_DoesNothing()
    {
        _ = CreateUnstarted();

        _trayIconManagerMock.Raise(m => m.OnNextPageRequested += null, _trayIconManagerMock.Object, EventArgs.Empty);

        _pageManagerMock.VerifyNoOtherCalls();
        Assert.Equal(0, _deferCallCount);
    }

    /// <summary>A second <see cref="TrayPagesAdapter.Start"/> call does not double-subscribe.</summary>
    [Fact]
    public void Start_CalledTwice_DoesNotDoubleSubscribe()
    {
        SetupPages(("a", null), ("b", null));
        _pageManagerMock.SetupGet(p => p.Pages).Returns(["a", "b"]);
        _pageManagerMock.SetupGet(p => p.CurrentPage).Returns("a");
        _pageManagerMock.Setup(p => p.SwitchTo("b"));
        var sut = CreateUnstarted();

        sut.Start();
        sut.Start();
        _trayIconManagerMock.Raise(m => m.OnNextPageRequested += null, _trayIconManagerMock.Object, EventArgs.Empty);

        _pageManagerMock.Verify(p => p.SwitchTo("b"), Times.Once);
    }

    /// <summary>
    /// <see cref="TrayPagesAdapter.Dispose"/> unsubscribes from <see cref="ITrayIconManager.OnNextPageRequested"/> —
    /// a double-click raised afterwards no longer reaches <see cref="IPageManager"/>.
    /// </summary>
    [Fact]
    public void Dispose_UnsubscribesFromOnNextPageRequested()
    {
        SetupPages(("a", null), ("b", null));
        _pageManagerMock.SetupGet(p => p.Pages).Returns(["a", "b"]);
        _pageManagerMock.SetupGet(p => p.CurrentPage).Returns("a");
        var sut = CreateStarted();

        sut.Dispose();
        _trayIconManagerMock.Raise(m => m.OnNextPageRequested += null, _trayIconManagerMock.Object, EventArgs.Empty);

        _pageManagerMock.Verify(p => p.SwitchTo(It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    /// A double-click that was already queued onto the (fake, synchronous) defer before
    /// <see cref="TrayPagesAdapter.Dispose"/> ran must not still act — <see cref="TrayPagesAdapter"/>
    /// re-checks its own disposed flag inside the deferred callback itself.
    /// </summary>
    [Fact]
    public void Dispose_CalledFromWithinTheDeferredCallback_SuppressesTheSwitch()
    {
        SetupPages(("a", null), ("b", null));
        _pageManagerMock.SetupGet(p => p.Pages).Returns(["a", "b"]);
        _pageManagerMock.SetupGet(p => p.CurrentPage).Returns("a");
        TrayPagesAdapter? sut = null;
        sut = new TrayPagesAdapter(
            _trayIconManagerMock.Object,
            _pageManagerMock.Object,
            _localization,
            action =>
            {
                sut!.Dispose();
                action();
            },
            DeferPagesRefresh);
        sut.Start();

        _trayIconManagerMock.Raise(m => m.OnNextPageRequested += null, _trayIconManagerMock.Object, EventArgs.Empty);

        _pageManagerMock.Verify(p => p.SwitchTo(It.IsAny<string>()), Times.Never);
    }

    /// <summary>Disposing twice is safe and does not double-unsubscribe.</summary>
    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        SetupPages(("a", null));
        _pageManagerMock.SetupGet(p => p.CurrentPage).Returns("a");
        var sut = CreateStarted();

        sut.Dispose();
        sut.Dispose();
    }

    /// <summary>
    /// L-4 fix (backlog 1.x wave 1 review): a <see cref="TrayPagesAdapter.Start"/> call arriving after
    /// <see cref="TrayPagesAdapter.Dispose"/> must not re-subscribe <see cref="ITrayIconManager.OnNextPageRequested"/>
    /// on a torn-down instance — nothing would ever unsubscribe it again.
    /// </summary>
    [Fact]
    public void Start_AfterDispose_DoesNotResubscribe()
    {
        SetupPages(("a", null), ("b", null));
        _pageManagerMock.SetupGet(p => p.Pages).Returns(["a", "b"]);
        _pageManagerMock.SetupGet(p => p.CurrentPage).Returns("a");
        var sut = CreateStarted();
        sut.Dispose();

        sut.Start();
        _trayIconManagerMock.Raise(m => m.OnNextPageRequested += null, _trayIconManagerMock.Object, EventArgs.Empty);

        _pageManagerMock.Verify(p => p.SwitchTo(It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    /// backlog-1x-master-plan.md §2.1: <see cref="TrayPagesAdapter.Start"/> pushes the initial "Pages"
    /// submenu snapshot, resolving each page's effective display name — a stored name wins, an unnamed
    /// <c>"default"</c> page falls back to the localized <c>Page_Default_DisplayName</c> — and marking
    /// <see cref="IPageManager.CurrentPage"/> as current.
    /// </summary>
    [Fact]
    public void Start_PushesInitialPages_WithResolvedDisplayNamesAndCurrentFlag()
    {
        SetupPages(("default", null), ("p2", "Work"));
        _pageManagerMock.SetupGet(p => p.CurrentPage).Returns("p2");

        CreateStarted();

        Assert.NotNull(_lastSetPages);
        Assert.Collection(
            _lastSetPages!,
            e =>
            {
                Assert.Equal("default", e.Id);
                Assert.Equal("Default", e.DisplayName);
                Assert.False(e.IsCurrent);
            },
            e =>
            {
                Assert.Equal("p2", e.Id);
                Assert.Equal("Work", e.DisplayName);
                Assert.True(e.IsCurrent);
            });
    }

    /// <summary>
    /// A page with a GUID-shaped id and a stored name resolves to the stored name, never the raw id
    /// (backlog-1x-master-plan.md §0.2: the tray never shows a raw id).
    /// </summary>
    [Fact]
    public void PushPages_PageWithGuidIdAndName_ResolvesToTheStoredName()
    {
        const string guidId = "3fa85f64-5717-4562-b3fc-2c963f66afa6";
        SetupPages((guidId, "Projects"));
        _pageManagerMock.SetupGet(p => p.CurrentPage).Returns(guidId);

        CreateStarted();

        var entry = Assert.Single(_lastSetPages!);
        Assert.Equal("Projects", entry.DisplayName);
    }

    /// <summary>
    /// <see cref="IPageManager.PagesChanged"/> (add/remove/rename) re-pushes the "Pages" submenu snapshot
    /// with the up-to-date descriptors — e.g. a page rename is reflected without restarting the app.
    /// </summary>
    [Fact]
    public void PagesChanged_RePushesPages_WithUpdatedDisplayName()
    {
        var descriptors = new List<PageDescriptor> { new("default", null) };
        _pageManagerMock.SetupGet(p => p.PageDescriptors).Returns(() => descriptors);
        _pageManagerMock.SetupGet(p => p.CurrentPage).Returns("default");
        CreateStarted();
        Assert.Equal("Default", Assert.Single(_lastSetPages!).DisplayName);

        descriptors = [new PageDescriptor("default", "Renamed")];
        RaisePagesChanged();

        Assert.Equal("Renamed", Assert.Single(_lastSetPages!).DisplayName);
    }

    /// <summary>
    /// <see cref="IPageManager.PageChanged"/> (the active page switched) re-pushes the snapshot with the
    /// new current page's <see cref="TrayPageEntry.IsCurrent"/> flag flipped.
    /// </summary>
    [Fact]
    public void PageChanged_RePushesPages_WithNewCurrentFlag()
    {
        SetupPages(("a", null), ("b", null));
        var current = "a";
        _pageManagerMock.SetupGet(p => p.CurrentPage).Returns(() => current);
        CreateStarted();
        Assert.True(_lastSetPages!.Single(e => e.Id == "a").IsCurrent);

        current = "b";
        RaisePageChanged("b");

        Assert.True(_lastSetPages!.Single(e => e.Id == "b").IsCurrent);
        Assert.False(_lastSetPages!.Single(e => e.Id == "a").IsCurrent);
    }

    /// <summary>
    /// docs/PLANS/localization.md: a language switch (<see cref="ILocalizationService.CultureChanged"/>)
    /// re-resolves an unnamed <c>"default"</c> page's display name in the new language — a stored name is
    /// data and does not change.
    /// </summary>
    [Fact]
    public void CultureChanged_RePushesPages_WithNamesInTheNewLanguage()
    {
        SetupPages(("default", null), ("p2", "Work"));
        _pageManagerMock.SetupGet(p => p.CurrentPage).Returns("default");
        CreateStarted();
        Assert.Equal("Default", _lastSetPages!.Single(e => e.Id == "default").DisplayName);
        Assert.Equal("Work", _lastSetPages!.Single(e => e.Id == "p2").DisplayName);

        _localization.ApplyLanguage("ru");

        Assert.Equal("Основная", _lastSetPages!.Single(e => e.Id == "default").DisplayName);
        Assert.Equal("Work", _lastSetPages!.Single(e => e.Id == "p2").DisplayName); // stored name: not translated
    }

    /// <summary>
    /// Every "Pages" submenu (re)push is marshalled through the injected refresh defer, mirroring the
    /// separate defer used for a page switch (backlog-1x-master-plan.md §2.1's "Background priority" note).
    /// </summary>
    [Fact]
    public void PushPages_IsMarshalledThroughTheDeferPagesRefreshSeam()
    {
        SetupPages(("default", null));
        _pageManagerMock.SetupGet(p => p.CurrentPage).Returns("default");

        CreateStarted();

        Assert.True(_deferPagesRefreshCallCount > 0);
    }

    /// <summary>
    /// backlog-1x-master-plan.md §2.1: clicking a "Pages" submenu item (<see cref="ITrayIconManager.OnPageSelected"/>)
    /// switches to it — through the same defer seam as the round-robin switch.
    /// </summary>
    [Fact]
    public void OnPageSelected_KnownId_CallsSwitchTo_ThroughDefer()
    {
        SetupPages(("a", null), ("b", null));
        _pageManagerMock.SetupGet(p => p.Pages).Returns(["a", "b"]);
        _pageManagerMock.SetupGet(p => p.CurrentPage).Returns("a");
        _pageManagerMock.Setup(p => p.SwitchTo("b"));
        CreateStarted();
        var deferCountBeforeClick = _deferCallCount;

        _trayIconManagerMock.Raise(m => m.OnPageSelected += null, _trayIconManagerMock.Object, "b");

        _pageManagerMock.Verify(p => p.SwitchTo("b"), Times.Once);
        Assert.Equal(deferCountBeforeClick + 1, _deferCallCount);
    }

    /// <summary>
    /// A click on a page id the submenu is stale about (already removed/renamed by the time the click was
    /// dispatched) is silently ignored — <see cref="IPageManager.SwitchTo"/> is never called with an id
    /// <see cref="IPageManager.Pages"/> no longer recognizes.
    /// </summary>
    [Fact]
    public void OnPageSelected_UnknownId_DoesNotCallSwitchTo()
    {
        SetupPages(("a", null));
        _pageManagerMock.SetupGet(p => p.Pages).Returns(["a"]);
        _pageManagerMock.SetupGet(p => p.CurrentPage).Returns("a");
        CreateStarted();

        _trayIconManagerMock.Raise(m => m.OnPageSelected += null, _trayIconManagerMock.Object, "stale-removed-page");

        _pageManagerMock.Verify(p => p.SwitchTo(It.IsAny<string>()), Times.Never);
    }

    /// <summary><see cref="TrayPagesAdapter.Dispose"/> unsubscribes from <see cref="ITrayIconManager.OnPageSelected"/> too.</summary>
    [Fact]
    public void Dispose_UnsubscribesFromOnPageSelected()
    {
        SetupPages(("a", null), ("b", null));
        _pageManagerMock.SetupGet(p => p.CurrentPage).Returns("a");
        var sut = CreateStarted();

        sut.Dispose();
        _trayIconManagerMock.Raise(m => m.OnPageSelected += null, _trayIconManagerMock.Object, "b");

        _pageManagerMock.Verify(p => p.SwitchTo(It.IsAny<string>()), Times.Never);
    }

    /// <summary><see cref="TrayPagesAdapter.Dispose"/> unsubscribes from <see cref="IPageManager.PagesChanged"/> and <see cref="IPageManager.PageChanged"/>.</summary>
    [Fact]
    public void Dispose_UnsubscribesFromPageManagerEvents()
    {
        SetupPages(("a", null));
        _pageManagerMock.SetupGet(p => p.CurrentPage).Returns("a");
        var sut = CreateStarted();
        var pushesBeforeDispose = _deferPagesRefreshCallCount;

        sut.Dispose();
        RaisePagesChanged();
        RaisePageChanged("a");

        Assert.Equal(pushesBeforeDispose, _deferPagesRefreshCallCount);
    }

    /// <summary><see cref="TrayPagesAdapter.Dispose"/> unsubscribes from <see cref="ILocalizationService.CultureChanged"/>.</summary>
    [Fact]
    public void Dispose_UnsubscribesFromCultureChanged()
    {
        SetupPages(("default", null));
        _pageManagerMock.SetupGet(p => p.CurrentPage).Returns("default");
        var sut = CreateStarted();

        sut.Dispose();
        var exception = Record.Exception(() => _localization.ApplyLanguage("ru"));

        Assert.Null(exception);
    }

    private void SetupPages(params (string Id, string? Name)[] pages)
    {
        IReadOnlyList<PageDescriptor> descriptors = pages.Select(p => new PageDescriptor(p.Id, p.Name)).ToList();
        _pageManagerMock.SetupGet(p => p.PageDescriptors).Returns(descriptors);
    }

    private void RaisePagesChanged() => _pageManagerMock.Raise(p => p.PagesChanged += null);

    private void RaisePageChanged(string pageId) => _pageManagerMock.Raise(p => p.PageChanged += null, pageId);

    private TrayPagesAdapter CreateStarted()
    {
        var sut = CreateUnstarted();
        sut.Start();
        return sut;
    }

    private TrayPagesAdapter CreateUnstarted() =>
        new(_trayIconManagerMock.Object, _pageManagerMock.Object, _localization, Defer, DeferPagesRefresh);

    private void Defer(Action action)
    {
        _deferCallCount++;
        action();
    }

    private void DeferPagesRefresh(Action action)
    {
        _deferPagesRefreshCallCount++;
        action();
    }
}
