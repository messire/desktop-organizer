using System.IO;
using DesktopOrganizer.Core;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Abstractions.Storage;
using DesktopOrganizer.Tests.Helpers;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;
using DesktopOrganizer.UI.ViewModels;
using Moq;

namespace DesktopOrganizer.Tests.Unit.ViewModels;

/// <summary>
/// ADR-009 amendment 2026-10-10 (O2): the settings "Files without a panel" card — background scan on demand,
/// references gathered from live panels and layout snapshots, explicit per-item return, nothing automatic.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class ManagedStorageOrphansViewModelTests
{
    private const string ManagedA = @"C:\Store\PanelItems\aaa\Report.txt";

    private readonly Mock<IManagedStorageOrphanScanner> _scanner = new();
    private readonly Mock<IPanelItemStorageService> _storage = new();
    private readonly Mock<IWindowManager> _windowManager = new();
    private readonly Mock<ILayoutSnapshotService> _snapshots = new();
    private readonly Mock<IApplicationStateStore> _stateStore = new();
    private readonly Mock<IExternalLinkLauncher> _launcher = new();
    private readonly Mock<IStatusNotifier> _status = new();

    /// <summary>Initializes a new instance of the <see cref="ManagedStorageOrphansViewModelTests"/> class with a writable session and no panels.</summary>
    public ManagedStorageOrphansViewModelTests()
    {
        _stateStore.SetupGet(s => s.IsWritable).Returns(true);
        _windowManager.Setup(w => w.GetAllChildWindows()).Returns([]);
        _snapshots.SetupGet(s => s.Snapshots).Returns([]);
    }

    /// <summary>The scan runs off the calling thread, receives the paths of every live panel and every snapshot panel, and fills the list.</summary>
    [Fact]
    public async Task ScanAsync_GathersPanelAndSnapshotReferences_RunsInBackground_FillsItems()
    {
        _windowManager.Setup(w => w.GetAllChildWindows()).Returns(
        [
            new ChildWindowModel { FilePaths = [@"C:\live\1.txt"] },
            new ChildWindowModel { FilePaths = [@"C:\live\2.txt"], PageId = "other" },
        ]);
        _snapshots.SetupGet(s => s.Snapshots).Returns(
        [
            new LayoutSnapshot { PanelJsonItems = ["{\"FilePaths\":[\"C:\\\\snap\\\\3.txt\"]}", "not json", "{}", "{\"FilePaths\":null}"] },
        ]);
        var callerThread = Environment.CurrentManagedThreadId;
        var scanThread = -1;
        IReadOnlyCollection<string>? received = null;
        _scanner.Setup(s => s.Scan(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .Returns<IReadOnlyCollection<string>, CancellationToken>((refs, _) =>
            {
                scanThread = Environment.CurrentManagedThreadId;
                received = refs;
                return [new ManagedStorageOrphan(ManagedA, @"C:\Desktop\Report.txt", "Report.txt", false, new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc))];
            });
        var sut = BuildSut();

        await sut.ScanAsync();

        Assert.NotEqual(callerThread, scanThread);
        Assert.NotNull(received);
        Assert.Contains(@"C:\live\1.txt", received);
        Assert.Contains(@"C:\live\2.txt", received);
        Assert.Contains(@"C:\snap\3.txt", received);
        _windowManager.Verify(w => w.SyncAllWindowsToModels(), Times.Once);
        var item = Assert.Single(sut.Items);
        Assert.Equal("Report.txt", item.Name);
        Assert.Equal(@"C:\Desktop\Report.txt", item.OriginalPath);
        Assert.Equal(ManagedA, item.ManagedPath);
        Assert.Equal(DateTimeKind.Local, item.StoredAt.Kind);
        Assert.Equal("Found: 1", sut.StatusText);
        Assert.False(sut.IsBusy);
        Assert.True(sut.HasScanned);
    }

    /// <summary>
    /// A Folder Portal's source folder - on a live panel and inside a layout snapshot - is passed to the scanner as
    /// "in use": a portal may point into panel storage, and returning that item would pull the folder away from it.
    /// </summary>
    /// <returns>A task completing when the check is done.</returns>
    [Fact]
    public async Task ScanAsync_PassesPortalSourceFoldersOfPanelsAndSnapshotsAsReferences()
    {
        _windowManager.Setup(w => w.GetAllChildWindows()).Returns(
        [
            new ChildWindowModel { SourceFolderPath = @"C:\Store\PanelItems\aaa\Folder" },
            new ChildWindowModel { SourceFolderPath = "  " },
        ]);
        _snapshots.SetupGet(s => s.Snapshots).Returns(
        [
            new LayoutSnapshot { PanelJsonItems = ["{\"SourceFolderPath\":\"C:\\\\Store\\\\PanelItems\\\\bbb\\\\Other\",\"FilePaths\":[]}", "{\"SourceFolderPath\":null}", "{\"SourceFolderPath\":5}"] },
        ]);
        IReadOnlyCollection<string>? received = null;
        _scanner.Setup(s => s.Scan(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .Returns<IReadOnlyCollection<string>, CancellationToken>((refs, _) =>
            {
                received = refs;
                return [];
            });
        var sut = BuildSut();

        await sut.ScanAsync();

        Assert.NotNull(received);
        Assert.Contains(@"C:\Store\PanelItems\aaa\Folder", received);
        Assert.Contains(@"C:\Store\PanelItems\bbb\Other", received);
        Assert.Equal(2, received.Count);
    }

    /// <summary>An item whose stored-at stamp could not be read (scanner reports MinValue) shows no date.</summary>
    /// <param name="year">The year of the scanner's timestamp.</param>
    /// <param name="expectedKnown">Whether the date is considered known.</param>
    [Theory]
    [InlineData(1, false)]
    [InlineData(2026, true)]
    public void Item_HasStoredAt_IsFalseForUnreadableTimestamp(int year, bool expectedKnown)
    {
        var stamp = year == 1 ? DateTime.MinValue : new DateTime(year, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        var item = new ManagedStorageOrphanItem(new ManagedStorageOrphan(ManagedA, @"C:\Desktop\Report.txt", "Report.txt", false, stamp));

        Assert.Equal(expectedKnown, item.HasStoredAt);
    }

    /// <summary>With nothing orphaned the list stays empty and says so.</summary>
    [Fact]
    public async Task ScanAsync_NoOrphans_ShowsNoneMessage()
    {
        _scanner.Setup(s => s.Scan(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>())).Returns([]);
        var sut = BuildSut();

        await sut.ScanAsync();

        Assert.Empty(sut.Items);
        Assert.Equal("No files without a panel were found.", sut.StatusText);
    }

    /// <summary>A read-only session (newer schema / no lock) reports its panels as absent - scanning would offer files real panels use, so it refuses.</summary>
    [Fact]
    public async Task ScanAsync_ReadOnlySession_DoesNotScan()
    {
        _stateStore.SetupGet(s => s.IsWritable).Returns(false);
        var sut = BuildSut();

        await sut.ScanAsync();

        _scanner.Verify(s => s.Scan(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal("Not available in read-only mode.", sut.StatusText);
        Assert.False(sut.ScanCommand.CanExecute(null));
        Assert.True(sut.IsReadOnlySession);
    }

    /// <summary>
    /// In a read-only session the card is not silently empty: with no scan ever run, the status line says why
    /// the scan button is unavailable (decisions review cycle 1, U2).
    /// </summary>
    [Fact]
    public void StatusText_ReadOnlySession_ExplainsUnavailabilityBeforeAnyScan()
    {
        _stateStore.SetupGet(s => s.IsWritable).Returns(false);

        var sut = BuildSut();

        Assert.Equal("Not available in read-only mode.", sut.StatusText);
        Assert.False(sut.ScanCommand.CanExecute(null));
    }

    /// <summary>A writable session starts with an empty status line.</summary>
    [Fact]
    public void StatusText_WritableSession_IsEmptyBeforeAnyScan()
    {
        var sut = BuildSut();

        Assert.Equal(string.Empty, sut.StatusText);
    }

    /// <summary>The "Original location" column shows the parent folder's name; the full path stays in <c>OriginalPath</c> for the tooltip.</summary>
    /// <param name="originalPath">The path the manifest remembers.</param>
    /// <param name="expectedFolder">The expected short name.</param>
    [Theory]
    [InlineData(@"C:\Users\me\Desktop\Report.txt", "Desktop")]
    [InlineData(@"C:\Report.txt", @"C:\")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void Item_OriginalFolderName_IsTheParentFolderLeaf(string originalPath, string expectedFolder)
    {
        var item = new ManagedStorageOrphanItem(new ManagedStorageOrphan(ManagedA, originalPath, "Report.txt", false, DateTime.UtcNow));

        Assert.Equal(expectedFolder, item.OriginalFolderName);
        Assert.Equal(originalPath, item.OriginalPath);
    }

    /// <summary>An item whose original location is unknown cannot be returned: the button is disabled and a forced call does nothing (no error after the click).</summary>
    /// <returns>A task completing when the check is done.</returns>
    [Fact]
    public async Task ReturnAsync_EmptyOriginalPath_CannotReturn_AndDoesNothing()
    {
        _scanner.Setup(s => s.Scan(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .Returns([new ManagedStorageOrphan(ManagedA, string.Empty, "Report.txt", false, DateTime.UtcNow)]);
        var sut = BuildSut();
        await sut.ScanAsync();
        var item = Assert.Single(sut.Items);

        Assert.False(item.CanReturn);
        Assert.False(sut.ReturnCommand.CanExecute(item));
        Assert.True(sut.OpenFolderCommand.CanExecute(item));

        await sut.ReturnAsync(item);

        _storage.Verify(s => s.Restore(It.IsAny<string>()), Times.Never);
        _status.Verify(s => s.Show(It.IsAny<StatusSeverity>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }

    /// <summary>An item with a known original location can be returned.</summary>
    /// <returns>A task completing when the check is done.</returns>
    [Fact]
    public async Task ReturnCommand_KnownOriginalPath_CanExecute()
    {
        var sut = await ScannedSut();

        Assert.True(sut.Items[0].CanReturn);
        Assert.True(sut.ReturnCommand.CanExecute(sut.Items[0]));
    }

    /// <summary>The success message names the item and the folder it went back to, in Russian too.</summary>
    /// <returns>A task completing when the check is done.</returns>
    [Fact]
    public async Task ReturnAsync_Restored_RussianMessageNamesItemAndFolder()
    {
        _scanner.Setup(s => s.Scan(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .Returns([new ManagedStorageOrphan(ManagedA, @"C:\Desktop\Report.txt", "Report.txt", false, DateTime.UtcNow)]);
        _storage.Setup(s => s.Restore(ManagedA)).Returns(RestoreOutcome.Restored);
        var sut = new ManagedStorageOrphansViewModel(
            _scanner.Object, _storage.Object, _windowManager.Object, _snapshots.Object, _stateStore.Object, _launcher.Object, _status.Object, TestLocalization.Russian);
        await sut.ScanAsync();

        await sut.ReturnAsync(sut.Items[0]);

        _status.Verify(s => s.Show(StatusSeverity.Success, "«Report.txt» возвращён в «Desktop»", "Файлы без панели"), Times.Once);
    }

    /// <summary>A scanner failure never escapes: the busy flag clears and a message is shown.</summary>
    [Fact]
    public async Task ScanAsync_ScannerThrows_ShowsFailureAndStopsBusy()
    {
        _scanner.Setup(s => s.Scan(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>())).Throws(new IOException("boom"));
        var sut = BuildSut();

        await sut.ScanAsync();

        Assert.Equal("Could not scan the storage. Details are in the log.", sut.StatusText);
        Assert.False(sut.IsBusy);
        Assert.Empty(sut.Items);
    }

    /// <summary>The scan command is disabled while a scan is in flight.</summary>
    [Fact]
    public async Task ScanAsync_WhileRunning_IsBusyAndCommandDisabled()
    {
        var release = new ManualResetEventSlim();
        var entered = new ManualResetEventSlim();
        _scanner.Setup(s => s.Scan(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(10));
                return [];
            });
        var sut = BuildSut();

        var task = sut.ScanAsync();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));

        Assert.True(sut.IsBusy);
        Assert.False(sut.ScanCommand.CanExecute(null));

        release.Set();
        await task;
        Assert.False(sut.IsBusy);
    }

    /// <summary>Returning an item calls the existing Restore, drops the row and reports success.</summary>
    [Fact]
    public async Task ReturnAsync_Restored_RemovesRowAndNotifiesSuccess()
    {
        var sut = await ScannedSut();
        _storage.Setup(s => s.Restore(ManagedA)).Returns(RestoreOutcome.Restored);

        await sut.ReturnAsync(sut.Items[0]);

        _storage.Verify(s => s.Restore(ManagedA), Times.Once);
        Assert.Empty(sut.Items);
        _status.Verify(s => s.Show(StatusSeverity.Success, "“Report.txt” returned to “Desktop”", "Files without a panel"), Times.Once);
    }

    /// <summary>A failed restore keeps the row (the file stays in storage) and says so.</summary>
    /// <param name="outcome">What the storage service reported.</param>
    /// <param name="expected">The expected warning text.</param>
    /// <returns>A task completing when the check is done.</returns>
    [Theory]
    [InlineData(RestoreOutcome.Failed, "Could not return “Report.txt”. The file stays in storage.")]
    [InlineData(RestoreOutcome.OriginalPathUnknown, "The original location of “Report.txt” is unknown.")]
    [InlineData(RestoreOutcome.NotManaged, "Could not return “Report.txt”. The file stays in storage.")]
    public async Task ReturnAsync_NotRestored_KeepsRowAndWarns(RestoreOutcome outcome, string expected)
    {
        var sut = await ScannedSut();
        _storage.Setup(s => s.Restore(ManagedA)).Returns(outcome);

        await sut.ReturnAsync(sut.Items[0]);

        Assert.Single(sut.Items);
        _status.Verify(s => s.Show(StatusSeverity.Caution, expected, "Files without a panel"), Times.Once);
    }

    /// <summary>If a panel started using the item since the scan, it is NOT restored: the row goes away with a notice.</summary>
    [Fact]
    public async Task ReturnAsync_ItemNowReferenced_DoesNotRestore()
    {
        var sut = await ScannedSut();
        _scanner.Setup(s => s.Scan(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>())).Returns([]);

        await sut.ReturnAsync(sut.Items[0]);

        _storage.Verify(s => s.Restore(It.IsAny<string>()), Times.Never);
        Assert.Empty(sut.Items);
        _status.Verify(s => s.Show(StatusSeverity.Caution, "“Report.txt” is used by a panel again.", "Files without a panel"), Times.Once);
    }

    /// <summary>A read-only session never moves a file.</summary>
    [Fact]
    public async Task ReturnAsync_ReadOnlySession_DoesNothing()
    {
        var sut = await ScannedSut();
        _stateStore.SetupGet(s => s.IsWritable).Returns(false);

        await sut.ReturnAsync(sut.Items[0]);

        _storage.Verify(s => s.Restore(It.IsAny<string>()), Times.Never);
        Assert.False(sut.ReturnCommand.CanExecute(sut.Items[0]));
    }

    /// <summary>"Open folder" shows the item's own storage folder, never the whole storage root.</summary>
    [Fact]
    public async Task OpenFolderCommand_OpensItemDirectory()
    {
        var sut = await ScannedSut();

        sut.OpenFolderCommand.Execute(sut.Items[0]);

        _launcher.Verify(l => l.OpenFolder(Path.GetDirectoryName(ManagedA)!), Times.Once);
    }

    private async Task<ManagedStorageOrphansViewModel> ScannedSut()
    {
        _scanner.Setup(s => s.Scan(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .Returns([new ManagedStorageOrphan(ManagedA, @"C:\Desktop\Report.txt", "Report.txt", false, DateTime.UtcNow)]);
        var sut = BuildSut();
        await sut.ScanAsync();
        Assert.Single(sut.Items);
        return sut;
    }

    private ManagedStorageOrphansViewModel BuildSut() => new(
        _scanner.Object,
        _storage.Object,
        _windowManager.Object,
        _snapshots.Object,
        _stateStore.Object,
        _launcher.Object,
        _status.Object,
        TestLocalization.English);
}
