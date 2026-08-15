using System.IO;
using DesktopOrganizer.Services.Abstractions;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="FileItemRenameService"/> — the single dispatch point for a physical
/// panel-item rename (ADR-013 Decision 4): a not-managed path is renamed with a plain
/// <see cref="File.Move(string, string)"/>/<see cref="Directory.Move(string, string)"/>, a managed
/// path is delegated to <see cref="IPanelItemStorageService.RenameManaged"/> (mocked here — the real
/// storage behavior is covered by <c>PanelItemStorageServiceTests</c>).
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.Services")]
public class FileItemRenameServiceTests
{
    private readonly Mock<IPanelItemStorageService> _storageMock = new();

    private FileItemRenameService BuildSut() => new(_storageMock.Object);

    private static string NewTempFilePath() => Path.Combine(Path.GetTempPath(), $"DOrgRenameSvc_{Guid.NewGuid():N}.txt");

    private static string NewTempDirPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"DOrgRenameSvcDir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ── Not managed: plain physical rename ──────────────────────────────────

    [Fact]
    public void Rename_NotManagedFile_RenamesPhysicallyAndReturnsNewPath()
    {
        var source = NewTempFilePath();
        File.WriteAllText(source, "content");
        _storageMock.Setup(s => s.IsManaged(source)).Returns(false);

        var sut = BuildSut();

        try
        {
            var result = sut.Rename(source, "Renamed.txt");

            Assert.Equal(RenameOutcome.Renamed, result.Outcome);
            Assert.NotNull(result.NewPath);
            Assert.Equal("Renamed.txt", Path.GetFileName(result.NewPath));
            Assert.Equal(Path.GetDirectoryName(source), Path.GetDirectoryName(result.NewPath));
            Assert.True(File.Exists(result.NewPath));
            Assert.False(File.Exists(source));
            Assert.Equal("content", File.ReadAllText(result.NewPath!));
        }
        finally
        {
            if (File.Exists(source)) File.Delete(source);
            var renamed = Path.Combine(Path.GetDirectoryName(source)!, "Renamed.txt");
            if (File.Exists(renamed)) File.Delete(renamed);
        }
    }

    [Fact]
    public void Rename_NotManagedDirectory_RenamesPhysicallyAndReturnsNewPath()
    {
        var source = NewTempDirPath();
        File.WriteAllText(Path.Combine(source, "f.txt"), "x");
        _storageMock.Setup(s => s.IsManaged(source)).Returns(false);

        var sut = BuildSut();
        string? newPath = null;

        try
        {
            var result = sut.Rename(source, "RenamedDir");
            newPath = result.NewPath;

            Assert.Equal(RenameOutcome.Renamed, result.Outcome);
            Assert.NotNull(result.NewPath);
            Assert.Equal("RenamedDir", Path.GetFileName(result.NewPath));
            Assert.True(Directory.Exists(result.NewPath));
            Assert.False(Directory.Exists(source));
            Assert.True(File.Exists(Path.Combine(result.NewPath!, "f.txt")));
        }
        finally
        {
            if (Directory.Exists(source)) Directory.Delete(source, recursive: true);
            if (newPath is not null && Directory.Exists(newPath)) Directory.Delete(newPath, recursive: true);
        }
    }

    // ── Collision ────────────────────────────────────────────────────────────

    [Fact]
    public void Rename_NotManagedFile_DestinationAlreadyExists_ReturnsNameTakenAndSourceIntact()
    {
        var source = NewTempFilePath();
        File.WriteAllText(source, "must-survive");
        var takenName = "AlreadyThere.txt";
        var takenPath = Path.Combine(Path.GetDirectoryName(source)!, takenName);
        File.WriteAllText(takenPath, "someone-elses-file");
        _storageMock.Setup(s => s.IsManaged(source)).Returns(false);

        var sut = BuildSut();

        try
        {
            var result = sut.Rename(source, takenName);

            Assert.Equal(RenameOutcome.NameTaken, result.Outcome);
            Assert.Null(result.NewPath);
            Assert.True(File.Exists(source), "Source must not be touched on a collision.");
            Assert.Equal("must-survive", File.ReadAllText(source));
            Assert.Equal("someone-elses-file", File.ReadAllText(takenPath));
        }
        finally
        {
            File.Delete(source);
            File.Delete(takenPath);
        }
    }

