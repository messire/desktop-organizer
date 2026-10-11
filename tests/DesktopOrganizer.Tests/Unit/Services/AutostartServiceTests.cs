using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Services;
using Microsoft.Win32;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Services;

/// <summary>
/// Store-publication A2: <see cref="AutostartService"/> under package identity (Microsoft Store / MSIX) must
/// not touch the HKCU <c>Run</c> key at all - the user controls the manifest <c>startupTask</c> in Windows
/// Settings - while outside identity (Inno channel) it keeps its registry behaviour. Registry assertions use a
/// disposable, per-test HKCU sub-key (<c>Software\DesktopOrganizer.Tests\&lt;GUID&gt;</c>, via the internal
/// constructor), never the real <c>Run</c> key: a test process killed mid-run must not leave an autostart entry behind.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public sealed class AutostartServiceTests : IDisposable
{
    private readonly Mock<IExternalLinkLauncher> _launcher = new();
    private readonly Mock<IAppLog> _log = new();
    private readonly string _valueName = AutostartService.InstallerValueName;
    private readonly string _runKeyPath = @"Software\DesktopOrganizer.Tests\" + Guid.NewGuid().ToString("N");

    /// <summary>Removes the disposable sub-key, whatever a failed test left behind.</summary>
    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_runKeyPath, throwOnMissingSubKey: false);
    }

    /// <summary>Verifies that <see cref="AutostartService.IsManagedBySystem"/> mirrors the package identity.</summary>
    /// <param name="packaged">Whether the process has package identity.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IsManagedBySystem_MirrorsPackageContext(bool packaged)
    {
        var sut = Build(packaged);

        Assert.Equal(packaged, sut.IsManagedBySystem);
    }

    /// <summary>
    /// Verifies that under identity the getter reports "off" without reading the registry: a Run value that
    /// does exist under the service's value name is ignored.
    /// </summary>
    [Fact]
    public void IsEnabled_Packaged_IsFalseEvenWhenRunValueExists()
    {
        WriteRunValue("\"C:\\does\\not\\exist.exe\"");
        var sut = Build(packaged: true);

        Assert.False(sut.IsEnabled);
    }

    /// <summary>Verifies that under identity enabling autostart creates no Run value and logs a warning.</summary>
    [Fact]
    public void IsEnabled_SetTrue_Packaged_WritesNothingAndLogsWarning()
    {
        var sut = Build(packaged: true);

        sut.IsEnabled = true;

        Assert.Null(ReadRunValue());
        _log.Verify(l => l.Warn("AutostartService", "WriteIgnoredPackaged"), Times.Once);
    }

    /// <summary>Verifies that under identity disabling autostart does not delete an existing Run value either.</summary>
    [Fact]
    public void IsEnabled_SetFalse_Packaged_LeavesExistingRunValueAlone()
    {
        WriteRunValue("\"C:\\does\\not\\exist.exe\"");
        var sut = Build(packaged: true);

        sut.IsEnabled = false;

        Assert.NotNull(ReadRunValue());
        _log.Verify(l => l.Warn("AutostartService", "WriteIgnoredPackaged"), Times.Once);
    }

    /// <summary>Verifies that outside identity the Run value is still the source of truth (Inno channel, unchanged behaviour).</summary>
    [Fact]
    public void IsEnabled_NotPackaged_RoundTripsThroughTheRunValue()
    {
        var sut = Build(packaged: false);
        Assert.False(sut.IsEnabled);

        sut.IsEnabled = true;
        Assert.True(sut.IsEnabled);
        Assert.NotNull(ReadRunValue());

        sut.IsEnabled = false;
        Assert.False(sut.IsEnabled);
        Assert.Null(ReadRunValue());
        _log.Verify(l => l.Warn("AutostartService", "WriteIgnoredPackaged"), Times.Never);
    }

    /// <summary>Verifies that <see cref="AutostartService.OpenSystemStartupSettings"/> asks the launcher for the system page exactly once.</summary>
    [Fact]
    public void OpenSystemStartupSettings_CallsLauncherOnce()
    {
        var sut = Build(packaged: true);

        sut.OpenSystemStartupSettings();

        _launcher.Verify(l => l.OpenStartupAppsSettings(), Times.Once);
    }

    /// <summary>Verifies the constructor rejects missing collaborators.</summary>
    [Fact]
    public void Constructor_NullCollaborators_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => new AutostartService(null!, _launcher.Object));
        Assert.Throws<ArgumentNullException>(() => new AutostartService(Mock.Of<IAppPackageContext>(), null!));
    }

    private AutostartService Build(bool packaged) =>
        new(Mock.Of<IAppPackageContext>(c => c.IsPackaged == packaged), _launcher.Object, _log.Object, _valueName, _runKeyPath);

    private void WriteRunValue(string data)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_runKeyPath);
        key.SetValue(_valueName, data);
    }

    private object? ReadRunValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_runKeyPath);
        return key?.GetValue(_valueName);
    }
}
