using System.IO;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Interfaces;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="FileLauncher.ShowContextMenu"/>: the thin, never-throwing bridge from the panel
/// to <see cref="INativeApi.ShowShellContextMenuForPath"/> (mocked — no real shell menu is ever shown).
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public sealed class FileLauncherContextMenuTests : IDisposable
{
    private static readonly IntPtr Owner = new(0x42);

    private readonly Mock<INativeApi> _nativeMock = new();
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"DOrgLauncher_{Guid.NewGuid():N}.txt");

    /// <summary>
    /// Initializes a new instance of the <see cref="FileLauncherContextMenuTests"/> class with one real
    /// temp file.
    /// </summary>
    public FileLauncherContextMenuTests() => File.WriteAllText(_file, "x");

    /// <inheritdoc />
    public void Dispose()
    {
        if (File.Exists(_file)) File.Delete(_file);
    }

    /// <summary>
    /// An existing item is forwarded unchanged (path, owner, physical coordinates) and the native outcome is
    /// returned as-is.
    /// </summary>
    /// <param name="outcome">The outcome the native layer reports.</param>
    [Theory]
    [InlineData(ShellContextMenuOutcome.Dismissed)]
    [InlineData(ShellContextMenuOutcome.Invoked)]
    [InlineData(ShellContextMenuOutcome.RenameRequested)]
    [InlineData(ShellContextMenuOutcome.NotShown)]
    public void ShowContextMenu_ExistingFile_ForwardsToNativeAndReturnsItsOutcome(ShellContextMenuOutcome outcome)
    {
        var native = new ShellContextMenuResult(outcome, outcome == ShellContextMenuOutcome.Invoked ? "copy" : null);
        _nativeMock.Setup(n => n.ShowShellContextMenuForPath(_file, Owner, 640, 480, false)).Returns(native);
        var sut = new FileLauncher(_nativeMock.Object);

        var result = sut.ShowContextMenu(_file, Owner, 640, 480, interceptDelete: false);

        Assert.Equal(native, result);
        _nativeMock.Verify(n => n.ShowShellContextMenuForPath(_file, Owner, 640, 480, false), Times.Once);
    }

    /// <summary>
    /// The delete-intercept request (a managed item) reaches the native layer unchanged, and its
    /// <see cref="ShellContextMenuOutcome.DeleteRequested"/> answer comes back as-is.
    /// </summary>
    [Fact]
    public void ShowContextMenu_InterceptDelete_IsForwarded_AndDeleteRequestedReturned()
    {
        _nativeMock
            .Setup(n => n.ShowShellContextMenuForPath(_file, Owner, 1, 2, true))
            .Returns(new ShellContextMenuResult(ShellContextMenuOutcome.DeleteRequested, null));
        var sut = new FileLauncher(_nativeMock.Object);

        var result = sut.ShowContextMenu(_file, Owner, 1, 2, interceptDelete: true);

        Assert.Equal(ShellContextMenuOutcome.DeleteRequested, result.Outcome);
        _nativeMock.Verify(n => n.ShowShellContextMenuForPath(_file, Owner, 1, 2, true), Times.Once);
    }

    /// <summary>
    /// A missing or blank path never reaches the shell (it could not build a menu for it anyway).
    /// </summary>
    /// <param name="path">The path; <c>"&lt;missing&gt;"</c> stands for a non-existent file.</param>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<missing>")]
    public void ShowContextMenu_MissingOrBlankPath_ReturnsNotShownWithoutNativeCall(string path)
    {
        if (path == "<missing>") path = Path.Combine(Path.GetTempPath(), $"DOrgLauncherMissing_{Guid.NewGuid():N}.txt");
        var sut = new FileLauncher(_nativeMock.Object);

        var result = sut.ShowContextMenu(path, Owner, 0, 0, interceptDelete: false);

        Assert.Equal(ShellContextMenuOutcome.NotShown, result.Outcome);
        _nativeMock.Verify(
            n => n.ShowShellContextMenuForPath(It.IsAny<string>(), It.IsAny<IntPtr>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>()),
            Times.Never);
    }

    /// <summary>
    /// Belt-and-suspenders: even a native layer that throws never takes the UI thread down.
    /// </summary>
    [Fact]
    public void ShowContextMenu_NativeThrows_ReturnsNotShown()
    {
        _nativeMock
            .Setup(n => n.ShowShellContextMenuForPath(It.IsAny<string>(), It.IsAny<IntPtr>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>()))
            .Throws(new System.Runtime.InteropServices.COMException("boom", unchecked((int)0x80004005)));
        var sut = new FileLauncher(_nativeMock.Object);

        var result = sut.ShowContextMenu(_file, Owner, 0, 0, interceptDelete: false);

        Assert.Equal(ShellContextMenuOutcome.NotShown, result.Outcome);
    }
}
