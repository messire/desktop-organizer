using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Services;
using Microsoft.Win32;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Services;

/// <summary>
/// <see cref="AutostartService"/> contract. Store-publication A2: under package identity (Microsoft Store /
/// MSIX) the service must not touch the HKCU <c>Run</c> key at all - the user controls the manifest
/// <c>startupTask</c> in Windows Settings - while outside identity (Inno channel) it keeps its registry
/// behaviour, now including the Task Manager flag under <c>StartupApproved\Run</c> (1.0.1: a Run value Windows
/// disabled there counts as off, enabling clears the flag) and the Debug build's session-only mode (1.0.1: a
/// developer build never registers <c>bin\Debug</c> for autostart). Registry assertions use disposable, per-test
/// HKCU sub-keys (<c>Software\DesktopOrganizer.Tests\&lt;GUID&gt;\...</c>, via the internal constructor), never
/// the real keys: a test process killed mid-run must not leave an autostart entry behind.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public sealed class AutostartServiceTests : IDisposable
{
    private static readonly byte[] StartupApprovedDisabled = [0x03, 0, 0, 0, 0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x01];
    private static readonly byte[] StartupApprovedEnabledLegacy = [0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    private readonly Mock<IExternalLinkLauncher> _launcher = new();
    private readonly Mock<IAppLog> _log = new();
    private readonly string _valueName = AutostartService.InstallerValueName;
    private readonly string _rootKeyPath = @"Software\DesktopOrganizer.Tests\" + Guid.NewGuid().ToString("N");
    private readonly string _runKeyPath;
    private readonly string _approvedKeyPath;

    /// <summary>Initializes a new instance of the <see cref="AutostartServiceTests"/> class.</summary>
    public AutostartServiceTests()
    {
        _runKeyPath = _rootKeyPath + @"\Run";
        _approvedKeyPath = _rootKeyPath + @"\StartupApproved";
    }

    /// <summary>Removes the disposable sub-key tree, whatever a failed test left behind.</summary>
    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_rootKeyPath, throwOnMissingSubKey: false);
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

    /// <summary>
    /// Verifies that a Run value Windows has switched off in Task Manager (<c>StartupApproved</c> first byte with
    /// bit 0 set) reads as disabled: Windows will not launch it, so the toggle must not claim it is on.
    /// </summary>
    [Fact]
    public void IsEnabled_RunValuePresentButDisabledByStartupApproved_IsFalse()
    {
        WriteRunValue("\"C:\\does\\not\\exist.exe\"");
        WriteApprovedValue(StartupApprovedDisabled);
        var sut = Build(packaged: false);

        Assert.False(sut.IsEnabled);
    }

    /// <summary>Verifies that an enabled <c>StartupApproved</c> entry (<c>02</c> or the older <c>06</c>) does not hide a present Run value.</summary>
    /// <param name="firstByte">First byte of the <c>StartupApproved</c> data.</param>
    [Theory]
    [InlineData(0x02)]
    [InlineData(0x06)]
    public void IsEnabled_RunValuePresentAndStartupApprovedEnabled_IsTrue(byte firstByte)
    {
        WriteRunValue("\"C:\\does\\not\\exist.exe\"");
        var data = (byte[])StartupApprovedEnabledLegacy.Clone();
        data[0] = firstByte;
        WriteApprovedValue(data);
        var sut = Build(packaged: false);

        Assert.True(sut.IsEnabled);
    }

    /// <summary>
    /// Verifies that enabling from the app after the user disabled the entry in Task Manager rewrites the
    /// <c>StartupApproved</c> flag to "enabled" (the exact 12 bytes Windows writes), so the new Run value is actually
    /// launched - the 1.0.1 "toggle does nothing" symptom.
    /// </summary>
    [Fact]
    public void IsEnabled_SetTrue_WhenDisabledByStartupApproved_ReenablesTheFlag()
    {
        WriteRunValue("\"C:\\old\\path.exe\"");
        WriteApprovedValue(StartupApprovedDisabled);
        var sut = Build(packaged: false);

        sut.IsEnabled = true;

        Assert.True(sut.IsEnabled);
        Assert.Equal(new byte[] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, ReadApprovedValue());
        Assert.Equal($"\"{Environment.ProcessPath}\"", ReadRunValue());
    }

    /// <summary>Verifies that enabling with no <c>StartupApproved</c> entry creates none: absence already means enabled.</summary>
    [Fact]
    public void IsEnabled_SetTrue_WithoutStartupApprovedEntry_CreatesNone()
    {
        var sut = Build(packaged: false);

        sut.IsEnabled = true;

        Assert.True(sut.IsEnabled);
        Assert.Null(ReadApprovedValue());
    }

    /// <summary>Verifies that disabling removes the <c>StartupApproved</c> entry together with the Run value.</summary>
    [Fact]
    public void IsEnabled_SetFalse_RemovesStartupApprovedEntryToo()
    {
        WriteRunValue("\"C:\\does\\not\\exist.exe\"");
        WriteApprovedValue(StartupApprovedDisabled);
        var sut = Build(packaged: false);

        sut.IsEnabled = false;

        Assert.Null(ReadRunValue());
        Assert.Null(ReadApprovedValue());
    }

    /// <summary>
    /// Verifies the Debug build's mode (<see cref="AutostartService.PersistsToRegistry"/> false): the toggle
    /// round-trips in memory only and the registry is neither read nor written.
    /// </summary>
    [Fact]
    public void IsEnabled_SessionOnly_RoundTripsInMemoryAndNeverTouchesRegistry()
    {
        WriteRunValue("\"C:\\does\\not\\exist.exe\"");
        var sut = Build(packaged: false, persistsToRegistry: false);
        Assert.False(sut.IsEnabled);

        sut.IsEnabled = true;
        Assert.True(sut.IsEnabled);

        sut.IsEnabled = false;
        Assert.False(sut.IsEnabled);
        Assert.NotNull(ReadRunValue());
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

    private AutostartService Build(bool packaged, bool persistsToRegistry = true) =>
        new(Mock.Of<IAppPackageContext>(c => c.IsPackaged == packaged), _launcher.Object, _log.Object, persistsToRegistry, _valueName, _runKeyPath, _approvedKeyPath);

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

    private void WriteApprovedValue(byte[] data)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_approvedKeyPath);
        key.SetValue(_valueName, data, RegistryValueKind.Binary);
    }

    private byte[]? ReadApprovedValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_approvedKeyPath);
        return key?.GetValue(_valueName) as byte[];
    }
}
