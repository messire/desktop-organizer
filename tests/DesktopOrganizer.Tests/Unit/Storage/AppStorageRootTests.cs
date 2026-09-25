using System.IO;
using DesktopOrganizer.Services.Storage;
using DesktopOrganizer.Services.Tools;
using DesktopOrganizer.UI.Services;

namespace DesktopOrganizer.Tests.Unit.Storage;

/// <summary>
/// Pins the Debug/Release storage isolation (ADR-024 amendment 2026-09-25): a developer (Debug)
/// build and the installed (Release) build must never share a per-user data root or an autostart
/// Run value, and every persistent surface must derive its default location from the one
/// <see cref="AppStorageRoot"/> rather than re-spelling <c>%LOCALAPPDATA%\DesktopOrganizer</c>.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class AppStorageRootTests
{
    /// <summary>
    /// Verifies that <see cref="AppStorageRoot.DebugFolderName"/> and <see cref="AppStorageRoot.ReleaseFolderName"/>
    /// are distinct, with the debug name derived from (prefixed by) the release name.
    /// </summary>
    [Fact]
    public void DebugAndReleaseFolderNames_AreDistinct()
    {
        Assert.NotEqual(AppStorageRoot.ReleaseFolderName, AppStorageRoot.DebugFolderName);
        Assert.StartsWith(AppStorageRoot.ReleaseFolderName, AppStorageRoot.DebugFolderName, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that <see cref="AppStorageRoot.ReleaseFolderName"/> equals the historical "DesktopOrganizer"
    /// folder name, so the installed application keeps reading what earlier releases wrote.
    /// </summary>
    [Fact]
    public void ReleaseFolderName_IsTheHistoricalRoot()
    {
        // The installed application must keep reading what earlier releases wrote.
        Assert.Equal("DesktopOrganizer", AppStorageRoot.ReleaseFolderName);
    }

    /// <summary>
    /// Verifies that <see cref="AppStorageRoot.FolderName"/> resolves to the debug or release folder name
    /// matching the active build configuration.
    /// </summary>
    [Fact]
    public void FolderName_MatchesThisBuildConfiguration()
    {
#if DEBUG
        Assert.Equal(AppStorageRoot.DebugFolderName, AppStorageRoot.FolderName);
#else
        Assert.Equal(AppStorageRoot.ReleaseFolderName, AppStorageRoot.FolderName);
#endif
    }

    /// <summary>
    /// Verifies that <see cref="AppStorageRoot.Default"/> equals <see cref="AppStorageRoot.FolderName"/>
    /// combined under <see cref="Environment.SpecialFolder.LocalApplicationData"/>.
    /// </summary>
    [Fact]
    public void Default_IsFolderNameUnderLocalApplicationData()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppStorageRoot.FolderName);

        Assert.Equal(expected, AppStorageRoot.Default);
    }

    /// <summary>
    /// Verifies that <see cref="StateStorePaths"/>'s default constructor derives its storage root, state
    /// directory, and lock file path from <see cref="AppStorageRoot.Default"/>.
    /// </summary>
    [Fact]
    public void StateStorePaths_DefaultConstructor_UsesAppStorageRoot()
    {
        var paths = new StateStorePaths();

        Assert.Equal(AppStorageRoot.Default, paths.StorageRoot);
        Assert.Equal(Path.Combine(AppStorageRoot.Default, "State"), paths.StateDirectory);
        Assert.Equal(Path.Combine(AppStorageRoot.Default, ".storage-lock"), paths.LockFilePath);
    }

    /// <summary>
    /// Verifies that <see cref="PanelItemStorageService"/>'s default constructor derives its storage root
    /// from <see cref="AppStorageRoot.Default"/> (safe to exercise directly since construction touches no files).
    /// </summary>
    [Fact]
    public void PanelItemStorageService_DefaultConstructor_UsesAppStorageRoot()
    {
        // Constructing the service touches no files (the manifest is read lazily), so the real
        // default constructor is safe to exercise here.
        var service = new PanelItemStorageService();

        Assert.Equal(Path.Combine(AppStorageRoot.Default, "PanelItems"), service.StorageRoot);
    }

    /// <summary>
    /// Verifies that <see cref="AutostartService.DebugValueName"/> and <see cref="AutostartService.InstallerValueName"/>
    /// are distinct Run key value names, and that <see cref="AutostartService.ValueName"/> resolves to the
    /// one matching the active build configuration.
    /// </summary>
    [Fact]
    public void AutostartValueNames_DebugAndInstallerAreDistinct()
    {
        Assert.NotEqual(AutostartService.InstallerValueName, AutostartService.DebugValueName);
#if DEBUG
        Assert.Equal(AutostartService.DebugValueName, AutostartService.ValueName);
#else
        Assert.Equal(AutostartService.InstallerValueName, AutostartService.ValueName);
#endif
    }
}