    // ── Not found ────────────────────────────────────────────────────────────

    [Fact]
    public void Rename_PathDoesNotExist_ReturnsNotFound()
    {
        var ghost = Path.Combine(Path.GetTempPath(), $"DOrgGhost_{Guid.NewGuid():N}.txt");
        _storageMock.Setup(s => s.IsManaged(ghost)).Returns(false);

        var sut = BuildSut();

        var result = sut.Rename(ghost, "Whatever.txt");

        Assert.Equal(RenameOutcome.NotFound, result.Outcome);
        Assert.Null(result.NewPath);
    }

    // ── Invalid name — rejected before any I/O ──────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("CON")]
    [InlineData("a/b")]
    [InlineData("Trailing.")]
    public void Rename_InvalidName_ReturnsInvalidNameAndTouchesNothing(string invalidName)
    {
        var source = NewTempFilePath();
        File.WriteAllText(source, "untouched");

        var sut = BuildSut();

        try
        {
            var result = sut.Rename(source, invalidName);

            Assert.Equal(RenameOutcome.InvalidName, result.Outcome);
            Assert.Null(result.NewPath);
            Assert.True(File.Exists(source), "Nothing on disk may be touched for an invalid name.");
            Assert.Equal("untouched", File.ReadAllText(source));

            // Validation happens before any storage-ownership check or I/O.
            _storageMock.Verify(s => s.IsManaged(It.IsAny<string>()), Times.Never);
            _storageMock.Verify(s => s.RenameManaged(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }
        finally
        {
            File.Delete(source);
        }
    }

    // ── Managed path: delegation ─────────────────────────────────────────────

    [Fact]
    public void Rename_ManagedPath_DelegatesToRenameManaged_AndReturnsRenamed()
    {
        const string managedPath = @"C:\fake\PanelItems\abc123\Old.txt";
        const string newManagedPath = @"C:\fake\PanelItems\abc123\New.txt";
        _storageMock.Setup(s => s.IsManaged(managedPath)).Returns(true);
        _storageMock.Setup(s => s.RenameManaged(managedPath, "New.txt"))
            .Returns(new RenameResult(RenameOutcome.Renamed, newManagedPath));

        var sut = BuildSut();

        var result = sut.Rename(managedPath, "New.txt");

        Assert.Equal(RenameOutcome.Renamed, result.Outcome);
        Assert.Equal(newManagedPath, result.NewPath);
        _storageMock.Verify(s => s.RenameManaged(managedPath, "New.txt"), Times.Once);
    }

    [Fact]
    public void Rename_ManagedPath_RenameManagedReturnsFailed_ForwardsFailed()
    {
        const string managedPath = @"C:\fake\PanelItems\abc123\Old.txt";
        _storageMock.Setup(s => s.IsManaged(managedPath)).Returns(true);
        _storageMock.Setup(s => s.RenameManaged(managedPath, "New.txt"))
            .Returns(new RenameResult(RenameOutcome.Failed, null));

        var sut = BuildSut();

        var result = sut.Rename(managedPath, "New.txt");

        Assert.Equal(RenameOutcome.Failed, result.Outcome);
        Assert.Null(result.NewPath);
    }

    /// <summary>
    /// M3 regression (found by code-quality-reviewer): a name collision on a MANAGED item must
    /// surface as the specific <see cref="RenameOutcome.NameTaken"/>, not get collapsed into the
    /// generic <see cref="RenameOutcome.Failed"/> — this service must forward
    /// <see cref="IPanelItemStorageService.RenameManaged"/>'s outcome verbatim rather than mapping
    /// every non-success result to Failed.
    /// </summary>
    [Fact]
    public void Rename_ManagedPath_RenameManagedReturnsNameTaken_ForwardsNameTaken()
    {
        const string managedPath = @"C:\fake\PanelItems\abc123\Old.txt";
        _storageMock.Setup(s => s.IsManaged(managedPath)).Returns(true);
        _storageMock.Setup(s => s.RenameManaged(managedPath, "Taken.txt"))
            .Returns(new RenameResult(RenameOutcome.NameTaken, null));

        var sut = BuildSut();

        var result = sut.Rename(managedPath, "Taken.txt");

        Assert.Equal(RenameOutcome.NameTaken, result.Outcome);
        Assert.Null(result.NewPath);
    }
}
