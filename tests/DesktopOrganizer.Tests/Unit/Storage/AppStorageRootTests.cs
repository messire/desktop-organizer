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
    [Fact]
    public void DebugAndReleaseFolderNames_AreDistinct()
    {
        Assert.NotEqual(AppStorageRoot.ReleaseFolderName, AppStorageRoot.DebugFolderName);
        Assert.StartsWith(AppStorageRoot.ReleaseFolderName, AppStorageRoot.DebugFolderName, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseFolderName_IsTheHistoricalRoot()
    {
        // The installed application must keep reading what earlier releases wrote.
        Assert.Equal("DesktopOrganizer", AppStorageRoot.ReleaseFolderName);
    }

    [Fact]
    public void FolderName_MatchesThisBuildConfiguration()
    {
#if DEBUG
        Assert.Equal(AppStorageRoot.DebugFolderName, AppStorageRoot.FolderName);
#else
        Assert.Equal(AppStorageRoot.ReleaseFolderName, AppStorageRoot.FolderName);
#endif
    }

    [Fact]
    public void Default_IsFolderNameUnderLocalApplicationData()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppStorageRoot.FolderName);

        Assert.Equal(expected, AppStorageRoot.Default);
    }

    [Fact]
    public void StateStorePaths_DefaultConstructor_UsesAppStorageRoot()
    {
        var paths = new StateStorePaths();

        Assert.Equal(AppStorageRoot.Default, paths.StorageRoot);
        Assert.Equal(Path.Combine(AppStorageRoot.Default, "State"), paths.StateDirectory);
        Assert.Equal(Path.Combine(AppStorageRoot.Default, ".storage-lock"), paths.LockFilePath);
    }

    [Fact]
    public void PanelItemStorageService_DefaultConstructor_UsesAppStorageRoot()
    {
        // Constructing the service touches no files (the manifest is read lazily), so the real
        // default constructor is safe to exercise here.
        var service = new PanelItemStorageService();

        Assert.Equal(Path.Combine(AppStorageRoot.Default, "PanelItems"), service.StorageRoot);
    }

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
