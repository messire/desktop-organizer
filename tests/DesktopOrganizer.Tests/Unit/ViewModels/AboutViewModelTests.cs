using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Updates;
using DesktopOrganizer.Tests.Helpers;
using DesktopOrganizer.UI.Services;
using DesktopOrganizer.UI.ViewModels;
using Moq;

namespace DesktopOrganizer.Tests.Unit.ViewModels;

/// <summary>
/// Unit tests for <see cref="AboutViewModel"/> (release-hardening-1.0.md §G2): the "Check for updates"
/// command's busy state, its status text for every <see cref="UpdateCheckStatus"/>, the hot language
/// switch, and that every link command opens exactly the constant URL/folder it should — never anything
/// derived from user/network input.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class AboutViewModelTests
{
    private readonly Mock<IUpdateCheckService> _updateCheckServiceMock = new(MockBehavior.Strict);
    private readonly Mock<IExternalLinkLauncher> _linkLauncherMock = new(MockBehavior.Strict);

    /// <summary>
    /// Verifies that <see cref="AboutViewModel.IsChecking"/> is <see langword="true"/> for the whole
    /// duration of a check and flips back to <see langword="false"/> only once it completes.
    /// </summary>
    [Fact]
    public async Task CheckForUpdatesAsync_WhileInFlight_IsCheckingIsTrueUntilItCompletes()
    {
        var tcs = new TaskCompletionSource<UpdateCheckResult>();
        _updateCheckServiceMock.Setup(s => s.CheckForUpdatesAsync(It.IsAny<CancellationToken>())).Returns(tcs.Task);
        var sut = BuildSut();

        var checkTask = sut.CheckForUpdatesAsync();

        Assert.True(sut.IsChecking);

        tcs.SetResult(UpdateCheckResult.UpToDate());
        await checkTask;

        Assert.False(sut.IsChecking);
    }

    /// <summary>
    /// Verifies that <see cref="AboutViewModel.CheckForUpdatesCommand"/> actually drives a real check —
    /// the command wraps <see cref="AboutViewModel.CheckForUpdatesAsync"/>, tested directly by every other
    /// test in this class.
    /// </summary>
    [Fact]
    public void CheckForUpdatesCommand_Execute_RunsTheCheck()
    {
        _updateCheckServiceMock.Setup(s => s.CheckForUpdatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(UpdateCheckResult.UpToDate());
        var sut = BuildSut();

        // Moq's ReturnsAsync hands back an already-completed Task, so the fire-and-forget command's
        // async chain runs to completion synchronously here (no real I/O to suspend on) — no polling
        // needed to observe the result.
        sut.CheckForUpdatesCommand.Execute(null);

        Assert.Equal("You're up to date.", sut.StatusText);
        Assert.False(sut.IsChecking);
    }

    /// <summary>
    /// Verifies that a second call while the first is still in flight is a guarded no-op — it must
    /// return immediately rather than starting (or waiting for) a second underlying check.
    /// </summary>
    [Fact]
    public async Task CheckForUpdatesAsync_SecondCallWhileFirstInFlight_IsIgnored()
    {
        var tcs = new TaskCompletionSource<UpdateCheckResult>();
        _updateCheckServiceMock.Setup(s => s.CheckForUpdatesAsync(It.IsAny<CancellationToken>())).Returns(tcs.Task);
        var sut = BuildSut();

        var firstCall = sut.CheckForUpdatesAsync();
        await sut.CheckForUpdatesAsync(); // guarded no-op — must return immediately, not hang

        tcs.SetResult(UpdateCheckResult.UpToDate());
        await firstCall;

        _updateCheckServiceMock.Verify(s => s.CheckForUpdatesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>Verifies the <see cref="UpdateCheckStatus.UpToDate"/> status text.</summary>
    [Fact]
    public async Task CheckForUpdatesAsync_UpToDate_SetsUpToDateStatusText()
    {
        _updateCheckServiceMock.Setup(s => s.CheckForUpdatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(UpdateCheckResult.UpToDate());
        var sut = BuildSut();

        await sut.CheckForUpdatesAsync();

        Assert.Equal("You're up to date.", sut.StatusText);
        Assert.False(sut.HasReleaseLink);
    }

    /// <summary>
    /// Verifies the <see cref="UpdateCheckStatus.UpdateAvailable"/> status text (version interpolated
    /// into the <c>_Format</c> key) and that <see cref="AboutViewModel.HasReleaseLink"/> turns on.
    /// </summary>
    [Fact]
    public async Task CheckForUpdatesAsync_UpdateAvailable_SetsFormattedStatusTextAndReleaseLink()
    {
        _updateCheckServiceMock.Setup(s => s.CheckForUpdatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(UpdateCheckResult.UpdateAvailable("1.3.0", "https://github.com/messire/DesktopOrganizer/releases/tag/v1.3.0"));
        var sut = BuildSut();

        await sut.CheckForUpdatesAsync();

        Assert.Equal("Update available: 1.3.0", sut.StatusText);
        Assert.True(sut.HasReleaseLink);
    }

    /// <summary>Verifies the <see cref="UpdateCheckStatus.NoReleases"/> status text.</summary>
    [Fact]
    public async Task CheckForUpdatesAsync_NoReleases_SetsNoReleasesStatusText()
    {
        _updateCheckServiceMock.Setup(s => s.CheckForUpdatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(UpdateCheckResult.NoReleases());
        var sut = BuildSut();

        await sut.CheckForUpdatesAsync();

        Assert.Equal("No releases have been published yet.", sut.StatusText);
        Assert.False(sut.HasReleaseLink);
    }

    /// <summary>Verifies that every <see cref="UpdateCheckFailureKind"/> produces its own reason text inside the shared <c>Failed_Format</c> message.</summary>
    /// <param name="kind">The failure kind under test.</param>
    /// <param name="expectedStatusText">The exact English status text <paramref name="kind"/> must produce.</param>
    [Theory]
    [InlineData(UpdateCheckFailureKind.Network, "Couldn't check for updates: network problem")]
    [InlineData(UpdateCheckFailureKind.Timeout, "Couldn't check for updates: the request timed out")]
    [InlineData(UpdateCheckFailureKind.RateLimited, "Couldn't check for updates: GitHub rate limit reached")]
    [InlineData(UpdateCheckFailureKind.InvalidResponse, "Couldn't check for updates: unexpected response from GitHub")]
    [InlineData(UpdateCheckFailureKind.Unexpected, "Couldn't check for updates: unknown error")]
    public async Task CheckForUpdatesAsync_Failed_SetsReasonSpecificStatusText(UpdateCheckFailureKind kind, string expectedStatusText)
    {
        _updateCheckServiceMock.Setup(s => s.CheckForUpdatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(UpdateCheckResult.Failed(kind));
        var sut = BuildSut();

        await sut.CheckForUpdatesAsync();

        Assert.Equal(expectedStatusText, sut.StatusText);
        Assert.False(sut.HasReleaseLink);
    }

    /// <summary>
    /// Verifies that <see cref="AboutViewModel.StatusText"/> is re-translated in place on a later hot
    /// language switch (<see cref="ILocalizationService.CultureChanged"/>) — it must not stay frozen in
    /// whatever language was active when the check completed.
    /// </summary>
    [Fact]
    public async Task CheckForUpdatesAsync_HotLanguageSwitch_ReTranslatesStatusText()
    {
        var localization = TestLocalization.English;
        _updateCheckServiceMock.Setup(s => s.CheckForUpdatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(UpdateCheckResult.UpToDate());
        var sut = new AboutViewModel(_updateCheckServiceMock.Object, _linkLauncherMock.Object, localization, Mock.Of<IAppPackageContext>());

        await sut.CheckForUpdatesAsync();
        Assert.Equal("You're up to date.", sut.StatusText);

        localization.ApplyLanguage("ru");

        Assert.Equal("У вас установлена последняя версия.", sut.StatusText);
    }

    /// <summary>Verifies that <see cref="AboutViewModel.OpenReleaseCommand"/> has nowhere to go before any check has completed.</summary>
    [Fact]
    public void OpenReleaseCommand_BeforeAnyUpdateAvailableResult_CannotExecute()
    {
        var sut = BuildSut();

        Assert.False(sut.OpenReleaseCommand.CanExecute(null));
    }

    /// <summary>
    /// Verifies that <see cref="AboutViewModel.OpenReleaseCommand"/> opens exactly the
    /// <see cref="UpdateCheckResult.ReleaseUrl"/> the most recent check returned — never a derived or
    /// reconstructed URL.
    /// </summary>
    [Fact]
    public async Task OpenReleaseCommand_AfterUpdateAvailable_OpensExactReleaseUrl()
    {
        const string releaseUrl = "https://github.com/messire/DesktopOrganizer/releases/tag/v1.3.0";
        _updateCheckServiceMock.Setup(s => s.CheckForUpdatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(UpdateCheckResult.UpdateAvailable("1.3.0", releaseUrl));
        _linkLauncherMock.Setup(l => l.OpenUrl(releaseUrl));
        var sut = BuildSut();
        await sut.CheckForUpdatesAsync();

        sut.OpenReleaseCommand.Execute(null);

        _linkLauncherMock.Verify(l => l.OpenUrl(releaseUrl), Times.Once);
    }

    /// <summary>Verifies that <see cref="AboutViewModel.OpenProjectPageCommand"/> opens exactly <see cref="UpdateEndpoints.ProjectPageUrl"/>.</summary>
    [Fact]
    public void OpenProjectPageCommand_OpensProjectPageUrl()
    {
        _linkLauncherMock.Setup(l => l.OpenUrl(UpdateEndpoints.ProjectPageUrl));
        var sut = BuildSut();

        sut.OpenProjectPageCommand.Execute(null);

        _linkLauncherMock.Verify(l => l.OpenUrl(UpdateEndpoints.ProjectPageUrl), Times.Once);
    }

    /// <summary>Verifies that <see cref="AboutViewModel.ReportProblemCommand"/> opens exactly <see cref="UpdateEndpoints.SupportUrl"/> (not the private repository's issue tracker).</summary>
    [Fact]
    public void ReportProblemCommand_OpensSupportUrl()
    {
        _linkLauncherMock.Setup(l => l.OpenUrl(UpdateEndpoints.SupportUrl));
        var sut = BuildSut();

        sut.ReportProblemCommand.Execute(null);

        _linkLauncherMock.Verify(l => l.OpenUrl(UpdateEndpoints.SupportUrl), Times.Once);
        _linkLauncherMock.Verify(l => l.OpenUrl(UpdateEndpoints.IssuesUrl), Times.Never);
    }

    /// <summary>Verifies the update check exists outside package identity (Inno channel, developer run).</summary>
    [Fact]
    public void IsUpdateCheckAvailable_WithoutPackageIdentity_IsTrue()
    {
        var sut = BuildSut(isPackaged: false);

        Assert.True(sut.IsUpdateCheckAvailable);
    }

    /// <summary>Verifies the update check is unavailable under package identity (Microsoft Store delivers updates).</summary>
    [Fact]
    public void IsUpdateCheckAvailable_WithPackageIdentity_IsFalse()
    {
        var sut = BuildSut(isPackaged: true);

        Assert.False(sut.IsUpdateCheckAvailable);
    }

    /// <summary>
    /// Verifies that under package identity <see cref="AboutViewModel.CheckForUpdatesAsync"/> returns at once:
    /// no network call (the strict <see cref="IUpdateCheckService"/> mock would throw on any call), no busy state, no status text.
    /// </summary>
    [Fact]
    public async Task CheckForUpdatesAsync_WithPackageIdentity_DoesNotCallTheUpdateService()
    {
        var sut = BuildSut(isPackaged: true);

        await sut.CheckForUpdatesAsync();

        _updateCheckServiceMock.Verify(s => s.CheckForUpdatesAsync(It.IsAny<CancellationToken>()), Times.Never);
        Assert.False(sut.IsChecking);
        Assert.Equal(string.Empty, sut.StatusText);
    }

    /// <summary>Verifies that <see cref="AboutViewModel.OpenLogsFolderCommand"/> opens <see cref="AboutViewModel.LogsFolderPath"/> (the per-user <c>Logs</c> folder) via <see cref="IExternalLinkLauncher.OpenFolder"/>, never <see cref="IExternalLinkLauncher.OpenUrl"/>.</summary>
    [Fact]
    public void OpenLogsFolderCommand_OpensLogsFolderPath()
    {
        _linkLauncherMock.Setup(l => l.OpenFolder(It.IsAny<string>()));
        var sut = BuildSut();

        sut.OpenLogsFolderCommand.Execute(null);

        _linkLauncherMock.Verify(l => l.OpenFolder(sut.LogsFolderPath), Times.Once);
        Assert.EndsWith("Logs", sut.LogsFolderPath, StringComparison.Ordinal);
    }

    private AboutViewModel BuildSut(bool isPackaged = false) =>
        new(
            _updateCheckServiceMock.Object,
            _linkLauncherMock.Object,
            TestLocalization.English,
            Mock.Of<IAppPackageContext>(c => c.IsPackaged == isPackaged));
}
