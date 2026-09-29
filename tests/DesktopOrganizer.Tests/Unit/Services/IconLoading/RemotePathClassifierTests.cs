using DesktopOrganizer.Services.Enums;
using DesktopOrganizer.Services.Interfaces;
using DesktopOrganizer.Services.Tools.IconLoading;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Services.IconLoading;

/// <summary>
/// Tests for <see cref="RemotePathClassifier"/> (ADR-046 D-2, docs/PLANS/background-icon-loading.md §5.2.4,
/// §8.1): root extraction is pure string work, drive letters go through a TTL-cached
/// <see cref="INativeApi.GetDriveKind"/>, and <c>.lnk</c> files through
/// <see cref="INativeApi.TryGetShortcutRawTarget"/> — all against a mocked <see cref="INativeApi"/>, with the
/// cache TTL driven by <see cref="ManualTimeProvider"/>. The drive-kind query runs on its own background
/// thread (R7-14): ordinary tests give it a generous 10 s bound (a mocked answer arrives in microseconds, so
/// nothing ever waits on it), hang tests a zero bound plus a signal-driven blocking mock — no sleeps.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public sealed class RemotePathClassifierTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    private readonly Mock<INativeApi> _nativeApi = new(MockBehavior.Strict);
    private readonly ManualTimeProvider _time = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="RemotePathClassifierTests"/> class: every drive is a fixed
    /// local disk unless a test says otherwise.
    /// </summary>
    public RemotePathClassifierTests()
    {
        _nativeApi.Setup(n => n.GetDriveKind(It.IsAny<string>())).Returns(DriveKind.Fixed);
    }

    /// <summary>Every UNC spelling maps to one <c>\\server\share</c> key without any native call.</summary>
    /// <param name="path">The UNC path.</param>
    /// <param name="expectedRoot">The expected root key.</param>
    [Theory]
    [InlineData(@"\\srv\share\dir\a.txt", @"\\srv\share")]
    [InlineData(@"\\srv\share", @"\\srv\share")]
    [InlineData(@"\\srv\share\", @"\\srv\share")]
    [InlineData(@"//srv/share/dir/a.txt", @"\\srv\share")]
    [InlineData(@"\\?\UNC\srv\share\dir\a.txt", @"\\srv\share")]
    [InlineData(@"\\.\UNC\srv\share\a.txt", @"\\srv\share")]
    [InlineData(@"\\srv", @"\\srv")]
    public void GetOwnRemoteRoot_UncPath_ReturnsServerShareRoot(string path, string expectedRoot)
    {
        var classifier = CreateClassifier();

        Assert.Equal(expectedRoot, classifier.GetOwnRemoteRoot(path));
        _nativeApi.Verify(n => n.GetDriveKind(It.IsAny<string>()), Times.Never);
    }

    /// <summary>A mapped network drive letter maps to its upper-case <c>"X:"</c> key; the drive root is what gets queried.</summary>
    /// <param name="path">A path on drive Z.</param>
    [Theory]
    [InlineData(@"Z:\dir\a.txt")]
    [InlineData(@"z:\dir\a.txt")]
    [InlineData(@"\\?\Z:\dir\a.txt")]
    [InlineData(@"\\.\Z:\dir\a.txt")]
    public void GetOwnRemoteRoot_RemoteDriveLetter_ReturnsDriveKey(string path)
    {
        _nativeApi.Setup(n => n.GetDriveKind(@"Z:\")).Returns(DriveKind.Remote);
        var classifier = CreateClassifier();

        Assert.Equal("Z:", classifier.GetOwnRemoteRoot(path));
        _nativeApi.Verify(n => n.GetDriveKind(@"Z:\"), Times.Once);
    }

    /// <summary>Local drives (including the extended <c>\\?\C:\</c> form) and non-rooted paths are not remote.</summary>
    /// <param name="path">A non-remote path.</param>
    [Theory]
    [InlineData(@"C:\dir\a.txt")]
    [InlineData(@"\\?\C:\dir\a.txt")]
    [InlineData(@"relative\a.txt")]
    [InlineData(@"\\?\Volume{12345678-1234-1234-1234-123456789abc}\a.txt")]
    [InlineData("")]
    public void GetOwnRemoteRoot_LocalOrUnrootedPath_ReturnsNull(string path)
    {
        var classifier = CreateClassifier();

        Assert.Null(classifier.GetOwnRemoteRoot(path));
    }

    /// <summary>Drives positively known to be local stay on the Local lane.</summary>
    /// <param name="kind">A local <see cref="DriveKind"/> (as <see cref="int"/>: the enum is internal, the test method public).</param>
    [Theory]
    [InlineData((int)DriveKind.Fixed)]
    [InlineData((int)DriveKind.Removable)]
    [InlineData((int)DriveKind.CdRom)]
    [InlineData((int)DriveKind.RamDisk)]
    public void GetOwnRemoteRoot_LocalDriveKind_ReturnsNull(int kind)
    {
        _nativeApi.Setup(n => n.GetDriveKind(@"E:\")).Returns((DriveKind)kind);
        var classifier = CreateClassifier();

        Assert.Null(classifier.GetOwnRemoteRoot(@"E:\a.txt"));
    }

    /// <summary>
    /// R7-14: a letter the OS cannot place — no root directory (e.g. a persistent mapping not restored at logon)
    /// or unknown — is routed Remote under its own key: only a positively local kind stays on the Local lane.
    /// </summary>
    /// <param name="kind">An unclassified <see cref="DriveKind"/> (as <see cref="int"/>).</param>
    [Theory]
    [InlineData((int)DriveKind.Unknown)]
    [InlineData((int)DriveKind.NoRootDirectory)]
    public void GetOwnRemoteRoot_UnclassifiedDriveKind_ReturnsDriveKey(int kind)
    {
        _nativeApi.Setup(n => n.GetDriveKind(@"E:\")).Returns((DriveKind)kind);
        var classifier = CreateClassifier();

        Assert.Equal("E:", classifier.GetOwnRemoteRoot(@"E:\a.txt"));
    }

    /// <summary>
    /// R7-14: a <c>GetDriveTypeW</c> that hangs (disconnected mapped letter) never holds the caller: the letter is
    /// classified Remote as soon as the bound passes, later callers get the same answer without a second native
    /// call or another wait, and once the query finally answers, its real answer is cached.
    /// </summary>
    [Fact]
    public async Task GetOwnRemoteRoot_DriveKindQueryHangs_LetterRemoteWithoutBlocking_ThenRealAnswerCached()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        _nativeApi.Setup(n => n.GetDriveKind(@"Z:\")).Returns(() =>
        {
            entered.Set();
            release.Wait(Budget);
            return DriveKind.Fixed;
        });
        var classifier = new RemotePathClassifier(_nativeApi.Object, _time, TimeSpan.Zero);

        Assert.Equal("Z:", classifier.GetOwnRemoteRoot(@"Z:\a.txt"));
        Assert.True(entered.Wait(Budget), "the drive-kind query never started");
        Assert.Equal("Z:", classifier.GetOwnRemoteRoot(@"Z:\b.lnk"));
        Assert.False(classifier.GetPendingDriveKindQuery('Z').IsCompleted);
        _nativeApi.Verify(n => n.GetDriveKind(@"Z:\"), Times.Once);

        release.Set();
        await classifier.GetPendingDriveKindQuery('Z').WaitAsync(Budget);

        Assert.Null(classifier.GetOwnRemoteRoot(@"Z:\a.txt"));
        _nativeApi.Verify(n => n.GetDriveKind(@"Z:\"), Times.Once);
    }

    /// <summary>A letter whose query hangs does not affect any other letter's classification.</summary>
    [Fact]
    public async Task GetOwnRemoteRoot_OneLetterHangs_OtherLettersUnaffected()
    {
        using var release = new ManualResetEventSlim();
        _nativeApi.Setup(n => n.GetDriveKind(@"Z:\")).Returns(() =>
        {
            release.Wait(Budget);
            return DriveKind.Remote;
        });
        var classifier = new RemotePathClassifier(_nativeApi.Object, _time, TimeSpan.Zero);

        try
        {
            Assert.Equal("Z:", classifier.GetOwnRemoteRoot(@"Z:\a.txt"));

            // With a zero bound the first C: call may or may not see its (instant) answer; once it has landed, C: is local.
            classifier.GetOwnRemoteRoot(@"C:\a.txt");
            await classifier.GetPendingDriveKindQuery('C').WaitAsync(Budget);
            Assert.Null(classifier.GetOwnRemoteRoot(@"C:\a.txt"));
        }
        finally
        {
            release.Set();
            await classifier.GetPendingDriveKindQuery('Z').WaitAsync(Budget);
        }
    }

    /// <summary>The drive kind is cached per letter for 60 s: two paths → one native call; after the TTL → asked again.</summary>
    [Fact]
    public void GetOwnRemoteRoot_SameLetter_CachedForTtlThenRefreshed()
    {
        _nativeApi.Setup(n => n.GetDriveKind(@"Z:\")).Returns(DriveKind.Remote);
        var classifier = CreateClassifier();

        Assert.Equal("Z:", classifier.GetOwnRemoteRoot(@"Z:\a.txt"));
        Assert.Equal("Z:", classifier.GetOwnRemoteRoot(@"z:\b\c.txt"));
        _nativeApi.Verify(n => n.GetDriveKind(@"Z:\"), Times.Once);

        _time.Advance(RemotePathClassifier.DriveKindCacheTtl - TimeSpan.FromSeconds(1));
        classifier.GetOwnRemoteRoot(@"Z:\a.txt");
        _nativeApi.Verify(n => n.GetDriveKind(@"Z:\"), Times.Once);

        // The letter was re-assigned to a local disk meanwhile: the refreshed answer wins.
        _nativeApi.Setup(n => n.GetDriveKind(@"Z:\")).Returns(DriveKind.Fixed);
        _time.Advance(TimeSpan.FromSeconds(1));

        Assert.Null(classifier.GetOwnRemoteRoot(@"Z:\a.txt"));
        _nativeApi.Verify(n => n.GetDriveKind(@"Z:\"), Times.Exactly(2));
    }

    /// <summary>Each letter has its own cache entry.</summary>
    [Fact]
    public void GetOwnRemoteRoot_DifferentLetters_QueriedSeparately()
    {
        _nativeApi.Setup(n => n.GetDriveKind(@"Y:\")).Returns(DriveKind.Remote);
        var classifier = CreateClassifier();

        Assert.Null(classifier.GetOwnRemoteRoot(@"C:\a.txt"));
        Assert.Equal("Y:", classifier.GetOwnRemoteRoot(@"Y:\a.txt"));

        _nativeApi.Verify(n => n.GetDriveKind(@"C:\"), Times.Once);
        _nativeApi.Verify(n => n.GetDriveKind(@"Y:\"), Times.Once);
    }

    /// <summary>A throwing native layer neither fails the job nor claims "local": the letter is unclassified, so Remote.</summary>
    [Fact]
    public void GetOwnRemoteRoot_GetDriveKindThrows_ReturnsDriveKey()
    {
        _nativeApi.Setup(n => n.GetDriveKind(It.IsAny<string>())).Throws(new InvalidOperationException("native failure"));
        var classifier = CreateClassifier();

        Assert.Equal("Z:", classifier.GetOwnRemoteRoot(@"Z:\a.txt"));
    }

    /// <summary>A <c>.lnk</c> whose raw target is on a share classifies as that share.</summary>
    [Fact]
    public void GetShortcutTargetRemoteRoot_UncTarget_ReturnsShareRoot()
    {
        SetupShortcut(@"C:\Desktop\app.lnk", @"\\srv\share\tools\app.exe");
        var classifier = CreateClassifier();

        Assert.Equal(@"\\srv\share", classifier.GetShortcutTargetRemoteRoot(@"C:\Desktop\app.lnk"));
    }

    /// <summary>A <c>.lnk</c> whose raw target is on a mapped network drive classifies as that drive.</summary>
    [Fact]
    public void GetShortcutTargetRemoteRoot_MappedDriveTarget_ReturnsDriveKey()
    {
        SetupShortcut(@"C:\Desktop\doc.lnk", @"Z:\docs\report.docx");
        _nativeApi.Setup(n => n.GetDriveKind(@"Z:\")).Returns(DriveKind.Remote);
        var classifier = CreateClassifier();

        Assert.Equal("Z:", classifier.GetShortcutTargetRemoteRoot(@"C:\Desktop\doc.lnk"));
    }

    /// <summary>A local target is not remote.</summary>
    [Fact]
    public void GetShortcutTargetRemoteRoot_LocalTarget_ReturnsNull()
    {
        SetupShortcut(@"C:\Desktop\notepad.lnk", @"C:\Windows\notepad.exe");
        var classifier = CreateClassifier();

        Assert.Null(classifier.GetShortcutTargetRemoteRoot(@"C:\Desktop\notepad.lnk"));
    }

    /// <summary>A PIDL-only link (no path at all) is not classified as remote (R7-15).</summary>
    [Fact]
    public void GetShortcutTargetRemoteRoot_PidlOnlyLink_ReturnsNull()
    {
        string? noTarget = null;
        _nativeApi.Setup(n => n.TryGetShortcutRawTarget(@"C:\Desktop\control.lnk", out noTarget)).Returns(false);
        var classifier = CreateClassifier();

        Assert.Null(classifier.GetShortcutTargetRemoteRoot(@"C:\Desktop\control.lnk"));
    }

    /// <summary>A throwing native layer degrades to "not remote".</summary>
    [Fact]
    public void GetShortcutTargetRemoteRoot_NativeThrows_ReturnsNull()
    {
        string? noTarget = null;
        _nativeApi
            .Setup(n => n.TryGetShortcutRawTarget(It.IsAny<string>(), out noTarget))
            .Throws(new InvalidOperationException("native failure"));
        var classifier = CreateClassifier();

        Assert.Null(classifier.GetShortcutTargetRemoteRoot(@"C:\Desktop\broken.lnk"));
    }

    private RemotePathClassifier CreateClassifier() => new(_nativeApi.Object, _time, Budget);

    private void SetupShortcut(string shortcutPath, string rawTarget)
    {
        string? target = rawTarget;
        _nativeApi.Setup(n => n.TryGetShortcutRawTarget(shortcutPath, out target)).Returns(true);
    }
}
