using DesktopOrganizer.Core;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Abstractions.Storage;
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
    private readonly Mock<IStatusNotifier> _statusNotifierMock = new();
    private readonly Mock<ILocalizationService> _localizationMock = new();
    private readonly Mock<IApplicationStateStore> _stateStoreMock = new();

    // ── Capture — M3 regression: models must be synced before serializing ──────────────────────

    /// <summary>
    /// Verifies that <see cref="LayoutSnapshotService.Capture"/> forces a
    /// <see cref="IWindowManager.SyncAllWindowsToModels"/> before it reads the panels, so it never
    /// serializes a snapshot with stale <c>ChildWindowModel.FilePaths</c>.
    /// </summary>
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

    /// <summary>
    /// Verifies that <see cref="LayoutSnapshotService.Capture"/> stores exactly one serialized JSON item
    /// per current panel.
    /// </summary>
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

    /// <summary>
    /// Verifies that <see cref="LayoutSnapshotService.Capture"/> persists the new snapshot by calling
    /// <see cref="IApplicationStateService.SaveNow"/>.
    /// </summary>
    [Fact]
    public void Capture_CallsApplicationStateServiceSaveNow()
    {
        var sut = BuildSut();

        sut.Capture("my-layout");

        _applicationStateServiceMock.Verify(s => s.SaveNow(), Times.Once);
    }

    // ── Restore — M4 regression: bulk removal must defer per-panel writes, flush once ──────────

    /// <summary>
    /// Verifies that <see cref="LayoutSnapshotService.Restore"/> is a silent no-op for an unknown
    /// snapshot id: no panels are removed and no exception is thrown.
    /// </summary>
    [Fact]
    public void Restore_UnknownSnapshotId_DoesNothing()
    {
        var sut = BuildSut();

        var exception = Record.Exception(() => sut.Restore("does-not-exist"));

        Assert.Null(exception);
        _windowManagerMock.Verify(wm => wm.RemoveChildWindow(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<bool>()), Times.Never);
    }

    /// <summary>
    /// Verifies that <see cref="LayoutSnapshotService.Restore"/> removes every current panel with the
    /// save deferred, then flushes the window state exactly once after the whole panel set has been torn
    /// down and recreated — never once per removed panel.
    /// </summary>
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

    /// <summary>
    /// Verifies that <see cref="LayoutSnapshotService.Restore"/> recreates each panel from the snapshot's
    /// serialized data, preserving fields like <c>Title</c> but assigning a fresh <c>Id</c>.
    /// </summary>
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

    /// <summary>
    /// Verifies that a single restored panel whose only file path is a stale original with exactly one
    /// live managed candidate gets remapped to that managed copy before the panel is created.
    /// </summary>
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

    /// <summary>
    /// Verifies the ADR-009 Amendment 2 tie-break rule: when two panels' distinct stale originals both
    /// resolve to the same single managed candidate, that candidate is claimed by neither panel and both
    /// keep their original stale path.
    /// </summary>
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

    /// <summary>
    /// Verifies that a stale-original candidate already directly referenced (and owned) by another
    /// panel in the same restore stays with that owning panel; the panel whose stale entry would have
    /// claimed it is left with its original stale path instead of duplicating the reference.
    /// </summary>
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

    /// <summary>
    /// Verifies that the workspace-grid and icon-layout fields (<c>GeometryMode</c>, span units,
    /// <c>SizingMode</c>, <c>IconLayoutPolicy</c>) survive the Capture/Restore JSON round trip unchanged.
    /// </summary>
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

    /// <summary>
    /// Verifies that restoring the same, unmutated snapshot twice in a row produces two panels agreeing
    /// on every workspace-grid/icon-layout field — only <c>Id</c> is expected to differ per restore.
    /// </summary>
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

    /// <summary>
    /// Verifies that <see cref="LayoutSnapshotService.Load"/> populates
    /// <see cref="LayoutSnapshotService.Snapshots"/> from <see cref="IApplicationStateService.Snapshots"/>.
    /// </summary>
    [Fact]
    public void Load_PopulatesSnapshotsFromApplicationStateService()
    {
        var preloaded = new LayoutSnapshot { Id = "s1", Name = "Preloaded" };
        var sut = BuildSut(initialSnapshots: [preloaded]);

        sut.Load();

        Assert.Contains(sut.Snapshots, s => s.Id == "s1");
    }

    /// <summary>
    /// Verifies that calling <see cref="LayoutSnapshotService.Load"/> twice does not duplicate entries in
    /// <see cref="LayoutSnapshotService.Snapshots"/>.
    /// </summary>
    [Fact]
    public void Load_CalledTwice_DoesNotDuplicateEntries()
    {
        var preloaded = new LayoutSnapshot { Id = "s1", Name = "Preloaded" };
        var sut = BuildSut(initialSnapshots: [preloaded]);

        sut.Load();
        sut.Load();

        Assert.Single(sut.Snapshots);
    }

    /// <summary>
    /// Verifies that calling <see cref="LayoutSnapshotService.Load"/> again after a
    /// <see cref="LayoutSnapshotService.Capture"/> in the same session does not wipe out the snapshot
    /// captured earlier — the in-memory list is merged, not replaced wholesale.
    /// </summary>
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

    // ── ADR-audit WP-B (B2): panels absent from the snapshot must not orphan their managed files ──

    /// <summary>
    /// Verifies that a current panel which the snapshot does not mention has its managed items returned
    /// to their original location (<see cref="IPanelItemStorageService.Restore"/>) BEFORE the panel is
    /// removed, while the items of a panel the snapshot does reference are left alone.
    /// </summary>
    [Fact]
    public void Restore_PanelOutsideSnapshot_RestoresItsManagedItems_BeforeRemovingIt()
    {
        const string inSnapshot = @"C:\ManagedRoot\a\kept.lnk";
        const string orphan = @"C:\ManagedRoot\b\orphan.lnk";
        var panelA = new ChildWindowModel { Id = Guid.NewGuid(), FilePaths = [inSnapshot] };
        var sut = BuildSut(panels: [panelA]);
        MarkManaged(inSnapshot, orphan);
        var snapshot = sut.Capture("before");

        // A panel created AFTER the snapshot was taken.
        var panelC = new ChildWindowModel { Id = Guid.NewGuid(), FilePaths = [orphan] };
        _windowManagerMock.Setup(wm => wm.GetAllChildWindows()).Returns([panelA, panelC]);

        var order = new List<string>();
        _itemStorageMock.Setup(s => s.Restore(orphan)).Returns(RestoreOutcome.Restored).Callback(() => order.Add("restore"));
        _windowManagerMock.Setup(wm => wm.RemoveChildWindow(panelC.Id, false, true)).Callback(() => order.Add("remove-C"));

        sut.Restore(snapshot.Id);

        _itemStorageMock.Verify(s => s.Restore(orphan), Times.Once);
        _itemStorageMock.Verify(s => s.Restore(inSnapshot), Times.Never);
        Assert.Equal(["restore", "remove-C"], order);
        _windowManagerMock.Verify(wm => wm.RemoveChildWindow(panelA.Id, false, true), Times.Once);

        // F6: the one returned item is reported as a (self-hiding) Success; nothing failed, so no Critical.
        _statusNotifierMock.Verify(n => n.Show(StatusSeverity.Success, "Snapshot_Restore_ItemsReturned_Format:1", It.IsAny<string?>()), Times.Once);
        _statusNotifierMock.Verify(n => n.Show(StatusSeverity.Critical, It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }

    /// <summary>
    /// Verifies that when every current panel's managed items are referenced by the snapshot, nothing is
    /// restored at all — the recreated panels still point at the very same managed paths.
    /// </summary>
    [Fact]
    public void Restore_AllManagedItemsReferencedBySnapshot_NeverCallsStorageRestore()
    {
        const string managed = @"C:\ManagedRoot\a\kept.lnk";
        var panel = new ChildWindowModel { Id = Guid.NewGuid(), FilePaths = [managed] };
        var sut = BuildSut(panels: [panel]);
        MarkManaged(managed);
        var snapshot = sut.Capture("before");

        sut.Restore(snapshot.Id);

        _itemStorageMock.Verify(s => s.Restore(It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    /// Verifies a managed item that now sits in a DIFFERENT panel than at capture time is still
    /// "referenced by the snapshot" (the set is global, not per panel) and is therefore not restored.
    /// </summary>
    [Fact]
    public void Restore_ManagedItemReferencedByAnotherSnapshotPanel_IsNotRestored()
    {
        const string managed = @"C:\ManagedRoot\a\moved.lnk";
        var snapshotPanel = new ChildWindowModel { Id = Guid.NewGuid(), FilePaths = [managed] };
        var sut = BuildSut(panels: [snapshotPanel]);
        MarkManaged(managed);
        var snapshot = sut.Capture("before");

        var currentPanel = new ChildWindowModel { Id = Guid.NewGuid(), FilePaths = [managed] };
        _windowManagerMock.Setup(wm => wm.GetAllChildWindows()).Returns([currentPanel]);

        sut.Restore(snapshot.Id);

        _itemStorageMock.Verify(s => s.Restore(It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    /// Verifies the fail-visibly rule (ADR-035): when an orphaned item cannot be returned, its panel is
    /// NOT removed, every other item is taken out of that panel (so nothing is referenced twice), the user
    /// is told via the status notifier, and the rest of the restore still completes.
    /// </summary>
    /// <param name="outcome">The non-<see cref="RestoreOutcome.Restored"/> outcome the storage reports.</param>
    [Theory]
    [InlineData(RestoreOutcome.Failed)]
    [InlineData(RestoreOutcome.OriginalPathUnknown)]
    public void Restore_OrphanedItemCannotBeRestored_KeepsPanelStripsOthersAndNotifies(RestoreOutcome outcome)
    {
        const string inSnapshot = @"C:\ManagedRoot\a\kept.lnk";
        const string stuck = @"C:\ManagedRoot\b\stuck.lnk";
        const string returned = @"C:\ManagedRoot\b\returned.lnk";
        const string plain = @"C:\Desktop\plain.txt";
        var panelA = new ChildWindowModel { Id = Guid.NewGuid(), FilePaths = [inSnapshot] };
        var sut = BuildSut(panels: [panelA]);
        MarkManaged(inSnapshot, stuck, returned);
        var snapshot = sut.Capture("before");

        var panelC = new ChildWindowModel { Id = Guid.NewGuid(), FilePaths = [stuck, returned, plain] };
        _windowManagerMock.Setup(wm => wm.GetAllChildWindows()).Returns([panelA, panelC]);
        _itemStorageMock.Setup(s => s.Restore(stuck)).Returns(outcome);
        _itemStorageMock.Setup(s => s.Restore(returned)).Returns(RestoreOutcome.Restored);

        IReadOnlyCollection<string>? stripped = null;
        _windowManagerMock.Setup(wm => wm.RemoveItemsFromPanels(It.IsAny<IReadOnlyCollection<string>>()))
            .Callback<IReadOnlyCollection<string>>(p => stripped = p);

        sut.Restore(snapshot.Id);

        _windowManagerMock.Verify(wm => wm.RemoveChildWindow(panelC.Id, It.IsAny<bool>(), It.IsAny<bool>()), Times.Never);
        _windowManagerMock.Verify(wm => wm.RemoveChildWindow(panelA.Id, false, true), Times.Once);
        Assert.NotNull(stripped);
        Assert.Equivalent(new[] { returned, plain }, stripped);
        _statusNotifierMock.Verify(n => n.Show(StatusSeverity.Critical, It.IsAny<string>(), It.IsAny<string?>()), Times.Once);
        _windowManagerMock.Verify(wm => wm.CreateChildWindow(It.IsAny<ChildWindowModel>()), Times.Once);
        _windowManagerMock.Verify(wm => wm.SaveWindowsStateNow(), Times.Once);
    }

    // ── WP-B review cycle 1 ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// F1: a panel that is part of the snapshot AND holds an item that cannot be returned must not be kept
    /// next to its recreated twin (a visible duplicate): the stuck path moves into the recreated panel and
    /// the old window is removed. Exactly one panel is created, it carries the stuck path, and nothing is
    /// stripped.
    /// </summary>
    /// <param name="outcome">The non-<see cref="RestoreOutcome.Restored"/> outcome the storage reports.</param>
    [Theory]
    [InlineData(RestoreOutcome.Failed)]
    [InlineData(RestoreOutcome.OriginalPathUnknown)]
    public void Restore_PanelInSnapshotHasUnreturnableItem_MergesIntoRecreatedPanelWithoutDuplicate(RestoreOutcome outcome)
    {
        const string inSnapshot = @"C:\ManagedRoot\a\kept.lnk";
        const string stuck = @"C:\ManagedRoot\a\stuck.lnk";
        var panelA = new ChildWindowModel { Id = Guid.NewGuid(), Title = "A", FilePaths = [inSnapshot] };
        var sut = BuildSut(panels: [panelA]);
        MarkManaged(inSnapshot, stuck);
        var snapshot = sut.Capture("before");

        // The same panel (same Id) gained an item after the snapshot was taken.
        panelA.FilePaths = [inSnapshot, stuck];
        _itemStorageMock.Setup(s => s.Restore(stuck)).Returns(outcome);
        var created = new List<ChildWindowModel>();
        _windowManagerMock.Setup(wm => wm.CreateChildWindow(It.IsAny<ChildWindowModel>()))
            .Callback<ChildWindowModel>(created.Add);

        sut.Restore(snapshot.Id);

        _windowManagerMock.Verify(wm => wm.RemoveChildWindow(panelA.Id, false, true), Times.Once);
        var recreated = Assert.Single(created);
        Assert.Equal("A", recreated.Title);
        Assert.Contains(stuck, recreated.FilePaths);
        Assert.Contains(inSnapshot, recreated.FilePaths);
        _windowManagerMock.Verify(wm => wm.RemoveItemsFromPanels(It.IsAny<IReadOnlyCollection<string>>()), Times.Never);
        _statusNotifierMock.Verify(n => n.Show(StatusSeverity.Critical, It.Is<string>(m => m.EndsWith(":1")), It.IsAny<string?>()), Times.Once);
    }

    /// <summary>
    /// F2: in a read-only session no managed item is returned (that would physically move a file although
    /// nothing is persisted): <see cref="IPanelItemStorageService.Restore"/> is never called, panels are
    /// removed and recreated as before, and no status is shown.
    /// </summary>
    [Fact]
    public void Restore_ReadOnlySession_NeverMovesFiles_AndStillReplacesPanels()
    {
        const string orphan = @"C:\ManagedRoot\b\orphan.lnk";
        var panelA = new ChildWindowModel { Id = Guid.NewGuid() };
        var sut = BuildSut(panels: [panelA]);
        var snapshot = sut.Capture("before");
        var panelC = new ChildWindowModel { Id = Guid.NewGuid(), FilePaths = [orphan] };
        _windowManagerMock.Setup(wm => wm.GetAllChildWindows()).Returns([panelA, panelC]);
        MarkManaged(orphan);
        _stateStoreMock.SetupGet(s => s.IsWritable).Returns(false);

        sut.Restore(snapshot.Id);

        _itemStorageMock.Verify(s => s.Restore(It.IsAny<string>()), Times.Never);
        _windowManagerMock.Verify(wm => wm.RemoveChildWindow(panelC.Id, false, true), Times.Once);
        _windowManagerMock.Verify(wm => wm.RemoveChildWindow(panelA.Id, false, true), Times.Once);
        _windowManagerMock.Verify(wm => wm.CreateChildWindow(It.IsAny<ChildWindowModel>()), Times.Once);
        _statusNotifierMock.Verify(n => n.Show(It.IsAny<StatusSeverity>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }

    /// <summary>
    /// F3: a Folder Portal panel mirrors a live folder and owns no managed items - it is skipped by the
    /// return pass (like <c>WindowManager.CollectManagedPaths</c>) and simply removed.
    /// </summary>
    [Fact]
    public void Restore_FolderPortalPanelOutsideSnapshot_IsSkippedByItemReturn()
    {
        const string mirrored = @"C:\ManagedRoot\p\mirrored.lnk";
        var panelA = new ChildWindowModel { Id = Guid.NewGuid() };
        var sut = BuildSut(panels: [panelA]);
        var snapshot = sut.Capture("before");
        var portal = new ChildWindowModel { Id = Guid.NewGuid(), SourceFolderPath = @"C:\Source", FilePaths = [mirrored] };
        _windowManagerMock.Setup(wm => wm.GetAllChildWindows()).Returns([panelA, portal]);
        MarkManaged(mirrored);

        sut.Restore(snapshot.Id);

        _itemStorageMock.Verify(s => s.Restore(It.IsAny<string>()), Times.Never);
        _windowManagerMock.Verify(wm => wm.RemoveChildWindow(portal.Id, false, true), Times.Once);
    }

    /// <summary>
    /// F6: when items were returned and none got stuck, a self-hiding Success status reports the count.
    /// </summary>
    [Fact]
    public void Restore_ItemsReturned_ShowsSuccessStatusWithCount()
    {
        const string orphan1 = @"C:\ManagedRoot\b\one.lnk";
        const string orphan2 = @"C:\ManagedRoot\b\two.lnk";
        var panelA = new ChildWindowModel { Id = Guid.NewGuid() };
        var sut = BuildSut(panels: [panelA]);
        var snapshot = sut.Capture("before");
        var panelC = new ChildWindowModel { Id = Guid.NewGuid(), FilePaths = [orphan1, orphan2] };
        _windowManagerMock.Setup(wm => wm.GetAllChildWindows()).Returns([panelA, panelC]);
        MarkManaged(orphan1, orphan2);

        sut.Restore(snapshot.Id);

        _statusNotifierMock.Verify(
            n => n.Show(StatusSeverity.Success, "Snapshot_Restore_ItemsReturned_Format:2", It.IsAny<string?>()),
            Times.Once);
    }

    /// <summary>
    /// F6: nothing to return, nothing to say - no status at all.
    /// </summary>
    [Fact]
    public void Restore_NoItemsInvolved_ShowsNoStatus()
    {
        var panelA = new ChildWindowModel { Id = Guid.NewGuid() };
        var sut = BuildSut(panels: [panelA]);
        var snapshot = sut.Capture("before");

        sut.Restore(snapshot.Id);

        _statusNotifierMock.Verify(n => n.Show(It.IsAny<StatusSeverity>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }

    // ── Review cycle 3 ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// M1: after a Restore the live panels carry fresh Ids while the snapshot JSON keeps the old ones. A second
    /// Restore of the same snapshot must still recognise the live panel as the snapshot panel's twin - with an
    /// item that cannot be returned it merges into the recreated panel instead of leaving a duplicate.
    /// </summary>
    [Fact]
    public void Restore_SameSnapshotTwiceWithUnreturnableItem_DoesNotDuplicatePanel()
    {
        const string inSnapshot = @"C:\ManagedRoot\a\kept.lnk";
        const string stuck = @"C:\ManagedRoot\a\stuck.lnk";
        var panelA = new ChildWindowModel { Id = Guid.NewGuid(), Title = "A", FilePaths = [inSnapshot] };
        var sut = BuildSut(panels: [panelA]);
        MarkManaged(inSnapshot, stuck);
        var snapshot = sut.Capture("before");

        panelA.FilePaths = [inSnapshot, stuck];
        _itemStorageMock.Setup(s => s.Restore(stuck)).Returns(RestoreOutcome.Failed);

        // A live panel set that follows Create/Remove, so the second Restore sees the first one's product.
        var live = new List<ChildWindowModel> { panelA };
        _windowManagerMock.Setup(wm => wm.GetAllChildWindows()).Returns(() => live.ToList());
        _windowManagerMock.Setup(wm => wm.RemoveChildWindow(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<bool>()))
            .Callback<Guid, bool, bool>((id, _, _) => live.RemoveAll(m => m.Id == id))
            .Returns(PanelRemovalResult.Removed);
        _windowManagerMock.Setup(wm => wm.CreateChildWindow(It.IsAny<ChildWindowModel>()))
            .Callback<ChildWindowModel>(live.Add);

        sut.Restore(snapshot.Id);
        var afterFirst = Assert.Single(live);
        Assert.NotEqual(panelA.Id, afterFirst.Id);

        sut.Restore(snapshot.Id);

        var afterSecond = Assert.Single(live);
        Assert.Contains(stuck, afterSecond.FilePaths);
        _windowManagerMock.Verify(wm => wm.RemoveChildWindow(afterFirst.Id, false, true), Times.Once);
        _windowManagerMock.Verify(wm => wm.RemoveItemsFromPanels(It.IsAny<IReadOnlyCollection<string>>()), Times.Never);
    }

    /// <summary>
    /// M2: a panel that fails to construct does not abort the restore - the others are created, the state is
    /// flushed and, because the failed panel's managed item now has no panel, the user gets a Critical status.
    /// </summary>
    [Fact]
    public void Restore_CreateChildWindowThrowsForOnePanel_CreatesTheRestSavesAndReportsCritical()
    {
        const string managedInA = @"C:\ManagedRoot\a\a.lnk";
        const string plainInA = @"C:\Desktop\plain.txt";
        var panelA = new ChildWindowModel { Id = Guid.NewGuid(), Title = "A", FilePaths = [managedInA, plainInA] };
        var panelB = new ChildWindowModel { Id = Guid.NewGuid(), Title = "B" };
        var sut = BuildSut(panels: [panelA, panelB]);
        MarkManaged(managedInA);
        var snapshot = sut.Capture("before");

        var created = new List<string>();
        _windowManagerMock.Setup(wm => wm.CreateChildWindow(It.IsAny<ChildWindowModel>()))
            .Callback<ChildWindowModel>(m =>
            {
                if (m.Title == "A") throw new InvalidOperationException("boom");
                created.Add(m.Title);
            });

        var exception = Record.Exception(() => sut.Restore(snapshot.Id));

        Assert.Null(exception);
        Assert.Equal(["B"], created);
        _windowManagerMock.Verify(wm => wm.SaveWindowsStateNow(), Times.Once);
        _statusNotifierMock.Verify(
            n => n.Show(StatusSeverity.Critical, "Snapshot_Restore_ItemsNotReturned_Format:1", It.IsAny<string?>()),
            Times.Once);
    }

    private void MarkManaged(params string[] paths)
    {
        foreach (var path in paths)
            _itemStorageMock.Setup(s => s.IsManaged(path)).Returns(true);
    }

    // ── helpers ──────────────────────────────────────────────────────────────
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

        _itemStorageMock.Setup(s => s.Restore(It.IsAny<string>())).Returns(RestoreOutcome.Restored);
        _localizationMock.Setup(l => l.GetString(It.IsAny<string>())).Returns<string>(key => key);
        _localizationMock.Setup(l => l.Format(It.IsAny<string>(), It.IsAny<object?[]>()))
            .Returns<string, object?[]>((key, args) => $"{key}:{string.Join(",", args)}");
        _stateStoreMock.SetupGet(s => s.IsWritable).Returns(true);

        return new LayoutSnapshotService(
            _windowManagerMock.Object,
            _applicationStateServiceMock.Object,
            _itemStorageMock.Object,
            _statusNotifierMock.Object,
            _localizationMock.Object,
            _stateStoreMock.Object);
    }
}
