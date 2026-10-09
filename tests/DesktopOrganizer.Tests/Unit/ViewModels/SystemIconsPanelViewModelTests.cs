using System.IO;
using System.Windows.Media;
using DesktopOrganizer.Core;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;
using DesktopOrganizer.UI.ViewModels;
using Moq;

namespace DesktopOrganizer.Tests.Unit.ViewModels;

/// <summary>
/// Unit tests for SystemIconsPanelViewModel.
/// ISystemShellService is mocked — no native or WPF shell calls are made.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class SystemIconsPanelViewModelTests
{
    private readonly Mock<ISystemShellService> _shellMock = new();
    private readonly Mock<IPanelSelectionCoordinator> _selectionCoordinatorMock = new();
    private readonly Mock<IWindowManager> _windowManagerMock = new();

    // ── VisibleItems: only visible icons are exposed ──────────────────────────

    /// <summary>
    /// Verifies that <see cref="SystemIconsPanelViewModel.VisibleItems"/> contains only the icons whose
    /// model item is visible.
    /// </summary>
    [Fact]
    public void VisibleItems_OnlyContainsVisibleIcons()
    {
        // Arrange
        var model = BuildModel(
            (SystemIconKind.ThisPc,       visible: true,  order: 0),
            (SystemIconKind.ControlPanel, visible: false, order: 1),
            (SystemIconKind.RecycleBin,   visible: true,  order: 2),
            (SystemIconKind.Network,      visible: false, order: 3),
            (SystemIconKind.UserFolder,   visible: true,  order: 4));

        using var sut = BuildSut(model);

        // Act
        var visibleKinds = sut.VisibleItems.Select(i => i.Kind).ToHashSet();

        // Assert — only the 3 visible items are present
        Assert.Equal(3, sut.VisibleItems.Count());
        Assert.Contains(SystemIconKind.ThisPc,    visibleKinds);
        Assert.Contains(SystemIconKind.RecycleBin, visibleKinds);
        Assert.Contains(SystemIconKind.UserFolder, visibleKinds);
        Assert.DoesNotContain(SystemIconKind.ControlPanel, visibleKinds);
        Assert.DoesNotContain(SystemIconKind.Network,      visibleKinds);
    }

    /// <summary>
    /// Verifies that <see cref="SystemIconsPanelViewModel.VisibleItems"/> is empty when every icon is
    /// hidden.
    /// </summary>
    [Fact]
    public void VisibleItems_IsEmpty_WhenAllIconsAreHidden()
    {
        // Arrange
        var model = BuildModel(
            (SystemIconKind.ThisPc,       visible: false, order: 0),
            (SystemIconKind.ControlPanel, visible: false, order: 1),
            (SystemIconKind.RecycleBin,   visible: false, order: 2),
            (SystemIconKind.Network,      visible: false, order: 3),
            (SystemIconKind.UserFolder,   visible: false, order: 4));

        using var sut = BuildSut(model);

        // Assert
        Assert.Empty(sut.VisibleItems);
    }

    // ── VisibleItems: sorted by Order ────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="SystemIconsPanelViewModel.VisibleItems"/> is sorted by ascending
    /// <see cref="SystemIconItemModel.Order"/> regardless of the model's insertion order.
    /// </summary>
    [Fact]
    public void VisibleItems_AreSortedByOrder()
    {
        // Arrange — insert in reverse order
        var model = BuildModel(
            (SystemIconKind.UserFolder,   visible: true, order: 4),
            (SystemIconKind.Network,      visible: true, order: 3),
            (SystemIconKind.RecycleBin,   visible: true, order: 2),
            (SystemIconKind.ControlPanel, visible: true, order: 1),
            (SystemIconKind.ThisPc,       visible: true, order: 0));

        using var sut = BuildSut(model);

        // Act
        var kinds = sut.VisibleItems.Select(i => i.Kind).ToList();

        // Assert — should be in ascending Order
        Assert.Equal(SystemIconKind.ThisPc,       kinds[0]);
        Assert.Equal(SystemIconKind.ControlPanel,  kinds[1]);
        Assert.Equal(SystemIconKind.RecycleBin,    kinds[2]);
        Assert.Equal(SystemIconKind.Network,       kinds[3]);
        Assert.Equal(SystemIconKind.UserFolder,    kinds[4]);
    }

    // ── Reorder: changes Order property in the underlying model ──────────────
    // NOTE: Reorder() is not yet implemented in SystemIconsPanelViewModel.
    // These tests are RED by design — they drive the implementation.

    /// <summary>
    /// Verifies that <see cref="SystemIconsPanelViewModel.Reorder"/> updates the target item's
    /// <see cref="SystemIconItemModel.Order"/> in the underlying model.
    /// </summary>
    [Fact]
    public void Reorder_ChangesOrderPropertyInModel()
    {
        // Arrange
        var model = BuildModel(
            (SystemIconKind.ThisPc,       visible: true, order: 0),
            (SystemIconKind.ControlPanel, visible: true, order: 1),
            (SystemIconKind.RecycleBin,   visible: true, order: 2),
            (SystemIconKind.Network,      visible: true, order: 3),
            (SystemIconKind.UserFolder,   visible: true, order: 4));

        using var sut = BuildSut(model);

        // Act — move "Network" (order=3) to position 0
        sut.Reorder(SystemIconKind.Network, newOrder: 0);

        // Assert — the model item's Order was updated
        var networkItem = model.Items.Single(i => i.Kind == SystemIconKind.Network);
        Assert.Equal(0, networkItem.Order);
    }

    /// <summary>
    /// Verifies that <see cref="SystemIconsPanelViewModel.Reorder"/> adjusts every other item's
    /// <see cref="SystemIconItemModel.Order"/> so all order values stay unique.
    /// </summary>
    [Fact]
    public void Reorder_AdjustsOtherItemsToKeepOrdersUnique()
    {
        // Arrange
        var model = BuildModel(
            (SystemIconKind.ThisPc,       visible: true, order: 0),
            (SystemIconKind.ControlPanel, visible: true, order: 1),
            (SystemIconKind.RecycleBin,   visible: true, order: 2));

        using var sut = BuildSut(model);

        // Act — move ControlPanel to position 0
        sut.Reorder(SystemIconKind.ControlPanel, newOrder: 0);

        // Assert — all order values are unique
        var orders = model.Items.Select(i => i.Order).ToList();
        Assert.Equal(orders.Distinct().Count(), orders.Count);
    }

    // ── RecycleBinRefresh: updates icon according to current state ────────────
    // NOTE: RefreshRecycleBin() is not yet implemented in SystemIconsPanelViewModel.
    // These tests are RED by design — they drive the implementation.

    /// <summary>
    /// Verifies that <see cref="SystemIconsPanelViewModel.RefreshRecycleBin"/> sets the Recycle Bin
    /// model item's icon from <see cref="ISystemShellService.GetSystemIcon"/> when the bin is empty.
    /// </summary>
    [Fact]
    public void RefreshRecycleBin_UpdatesRecycleBinItemIcon()
    {
        // Arrange
        var emptyIcon = new System.Windows.Media.Imaging.BitmapImage();

        _shellMock
            .Setup(s => s.GetRecycleBinState())
            .Returns(new RecycleBinState(IsEmpty: true, ItemCount: 0, TotalSize: 0));

        _shellMock
            .Setup(s => s.GetSystemIcon(SystemIconKind.RecycleBin, It.IsAny<int>()))
            .Returns(emptyIcon);

        var model = BuildModel((SystemIconKind.RecycleBin, visible: true, order: 0));
        using var sut = BuildSut(model);

        // Act
        sut.RefreshRecycleBin();

        // Assert — the icon on the model item was set from the shell service
        var binItem = model.Items.Single(i => i.Kind == SystemIconKind.RecycleBin);
        Assert.Equal(emptyIcon, binItem.Icon);
    }

    /// <summary>
    /// Verifies that <see cref="SystemIconsPanelViewModel.RefreshRecycleBin"/> sets a non-null icon on
    /// the Recycle Bin model item when the bin is not empty.
    /// </summary>
    [Fact]
    public void RefreshRecycleBin_WhenBinIsNotEmpty_SetsNonNullIcon()
    {
        // Arrange
        var fullIcon = new System.Windows.Media.Imaging.BitmapImage();

        _shellMock
            .Setup(s => s.GetRecycleBinState())
            .Returns(new RecycleBinState(IsEmpty: false, ItemCount: 5, TotalSize: 2048));

        _shellMock
            .Setup(s => s.GetSystemIcon(SystemIconKind.RecycleBin, It.IsAny<int>()))
            .Returns(fullIcon);

        var model = BuildModel((SystemIconKind.RecycleBin, visible: true, order: 0));
        using var sut = BuildSut(model);

        // Act
        sut.RefreshRecycleBin();

        // Assert
        var binItem = model.Items.Single(i => i.Kind == SystemIconKind.RecycleBin);
        Assert.NotNull(binItem.Icon);
    }

    /// <summary>
    /// Verifies that <see cref="SystemIconsPanelViewModel.RefreshRecycleBin"/> does not throw when
    /// <see cref="ISystemShellService.GetRecycleBinState"/> throws.
    /// </summary>
    [Fact]
    public void RefreshRecycleBin_DoesNotThrow_WhenShellServiceFails()
    {
        // Arrange
        _shellMock
            .Setup(s => s.GetRecycleBinState())
            .Throws<Exception>();

        var model = BuildModel((SystemIconKind.RecycleBin, visible: true, order: 0));
        using var sut = BuildSut(model);

        // Act & Assert
        var ex = Record.Exception(() => sut.RefreshRecycleBin());
        Assert.Null(ex);
    }

    // ── RecycleBinChanged event: the panel stays wired to the shell notification ──────────────
    //
    // These two tests deliberately assert the *wiring* (subscribe / handle safely / unsubscribe)
    // rather than the icon that ends up on the model item after the event is raised.
    //
    // SystemIconsPanelViewModel.OnRecycleBinChanged marshals RefreshRecycleBin onto
    // Application.Current's dispatcher, because the shell raises RecycleBinChanged from a polling
    // thread. In the test host that dispatcher is either absent or dead:
    //   * run alone, Application.Current is null — the handler degrades to an inline refresh;
    //   * run as part of the full suite, ChildWindowViewModelTests has bootstrapped a process-wide
    //     WPF Application (it needs one for template resolution) on whichever worker thread reached
    //     it first, and nothing ever pumps that Application's message queue — so anything posted via
    //     BeginInvoke is queued and never executed.
    // Asserting the refreshed icon here would therefore pass or fail purely on test execution order.
    // WindowManagerTests documents the same hazard for SaveWindowsState vs SaveWindowsStateNow and
    // resolves it the same way: assert through the deterministic, synchronous entry point. The icon
    // refresh itself — the whole body of what the handler delegates to — is covered above by
    // RefreshRecycleBin_UpdatesRecycleBinItemIcon and
    // RefreshRecycleBin_DoesNotThrow_WhenShellServiceFails.

    /// <summary>
    /// Verifies that the constructor subscribes to <see cref="ISystemShellService.RecycleBinChanged"/>
    /// for the view model's lifetime, and that <see cref="SystemIconsPanelViewModel.Dispose"/>
    /// unsubscribes again.
    /// </summary>
    [Fact]
    public void Constructor_SubscribesToRecycleBinChanged_AndDisposeUnsubscribes()
    {
        // Arrange
        var model = BuildModel((SystemIconKind.RecycleBin, visible: true, order: 0));
        var sut = BuildSut(model);

        // Assert — subscribed for the lifetime of the ViewModel …
        _shellMock.VerifyAdd(
            s => s.RecycleBinChanged += It.IsAny<EventHandler<RecycleBinState>>(),
            Times.Once);

        // Act
        sut.Dispose();

        // Assert — … and released again on Dispose, so a long-lived shell service does not keep a
        // closed panel alive.
        _shellMock.VerifyRemove(
            s => s.RecycleBinChanged -= It.IsAny<EventHandler<RecycleBinState>>(),
            Times.Once);
    }

    /// <summary>
    /// Verifies that the shell's <see cref="ISystemShellService.RecycleBinChanged"/> handler never
    /// throws back into the shell's own polling thread, whether it refreshes inline or marshals to a
    /// dispatcher.
    /// </summary>
    [Fact]
    public void OnRecycleBinChanged_WhenEventFired_DoesNotThrowOnTheShellsPollingThread()
    {
        // Arrange
        _shellMock
            .Setup(s => s.GetSystemIcon(SystemIconKind.RecycleBin, It.IsAny<int>()))
            .Returns(new System.Windows.Media.Imaging.BitmapImage());

        var model = BuildModel((SystemIconKind.RecycleBin, visible: true, order: 0));
        using var sut = BuildSut(model);

        // Act — simulate the shell service firing RecycleBinChanged
        var exception = Record.Exception(() => _shellMock.Raise(
            s => s.RecycleBinChanged += null,
            _shellMock.Object,
            new RecycleBinState(IsEmpty: false, ItemCount: 2, TotalSize: 512)));

        // Assert — the handler never throws back into the shell's polling loop, whether it refreshes
        // inline (no dispatcher) or posts to one.
        Assert.Null(exception);
    }

    // ── Dispose: does not throw ───────────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="SystemIconsPanelViewModel.Dispose"/> does not throw.
    /// </summary>
    [Fact]
    public void Dispose_DoesNotThrow()
    {
        // Arrange
        var model = BuildModel((SystemIconKind.ThisPc, visible: true, order: 0));
        var sut = BuildSut(model);

        // Act & Assert
        var ex = Record.Exception(() => sut.Dispose());
        Assert.Null(ex);
    }

    // ── ui-polishing-1 §5.4/§5.6: SelectedItem ────────────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="SystemIconsPanelViewModel.SelectedItem"/> can be set and read back.
    /// </summary>
    [Fact]
    public void SelectedItem_CanBeSetAndRead()
    {
        var model = BuildModel((SystemIconKind.ThisPc, visible: true, order: 0));
        using var sut = BuildSut(model);
        var item = model.Items.Single();

        sut.SelectedItem = item;

        Assert.Same(item, sut.SelectedItem);
    }

    /// <summary>
    /// Verifies that <see cref="SystemIconsPanelViewModel.SelectedItem"/> can be cleared back to
    /// <c>null</c>.
    /// </summary>
    [Fact]
    public void SelectedItem_CanBeClearedBackToNull()
    {
        var model = BuildModel((SystemIconKind.ThisPc, visible: true, order: 0));
        using var sut = BuildSut(model);
        sut.SelectedItem = model.Items.Single();

        sut.SelectedItem = null;

        Assert.Null(sut.SelectedItem);
    }

    /// <summary>
    /// ui-polishing-1 §5.4: verifies that <see cref="SystemIconsPanelViewModel.SelectedItem"/> is
    /// cleared when the selected item's <see cref="SystemIconItemModel.IsVisible"/> flips to <c>false</c>.
    /// </summary>
    [Fact]
    public void SelectedItem_ClearedWhenItemIsHidden()
    {
        // ui-polishing-1 §5.4: an item that stops being visible must not remain "selected" purely by
        // dangling reference — RebuildVisibleItems (triggered here by IsVisible flipping) must clear it.
        var model = BuildModel((SystemIconKind.ThisPc, visible: true, order: 0));
        using var sut = BuildSut(model);
        var item = model.Items.Single();
        sut.SelectedItem = item;

        item.IsVisible = false;

        Assert.Null(sut.SelectedItem);
    }

    /// <summary>
    /// Verifies that <see cref="SystemIconsPanelViewModel.SelectedItem"/> is cleared when the selected
    /// item is removed from the model's item collection.
    /// </summary>
    [Fact]
    public void SelectedItem_ClearedWhenItemIsRemovedFromModel()
    {
        var model = BuildModel(
            (SystemIconKind.ThisPc, visible: true, order: 0),
            (SystemIconKind.RecycleBin, visible: true, order: 1));
        using var sut = BuildSut(model);
        var item = model.Items.First(i => i.Kind == SystemIconKind.ThisPc);
        sut.SelectedItem = item;

        model.Items.Remove(item);

        Assert.Null(sut.SelectedItem);
    }

    /// <summary>
    /// ui-polishing-1 §5.4: verifies that <see cref="SystemIconsPanelViewModel.SelectedItem"/> survives
    /// a <see cref="SystemIconsPanelViewModel.Reorder"/> call untouched, since the same item instance
    /// simply moves to a different position.
    /// </summary>
    [Fact]
    public void SelectedItem_PreservedAcrossReorder()
    {
        // ui-polishing-1 §5.4: reorder moves the same object reference to a different position —
        // selection must survive untouched.
        var model = BuildModel(
            (SystemIconKind.ThisPc, visible: true, order: 0),
            (SystemIconKind.RecycleBin, visible: true, order: 1),
            (SystemIconKind.Network, visible: true, order: 2));
        using var sut = BuildSut(model);
        var item = model.Items.First(i => i.Kind == SystemIconKind.ThisPc);
        sut.SelectedItem = item;

        sut.Reorder(SystemIconKind.ThisPc, newOrder: 2);

        Assert.Same(item, sut.SelectedItem);
    }

    /// <summary>
    /// Verifies that setting <see cref="SystemIconsPanelViewModel.SelectedItem"/> to a non-null item
    /// notifies <see cref="IPanelSelectionCoordinator"/> of the claim.
    /// </summary>
    [Fact]
    public void SelectedItem_SetToNonNull_NotifiesCoordinator()
    {
        var model = BuildModel((SystemIconKind.ThisPc, visible: true, order: 0));
        using var sut = BuildSut(model);
        var item = model.Items.Single();

        sut.SelectedItem = item;

        _selectionCoordinatorMock.Verify(c => c.NotifySelected(sut), Times.Once);
    }

    /// <summary>
    /// Verifies that clearing <see cref="SystemIconsPanelViewModel.SelectedItem"/> to <c>null</c> does
    /// not notify <see cref="IPanelSelectionCoordinator"/>.
    /// </summary>
    [Fact]
    public void SelectedItem_SetToNull_DoesNotNotifyCoordinator()
    {
        var model = BuildModel((SystemIconKind.ThisPc, visible: true, order: 0));
        using var sut = BuildSut(model);
        sut.SelectedItem = model.Items.Single();
        _selectionCoordinatorMock.Invocations.Clear();

        sut.SelectedItem = null;

        _selectionCoordinatorMock.Verify(c => c.NotifySelected(It.IsAny<object>()), Times.Never);
    }

    /// <summary>
    /// Verifies that when <see cref="IPanelSelectionCoordinator.SelectionClaimed"/> fires for a
    /// different owner, <see cref="SystemIconsPanelViewModel.SelectedItem"/> is cleared.
    /// </summary>
    [Fact]
    public void SelectionClaimed_FromAnotherOwner_ClearsSelectedItem()
    {
        var model = BuildModel((SystemIconKind.ThisPc, visible: true, order: 0));
        using var sut = BuildSut(model);
        sut.SelectedItem = model.Items.Single();

        _selectionCoordinatorMock.Raise(c => c.SelectionClaimed += null, _selectionCoordinatorMock.Object, new object());

        Assert.Null(sut.SelectedItem);
    }

    /// <summary>
    /// Verifies that when <see cref="IPanelSelectionCoordinator.SelectionClaimed"/> fires for this same
    /// view model as owner, <see cref="SystemIconsPanelViewModel.SelectedItem"/> is left untouched.
    /// </summary>
    [Fact]
    public void SelectionClaimed_FromSelf_DoesNotClearSelectedItem()
    {
        var model = BuildModel((SystemIconKind.ThisPc, visible: true, order: 0));
        using var sut = BuildSut(model);
        var item = model.Items.Single();
        sut.SelectedItem = item;

        _selectionCoordinatorMock.Raise(c => c.SelectionClaimed += null, _selectionCoordinatorMock.Object, sut);

        Assert.Same(item, sut.SelectedItem);
    }

    /// <summary>
    /// Verifies that <see cref="SystemIconsPanelViewModel.Dispose"/> unsubscribes from
    /// <see cref="IPanelSelectionCoordinator.SelectionClaimed"/>, so a later claim from another owner
    /// does not touch the disposed instance or throw.
    /// </summary>
    [Fact]
    public void Dispose_UnsubscribesFromSelectionClaimed()
    {
        var coordinator = new PanelSelectionCoordinator();
        var model = BuildModel((SystemIconKind.ThisPc, visible: true, order: 0));
        var sut = BuildSut(model, coordinator);
        sut.SelectedItem = model.Items.Single();

        sut.Dispose();

        var exception = Record.Exception(() => coordinator.NotifySelected(new object()));
        Assert.Null(exception);
    }

    /// <summary>
    /// Integration-style: verifies that two <see cref="SystemIconsPanelViewModel"/> instances sharing a
    /// real <see cref="PanelSelectionCoordinator"/> clear each other's selection whenever one selects
    /// an item.
    /// </summary>
    [Fact]
    public void SharingCoordinatorWithAnotherSidebarInstance_SelectingInOneClearsTheOther()
    {
        // Integration-style: a real (non-mocked) coordinator shared by two independent
        // SystemIconsPanelViewModel instances — same DoD as ChildWindowViewModelTests'
        // TwoPanels_SharingCoordinator_SelectingInOneClearsTheOther, applied within this panel type.
        var coordinator = new PanelSelectionCoordinator();
        var model1 = BuildModel((SystemIconKind.ThisPc, visible: true, order: 0));
        var model2 = BuildModel((SystemIconKind.RecycleBin, visible: true, order: 0));
        using var sut1 = BuildSut(model1, coordinator);
        using var sut2 = BuildSut(model2, coordinator);

        var item1 = model1.Items.Single();
        var item2 = model2.Items.Single();

        sut1.SelectedItem = item1;
        sut2.SelectedItem = item2;

        Assert.Null(sut1.SelectedItem);
        Assert.Same(item2, sut2.SelectedItem);
    }

    // ── SendToRecycleBin: post-recycle cleanup of dead references (review cycle 3, M4) ──────────────

    /// <summary>
    /// M4: a path that is really gone after the Recycle Bin operation is taken out of the panels (a drag from a
    /// panel onto the sidebar's Recycle Bin is recycled after the panel's own moved-out check may have given up);
    /// a path that still exists (declined / failed) is left in place.
    /// </summary>
    [Fact]
    public void SendToRecycleBin_RemovesVanishedPathsFromPanels_KeepsSurvivors()
    {
        var survivor = Path.GetTempFileName();
        var gone = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            IReadOnlyCollection<string>? removed = null;
            _windowManagerMock.Setup(wm => wm.RemoveItemsFromPanels(It.IsAny<IReadOnlyCollection<string>>()))
                .Callback<IReadOnlyCollection<string>>(p => removed = p);
            using var sut = BuildSut(BuildModel());

            sut.SendToRecycleBin([survivor, gone], IntPtr.Zero);

            _shellMock.Verify(s => s.SendToRecycleBin(It.IsAny<string[]>(), IntPtr.Zero), Times.Once);
            Assert.Equal([gone], removed);
        }
        finally
        {
            File.Delete(survivor);
        }
    }

    /// <summary>M4: when every path survived (e.g. the user declined the nuke warning) the panels are not touched.</summary>
    [Fact]
    public void SendToRecycleBin_NothingVanished_DoesNotTouchPanels()
    {
        var survivor = Path.GetTempFileName();
        try
        {
            using var sut = BuildSut(BuildModel());

            sut.SendToRecycleBin([survivor], IntPtr.Zero);

            _windowManagerMock.Verify(wm => wm.RemoveItemsFromPanels(It.IsAny<IReadOnlyCollection<string>>()), Times.Never);
        }
        finally
        {
            File.Delete(survivor);
        }
    }

    /// <summary>M4: a failing cleanup never breaks the drop handler - the bin icon refresh still runs.</summary>
    [Fact]
    public void SendToRecycleBin_CleanupThrows_IsSwallowed()
    {
        _windowManagerMock.Setup(wm => wm.RemoveItemsFromPanels(It.IsAny<IReadOnlyCollection<string>>()))
            .Throws(new InvalidOperationException("boom"));
        using var sut = BuildSut(BuildModel());

        var exception = Record.Exception(() => sut.SendToRecycleBin([@"C:\definitely\missing-file.txt"], IntPtr.Zero));

        Assert.Null(exception);
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    private SystemIconsPanelModel BuildModel(params (SystemIconKind kind, bool visible, int order)[] items)
    {
        var model = new SystemIconsPanelModel();
        model.Items.Clear();

        foreach (var (kind, visible, order) in items)
        {
            model.Items.Add(new SystemIconItemModel
            {
                Kind = kind,
                IsVisible = visible,
                Order = order,
            });
        }

        return model;
    }

    private SystemIconsPanelViewModel BuildSut(SystemIconsPanelModel model, IPanelSelectionCoordinator? selectionCoordinator = null)
        => new(model, _shellMock.Object, selectionCoordinator ?? _selectionCoordinatorMock.Object, _windowManagerMock.Object);
}
