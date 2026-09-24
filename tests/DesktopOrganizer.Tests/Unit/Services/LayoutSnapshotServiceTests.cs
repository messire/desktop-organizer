using DesktopOrganizer.Core;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;
using DesktopOrganizer.UI.State;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="LayoutSnapshotService"/>. <see cref="IWindowManager"/>,
/// <see cref="IApplicationStateService"/> and <see cref="IPanelItemStorageService"/> are mocked so the
/// real WPF <c>ChildWindow</c>/<c>WindowManager</c> stack never needs to be constructed.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class LayoutSnapshotServiceTests
{
    private readonly Mock<IWindowManager> _windowManagerMock = new();
    private readonly Mock<IApplicationStateService> _applicationStateServiceMock = new();
    private readonly Mock<IPanelItemStorageService> _itemStorageMock = new();

    private LayoutSnapshotService BuildSut(
        IEnumerable<ChildWindowModel>? panels = null,
        IReadOnlyList<LayoutSnapshot>? initialSnapshots = null)
    {
        var panelList = (panels ?? Enumerable.Empty<ChildWindowModel>()).ToList();
        _windowManagerMock.Setup(wm => wm.GetAllChildWindows()).Returns(panelList);
        _windowManagerMock.Setup(wm => wm.SyncAllWindowsToModels());
        _windowManagerMock.Setup(wm => wm.SaveWindowsStateNow());
        _windowManagerMock.Setup(wm => wm.RemoveChildWindow(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<bool>()));
        _windowManagerMock.Setup(wm => wm.CreateChildWindow(It.IsAny<ChildWindowModel>()));

        _applicationStateServiceMock.SetupGet(s => s.Snapshots).Returns(initialSnapshots ?? []);
        _applicationStateServiceMock.Setup(s => s.SaveNow());

        // Default: nothing is managed, no manifest candidates — matches the pre-existing (pre-MAJOR-1-fix)
        // behavior for every test that doesn't itself save/restore a panel with a stale FilePaths entry.
        _itemStorageMock.Setup(s => s.IsManaged(It.IsAny<string>())).Returns(false);
        _itemStorageMock.Setup(s => s.FindManagedCandidates(It.IsAny<string>())).Returns([]);

        return new LayoutSnapshotService(_windowManagerMock.Object, _applicationStateServiceMock.Object, _itemStorageMock.Object);
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

    [Fact]
    public void Capture_CallsApplicationStateServiceSaveNow()
    {
        var sut = BuildSut();

        sut.Capture("my-layout");

        _applicationStateServiceMock.Verify(s => s.SaveNow(), Times.Once);
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

    // ── MAJOR-1 (P08 fix cycle 2, 2026-09-22): cross-panel-aware stale-path resolution ──────────────

    [Fact]
    public void Restore_SinglePanelWithUnambiguousStaleOriginal_RemapsToManagedCopyBeforeCreating()
    {
        // Baseline: Restore itself must now perform the same stale-original->managed-copy resolution
        // WindowManager.LoadWindowsState already performs (M1, fix cycle 1) BEFORE handing any panel to
        // CreateChildWindow -- previously Restore had no resolution logic of its own at all (it relied
        // entirely on each panel's own, later, per-panel ChildWindowViewModel.LoadFromModel, which this
        // mocked IWindowManager never constructs), so a mocked CreateChildWindow would otherwise observe
        // the stale path completely untouched.
        const string stale = @"C:\fake\stale-original.lnk";
        const string managed = @"C:\ManagedRoot\managed.lnk";
        var panel = new ChildWindowModel { Id = Guid.NewGuid(), FilePaths = [stale] };
        var sut = BuildSut(panels: [panel]);
        var snapshot = sut.Capture("before");

        _itemStorageMock.Setup(s => s.FindManagedCandidates(stale)).Returns([managed]);

        ChildWindowModel? created = null;
        _windowManagerMock.Setup(wm => wm.CreateChildWindow(It.IsAny<ChildWindowModel>()))
            .Callback<ChildWindowModel>(m => created = m);

        sut.Restore(snapshot.Id);

        Assert.NotNull(created);
        Assert.Equal(managed, Assert.Single(created!.FilePaths));
    }

    [Fact]
    public void Restore_TwoPanelsWithDifferentStaleOriginalsResolvingToTheSameSingleCandidate_NeitherClaimsIt()
    {
        // Regression for the exact MAJOR-1 scenario: two snapshot panels, each with a DIFFERENT stale
        // original path, both unambiguously resolving (per FindManagedCandidates) to the SAME single
        // managed copy. The pre-fix implementation created panels one at a time with no cross-panel
        // visibility, so nothing here would have stopped both from being handed the same managed copy.
        // Restore must now resolve all panels together, and — per the MINOR-2 tie-break rule
        // (ADR-009 Amendment 2) also fixed this cycle — a candidate contested by 2+ distinct stale
        // entries is claimed by NEITHER, not silently awarded to whichever panel is processed first.
        const string staleA = @"C:\fake\stale-a.lnk";
        const string staleB = @"C:\fake\stale-b.lnk";
        const string onlyCandidate = @"C:\ManagedRoot\only.lnk";
        var panelA = new ChildWindowModel { Id = Guid.NewGuid(), FilePaths = [staleA] };
        var panelB = new ChildWindowModel { Id = Guid.NewGuid(), FilePaths = [staleB] };
        var sut = BuildSut(panels: [panelA, panelB]);
        var snapshot = sut.Capture("before");

        _itemStorageMock.Setup(s => s.FindManagedCandidates(staleA)).Returns([onlyCandidate]);
        _itemStorageMock.Setup(s => s.FindManagedCandidates(staleB)).Returns([onlyCandidate]);

        var created = new List<ChildWindowModel>();
        _windowManagerMock.Setup(wm => wm.CreateChildWindow(It.IsAny<ChildWindowModel>()))
            .Callback<ChildWindowModel>(m => created.Add(m));

        sut.Restore(snapshot.Id);

        Assert.Equal(2, created.Count);
        Assert.Equal(staleA, Assert.Single(created[0].FilePaths));
        Assert.Equal(staleB, Assert.Single(created[1].FilePaths));
    }

    [Fact]
    public void Restore_StaleOriginalCandidateAlreadyReferencedByAnotherPanel_LeavesItUntouched()
    {
        // Cross-panel ownership: panel B already directly references the managed copy (not stale);
        // panel A's stale entry's only live manifest candidate is that SAME managed copy. It must stay
        // with panel B — panel A's entry is left stale, not silently duplicated across two panels.
        const string stale = @"C:\fake\stale-original.lnk";
        const string shared = @"C:\ManagedRoot\shared.lnk";
        var panelA = new ChildWindowModel { Id = Guid.NewGuid(), FilePaths = [stale] };
        var panelB = new ChildWindowModel { Id = Guid.NewGuid(), FilePaths = [shared] };
        var sut = BuildSut(panels: [panelA, panelB]);
        var snapshot = sut.Capture("before");

        _itemStorageMock.Setup(s => s.IsManaged(shared)).Returns(true);
        _itemStorageMock.Setup(s => s.FindManagedCandidates(stale)).Returns([shared]);

        var created = new List<ChildWindowModel>();
        _windowManagerMock.Setup(wm => wm.CreateChildWindow(It.IsAny<ChildWindowModel>()))
            .Callback<ChildWindowModel>(m => created.Add(m));

        sut.Restore(snapshot.Id);

        Assert.Equal(2, created.Count);
        Assert.Equal(stale, Assert.Single(created[0].FilePaths)); // untouched
        Assert.Equal(shared, Assert.Single(created[1].FilePaths));
    }

    // ── ADR-028/ADR-029 (docs/PLANS/workspace-grid-migration.md) — workspace-grid/icon-layout fields
    // must survive the raw ChildWindowModel JSON round trip Capture/Restore uses, including two
    // restore cycles in a row producing identical values (docs/fix-prompts/06-fit-content.md DoD). ──

    [Fact]
    public void Restore_PreservesGeometryModeSpanUnitsSizingModeAndIconLayoutPolicy()
    {
        var panel = new ChildWindowModel
        {
            Id = Guid.NewGuid(),
            Title = "Grid-aligned",
            GeometryMode = PanelGeometryMode.GridAligned,
            WidthSpanUnits = 20,
            HeightSpanUnits = 15,
            SizingMode = PanelSizingMode.AutoContent,
            IconLayoutPolicy = IconLayoutPolicy.FixedGap,
        };
        var sut = BuildSut(panels: [panel]);
        var snapshot = sut.Capture("before");

        ChildWindowModel? created = null;
        _windowManagerMock.Setup(wm => wm.CreateChildWindow(It.IsAny<ChildWindowModel>()))
            .Callback<ChildWindowModel>(m => created = m);

        sut.Restore(snapshot.Id);

        Assert.NotNull(created);
        Assert.Equal(PanelGeometryMode.GridAligned, created!.GeometryMode);
        Assert.Equal(20, created.WidthSpanUnits);
        Assert.Equal(15, created.HeightSpanUnits);
        Assert.Equal(PanelSizingMode.AutoContent, created.SizingMode);
        Assert.Equal(IconLayoutPolicy.FixedGap, created.IconLayoutPolicy);
    }

    [Fact]
    public void Restore_TwiceInARow_ProducesIdenticalWorkspaceGridAndIconLayoutFields()
    {
        // docs/fix-prompts/06-fit-content.md DoD: "snapshots after two restore cycles are identical."
        // Restoring the same snapshot twice in a row (each restore reads the same, unmutated
        // snapshot.PanelJsonItems) must produce two panels agreeing on every new field — only Id is
        // expected to differ (fresh per restore, by design).
        var panel = new ChildWindowModel
        {
            Id = Guid.NewGuid(),
            GeometryMode = PanelGeometryMode.GridAligned,
            WidthSpanUnits = 32,
            HeightSpanUnits = 30,
            SizingMode = PanelSizingMode.AutoContent,
            IconLayoutPolicy = IconLayoutPolicy.FixedGap,
        };
        var sut = BuildSut(panels: [panel]);
        var snapshot = sut.Capture("before");

        var created = new List<ChildWindowModel>();
        _windowManagerMock.Setup(wm => wm.CreateChildWindow(It.IsAny<ChildWindowModel>()))
            .Callback<ChildWindowModel>(m => created.Add(m));

        sut.Restore(snapshot.Id);
        sut.Restore(snapshot.Id);

        Assert.Equal(2, created.Count);
        var first = created[0];
        var second = created[1];
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(first.GeometryMode, second.GeometryMode);
        Assert.Equal(first.WidthSpanUnits, second.WidthSpanUnits);
        Assert.Equal(first.HeightSpanUnits, second.HeightSpanUnits);
        Assert.Equal(first.SizingMode, second.SizingMode);
        Assert.Equal(first.IconLayoutPolicy, second.IconLayoutPolicy);
    }

    // ── Load — reads IApplicationStateService.Snapshots ─────────────────────────────────────────

    [Fact]
    public void Load_PopulatesSnapshotsFromApplicationStateService()
    {
        var preloaded = new LayoutSnapshot { Id = "s1", Name = "Preloaded" };
        var sut = BuildSut(initialSnapshots: [preloaded]);

        sut.Load();

        Assert.Contains(sut.Snapshots, s => s.Id == "s1");
    }

    [Fact]
    public void Load_CalledTwice_DoesNotDuplicateEntries()
    {
        var preloaded = new LayoutSnapshot { Id = "s1", Name = "Preloaded" };
        var sut = BuildSut(initialSnapshots: [preloaded]);

        sut.Load();
        sut.Load();

        Assert.Single(sut.Snapshots);
    }

    [Fact]
    public void Load_CalledAgainAfterCaptureInSession_DoesNotDropTheNewSnapshot()
    {
        // Regression test for the M-2 review finding: Load() used to unconditionally Clear()+re-add
        // from IApplicationStateService.Snapshots on every call — a value frozen at the single startup
        // Load(), never updated as snapshots are captured/deleted during the session (those live only
        // in _snapshots). A second Load() call (e.g. LayoutSnapshotsViewModel being reconstructed when
        // the settings window's snapshot section is reopened) must not wipe out a snapshot captured
        // earlier in the same session.
        var preloaded = new LayoutSnapshot { Id = "s1", Name = "Preloaded" };
        var sut = BuildSut(initialSnapshots: [preloaded]);
        sut.Load();

        sut.Capture("captured-this-session");

        sut.Load();

        Assert.Contains(sut.Snapshots, s => s.Name == "captured-this-session");
        Assert.Contains(sut.Snapshots, s => s.Id == "s1");
    }
}
