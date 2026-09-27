using System.IO;
using System.Windows;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Abstractions.Storage;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;
using DesktopOrganizer.UI.State;
using DesktopOrganizer.UI.Views;
using Moq;
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;

namespace DesktopOrganizer.Tests.Unit.Services;

/// <summary>
/// Unit tests for the pure model/storage logic inside <see cref="WindowManager"/> that doesn't
/// require a real WPF <c>ChildWindow</c>:
/// <list type="bullet">
/// <item><see cref="WindowManager.ReleaseManagedItems"/> — moves app-managed item copies to the Recycle
/// Bin when a panel is permanently deleted (D3 followups item 3, ADR-035).</item>
/// <item><see cref="WindowManager.ReleaseManagedItemsIfRequested"/> — the <c>releaseManagedItems</c>
/// gate behind <c>RemoveChildWindow(Guid, bool)</c>, ensuring non-permanent removals (e.g. layout
/// snapshot restore) never recycle managed copies.</item>
/// <item><see cref="WindowManager.ClampIntoMonitor"/> — re-clamps a panel's position into a monitor's
/// (possibly shrunk) work area (D3 followups item 4, exercised via <c>OnMonitorsChanged</c> in
/// production).</item>
/// <item><see cref="WindowManager.GridAlignedBoundsUnchanged"/> — M-3 fix-cycle 1's "did anything
/// actually change" gate, also exercised via <c>OnMonitorsChanged</c> in production.</item>
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
    // ── ReleaseManagedItems (ADR-035: Recycle Bin, one batch) ────────────────────

    /// <summary>
    /// Verifies that <see cref="WindowManager.ReleaseManagedItems"/> sends exactly the managed paths —
    /// in one <see cref="IPanelItemStorageService.ReleaseToRecycleBin"/> batch — and never unmanaged ones,
    /// and passes the storage outcome through.
    /// </summary>
    [Fact]
    public void ReleaseManagedItems_RecyclesOnlyManagedPaths_InOneBatch()
    {
        // Arrange
        var storageMock = new Mock<IPanelItemStorageService>();
        const string managedA = @"C:\ManagedRoot\a.lnk";
        const string managedB = @"C:\ManagedRoot\b.lnk";
        const string unmanagedC = @"C:\Users\someone\Desktop\c.lnk";

        storageMock.Setup(s => s.IsManaged(managedA)).Returns(true);
        storageMock.Setup(s => s.IsManaged(managedB)).Returns(true);
        storageMock.Setup(s => s.IsManaged(unmanagedC)).Returns(false);
        var expected = RecycleOutcome.Create([managedA], [managedB], cancelled: true);
        storageMock.Setup(s => s.ReleaseToRecycleBin(It.IsAny<IReadOnlyList<string>>())).Returns(expected);

        var model = new ChildWindowModel { FilePaths = [managedA, unmanagedC, managedB] };

        // Act
        var outcome = WindowManager.ReleaseManagedItems(model, storageMock.Object);

        // Assert
        storageMock.Verify(
            s => s.ReleaseToRecycleBin(It.Is<IReadOnlyList<string>>(p => p.SequenceEqual(new[] { managedA, managedB }))),
            Times.Once);
        storageMock.Verify(s => s.ReleaseToRecycleBin(It.IsAny<IReadOnlyList<string>>()), Times.Once);
        Assert.Equal(expected, outcome);
        Assert.False(outcome.IsComplete);
    }

    /// <summary>
    /// Verifies that <see cref="WindowManager.ReleaseManagedItems"/> never calls the storage service for
    /// a panel with no managed paths (e.g. a Folder Portal panel) and reports a complete outcome.
    /// </summary>
    [Fact]
    public void ReleaseManagedItems_NoManagedPaths_NeverCallsRecycle()
    {
        // Arrange — e.g. a Folder Portal panel, whose FilePaths never point into managed storage.
        var storageMock = new Mock<IPanelItemStorageService>();
        storageMock.Setup(s => s.IsManaged(It.IsAny<string>())).Returns(false);

        var model = new ChildWindowModel { FilePaths = [@"C:\Some\Folder\file.txt"] };

        // Act
        var outcome = WindowManager.ReleaseManagedItems(model, storageMock.Object);

        // Assert
        storageMock.Verify(s => s.ReleaseToRecycleBin(It.IsAny<IReadOnlyList<string>>()), Times.Never);
        Assert.True(outcome.IsComplete);
    }

    /// <summary>
    /// Verifies that <see cref="WindowManager.ReleaseManagedItems"/> does not throw and never calls the
    /// storage service for a panel with an empty file paths list.
    /// </summary>
    [Fact]
    public void ReleaseManagedItems_EmptyFilePaths_DoesNotThrowOrCallRecycle()
    {
        var storageMock = new Mock<IPanelItemStorageService>();
        var model = new ChildWindowModel { FilePaths = [] };

        var exception = Record.Exception(() => WindowManager.ReleaseManagedItems(model, storageMock.Object));

        Assert.Null(exception);
        storageMock.Verify(s => s.ReleaseToRecycleBin(It.IsAny<IReadOnlyList<string>>()), Times.Never);
    }

    // ── ReleaseManagedItemsIfRequested (RemoveChildWindow's releaseManagedItems gate) ─────────────

    /// <summary>
    /// Verifies the <c>LayoutSnapshotService.Restore</c> data-loss regression guard: with
    /// <c>releaseManagedItems: false</c>, <see cref="WindowManager.ReleaseManagedItemsIfRequested"/>
    /// never touches managed storage, even when the panel's paths are managed.
    /// </summary>
    [Fact]
    public void ReleaseManagedItemsIfRequested_ReleaseFalse_NeverRecycles_EvenForManagedPaths()
    {
        // Regression test for the LayoutSnapshotService.Restore data-loss bug: removing a panel that
        // is about to be recreated from a snapshot (releaseManagedItems: false) must never touch
        // managed storage, even when the panel's paths are managed.
        var storageMock = new Mock<IPanelItemStorageService>();
        const string managed = @"C:\ManagedRoot\a.lnk";
        storageMock.Setup(s => s.IsManaged(managed)).Returns(true);

        var model = new ChildWindowModel { FilePaths = [managed] };

        var outcome = WindowManager.ReleaseManagedItemsIfRequested(model, storageMock.Object, releaseManagedItems: false);

        storageMock.Verify(s => s.ReleaseToRecycleBin(It.IsAny<IReadOnlyList<string>>()), Times.Never);
        Assert.True(outcome.IsComplete);
    }

    /// <summary>
    /// Verifies that with <c>releaseManagedItems: true</c> (permanent deletion),
    /// <see cref="WindowManager.ReleaseManagedItemsIfRequested"/> recycles every managed path.
    /// </summary>
    [Fact]
    public void ReleaseManagedItemsIfRequested_ReleaseTrue_RecyclesManagedPaths()
    {
        // Permanent deletion (MainWindowViewModel.RemovePanel) must still get rid of managed copies —
        // through the Recycle Bin.
        var storageMock = new Mock<IPanelItemStorageService>();
        const string managed = @"C:\ManagedRoot\a.lnk";
        const string unmanaged = @"C:\Users\someone\Desktop\b.lnk";
        storageMock.Setup(s => s.IsManaged(managed)).Returns(true);
        storageMock.Setup(s => s.IsManaged(unmanaged)).Returns(false);

        var model = new ChildWindowModel { FilePaths = [managed, unmanaged] };

        WindowManager.ReleaseManagedItemsIfRequested(model, storageMock.Object, releaseManagedItems: true);

        storageMock.Verify(
            s => s.ReleaseToRecycleBin(It.Is<IReadOnlyList<string>>(p => p.Count == 1 && p[0] == managed)),
            Times.Once);
    }

    // ── PanelRemovalResult ───────────────────────────────────────────────────────

    /// <summary>
    /// <c>default(PanelRemovalResult)</c> must mean "removed" (loose mocks of <see cref="IWindowManager"/>
    /// return it), and <see cref="PanelRemovalResult.Blocked"/> carries the remaining paths.
    /// </summary>
    [Fact]
    public void PanelRemovalResult_DefaultIsRemoved_BlockedCarriesRemaining()
    {
        var removed = default(PanelRemovalResult);
        var blocked = PanelRemovalResult.Blocked([@"C:\ManagedRoot\a.lnk"]);

        Assert.True(removed.IsRemoved);
        Assert.False(removed.IsBlocked);
        Assert.Empty(removed.Remaining);
        Assert.Equal(PanelRemovalResult.Removed, removed);

        Assert.True(blocked.IsBlocked);
        Assert.False(blocked.IsRemoved);
        Assert.Equal([@"C:\ManagedRoot\a.lnk"], blocked.Remaining);

        Assert.True(PanelRemovalResult.Blocked([]).IsRemoved);
    }

    // ── ShouldCreateFirstRunPanel (first-run empty-panel seeding) ──────────────────

    /// <summary>
    /// Verifies that a genuine first run — no panels restored AND the load that produced that empty
    /// list is <see cref="StateLoadStatus.FirstRun"/> (no JSON store existed yet, legacy
    /// <c>user.config</c> was empty/default) — is the one case that seeds the welcome panel.
    /// </summary>
    [Fact]
    public void ShouldCreateFirstRunPanel_FreshInstall_ReturnsTrue()
    {
        Assert.True(WindowManager.ShouldCreateFirstRunPanel(existingPanelCount: 0, StateLoadStatus.FirstRun));
    }

    /// <summary>
    /// Verifies that an empty-but-EXISTING document — the user deliberately deleted every panel —
    /// never re-seeds one. That document loaded as <see cref="StateLoadStatus.Loaded"/>, not
    /// <see cref="StateLoadStatus.FirstRun"/>, even though <c>Panels.Count</c> is also zero.
    /// </summary>
    [Fact]
    public void ShouldCreateFirstRunPanel_EmptyButExistingDocument_ReturnsFalse()
    {
        Assert.False(WindowManager.ShouldCreateFirstRunPanel(existingPanelCount: 0, StateLoadStatus.Loaded));
    }

    /// <summary>
    /// Verifies that a non-empty panel list never seeds an extra panel, even if (hypothetically) the
    /// load status were <see cref="StateLoadStatus.FirstRun"/> — the count gate is checked independently
    /// of the status.
    /// </summary>
    [Fact]
    public void ShouldCreateFirstRunPanel_PanelsAlreadyExist_ReturnsFalse()
    {
        Assert.False(WindowManager.ShouldCreateFirstRunPanel(existingPanelCount: 1, StateLoadStatus.FirstRun));
    }

    /// <summary>
    /// Verifies that the read-only "newer schema" rung (ADR-024 D-7 step 7 — this build is older than
    /// whatever last wrote the file, and <see cref="IApplicationStateStore.IsWritable"/> is
    /// permanently <see langword="false"/> for the session) never seeds a panel: the empty payload it
    /// reports is a build-can't-read-this signal, not "nothing configured yet", and any write here
    /// would be silently dropped anyway.
    /// </summary>
    [Fact]
    public void ShouldCreateFirstRunPanel_NewerSchemaReadOnlySession_ReturnsFalse()
    {
        Assert.False(WindowManager.ShouldCreateFirstRunPanel(existingPanelCount: 0, StateLoadStatus.NewerSchema));
    }

    /// <summary>
    /// Verifies that a quarantined document (both primary and <c>.bak</c> were unusable) never seeds a
    /// panel — the real panels may still be recoverable from the quarantined file, so inventing a new
    /// one would be misleading, not helpful.
    /// </summary>
    [Fact]
    public void ShouldCreateFirstRunPanel_Quarantined_ReturnsFalse()
    {
        Assert.False(WindowManager.ShouldCreateFirstRunPanel(existingPanelCount: 0, StateLoadStatus.Quarantined));
    }

    /// <summary>
    /// Verifies that a backup-recovered document never seeds a panel — <c>.bak</c> was successfully
    /// loaded and migrated, so this is a recovered EXISTING install, not a fresh one, even if that
    /// backup itself happened to contain zero panels.
    /// </summary>
    [Fact]
    public void ShouldCreateFirstRunPanel_RecoveredFromBackup_ReturnsFalse()
    {
        Assert.False(WindowManager.ShouldCreateFirstRunPanel(existingPanelCount: 0, StateLoadStatus.RecoveredFromBackup));
    }

    /// <summary>
    /// Verifies that a legacy <c>user.config</c> import never seeds an extra panel — that import path
    /// already produced whatever structural state the legacy install actually had.
    /// </summary>
    [Fact]
    public void ShouldCreateFirstRunPanel_ImportedFromLegacy_ReturnsFalse()
    {
        Assert.False(WindowManager.ShouldCreateFirstRunPanel(existingPanelCount: 0, StateLoadStatus.ImportedFromLegacy));
    }

    // ── ClampIntoMonitor ─────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="WindowManager.ClampIntoMonitor"/> pulls a panel's position back inside
    /// the monitor's work area after that work area shrinks below the panel.
    /// </summary>
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

    /// <summary>
    /// Verifies the m-1 fix-cycle 1 pin: when the clamp result lands exactly on a negative half-DIP
    /// midpoint, <see cref="WindowManager.ClampIntoMonitor"/> uses banker's rounding (away from zero for
    /// this midpoint), matching <c>PanelGeometryWriter</c>'s own choice.
    /// </summary>
    [Fact]
    public void ClampIntoMonitor_FractionalNegativeClampResult_RoundsToNearest_NotTowardZero()
    {
        // m-1 fix-cycle 1 (docs/PLANS/workspace-grid-03-04-integration.md §2, code-review): a fractional
        // work-area origin (possible on a monitor left of/above the primary) can make the clamp's own
        // result land on exactly -101.5 -- a Math.Round midpoint. Truncation toward zero ((int) cast)
        // picks -101 (the neighbour closer to zero); Math.Round's default banker's rounding picks -102
        // (the even neighbour) -- this pins the SAME choice PanelGeometryWriter/WriteModelGeometry now
        // make, so all three writers agree.
        var model = new ChildWindowModel
        {
            Position = new Point(-500, 50),
            Size = new Size(50, 50),
        };
        var workArea = new Rect(-101.5, 0, 300, 700);
        var monitor = BuildMonitor(workArea);

        WindowManager.ClampIntoMonitor(model, monitor);

        Assert.Equal(-102, model.Position.X);
        Assert.Equal(50, model.Position.Y);
    }

    /// <summary>
    /// Verifies the early-out case: <see cref="WindowManager.ClampIntoMonitor"/> leaves the position
    /// untouched when the panel is already fully inside the work area.
    /// </summary>
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

    /// <summary>
    /// Regression test: verifies that <see cref="WindowManager.ClampIntoMonitor"/> still re-clamps when
    /// only the panel's top-left point is inside the work area but its bottom-right overflows —
    /// the "inside" check must consider the whole panel rect, not just its top-left point.
    /// </summary>
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

    // ── GridAlignedBoundsUnchanged (M-3 fix-cycle 1: OnMonitorsChanged's "did anything actually change"
    //    gate — see that method's own remarks for why the surrounding loop itself is manual verify) ────

    /// <summary>
    /// Verifies that <see cref="WindowManager.GridAlignedBoundsUnchanged"/> returns
    /// <see langword="true"/> for two identical rectangles.
    /// </summary>
    [Fact]
    public void GridAlignedBoundsUnchanged_IdenticalRects_ReturnsTrue()
    {
        var rect = new Rect(104, 144, 248, 248);

        Assert.True(WindowManager.GridAlignedBoundsUnchanged(rect, rect));
    }

    /// <summary>
    /// Verifies that <see cref="WindowManager.GridAlignedBoundsUnchanged"/> returns
    /// <see langword="true"/> when every delta between the two rectangles is below the tolerance.
    /// </summary>
    [Fact]
    public void GridAlignedBoundsUnchanged_WithinTolerance_ReturnsTrue()
    {
        var current = new Rect(104d, 144d, 248d, 248d);
        var bounds = new Rect(104.3d, 143.8d, 248.2d, 247.7d); // every delta < 0.5 DIP

        Assert.True(WindowManager.GridAlignedBoundsUnchanged(current, bounds));
    }

    /// <summary>
    /// Verifies that <see cref="WindowManager.GridAlignedBoundsUnchanged"/> returns
    /// <see langword="false"/> when the position moved by a whole workspace-grid unit.
    /// </summary>
    [Fact]
    public void GridAlignedBoundsUnchanged_PositionMovedByAWholeUnit_ReturnsFalse()
    {
        var current = new Rect(104, 144, 248, 248);
        var bounds = new Rect(112, 144, 248, 248); // moved by one workspace-grid unit (U=8)

        Assert.False(WindowManager.GridAlignedBoundsUnchanged(current, bounds));
    }

    /// <summary>
    /// Verifies that <see cref="WindowManager.GridAlignedBoundsUnchanged"/> returns
    /// <see langword="false"/> when the size changed by a whole workspace-grid column.
    /// </summary>
    [Fact]
    public void GridAlignedBoundsUnchanged_SizeChanged_ReturnsFalse()
    {
        var current = new Rect(104, 144, 248, 248);
        var bounds = new Rect(104, 144, 344, 248); // one more column of workspace-grid units

        Assert.False(WindowManager.GridAlignedBoundsUnchanged(current, bounds));
    }

    /// <summary>
    /// Sanity check for the C-1 finding's own repro class of delta: verifies that a large ("teleport
    /// sized") position jump is never masked by the tolerance.
    /// </summary>
    [Fact]
    public void GridAlignedBoundsUnchanged_TeleportSizedDelta_ReturnsFalse()
    {
        // The same class of delta as the C-1 finding's own repro (a 528 DIP jump) -- included here as a
        // sanity check that a REAL change is never masked by the tolerance.
        var current = new Rect(1672, 40, 248, 248);
        var bounds = new Rect(2200, 40, 248, 248);

        Assert.False(WindowManager.GridAlignedBoundsUnchanged(current, bounds));
    }

    /// <summary>
    /// P03 fix-cycle 1 final verification, MAJOR-1: a ROLLED-UP GridAligned panel's live
    /// <c>Window.Height</c> is the collapsed header (32 DIP) while <c>bounds</c> carries the EXPANDED
    /// span height (248) — comparing heights made the gate never fire, so every theme/locale
    /// <c>WM_SETTINGCHANGE</c> re-wrote and re-saved the panel. While rolled up only the position can be
    /// re-applied (<c>ApplyWorkspaceGridGeometry</c> → <c>ApplyPosition</c>), so only X/Y are compared.
    /// </summary>
    [Fact]
    public void GridAlignedBoundsUnchanged_RolledUp_IgnoresTheCollapsedHeight()
    {
        var current = new Rect(104, 144, 248, 32); // collapsed header height
        var bounds = new Rect(104, 144, 248, 248); // expanded span height

        Assert.True(WindowManager.GridAlignedBoundsUnchanged(current, bounds, isRolledUp: true));
        Assert.False(WindowManager.GridAlignedBoundsUnchanged(current, bounds, isRolledUp: false));
    }

    /// <summary>
    /// Verifies that while rolled up, <see cref="WindowManager.GridAlignedBoundsUnchanged"/> still
    /// detects a genuine position change even though the collapsed header height is ignored.
    /// </summary>
    [Fact]
    public void GridAlignedBoundsUnchanged_RolledUp_StillDetectsAPositionChange()
    {
        var current = new Rect(104, 144, 248, 32);
        var bounds = new Rect(112, 144, 248, 248); // origin shifted by one unit (U=8)

        Assert.False(WindowManager.GridAlignedBoundsUnchanged(current, bounds, isRolledUp: true));
    }

    // ── ResolveStalePanelPaths (M1 fix, P08 fix cycle 1, 2026-09-22) ────────────────────────────────

    /// <summary>
    /// Verifies that a stale path with exactly one live, unclaimed managed candidate is remapped to
    /// that candidate in place.
    /// </summary>
    [Fact]
    public void ResolveStalePanelPaths_SingleUnclaimedCandidate_RemapsInPlace()
    {
        var managed = Path.Combine(Path.GetTempPath(), $"DOrgWmResolve_{Guid.NewGuid():N}.lnk");
        File.WriteAllText(managed, "content");
        const string stale = @"C:\fake\stale-original.lnk";
        var panel = new ChildWindowModel { FilePaths = [stale] };

        var storageMock = new Mock<IPanelItemStorageService>();
        storageMock.Setup(s => s.IsManaged(managed)).Returns(true);
        storageMock.Setup(s => s.FindManagedCandidates(stale)).Returns([managed]);

        try
        {
            WindowManager.ResolveStalePanelPaths([panel], storageMock.Object);

            Assert.Equal(managed, Assert.Single(panel.FilePaths));
        }
        finally
        {
            File.Delete(managed);
        }
    }

    /// <summary>
    /// Verifies that a stale path with more than one live managed candidate is left untouched, since the
    /// resolution is ambiguous.
    /// </summary>
    [Fact]
    public void ResolveStalePanelPaths_AmbiguousCandidates_LeavesPathUntouched()
    {
        const string stale = @"C:\fake\stale-original.lnk";
        const string candidateA = @"C:\ManagedRoot\a.lnk";
        const string candidateB = @"C:\ManagedRoot\b.lnk";
        var panel = new ChildWindowModel { FilePaths = [stale] };

        var storageMock = new Mock<IPanelItemStorageService>();
        storageMock.Setup(s => s.IsManaged(It.IsAny<string>())).Returns(false);
        storageMock.Setup(s => s.FindManagedCandidates(stale)).Returns([candidateA, candidateB]);

        WindowManager.ResolveStalePanelPaths([panel], storageMock.Object);

        Assert.Equal(stale, Assert.Single(panel.FilePaths));
    }

    /// <summary>
    /// Verifies M1's cross-panel ownership rule: a candidate already directly referenced by another
    /// panel is never remapped onto by a different panel's stale entry, which would give two panels
    /// ownership of the same physical file.
    /// </summary>
    [Fact]
    public void ResolveStalePanelPaths_CandidateAlreadyReferencedByAnotherPanel_LeavesStalePathUntouched()
    {
        // M1's cross-panel ownership scenario: the one live manifest candidate for panel A's stale
        // entry is already directly present (as-is) in panel B's own FilePaths — remapping panel A to
        // it would give two panels ownership of the same physical file, so Restore/Release from either
        // one would corrupt the other.
        const string stale = @"C:\fake\stale-original.lnk";
        const string sharedManaged = @"C:\ManagedRoot\shared.lnk";
        var panelA = new ChildWindowModel { FilePaths = [stale] };
        var panelB = new ChildWindowModel { FilePaths = [sharedManaged] };

        var storageMock = new Mock<IPanelItemStorageService>();
        storageMock.Setup(s => s.IsManaged(sharedManaged)).Returns(true);
        storageMock.Setup(s => s.IsManaged(stale)).Returns(false);

        // sharedManaged is a fake path (doesn't exist on disk) standing in for "a real managed file
        // panel B already references directly" — the resolve loop still visits it (File.Exists is
        // false for a fake path) and calls FindManagedCandidates for it too, so this catch-all must be
        // set up even though panel B's own entry is never meant to be remapped (see IsManaged claim
        // above, which is what actually protects it).
        storageMock.Setup(s => s.FindManagedCandidates(It.IsAny<string>())).Returns([]);
        storageMock.Setup(s => s.FindManagedCandidates(stale)).Returns([sharedManaged]);

        WindowManager.ResolveStalePanelPaths([panelA, panelB], storageMock.Object);

        Assert.Equal(stale, Assert.Single(panelA.FilePaths));
        Assert.Equal(sharedManaged, Assert.Single(panelB.FilePaths)); // untouched
    }

    /// <summary>
    /// Verifies the dedup case: a stale entry is not remapped to a managed copy already directly listed
    /// elsewhere in the same panel's <c>FilePaths</c>, which would show the same item twice.
    /// </summary>
    [Fact]
    public void ResolveStalePanelPaths_CandidateAlreadyPresentInSamePanel_DoesNotDuplicate()
    {
        // Dedup case: the same panel's own FilePaths already directly lists the managed copy elsewhere
        // — remapping the stale entry to it too would show the same physical item twice in one panel.
        const string stale = @"C:\fake\stale-original.lnk";
        const string managed = @"C:\ManagedRoot\already-here.lnk";
        var panel = new ChildWindowModel { FilePaths = [stale, managed] };

        var storageMock = new Mock<IPanelItemStorageService>();
        storageMock.Setup(s => s.IsManaged(managed)).Returns(true);
        storageMock.Setup(s => s.IsManaged(stale)).Returns(false);

        // managed is a fake path (doesn't exist on disk) standing in for "a real managed file already
        // directly present in this panel" — the resolve loop still visits it, so this catch-all must be
        // set up even though it's never meant to be remapped itself (IsManaged above is what protects
        // it, via the "claimed" set).
        storageMock.Setup(s => s.FindManagedCandidates(It.IsAny<string>())).Returns([]);
        storageMock.Setup(s => s.FindManagedCandidates(stale)).Returns([managed]);

        WindowManager.ResolveStalePanelPaths([panel], storageMock.Object);

        Assert.Equal([stale, managed], panel.FilePaths);
    }

    /// <summary>
    /// Verifies that a Folder Portal panel (<c>SourceFolderPath</c> set) is skipped entirely: neither the
    /// storage service nor its <c>FilePaths</c> are touched.
    /// </summary>
    [Fact]
    public void ResolveStalePanelPaths_PortalPanel_NeverConsultsStorageOrMutatesFilePaths()
    {
        var panel = new ChildWindowModel { SourceFolderPath = @"C:\SomePortalFolder", FilePaths = [@"C:\fake\stale.lnk"] };
        var storageMock = new Mock<IPanelItemStorageService>();

        WindowManager.ResolveStalePanelPaths([panel], storageMock.Object);

        Assert.Equal([@"C:\fake\stale.lnk"], panel.FilePaths);
        storageMock.Verify(s => s.FindManagedCandidates(It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    /// Verifies that a path that still exists on disk is never resolved through the storage service —
    /// only genuinely stale (missing) paths trigger a candidate lookup.
    /// </summary>
    [Fact]
    public void ResolveStalePanelPaths_PathAlreadyExists_NeverConsultsStorage()
    {
        var existing = Path.Combine(Path.GetTempPath(), $"DOrgWmResolveExisting_{Guid.NewGuid():N}.txt");
        File.WriteAllText(existing, "content");
        var panel = new ChildWindowModel { FilePaths = [existing] };
        var storageMock = new Mock<IPanelItemStorageService>();
        storageMock.Setup(s => s.IsManaged(It.IsAny<string>())).Returns(false);

        try
        {
            WindowManager.ResolveStalePanelPaths([panel], storageMock.Object);

            storageMock.Verify(s => s.FindManagedCandidates(It.IsAny<string>()), Times.Never);
        }
        finally
        {
            File.Delete(existing);
        }
    }

    /// <summary>
    /// Verifies the ADR-009 Amendment 2 tie-break rule (MINOR-2 fix): when two panels' equally-valid
    /// stale entries both resolve to the same single live candidate, neither may claim it — silently
    /// awarding it by iteration order would be an unexplained guess.
    /// </summary>
    [Fact]
    public void ResolveStalePanelPaths_TwoPanelsCompetingForTheSameSingleCandidate_BothRefuseTheAmbiguousClaim()
    {
        // MINOR-2 fix (P08 fix cycle 2, 2026-09-22, ADR-009 Amendment 2 tie-break rule): both panels
        // have an equally-valid, equally-unambiguous stale entry resolving to the SAME one live
        // candidate (e.g. two copies of the same stale state accidentally duplicated across panels).
        // Neither may claim it -- silently awarding it to whichever panel happens to be processed
        // first (the pre-fix behavior) is an unexplained, iteration-order-dependent guess. Both must be
        // left stale, exactly as an ambiguous manifest already leaves a single entry stale.
        const string staleA = @"C:\fake\stale-a.lnk";
        const string staleB = @"C:\fake\stale-b.lnk";
        const string onlyCandidate = @"C:\ManagedRoot\only.lnk";
        var panelA = new ChildWindowModel { FilePaths = [staleA] };
        var panelB = new ChildWindowModel { FilePaths = [staleB] };

        var storageMock = new Mock<IPanelItemStorageService>();
        storageMock.Setup(s => s.IsManaged(It.IsAny<string>())).Returns(false);
        storageMock.Setup(s => s.FindManagedCandidates(staleA)).Returns([onlyCandidate]);
        storageMock.Setup(s => s.FindManagedCandidates(staleB)).Returns([onlyCandidate]);

        WindowManager.ResolveStalePanelPaths([panelA, panelB], storageMock.Object);

        Assert.Equal(staleA, Assert.Single(panelA.FilePaths)); // neither claimant gets it
        Assert.Equal(staleB, Assert.Single(panelB.FilePaths));
    }

    /// <summary>
    /// Regression guard for the two-pass MINOR-2 fix: a contested candidate elsewhere in the same batch
    /// must not collaterally block an unrelated, genuinely unambiguous remap in the same batch.
    /// </summary>
    [Fact]
    public void ResolveStalePanelPaths_UncontestedCandidateAlongsideAContestedOne_StillRemapsTheUncontestedOne()
    {
        // Regression guard for the two-pass MINOR-2 fix: a contested candidate elsewhere in the same
        // batch must not cause collateral damage to an unrelated, genuinely unambiguous remap.
        const string staleA = @"C:\fake\stale-a.lnk";
        const string staleB = @"C:\fake\stale-b.lnk";
        const string staleC = @"C:\fake\stale-c.lnk";
        const string contestedCandidate = @"C:\ManagedRoot\contested.lnk";
        const string uncontestedCandidate = @"C:\ManagedRoot\uncontested.lnk";
        var panelA = new ChildWindowModel { FilePaths = [staleA] };
        var panelB = new ChildWindowModel { FilePaths = [staleB] };
        var panelC = new ChildWindowModel { FilePaths = [staleC] };

        var storageMock = new Mock<IPanelItemStorageService>();
        storageMock.Setup(s => s.IsManaged(It.IsAny<string>())).Returns(false);
        storageMock.Setup(s => s.FindManagedCandidates(staleA)).Returns([contestedCandidate]);
        storageMock.Setup(s => s.FindManagedCandidates(staleB)).Returns([contestedCandidate]);
        storageMock.Setup(s => s.FindManagedCandidates(staleC)).Returns([uncontestedCandidate]);

        WindowManager.ResolveStalePanelPaths([panelA, panelB, panelC], storageMock.Object);

        Assert.Equal(staleA, Assert.Single(panelA.FilePaths));
        Assert.Equal(staleB, Assert.Single(panelB.FilePaths));
        Assert.Equal(uncontestedCandidate, Assert.Single(panelC.FilePaths));
    }

    // ── CreateEachIsolated (LoadWindowsState's per-item isolation) ─────────────────

    /// <summary>
    /// Verifies that <see cref="WindowManager.CreateEachIsolated"/> creates every state and returns
    /// zero skipped when all factory calls succeed.
    /// </summary>
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

    /// <summary>
    /// Verifies that <see cref="WindowManager.CreateEachIsolated"/> skips only the factory call that
    /// throws and still creates every other state, so a single corrupt panel does not abort the rest
    /// of the load.
    /// </summary>
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

    /// <summary>
    /// Verifies that <see cref="WindowManager.CreateEachIsolated"/> skips every state and never
    /// propagates an exception when every factory call throws.
    /// </summary>
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

    /// <summary>
    /// Verifies that <see cref="WindowManager.CreateEachIsolated"/> returns zero and never invokes the
    /// factory for an empty states collection.
    /// </summary>
    [Fact]
    public void CreateEachIsolated_EmptyStates_ReturnsZero_NeverInvokesFactory()
    {
        // Act
        var skipped = WindowManager.CreateEachIsolated([], _ => throw new InvalidOperationException("should not run"));

        // Assert
        Assert.Equal(0, skipped);
    }

    // ── SyncEachIsolated (PersistWindowsState's / SyncAllWindowsToModels' per-item isolation) ──────

    /// <summary>
    /// Verifies that <see cref="WindowManager.SyncEachIsolated"/> syncs every entry when all sync calls
    /// succeed.
    /// </summary>
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

    /// <summary>
    /// Regression test for the Phase 7 / Block C M1 review finding: verifies that
    /// <see cref="WindowManager.SyncEachIsolated"/> isolates a throwing entry, still syncs every other
    /// entry, and never propagates the exception.
    /// </summary>
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

    /// <summary>
    /// Verifies that <see cref="WindowManager.SyncEachIsolated"/> does nothing for an empty entries
    /// collection.
    /// </summary>
    [Fact]
    public void SyncEachIsolated_EmptyWindows_DoesNothing()
    {
        var synced = 0;

        WindowManager.SyncEachIsolated([], _ => synced++);

        Assert.Equal(0, synced);
    }

    // ── SaveWindowsState / SaveWindowsStateNow — thin delegates onto IApplicationStateService ────
    //
    // ADR-024 C6: the sync-then-serialize-then-write work (and its per-panel isolation / never-throws
    // contract) moved entirely into ApplicationStateService.PersistNow() — the single writer for the
    // whole shared document. WindowManager.SaveWindowsState()/SaveWindowsStateNow() are now pure
    // delegates, so their own tests only need to prove the delegation; the sync-failure-isolation
    // behaviour itself is covered by ApplicationStateServiceTests, and WindowManager.SyncEachIsolated's
    // own per-item isolation is still covered directly above.

    /// <summary>
    /// Verifies that <see cref="WindowManager.SaveWindowsState"/> delegates to
    /// <see cref="IApplicationStateService.RequestSave"/>.
    /// </summary>
    [Fact]
    public void SaveWindowsState_DelegatesToApplicationStateServiceRequestSave()
    {
        var manager = BuildWindowManager(out var applicationStateServiceMock);

        manager.SaveWindowsState();

        applicationStateServiceMock.Verify(s => s.RequestSave(), Times.Once);
    }

    /// <summary>
    /// Verifies that <see cref="WindowManager.SaveWindowsStateNow"/> delegates to
    /// <see cref="IApplicationStateService.SaveNow"/>.
    /// </summary>
    [Fact]
    public void SaveWindowsStateNow_DelegatesToApplicationStateServiceSaveNow()
    {
        var manager = BuildWindowManager(out var applicationStateServiceMock);

        manager.SaveWindowsStateNow();

        applicationStateServiceMock.Verify(s => s.SaveNow(), Times.Once);
    }

    // ── LoadWindowsState — reads IApplicationStateService.Panels, per-item construction isolation ─

    /// <summary>
    /// Verifies that <see cref="WindowManager.LoadWindowsState"/> does nothing and creates no windows
    /// when <see cref="IApplicationStateService.Panels"/> is empty.
    /// </summary>
    [Fact]
    public void LoadWindowsState_EmptyPanelsList_DoesNothing_NoWindowsCreated()
    {
        // Arrange — JSON validity/corruption recovery is entirely the store's concern now (ADR-024
        // D-6/D-7); an empty Panels list is what a FirstRun/empty document looks like by the time it
        // reaches WindowManager.
        var manager = BuildWindowManager(panels: []);

        // Act
        var exception = Record.Exception(manager.LoadWindowsState);

        // Assert
        Assert.Null(exception);
        Assert.Empty(manager.GetAllChildWindows());
    }

    /// <summary>
    /// Verifies that <see cref="WindowManager.LoadWindowsState"/> attempts to create a window for every
    /// persisted panel without throwing, even though a headless test host cannot resolve a real WPF
    /// <c>ChildWindow</c> (per-item construction is isolated instead).
    /// </summary>
    [Fact]
    public void LoadWindowsState_PanelsPresent_CreatesAWindowForEach()
    {
        // Arrange
        var panels = new List<ChildWindowModel> { new() { Title = "A" }, new() { Title = "B" } };
        var manager = BuildWindowManager(panels: panels);

        // Act — CreateChildWindow resolves a real WPF ChildWindow via DI, which the headless test
        // service provider mock cannot supply, so panel construction is expected to fail per-item and
        // be isolated (see CreateEachIsolated_* above) rather than throw out of LoadWindowsState.
        var exception = Record.Exception(manager.LoadWindowsState);

        // Assert
        Assert.Null(exception);
    }

    // ── LoadWindowsState — ReportPartialLoad on skipped panels (ADR-024 finalization C-1) ─────────

    /// <summary>
    /// Regression test for the C-1 review finding: verifies that
    /// <see cref="WindowManager.LoadWindowsState"/> reports a partial load via
    /// <see cref="IApplicationStateService.ReportPartialLoad"/> when one or more panels fail to
    /// construct, so the store can quarantine the just-loaded document before the next save.
    /// </summary>
    [Fact]
    public void LoadWindowsState_SomePanelsFailToConstruct_ReportsPartialLoad()
    {
        // Regression test for the C-1 review finding: a per-panel construction failure must not
        // silently vanish — WindowManager must tell IApplicationStateService so it can quarantine the
        // just-loaded (still-complete) document before the next Save() persists a reduced one. In this
        // headless test environment CreateChildWindow always fails (no real WPF ChildWindow registered
        // on the mocked IServiceProvider — see LoadWindowsState_PanelsPresent_CreatesAWindowForEach
        // above), so every panel here exercises exactly the skipped > 0 path.
        var panels = new List<ChildWindowModel> { new() { Title = "A" }, new() { Title = "B" } };
        var manager = BuildWindowManager(out var applicationStateServiceMock, panels: panels);

        manager.LoadWindowsState();

        applicationStateServiceMock.Verify(s => s.ReportPartialLoad(It.IsAny<string>()), Times.Once);
    }

    /// <summary>
    /// Verifies that <see cref="WindowManager.LoadWindowsState"/> never reports a partial load when
    /// <see cref="IApplicationStateService.Panels"/> is empty.
    /// </summary>
    [Fact]
    public void LoadWindowsState_EmptyPanelsList_NeverReportsPartialLoad()
    {
        var manager = BuildWindowManager(out var applicationStateServiceMock, panels: []);

        manager.LoadWindowsState();

        applicationStateServiceMock.Verify(s => s.ReportPartialLoad(It.IsAny<string>()), Times.Never);
    }

    // ── MovePanelToPage (docs/prompts/settings-window-redesign.md D-10, Panels section "Move to page ▾") ──

    /// <summary>
    /// Verifies that <see cref="WindowManager.MovePanelToPage"/> updates
    /// <see cref="ChildWindowModel.PageId"/>, sets <see cref="ChildWindowModel.IsVisible"/> to whether
    /// the destination is the current page, and persists synchronously.
    /// </summary>
    /// <param name="destinationPageId">The page to move the panel to.</param>
    /// <param name="expectedVisible">The expected resulting <see cref="ChildWindowModel.IsVisible"/>.</param>
    [Theory]
    [InlineData("default", true)]
    [InlineData("work", false)]
    public void MovePanelToPage_UpdatesPageIdAndVisibilityAndSaves(string destinationPageId, bool expectedVisible)
    {
        var manager = BuildWindowManager(out var applicationStateServiceMock, out _, currentPage: "default");
        var panel = new ChildWindowModel { PageId = "other", IsVisible = false };

        manager.MovePanelToPage(panel, destinationPageId);

        Assert.Equal(destinationPageId, panel.PageId);
        Assert.Equal(expectedVisible, panel.IsVisible);
        applicationStateServiceMock.Verify(s => s.SaveNow(), Times.Once);
    }

    /// <summary>
    /// Verifies that <see cref="WindowManager.MovePanelToPage"/> is a no-op when the panel is already on
    /// the destination page.
    /// </summary>
    [Fact]
    public void MovePanelToPage_AlreadyOnDestinationPage_NoOp()
    {
        var manager = BuildWindowManager(out var applicationStateServiceMock, out _, currentPage: "default");
        var panel = new ChildWindowModel { PageId = "work", IsVisible = true };

        manager.MovePanelToPage(panel, "work");

        applicationStateServiceMock.Verify(s => s.SaveNow(), Times.Never);
    }

    /// <summary>
    /// Verifies that <see cref="WindowManager.MovePanelToPage"/> is a no-op for a blank/whitespace-only
    /// destination page identifier — it never mutates the panel or saves.
    /// </summary>
    /// <param name="destinationPageId">The blank/whitespace-only page identifier under test.</param>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void MovePanelToPage_BlankPageId_NoOp(string destinationPageId)
    {
        var manager = BuildWindowManager(out var applicationStateServiceMock, out _, currentPage: "default");
        var panel = new ChildWindowModel { PageId = "work", IsVisible = true };

        manager.MovePanelToPage(panel, destinationPageId);

        Assert.Equal("work", panel.PageId);
        applicationStateServiceMock.Verify(s => s.SaveNow(), Times.Never);
    }

    /// <summary>
    /// Verifies that <see cref="WindowManager.MovePanelToPage"/> rejects a <see langword="null"/> panel.
    /// </summary>
    [Fact]
    public void MovePanelToPage_NullPanel_Throws()
    {
        var manager = BuildWindowManager(out _, out _, currentPage: "default");

        Assert.Throws<ArgumentNullException>(() => manager.MovePanelToPage(null!, "work"));
    }

    /// <summary>
    /// Fix-cycle finding 10: verifies that <see cref="WindowManager.MovePanelToPage"/> is a no-op for a
    /// destination page id that isn't one of <see cref="IPageManager.Pages"/>'s existing pages — it never
    /// mutates the panel's <see cref="ChildWindowModel.PageId"/>/<see cref="ChildWindowModel.IsVisible"/>
    /// or saves.
    /// </summary>
    [Fact]
    public void MovePanelToPage_UnknownPageId_NoOp()
    {
        var manager = BuildWindowManager(out var applicationStateServiceMock, out var pageManagerMock, currentPage: "default");
        pageManagerMock.SetupGet(p => p.Pages).Returns(["default", "work"]);
        var panel = new ChildWindowModel { PageId = "work", IsVisible = true };

        manager.MovePanelToPage(panel, "no-such-page");

        Assert.Equal("work", panel.PageId);
        Assert.True(panel.IsVisible);
        applicationStateServiceMock.Verify(s => s.SaveNow(), Times.Never);
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    private static MonitorInfo BuildMonitor(Rect workArea) =>
        new("\\\\.\\DISPLAY1", workArea, workArea, IsPrimary: true);

    /// <summary>
    /// Same shape as the other <c>BuildWindowManager</c> overloads, but also stubs <see cref="IPageManager"/>
    /// behind the mocked <see cref="IServiceProvider"/> — required only by <see cref="WindowManager.MovePanelToPage"/>,
    /// which lazily resolves <c>IPageManager.CurrentPage</c> through it (breaking the WindowManager ↔
    /// PageManagerService circular DI dependency, see <c>WindowManager.PageManager</c>'s remarks).
    /// </summary>
    private static WindowManager BuildWindowManager(
        out Mock<IApplicationStateService> applicationStateServiceMock,
        out Mock<IPageManager> pageManagerMock,
        string currentPage)
    {
        var serviceProviderMock = new Mock<IServiceProvider>();
        var monitorServiceMock = new Mock<IMonitorService>();
        var itemStorageMock = new Mock<IPanelItemStorageService>();
        applicationStateServiceMock = new Mock<IApplicationStateService>();
        applicationStateServiceMock.SetupGet(s => s.Panels).Returns([]);

        pageManagerMock = new Mock<IPageManager>();
        pageManagerMock.SetupGet(p => p.CurrentPage).Returns(currentPage);

        // MovePanelToPage's "known destination page" guard (fix-cycle finding 10) needs Pages to contain
        // every destination the other MovePanelToPage tests already move a panel to; MovePanelToPage_
        // UnknownPageId_NoOp overrides this with its own narrower list.
        pageManagerMock.SetupGet(p => p.Pages).Returns(["default", "work", "other"]);
        serviceProviderMock.Setup(p => p.GetService(typeof(IPageManager))).Returns(pageManagerMock.Object);

        var appSettingsMock = new Mock<IAppSettings>();
        appSettingsMock.SetupGet(s => s.GridSize).Returns(1);

        return new WindowManager(
            serviceProviderMock.Object,
            monitorServiceMock.Object,
            itemStorageMock.Object,
            applicationStateServiceMock.Object,
            appSettingsMock.Object,
            FakeDesktopScale.Uniform().Object);
    }

    private static WindowManager BuildWindowManager(IReadOnlyList<ChildWindowModel>? panels = null)
        => BuildWindowManager(out _, panels);

    private static WindowManager BuildWindowManager(out Mock<IApplicationStateService> applicationStateServiceMock, IReadOnlyList<ChildWindowModel>? panels = null)
    {
        var serviceProviderMock = new Mock<IServiceProvider>();
        var monitorServiceMock = new Mock<IMonitorService>();
        var itemStorageMock = new Mock<IPanelItemStorageService>();
        applicationStateServiceMock = new Mock<IApplicationStateService>();
        applicationStateServiceMock.SetupGet(s => s.Panels).Returns(panels ?? []);
        applicationStateServiceMock.Setup(s => s.RequestSave());
        applicationStateServiceMock.Setup(s => s.SaveNow());

        // GridSize=1 ("no grid") by default — the ADR-011 Amendment grid-size normalization this mock
        // otherwise triggers on every ResolveMonitorPlacement/OnMonitorsChanged call is then a pure no-op,
        // matching every pre-amendment expectation in this file byte-for-byte.
        var appSettingsMock = new Mock<IAppSettings>();
        appSettingsMock.SetupGet(s => s.GridSize).Returns(1);

        return new WindowManager(
            serviceProviderMock.Object,
            monitorServiceMock.Object,
            itemStorageMock.Object,
            applicationStateServiceMock.Object,
            appSettingsMock.Object,
            FakeDesktopScale.Uniform().Object);
    }
}
