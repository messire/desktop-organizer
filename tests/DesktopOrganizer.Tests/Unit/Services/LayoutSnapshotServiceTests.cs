using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="LayoutSnapshotService"/>. <see cref="IWindowManager"/> and
/// <see cref="IAppSettings"/> are mocked so the real WPF <c>ChildWindow</c>/<c>WindowManager</c> stack
/// never needs to be constructed.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class LayoutSnapshotServiceTests
{
    private readonly Mock<IWindowManager> _windowManagerMock = new();
    private readonly Mock<IAppSettings> _appSettingsMock = new();

    private LayoutSnapshotService BuildSut(IEnumerable<ChildWindowModel>? panels = null)
    {
        var panelList = (panels ?? Enumerable.Empty<ChildWindowModel>()).ToList();
        _windowManagerMock.Setup(wm => wm.GetAllChildWindows()).Returns(panelList);
        _windowManagerMock.Setup(wm => wm.SyncAllWindowsToModels());
        _windowManagerMock.Setup(wm => wm.SaveWindowsStateNow());
        _windowManagerMock.Setup(wm => wm.RemoveChildWindow(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<bool>()));
        _windowManagerMock.Setup(wm => wm.CreateChildWindow(It.IsAny<ChildWindowModel>()));

        _appSettingsMock.SetupProperty(s => s.LayoutSnapshots, "[]");
        _appSettingsMock.Setup(s => s.Save());

        return new LayoutSnapshotService(_windowManagerMock.Object, _appSettingsMock.Object);
    }

    // ── Capture — M3 regression: models must be synced before serializing ──────────────────────

    [Fact]
    public void Capture_SyncsAllWindowsToModels_BeforeReadingPanels()
    {
        // Regression test for the Phase 7 / Block C M3 review finding: WindowManager's regular save is
        // debounced up to 500ms, so ChildWindowModel.FilePaths (only refreshed by
        // ChildWindow.SyncToModel) can be stale for up to that long. Capture must force a sync first so
        // it never serializes a snapshot with out-of-date FilePaths.
        var sut = BuildSut();
        var order = new List<string>();
        _windowManagerMock.Setup(wm => wm.SyncAllWindowsToModels())
            .Callback(() => order.Add("sync"));
        _windowManagerMock.Setup(wm => wm.GetAllChildWindows())
            .Callback(() => order.Add("read"))
            .Returns([]);

        sut.Capture("my-layout");

        Assert.Equal(["sync", "read"], order);
        _windowManagerMock.Verify(wm => wm.SyncAllWindowsToModels(), Times.Once);
    }

    [Fact]
    public void Capture_StoresOneJsonItemPerPanel()
    {
        var panel1 = new ChildWindowModel { Title = "Panel 1" };
        var panel2 = new ChildWindowModel { Title = "Panel 2" };
        var sut = BuildSut(panels: [panel1, panel2]);

        var snapshot = sut.Capture("my-layout");

        Assert.Equal(2, snapshot.PanelJsonItems.Count);
        Assert.Contains("Panel 1", snapshot.PanelJsonItems[0]);
        Assert.Contains("Panel 2", snapshot.PanelJsonItems[1]);
    }

    // ── Restore — M4 regression: bulk removal must defer per-panel writes, flush once ──────────

    [Fact]
    public void Restore_UnknownSnapshotId_DoesNothing()
    {
        var sut = BuildSut();

        var exception = Record.Exception(() => sut.Restore("does-not-exist"));

        Assert.Null(exception);
        _windowManagerMock.Verify(wm => wm.RemoveChildWindow(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public void Restore_RemovesEveryCurrentPanel_WithDeferSaveTrue_ThenFlushesOnce()
    {
        // Regression test for the Phase 7 / Block C M4 review finding: removing N current panels while
        // restoring a snapshot must not perform N synchronous full-state writes — each removal defers
        // its write, and Restore flushes synchronously exactly once after the whole panel set has been
        // torn down and recreated.
        var panel1 = new ChildWindowModel { Id = Guid.NewGuid() };
        var panel2 = new ChildWindowModel { Id = Guid.NewGuid() };
        var sut = BuildSut(panels: [panel1, panel2]);
        var snapshot = sut.Capture("before");

        sut.Restore(snapshot.Id);

        _windowManagerMock.Verify(
            wm => wm.RemoveChildWindow(It.IsAny<Guid>(), false, true),
            Times.Exactly(2));
        _windowManagerMock.Verify(wm => wm.SaveWindowsStateNow(), Times.Once);
    }

    [Fact]
    public void Restore_RecreatesPanelsFromSnapshot_WithFreshIds()
    {
        var panel = new ChildWindowModel { Id = Guid.NewGuid(), Title = "Original" };
        var sut = BuildSut(panels: [panel]);
        var snapshot = sut.Capture("before");

        ChildWindowModel? created = null;
        _windowManagerMock.Setup(wm => wm.CreateChildWindow(It.IsAny<ChildWindowModel>()))
            .Callback<ChildWindowModel>(m => created = m);

        sut.Restore(snapshot.Id);

        Assert.NotNull(created);
        Assert.Equal("Original", created!.Title);
        Assert.NotEqual(panel.Id, created.Id);
    }
}
