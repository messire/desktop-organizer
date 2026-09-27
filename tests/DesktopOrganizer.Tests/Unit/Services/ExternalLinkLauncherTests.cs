using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Services;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="ExternalLinkLauncher"/> (M4, release-hardening review): <see cref="ExternalLinkLauncher.OpenUrl"/>
/// must reject anything that isn't a well-formed, absolute <c>https://</c> URL rather than handing it to
/// <c>ShellExecute</c> unchecked.
/// </summary>
/// <remarks>
/// Deliberately never exercises the "accepted" path with a real URL/folder here: doing so would call the
/// real <see cref="System.Diagnostics.Process.Start(System.Diagnostics.ProcessStartInfo)"/> with
/// <c>UseShellExecute = true</c> and actually launch a browser/Explorer window on whatever machine runs
/// the test suite. <c>DesktopOrganizer.Tests.Unit.ViewModels.AboutViewModelTests</c> already covers "opens
/// exactly this target" end-to-end against a mocked <see cref="IExternalLinkLauncher"/>; this class covers
/// only <see cref="ExternalLinkLauncher"/>'s own rejection logic, which never reaches <c>Process.Start</c>.
/// </remarks>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class ExternalLinkLauncherTests
{
    private readonly Mock<IAppLog> _appLogMock = new();

    /// <summary>A <c>javascript:</c> URL must never reach <c>ShellExecute</c>.</summary>
    [Fact]
    public void OpenUrl_JavascriptScheme_RejectedNoThrow()
    {
        var sut = new ExternalLinkLauncher(_appLogMock.Object);

        var exception = Record.Exception(() => sut.OpenUrl("javascript:alert(1)"));

        Assert.Null(exception);
        _appLogMock.Verify(
            l => l.Warn("ExternalLinkLauncher", "OpenUrlRejected", It.IsAny<KeyValuePair<string, object?>[]>()),
            Times.Once);
    }

    /// <summary>A <c>file:</c> URL must never reach <c>ShellExecute</c>.</summary>
    [Fact]
    public void OpenUrl_FileScheme_RejectedNoThrow()
    {
        var sut = new ExternalLinkLauncher(_appLogMock.Object);

        var exception = Record.Exception(() => sut.OpenUrl(@"file:///C:/Windows/System32/cmd.exe"));

        Assert.Null(exception);
        _appLogMock.Verify(
            l => l.Warn("ExternalLinkLauncher", "OpenUrlRejected", It.IsAny<KeyValuePair<string, object?>[]>()),
            Times.Once);
    }

    /// <summary>A plain (unencrypted) <c>http://</c> URL is rejected — only <c>https://</c> is trusted.</summary>
    [Fact]
    public void OpenUrl_PlainHttpScheme_RejectedNoThrow()
    {
        var sut = new ExternalLinkLauncher(_appLogMock.Object);

        var exception = Record.Exception(() => sut.OpenUrl("http://github.com/messire/DesktopOrganizer"));

        Assert.Null(exception);
        _appLogMock.Verify(
            l => l.Warn("ExternalLinkLauncher", "OpenUrlRejected", It.IsAny<KeyValuePair<string, object?>[]>()),
            Times.Once);
    }

    /// <summary>
    /// A malformed value smuggling extra content past a naive string check (quotes, embedded whitespace)
    /// fails <see cref="Uri.TryCreate(string?, UriKind, out Uri?)"/>'s own well-formedness check and is
    /// rejected the same way.
    /// </summary>
    /// <param name="value">The malformed/quoted string under test.</param>
    [Theory]
    [InlineData("\"https://github.com/messire/DesktopOrganizer\" & calc.exe")]
    [InlineData("not a url at all")]
    [InlineData("")]
    [InlineData("   ")]
    public void OpenUrl_MalformedOrQuotedString_RejectedNoThrow(string value)
    {
        var sut = new ExternalLinkLauncher(_appLogMock.Object);

        var exception = Record.Exception(() => sut.OpenUrl(value));

        Assert.Null(exception);
    }

    /// <summary>A relative URL (no scheme/host at all) is rejected — <see cref="ExternalLinkLauncher.OpenUrl"/> only ever accepts an absolute URL.</summary>
    [Fact]
    public void OpenUrl_RelativeUrl_RejectedNoThrow()
    {
        var sut = new ExternalLinkLauncher(_appLogMock.Object);

        var exception = Record.Exception(() => sut.OpenUrl("/messire/DesktopOrganizer/releases"));

        Assert.Null(exception);
        _appLogMock.Verify(
            l => l.Warn("ExternalLinkLauncher", "OpenUrlRejected", It.IsAny<KeyValuePair<string, object?>[]>()),
            Times.Once);
    }
}
