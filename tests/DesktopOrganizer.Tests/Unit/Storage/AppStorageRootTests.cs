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
    /// Verifies that <see cref="AppStorageRoot.Default"/> is exactly what <see cref="AppStorageRoot.Resolve"/>
    /// yields for this process. The test host has no package identity, so this is the Inno-channel
    /// location: <see cref="AppStorageRoot.FolderName"/> under <c>%LOCALAPPDATA%</c> (behaviour unchanged).
    /// </summary>
    [Fact]
    public void Default_EqualsResolveForThisProcess()
    {
        var expected = AppStorageRoot.Resolve(NativeApi.HasPackageIdentity, AppStorageRoot.IsDebugBuild, Environment.GetFolderPath);

        Assert.Equal(expected, AppStorageRoot.Default);
        Assert.False(AppStorageRoot.IsPackagedLocation);
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppStorageRoot.FolderName),
            AppStorageRoot.Default);
    }

    /// <summary>
    /// Verifies that <see cref="AppStorageRoot.IsDebugBuild"/> agrees with the build-configuration folder name.
    /// </summary>
    [Fact]
    public void IsDebugBuild_SelectsTheMatchingFolderName()
    {
        Assert.Equal(
            AppStorageRoot.IsDebugBuild ? AppStorageRoot.DebugFolderName : AppStorageRoot.ReleaseFolderName,
            AppStorageRoot.FolderName);
    }

    /// <summary>
    /// Pins the four-way resolver table (identity × build configuration): the Inno channel stays under
    /// <c>%LOCALAPPDATA%</c>, a process with package identity moves the whole root under <c>%USERPROFILE%</c>.
    /// </summary>
    /// <param name="isPackaged">Whether the process has package identity.</param>
    /// <param name="isDebug">Whether this is a Debug build.</param>
    /// <param name="expected">The expected root.</param>
    [Theory]
    [InlineData(false, false, @"L:\DesktopOrganizer")]
    [InlineData(false, true, @"L:\DesktopOrganizer.Dev")]
    [InlineData(true, false, @"U:\DesktopOrganizer")]
    [InlineData(true, true, @"U:\DesktopOrganizer.Dev")]
    public void Resolve_FourVariants_MatchTheTable(bool isPackaged, bool isDebug, string expected)
    {
        Assert.Equal(expected, AppStorageRoot.Resolve(isPackaged, isDebug, FakeFolders));
    }

    /// <summary>
    /// Verifies the documented degradation: with package identity but no resolvable user profile the root
    /// falls back to <c>%LOCALAPPDATA%</c> instead of a root with an empty prefix.
    /// </summary>
    /// <param name="emptyProfile">The empty value <c>UserProfile</c> resolves to.</param>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_PackagedWithEmptyUserProfile_FallsBackToLocalAppData(string emptyProfile)
    {
        string Folders(Environment.SpecialFolder folder)
            => folder == Environment.SpecialFolder.UserProfile ? emptyProfile : FakeFolders(folder);

        Assert.Equal(@"L:\DesktopOrganizer", AppStorageRoot.Resolve(isPackaged: true, isDebug: false, Folders));
        Assert.Equal(@"L:\DesktopOrganizer.Dev", AppStorageRoot.Resolve(isPackaged: true, isDebug: true, Folders));
    }

    /// <summary>
    /// Pins the "does the root really survive an uninstall" flag over the identity × profile matrix: only a
    /// packaged process with a resolvable profile reports <see langword="true"/>; the fallback to
    /// <c>%LOCALAPPDATA%</c> (packaged, empty profile) is virtualised into the package and must report
    /// <see langword="false"/>, so the first-run README never promises persistence it cannot keep.
    /// </summary>
    /// <param name="isPackaged">Whether the process has package identity.</param>
    /// <param name="profile">The value <c>UserProfile</c> resolves to.</param>
    /// <param name="expectedRoot">The expected root.</param>
    /// <param name="expectedUnderUserProfile">Whether the root is expected to live under the user profile.</param>
    [Theory]
    [InlineData(false, @"U:\", @"L:\DesktopOrganizer", false)]
    [InlineData(false, "", @"L:\DesktopOrganizer", false)]
    [InlineData(true, @"U:\", @"U:\DesktopOrganizer", true)]
    [InlineData(true, "", @"L:\DesktopOrganizer", false)]
    [InlineData(true, "   ", @"L:\DesktopOrganizer", false)]
    public void ResolveLocation_ReportsWhetherTheRootIsUnderTheUserProfile(
        bool isPackaged, string profile, string expectedRoot, bool expectedUnderUserProfile)
    {
        string Folders(Environment.SpecialFolder folder)
            => folder == Environment.SpecialFolder.UserProfile ? profile : FakeFolders(folder);

        var (root, underUserProfile) = AppStorageRoot.ResolveLocation(isPackaged, isDebug: false, Folders);

        Assert.Equal(expectedRoot, root);
        Assert.Equal(expectedUnderUserProfile, underUserProfile);
        Assert.Equal(root, AppStorageRoot.Resolve(isPackaged, isDebug: false, Folders));
    }

    /// <summary>
    /// Verifies that a null lookup is rejected by <see cref="AppStorageRoot.ResolveLocation"/> too.
    /// </summary>
    [Fact]
    public void ResolveLocation_NullLookup_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => AppStorageRoot.ResolveLocation(true, false, null!));
    }

    /// <summary>
    /// Verifies that an unpackaged process never looks at <c>UserProfile</c> (so an empty one cannot matter).
    /// </summary>
    [Fact]
    public void Resolve_Unpackaged_DoesNotConsultUserProfile()
    {
        string Folders(Environment.SpecialFolder folder)
            => folder == Environment.SpecialFolder.UserProfile
                ? throw new InvalidOperationException("UserProfile must not be read for the Inno channel.")
                : FakeFolders(folder);

        Assert.Equal(@"L:\DesktopOrganizer", AppStorageRoot.Resolve(isPackaged: false, isDebug: false, Folders));
    }

    /// <summary>
    /// Verifies that a null lookup is rejected.
    /// </summary>
    [Fact]
    public void Resolve_NullLookup_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => AppStorageRoot.Resolve(false, false, null!));
    }

    /// <summary>
    /// Verifies that <see cref="AppStorageRoot.Default"/> is resolved once and is stable across calls.
    /// </summary>
    [Fact]
    public void Default_IsStableAcrossCalls()
    {
        Assert.Same(AppStorageRoot.Default, AppStorageRoot.Default);
        Assert.Equal(AppStorageRoot.Default, AppStorageRoot.Default);
    }

    /// <summary>
    /// Verifies that <see cref="AppStorageRoot.PanelItemsDirectory"/> and <see cref="AppStorageRoot.LogsDirectory"/>
    /// are the <c>PanelItems</c>/<c>Logs</c> children of <see cref="AppStorageRoot.Default"/> — the one place the
    /// two literals now live.
    /// </summary>
    [Fact]
    public void PanelItemsDirectoryAndLogsDirectory_DeriveFromDefault()
    {
        Assert.Equal(Path.Combine(AppStorageRoot.Default, "PanelItems"), AppStorageRoot.PanelItemsDirectory);
        Assert.Equal(Path.Combine(AppStorageRoot.Default, "Logs"), AppStorageRoot.LogsDirectory);
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
        var service = new PanelItemStorageService(Moq.Mock.Of<DesktopOrganizer.Services.Interfaces.INativeApi>());

        Assert.Equal(AppStorageRoot.PanelItemsDirectory, service.StorageRoot);
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

    /// <summary>
    /// Verifies that <see cref="AutostartService.StartupTaskId"/> is the manifest <c>startupTask</c> id the
    /// MSIX packaging reads: a fixed, dot-free value distinct from the HKCU Run value names.
    /// </summary>
    [Fact]
    public void AutostartStartupTaskId_IsTheManifestContractValue()
    {
        Assert.Equal("DesktopOrganizerStartup", AutostartService.StartupTaskId);
        Assert.DoesNotContain('.', AutostartService.StartupTaskId);
    }

    private static string FakeFolders(Environment.SpecialFolder folder) => folder switch
    {
        Environment.SpecialFolder.LocalApplicationData => @"L:\",
        Environment.SpecialFolder.UserProfile => @"U:\",
        _ => throw new InvalidOperationException($"Unexpected folder {folder}."),
    };
}
