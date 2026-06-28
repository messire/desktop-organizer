using System.Windows.Media;
using DesktopOrganizer.Core.Models;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Models;
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
    // ── helpers ──────────────────────────────────────────────────────────────

    private readonly Mock<ISystemShellService> _shellMock = new();

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

    private SystemIconsPanelViewModel BuildSut(SystemIconsPanelModel model)
        => new(model, _shellMock.Object);

    // ── VisibleItems: only visible icons are exposed ──────────────────────────

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

    // ── RecycleBinChanged event: icon refreshed when event fires ─────────────

    [Fact]
    public void OnRecycleBinChanged_WhenEventFired_UpdatesRecycleBinItemIcon()
    {
        // Arrange
        var updatedIcon = new System.Windows.Media.Imaging.BitmapImage();

        _shellMock
            .Setup(s => s.GetSystemIcon(SystemIconKind.RecycleBin, It.IsAny<int>()))
            .Returns(updatedIcon);

        var model = BuildModel((SystemIconKind.RecycleBin, visible: true, order: 0));
        using var sut = BuildSut(model);

        // Act — simulate the shell service firing RecycleBinChanged
        _shellMock.Raise(s => s.RecycleBinChanged += null,
            _shellMock.Object,
            new RecycleBinState(IsEmpty: false, ItemCount: 2, TotalSize: 512));

        // Assert — icon was refreshed on the model item
        var binItem = model.Items.Single(i => i.Kind == SystemIconKind.RecycleBin);
        Assert.Equal(updatedIcon, binItem.Icon);
    }

    // ── Dispose: does not throw ───────────────────────────────────────────────

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
}
