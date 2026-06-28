using System.IO;
using System.Reflection;
using System.Windows;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Properties;
using DesktopOrganizer.UI.Services;
using DesktopOrganizer.UI.Views;
using Moq;
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;

namespace DesktopOrganizer.Tests.Unit.Services;

/// <summary>
/// Unit tests for the pure model/storage logic inside <see cref="WindowManager"/> that doesn't
/// require a real WPF <c>ChildWindow</c>:
/// <list type="bullet">
/// <item><see cref="WindowManager.ReleaseManagedItems"/> — cleans up app-managed item copies when a
/// panel is permanently deleted (D3 followups item 3).</item>
/// <item><see cref="WindowManager.ReleaseManagedItemsIfRequested"/> — the <c>releaseManagedItems</c>
/// gate behind <c>RemoveChildWindow(Guid, bool)</c>, ensuring non-permanent removals (e.g. layout
/// snapshot restore) never release managed copies.</item>
/// <item><see cref="WindowManager.ClampIntoMonitor"/> — re-clamps a panel's position into a monitor's
/// (possibly shrunk) work area (D3 followups item 4, exercised via <c>OnMonitorsChanged</c> in
/// production).</item>
/// </list>
/// </summary>
/// <remarks>
/// Both members are <see langword="internal"/> (rather than <see langword="private"/>) specifically
/// to enable this direct testing — see their doc remarks in <c>WindowManager.cs</c>. The rest of
/// <see cref="WindowManager"/> (<c>CreateChildWindow</c>, <c>RemoveChildWindow</c>,
/// <c>OnMonitorsChanged</c>'s <c>_childWindows</c> loop) resolves a real WPF <c>ChildWindow</c> via DI
/// and is not practically unit-testable — see the class-level remarks on <c>OnMonitorsChanged</c>;
/// that end-to-end wiring is manual verify (Win10/Win11).
/// </remarks>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class WindowManagerTests
{
    // ── ReleaseManagedItems ──────────────────────────────────────────────────────

    [Fact]
    public void ReleaseManagedItems_OnlyReleasesManagedPaths_OnceEach()
    {
        // Arrange
        var storageMock = new Mock<IPanelItemStorageService>();
        const string managedA = @"C:\ManagedRoot\a.lnk";
        const string managedB = @"C:\ManagedRoot\b.lnk";
        const string unmanagedC = @"C:\Users\someone\Desktop\c.lnk";

        storageMock.Setup(s => s.IsManaged(managedA)).Returns(true);
        storageMock.Setup(s => s.IsManaged(managedB)).Returns(true);
        storageMock.Setup(s => s.IsManaged(unmanagedC)).Returns(false);

        var model = new ChildWindowModel { FilePaths = [managedA, unmanagedC, managedB] };

        // Act
        WindowManager.ReleaseManagedItems(model, storageMock.Object);

        // Assert
        storageMock.Verify(s => s.Release(managedA), Times.Once);
        storageMock.Verify(s => s.Release(managedB), Times.Once);
        storageMock.Verify(s => s.Release(unmanagedC), Times.Never);
    }

    [Fact]
    public void ReleaseManagedItems_NoManagedPaths_NeverCallsRelease()
    {
        // Arrange — e.g. a Folder Portal panel, whose FilePaths never point into managed storage.
        var storageMock = new Mock<IPanelItemStorageService>();
        storageMock.Setup(s => s.IsManaged(It.IsAny<string>())).Returns(false);

        var model = new ChildWindowModel { FilePaths = [@"C:\Some\Folder\file.txt"] };

        // Act
        WindowManager.ReleaseManagedItems(model, storageMock.Object);

        // Assert
        storageMock.Verify(s => s.Release(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void ReleaseManagedItems_EmptyFilePaths_DoesNotThrowOrCallRelease()
    {
        var storageMock = new Mock<IPanelItemStorageService>();
        var model = new ChildWindowModel { FilePaths = [] };

        var exception = Record.Exception(() => WindowManager.ReleaseManagedItems(model, storageMock.Object));

        Assert.Null(exception);
        storageMock.Verify(s => s.Release(It.IsAny<string>()), Times.Never);
    }

    // ── ReleaseManagedItemsIfRequested (RemoveChildWindow's releaseManagedItems gate) ─────────────

    [Fact]
    public void ReleaseManagedItemsIfRequested_ReleaseFalse_NeverCallsRelease_EvenForManagedPaths()
    {
        // Regression test for the LayoutSnapshotService.Restore data-loss bug: removing a panel that
        // is about to be recreated from a snapshot (releaseManagedItems: false) must never touch
        // managed storage, even when the panel's paths are managed.
        var storageMock = new Mock<IPanelItemStorageService>();
        const string managed = @"C:\ManagedRoot\a.lnk";
        storageMock.Setup(s => s.IsManaged(managed)).Returns(true);

        var model = new ChildWindowModel { FilePaths = [managed] };

        WindowManager.ReleaseManagedItemsIfRequested(model, storageMock.Object, releaseManagedItems: false);

        storageMock.Verify(s => s.Release(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void ReleaseManagedItemsIfRequested_ReleaseTrue_CallsReleaseForManagedPaths()
    {
        // Permanent deletion (MainWindowViewModel.RemovePanel / PageManagerService.RemovePage) must
        // still release managed copies.
        var storageMock = new Mock<IPanelItemStorageService>();
        const string managed = @"C:\ManagedRoot\a.lnk";
        const string unmanaged = @"C:\Users\someone\Desktop\b.lnk";
        storageMock.Setup(s => s.IsManaged(managed)).Returns(true);
        storageMock.Setup(s => s.IsManaged(unmanaged)).Returns(false);

        var model = new ChildWindowModel { FilePaths = [managed, unmanaged] };

        WindowManager.ReleaseManagedItemsIfRequested(model, storageMock.Object, releaseManagedItems: true);

        storageMock.Verify(s => s.Release(managed), Times.Once);
        storageMock.Verify(s => s.Release(unmanaged), Times.Never);
    }

    // ── ClampIntoMonitor ─────────────────────────────────────────────────────────

    private static MonitorInfo BuildMonitor(Rect workArea) =>
        new("\\\\.\\DISPLAY1", workArea, workArea, IsPrimary: true);

    [Fact]
    public void ClampIntoMonitor_PanelOutsideShrunkWorkArea_PullsPositionBackInside()
    {
        // Arrange — the panel was positioned near the (former) right/bottom edge of a large work
        // area; the work area then shrinks (e.g. resolution/DPI/taskbar change) so the panel now sits
        // outside it.
        var model = new ChildWindowModel
        {
            Position = new Point(1800, 900),
            Size = new Size(300, 200),
        };
        var shrunkWorkArea = new Rect(0, 0, 1200, 700);
        var monitor = BuildMonitor(shrunkWorkArea);

        // Act
        WindowManager.ClampIntoMonitor(model, monitor);

        // Assert — pulled back so the whole panel fits inside the new, smaller work area.
        Assert.True(model.Position.X + model.Size.Width <= shrunkWorkArea.Right);
        Assert.True(model.Position.Y + model.Size.Height <= shrunkWorkArea.Bottom);
        Assert.True(model.Position.X >= shrunkWorkArea.Left);
        Assert.True(model.Position.Y >= shrunkWorkArea.Top);
    }

    [Fact]
    public void ClampIntoMonitor_PanelAlreadyInsideWorkArea_LeavesPositionUntouched()
    {
        // Arrange — early-out case: nothing to do when the panel is already fully inside.
        var model = new ChildWindowModel
        {
            Position = new Point(100, 100),
            Size = new Size(300, 200),
        };
        var workArea = new Rect(0, 0, 1200, 700);
        var monitor = BuildMonitor(workArea);

        // Act
        WindowManager.ClampIntoMonitor(model, monitor);

        // Assert
        Assert.Equal(new Point(100, 100), model.Position);
    }

    [Fact]
    public void ClampIntoMonitor_TopLeftInsideButBottomRightOverflows_PullsPositionBackInside()
    {
        // Arrange — regression test: the panel's top-left point remains inside the (shrunk) work
        // area, but Position + Size overflows the right/bottom edge (e.g. a taskbar appears and eats
        // the bottom of the work area while the panel's top-left never moved). The "inside" check must
        // consider the whole panel rect, not just its top-left point, otherwise this case is wrongly
        // treated as already-inside and never re-clamped.
        var model = new ChildWindowModel
        {
            Position = new Point(1000, 550),
            Size = new Size(300, 200),
        };
        var shrunkWorkArea = new Rect(0, 0, 1200, 700);
        var monitor = BuildMonitor(shrunkWorkArea);

        // Sanity check for the scenario: top-left is inside, but the panel's rect is not.
        Assert.True(model.Position.X >= shrunkWorkArea.Left && model.Position.X < shrunkWorkArea.Right);
        Assert.True(model.Position.Y >= shrunkWorkArea.Top && model.Position.Y < shrunkWorkArea.Bottom);
        Assert.True(model.Position.X + model.Size.Width > shrunkWorkArea.Right);
        Assert.True(model.Position.Y + model.Size.Height > shrunkWorkArea.Bottom);

        // Act
        WindowManager.ClampIntoMonitor(model, monitor);

        // Assert — pulled back so the whole panel fits inside the work area, not left untouched.
        Assert.True(model.Position.X + model.Size.Width <= shrunkWorkArea.Right);
        Assert.True(model.Position.Y + model.Size.Height <= shrunkWorkArea.Bottom);
        Assert.NotEqual(new Point(1000, 550), model.Position);
    }

    // ── CreateEachIsolated (LoadWindowsState's per-item isolation) ─────────────────

    [Fact]
    public void CreateEachIsolated_AllFactoriesSucceed_CreatesEveryStateAndReturnsZeroSkipped()
    {
        // Arrange
        var states = new List<ChildWindowModel> { new(), new(), new() };
        var created = new List<Guid>();

        // Act
        var skipped = WindowManager.CreateEachIsolated(states, state => created.Add(state.Id));

        // Assert
        Assert.Equal(0, skipped);
        Assert.Equal(states.Select(s => s.Id), created);
    }

    [Fact]
    public void CreateEachIsolated_OneOfSeveralFactoriesThrows_SkipsOnlyThatOne_CreatesTheRest()
    {
        // Arrange — a corrupt panel among several valid ones (e.g. ResolveMonitorPlacement/DI
        // throwing for a single saved state) must not abort the rest of the load.
        var good1 = new ChildWindowModel { Title = "Good 1" };
        var corrupt = new ChildWindowModel { Title = "Corrupt" };
        var good2 = new ChildWindowModel { Title = "Good 2" };
        var states = new List<ChildWindowModel> { good1, corrupt, good2 };
        var created = new List<ChildWindowModel>();

        // Act
        var skipped = WindowManager.CreateEachIsolated(states, state =>
        {
            if (ReferenceEquals(state, corrupt))
                throw new InvalidOperationException("simulated corrupt panel");
            created.Add(state);
        });

        // Assert
        Assert.Equal(1, skipped);
        Assert.Equal([good1, good2], created);
    }

    [Fact]
    public void CreateEachIsolated_EveryFactoryThrows_SkipsAll_NeverPropagatesException()
    {
        // Arrange
        var states = new List<ChildWindowModel> { new(), new() };

        // Act
        int skipped = 0;
        var exception = Record.Exception(() =>
            skipped = WindowManager.CreateEachIsolated(states, _ => throw new InvalidOperationException("boom")));

        // Assert
        Assert.Null(exception);
        Assert.Equal(2, skipped);
    }

    [Fact]
    public void CreateEachIsolated_EmptyStates_ReturnsZero_NeverInvokesFactory()
    {
        // Act
        var skipped = WindowManager.CreateEachIsolated([], _ => throw new InvalidOperationException("should not run"));

        // Assert
        Assert.Equal(0, skipped);
    }

    // ── SyncEachIsolated (PersistWindowsState's / SyncAllWindowsToModels' per-item isolation) ──────

    [Fact]
    public void SyncEachIsolated_AllSucceed_SyncsEveryEntry()
    {
        // Arrange
        var entries = new List<(ChildWindow Window, ChildWindowModel Model)>
        {
            (null!, new ChildWindowModel()),
            (null!, new ChildWindowModel()),
        };
        var synced = new List<ChildWindowModel>();

        // Act
        WindowManager.SyncEachIsolated(entries, e => synced.Add(e.Model));

        // Assert
        Assert.Equal(entries.Select(e => e.Model), synced);
    }

    [Fact]
    public void SyncEachIsolated_OneOfSeveralThrows_SyncsTheRest_NeverPropagatesException()
    {
        // Regression test for the Phase 7 / Block C M1 review finding: PersistWindowsState used to wrap
        // its whole sync loop in a single try/catch, so one throwing panel aborted syncing (and,
        // transitively, persisting) every other panel too. Per-item isolation means only the throwing
        // entry is skipped; every other entry is still synced, and the loop never propagates.
        var good1 = new ChildWindowModel { Title = "Good 1" };
        var corrupt = new ChildWindowModel { Title = "Corrupt" };
        var good2 = new ChildWindowModel { Title = "Good 2" };
        var entries = new List<(ChildWindow Window, ChildWindowModel Model)>
        {
            (null!, good1),
            (null!, corrupt),
            (null!, good2),
        };
        var synced = new List<ChildWindowModel>();

        // Act
        var exception = Record.Exception(() => WindowManager.SyncEachIsolated(entries, e =>
        {
            if (ReferenceEquals(e.Model, corrupt))
                throw new InvalidOperationException("simulated sync failure");
            synced.Add(e.Model);
        }));

        // Assert
        Assert.Null(exception);
        Assert.Equal([good1, good2], synced);
    }

    [Fact]
    public void SyncEachIsolated_EmptyWindows_DoesNothing()
    {
        var synced = 0;

        WindowManager.SyncEachIsolated([], _ => synced++);

        Assert.Equal(0, synced);
    }

    // ── SaveWindowsState / SaveWindowsStateNow — persistence failures never escape ──────────────

    private static WindowManager BuildWindowManager()
    {
        var serviceProviderMock = new Mock<IServiceProvider>();
        var monitorServiceMock = new Mock<IMonitorService>();
        var itemStorageMock = new Mock<IPanelItemStorageService>();
        return new WindowManager(serviceProviderMock.Object, monitorServiceMock.Object, itemStorageMock.Object);
    }

    /// <summary>
    /// Reaches into <see cref="WindowManager"/>'s private <c>_childWindows</c> dictionary to plant an
    /// entry whose <see cref="ChildWindow"/> is <see langword="null"/> — a real WPF <c>ChildWindow</c>
    /// cannot be constructed in a headless unit test (it needs a live DI graph and, transitively, a
    /// UI dispatcher). A null window is a legitimate, deterministic way to force
    /// <c>PersistWindowsState</c>'s <c>window.SyncToModel()</c> loop to throw, so the surrounding
    /// try/catch (documented as "never throws") can actually be exercised.
    /// </summary>
    private static void PlantWindowThatThrowsOnSync(WindowManager manager)
    {
        var field = typeof(WindowManager).GetField("_childWindows", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var childWindows = (Dictionary<Guid, (ChildWindow Window, ChildWindowModel Model)>)field.GetValue(manager)!;
        var model = new ChildWindowModel();
        childWindows[model.Id] = (null!, model);
    }

    [Fact]
    public void SaveWindowsState_PersistenceFails_DoesNotThrow()
    {
        // Arrange
        var manager = BuildWindowManager();
        PlantWindowThatThrowsOnSync(manager);

        // Act — Application.Current is null in the test host, so SaveWindowsState's debounce
        // degrades to an immediate synchronous PersistWindowsState() call (see SaveScheduler.Request).
        var exception = Record.Exception(manager.SaveWindowsState);

        // Assert
        Assert.Null(exception);
    }

    [Fact]
    public void SaveWindowsState_PanelFailsToSync_LogsToConsoleError()
    {
        // Arrange — since the Phase 7 / Block C M1 fix, a panel that fails to sync is isolated and
        // logged per-panel ("Failed to sync panel ..."); it no longer aborts the whole write nor logs
        // the outer "Failed to save window state" message (reserved for an actual serialize/disk
        // failure — see SaveWindowsStateNow_OnePanelFailsToSync_StillWritesSavedState below).
        //
        // Uses SaveWindowsStateNow (the synchronous flush), not SaveWindowsState (the debounced
        // request): SaveWindowsState only degrades to an immediate synchronous save when
        // Application.Current is null, which no longer holds process-wide once any test elsewhere in
        // the same run (e.g. ChildWindowViewModelTests) has bootstrapped a real WPF Application —
        // SaveWindowsStateNow's Flush() always runs synchronously regardless, keeping this assertion
        // deterministic irrespective of test execution order.
        var manager = BuildWindowManager();
        PlantWindowThatThrowsOnSync(manager);
        var originalError = Console.Error;
        var capturedError = new StringWriter();
        Console.SetError(capturedError);

        try
        {
            // Act
            manager.SaveWindowsStateNow();
        }
        finally
        {
            Console.SetError(originalError);
        }

        // Assert
        Assert.Contains("Failed to sync panel", capturedError.ToString());
    }

    [Fact]
    public void SaveWindowsStateNow_PersistenceFails_DoesNotThrow()
    {
        // Arrange
        var manager = BuildWindowManager();
        PlantWindowThatThrowsOnSync(manager);

        // Act — the synchronous flush variant used by structural changes/app exit.
        var exception = Record.Exception(manager.SaveWindowsStateNow);

        // Assert
        Assert.Null(exception);
    }

    [Fact]
    public void SaveWindowsStateNow_OnePanelFailsToSync_StillWritesSavedState()
    {
        // Regression test for the Phase 7 / Block C M1 review finding: before the fix,
        // PersistWindowsState wrapped the whole sync loop together with the JSON serialize +
        // Settings.Save() in a single try/catch, so a single throwing panel meant the write never ran
        // at all — a silent, unrecoverable loss of the whole saved layout on every subsequent save
        // (including the final flush on app exit, since the unsynced panel stays in _childWindows
        // forever). Per-item sync isolation means the write always proceeds once every panel has been
        // attempted.
        var manager = BuildWindowManager();
        PlantWindowThatThrowsOnSync(manager);
        var original = Settings.Default.ChildWindowsState;
        Settings.Default.ChildWindowsState = string.Empty;

        try
        {
            // Act
            manager.SaveWindowsStateNow();

            // Assert
            Assert.False(string.IsNullOrEmpty(Settings.Default.ChildWindowsState));
        }
        finally
        {
            Settings.Default.ChildWindowsState = original;
        }
    }

    [Fact]
    public void SaveWindowsStateNow_MultiplePanelsFailToSync_LogsEachFailure_ContinuesPastTheFirst()
    {
        // Regression test: the previous single-try/catch implementation would abort the whole loop —
        // and thus the whole save — at the first throwing panel. Per-item isolation must log every
        // failing panel independently, proving the loop visits every remaining entry instead of
        // stopping short.
        var manager = BuildWindowManager();
        PlantWindowThatThrowsOnSync(manager);
        PlantWindowThatThrowsOnSync(manager);
        var originalError = Console.Error;
        var capturedError = new StringWriter();
        Console.SetError(capturedError);

        try
        {
            // Act
            var exception = Record.Exception(manager.SaveWindowsStateNow);

            // Assert
            Assert.Null(exception);
        }
        finally
        {
            Console.SetError(originalError);
        }

        var occurrences = capturedError.ToString()
            .Split("Failed to sync panel", StringSplitOptions.None).Length - 1;
        Assert.Equal(2, occurrences);
    }

    // ── LoadWindowsState — corrupt saved state never crashes the load ──────────────────────────

    [Fact]
    public void LoadWindowsState_CorruptJson_DoesNotThrow_AndLeavesNoWindowsCreated()
    {
        // Arrange
        var manager = BuildWindowManager();
        var original = Settings.Default.ChildWindowsState;
        Settings.Default.ChildWindowsState = "{ this is not valid json";

        try
        {
            // Act
            var exception = Record.Exception(manager.LoadWindowsState);

            // Assert
            Assert.Null(exception);
            Assert.Empty(manager.GetAllChildWindows());
        }
        finally
        {
            // Restore in-memory state so other tests (and this process, should Settings.Save() ever
            // run later) never observe or persist the poisoned value.
            Settings.Default.ChildWindowsState = original;
        }
    }

    [Fact]
    public void LoadWindowsState_EmptySavedState_DoesNothing_NoWindowsCreated()
    {
        // Arrange
        var manager = BuildWindowManager();
        var original = Settings.Default.ChildWindowsState;
        Settings.Default.ChildWindowsState = string.Empty;

        try
        {
            // Act
            var exception = Record.Exception(manager.LoadWindowsState);

            // Assert
            Assert.Null(exception);
            Assert.Empty(manager.GetAllChildWindows());
        }
        finally
        {
            Settings.Default.ChildWindowsState = original;
        }
    }
}
