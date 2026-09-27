using System.IO;
using DesktopOrganizer.UI.Lifecycle;

namespace DesktopOrganizer.Tests.Unit.Lifecycle;

/// <summary>
/// Unit tests for <see cref="UserConfigFolderMigration"/> (2026-09-28, explicit user approval — see
/// CLAUDE.md's frozen settings-contour note; hardened the same day per code review) — the one-time startup
/// migration of the per-user <c>user.config</c> folder from the old pinned <c>&lt;Company&gt;</c>
/// (<c>"DesktopOrganizer.UI"</c>) to the new one (<c>"khagendev"</c>). Every scenario runs against a real
/// disposable temp directory standing in for <c>%LocalAppData%</c>, same convention as
/// <see cref="DesktopOrganizer.Tests.Unit.Tools.PanelItemsUninstallRestorerTests"/>, and calls the pure
/// <see cref="UserConfigFolderMigration.Run(string, string)"/> overload directly — bypassing
/// <see cref="UserConfigFolderMigration.Run()"/>'s own resolution of the real
/// <c>%LocalAppData%</c>/evidence-folder-name pair, but exercising the same named-mutex-guarded core
/// algorithm production uses.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class UserConfigFolderMigrationTests
{
    private const string EvidenceFolderName = "DesktopOrganizer.UI_Url_abc123def456";
    private const string OtherEvidenceFolderName = "DesktopOrganizer.UI_Url_deadbeefcafe";
    private const string VersionFolderName = "1.0.0.0";
    private const string UserConfigFileName = "user.config";
    private const string StagingFolderSearchPattern = ".migrating-*";

    /// <summary>Fresh machine — neither company folder exists — is a complete no-op.</summary>
    [Fact]
    public void Run_FreshMachine_IsNoOp()
    {
        var root = CreateTempRoot();
        try
        {
            UserConfigFolderMigration.Run(root, EvidenceFolderName);

            Assert.False(Directory.Exists(Path.Combine(root, UserConfigFolderMigration.LegacyCompanyFolderName)));
            Assert.False(Directory.Exists(Path.Combine(root, UserConfigFolderMigration.CurrentCompanyFolderName)));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    /// <summary>
    /// The old company folder has a real evidence/version/user.config tree and the new one does not
    /// exist yet: the entire tree is copied into the new company folder (via the staging folder, which is
    /// never left behind on success), the old tree is left untouched, and a diagnostics marker is written.
    /// </summary>
    [Fact]
    public void Run_OldExistsNewMissing_CopiesTree_KeepsOld_WritesMarker_NoStagingLeftover()
    {
        var root = CreateTempRoot();
        try
        {
            const string content = "<configuration><userSettings/></configuration>";
            var oldUserConfig = CreateLegacyUserConfig(root, EvidenceFolderName, content);

            UserConfigFolderMigration.Run(root, EvidenceFolderName);

            var newUserConfig = Path.Combine(
                root,
                UserConfigFolderMigration.CurrentCompanyFolderName,
                EvidenceFolderName,
                VersionFolderName,
                UserConfigFileName);

            Assert.True(File.Exists(newUserConfig), "The migrated user.config must exist under the new company folder.");
            Assert.Equal(content, File.ReadAllText(newUserConfig));

            Assert.True(File.Exists(oldUserConfig), "The old user.config must never be deleted.");
            Assert.Equal(content, File.ReadAllText(oldUserConfig));

            var newRoot = Path.Combine(root, UserConfigFolderMigration.CurrentCompanyFolderName);
            var markerPath = Path.Combine(newRoot, UserConfigFolderMigration.MarkerFileName);
            Assert.True(File.Exists(markerPath), "A diagnostics marker file must be written after a completed migration.");
            var markerContents = File.ReadAllText(markerPath);
            Assert.Contains(Path.Combine(root, UserConfigFolderMigration.LegacyCompanyFolderName), markerContents, StringComparison.Ordinal);

            Assert.Empty(Directory.GetDirectories(newRoot, StagingFolderSearchPattern, SearchOption.TopDirectoryOnly));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    /// <summary>
    /// The marker already exists under the new root — the only "done" signal (M1) — so the run is a
    /// complete no-op regardless of what the new evidence folder actually contains.
    /// </summary>
    [Fact]
    public void Run_MarkerExists_IsNoOp_NeverTouchesNewFolder()
    {
        var root = CreateTempRoot();
        try
        {
            CreateLegacyUserConfig(root, EvidenceFolderName, "<old/>");

            var newEvidenceDir = Path.Combine(root, UserConfigFolderMigration.CurrentCompanyFolderName, EvidenceFolderName, VersionFolderName);
            Directory.CreateDirectory(newEvidenceDir);
            var newUserConfig = Path.Combine(newEvidenceDir, UserConfigFileName);
            File.WriteAllText(newUserConfig, "<already-migrated/>");

            var markerPath = Path.Combine(root, UserConfigFolderMigration.CurrentCompanyFolderName, UserConfigFolderMigration.MarkerFileName);
            File.WriteAllText(markerPath, "already migrated");
            var markerWriteUtc = File.GetLastWriteTimeUtc(markerPath);

            UserConfigFolderMigration.Run(root, EvidenceFolderName);

            Assert.Equal("<already-migrated/>", File.ReadAllText(newUserConfig));
            Assert.Equal(markerWriteUtc, File.GetLastWriteTimeUtc(markerPath));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    /// <summary>
    /// M1: the new evidence folder already exists (a previous run copied everything but crashed before
    /// writing the marker) and has no marker — this is the resume path: only the file still missing from
    /// it is copied; the file already present is trusted as-is and left untouched.
    /// </summary>
    [Fact]
    public void Run_PartialDestinationWithoutMarker_ResumesAndCompletes()
    {
        var root = CreateTempRoot();
        try
        {
            var oldEvidenceDir = Path.Combine(root, UserConfigFolderMigration.LegacyCompanyFolderName, EvidenceFolderName, VersionFolderName);
            Directory.CreateDirectory(oldEvidenceDir);
            File.WriteAllText(Path.Combine(oldEvidenceDir, UserConfigFileName), "<content/>");
            File.WriteAllText(Path.Combine(oldEvidenceDir, "other.txt"), "fine");

            var newEvidenceVersionDir = Path.Combine(root, UserConfigFolderMigration.CurrentCompanyFolderName, EvidenceFolderName, VersionFolderName);
            Directory.CreateDirectory(newEvidenceVersionDir);
            var alreadyCopiedFile = Path.Combine(newEvidenceVersionDir, UserConfigFileName);
            File.WriteAllText(alreadyCopiedFile, "<content/>");
            var alreadyCopiedWriteUtc = File.GetLastWriteTimeUtc(alreadyCopiedFile);

            UserConfigFolderMigration.Run(root, EvidenceFolderName);

            var markerPath = Path.Combine(root, UserConfigFolderMigration.CurrentCompanyFolderName, UserConfigFolderMigration.MarkerFileName);
            Assert.True(File.Exists(markerPath), "A completed resume must write the marker.");

            Assert.Equal(alreadyCopiedWriteUtc, File.GetLastWriteTimeUtc(alreadyCopiedFile));
            Assert.Equal("<content/>", File.ReadAllText(alreadyCopiedFile));

            var completedOtherFile = Path.Combine(newEvidenceVersionDir, "other.txt");
            Assert.True(File.Exists(completedOtherFile), "The file missing from the partial destination must be copied to complete the resume.");
            Assert.Equal("fine", File.ReadAllText(completedOtherFile));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    /// <summary>
    /// A single file that cannot be copied (locked by another handle, simulating a sharing violation)
    /// never leaves a partially-migrated evidence folder behind and never writes a marker — the staging
    /// copy is discarded entirely. A second run, after the lock is released, restarts the copy from
    /// scratch and completes it, writing the marker.
    /// </summary>
    [Fact]
    public void Run_OneFileCopyFails_NoMarker_NoPartialFolder_SecondRunAfterUnlockCompletes()
    {
        var root = CreateTempRoot();
        try
        {
            var oldEvidenceDir = Path.Combine(root, UserConfigFolderMigration.LegacyCompanyFolderName, EvidenceFolderName, VersionFolderName);
            Directory.CreateDirectory(oldEvidenceDir);

            var lockedFile = Path.Combine(oldEvidenceDir, UserConfigFileName);
            File.WriteAllText(lockedFile, "<locked/>");

            var okFile = Path.Combine(oldEvidenceDir, "other.txt");
            File.WriteAllText(okFile, "fine");

            var newRoot = Path.Combine(root, UserConfigFolderMigration.CurrentCompanyFolderName);
            var markerPath = Path.Combine(newRoot, UserConfigFolderMigration.MarkerFileName);
            var finalEvidenceDir = Path.Combine(newRoot, EvidenceFolderName);

            using (var handle = new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var exception = Record.Exception(() => UserConfigFolderMigration.Run(root, EvidenceFolderName));
                Assert.Null(exception);
                GC.KeepAlive(handle);
            }

            Assert.False(File.Exists(markerPath), "A run with a copy failure must not write a marker.");
            Assert.False(Directory.Exists(finalEvidenceDir), "An incomplete copy must never be exposed under the final evidence folder name.");
            Assert.False(
                Directory.Exists(newRoot) && Directory.GetDirectories(newRoot, StagingFolderSearchPattern, SearchOption.TopDirectoryOnly).Length > 0,
                "The failed attempt's staging folder must be cleaned up, not left behind.");

            UserConfigFolderMigration.Run(root, EvidenceFolderName);

            Assert.True(File.Exists(markerPath), "The retry, once the lock is released, must complete and write the marker.");
            var newVersionDir = Path.Combine(finalEvidenceDir, VersionFolderName);
            Assert.Equal("<locked/>", File.ReadAllText(Path.Combine(newVersionDir, UserConfigFileName)));
            Assert.Equal("fine", File.ReadAllText(Path.Combine(newVersionDir, "other.txt")));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    /// <summary>Running the migration twice in a row is idempotent: the second run is a clean no-op.</summary>
    [Fact]
    public void Run_Twice_SecondRunIsNoOp()
    {
        var root = CreateTempRoot();
        try
        {
            CreateLegacyUserConfig(root, EvidenceFolderName, "<content/>");

            UserConfigFolderMigration.Run(root, EvidenceFolderName);

            var markerPath = Path.Combine(root, UserConfigFolderMigration.CurrentCompanyFolderName, UserConfigFolderMigration.MarkerFileName);
            var firstMarkerWriteUtc = File.GetLastWriteTimeUtc(markerPath);

            var exception = Record.Exception(() => UserConfigFolderMigration.Run(root, EvidenceFolderName));

            Assert.Null(exception);
            Assert.Equal(firstMarkerWriteUtc, File.GetLastWriteTimeUtc(markerPath));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    /// <summary>Unrelated content already sitting directly under the new root is left alone; migration still proceeds.</summary>
    [Fact]
    public void Run_NewRootHasUnrelatedContent_StillMigrates()
    {
        var root = CreateTempRoot();
        try
        {
            const string content = "<content/>";
            CreateLegacyUserConfig(root, EvidenceFolderName, content);

            var newRoot = Path.Combine(root, UserConfigFolderMigration.CurrentCompanyFolderName);
            Directory.CreateDirectory(newRoot);
            var unrelatedFile = Path.Combine(newRoot, "some-other-file.txt");
            File.WriteAllText(unrelatedFile, "unrelated");

            UserConfigFolderMigration.Run(root, EvidenceFolderName);

            var newUserConfig = Path.Combine(newRoot, EvidenceFolderName, VersionFolderName, UserConfigFileName);
            Assert.True(File.Exists(newUserConfig));
            Assert.Equal(content, File.ReadAllText(newUserConfig));
            Assert.True(File.Exists(unrelatedFile), "Unrelated content already under the new root must be left alone.");
            Assert.Equal("unrelated", File.ReadAllText(unrelatedFile));
            Assert.True(File.Exists(Path.Combine(newRoot, UserConfigFolderMigration.MarkerFileName)));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    /// <summary>
    /// M2: two processes starting for the first time at the same moment never race — both calls target
    /// the same temp root concurrently; the named mutex serializes them, so the destination is never torn
    /// and exactly one of them does the actual copy work (the other finds the marker already written).
    /// </summary>
    [Fact]
    public async Task Run_TwoParallelCalls_SameRoot_FinalContentsIntact_OneMarker_NoStagingLeftover()
    {
        var root = CreateTempRoot();
        try
        {
            const string content = "<content/>";
            var oldUserConfig = CreateLegacyUserConfig(root, EvidenceFolderName, content);

            var first = Task.Run(() => UserConfigFolderMigration.Run(root, EvidenceFolderName));
            var second = Task.Run(() => UserConfigFolderMigration.Run(root, EvidenceFolderName));
            await Task.WhenAll(first, second);

            var newRoot = Path.Combine(root, UserConfigFolderMigration.CurrentCompanyFolderName);
            var newUserConfig = Path.Combine(newRoot, EvidenceFolderName, VersionFolderName, UserConfigFileName);
            Assert.True(File.Exists(newUserConfig));
            Assert.Equal(content, File.ReadAllText(newUserConfig));
            Assert.Equal(content, File.ReadAllText(oldUserConfig));

            Assert.True(File.Exists(Path.Combine(newRoot, UserConfigFolderMigration.MarkerFileName)));
            Assert.Empty(Directory.GetDirectories(newRoot, StagingFolderSearchPattern, SearchOption.TopDirectoryOnly));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    /// <summary>m1: only this exe's own evidence folder is ever migrated — a sibling folder under the old root (some other exe location) is never copied.</summary>
    [Fact]
    public void Run_OwnFolderOnly_OtherEvidenceFoldersUnderOldRootAreNotCopied()
    {
        var root = CreateTempRoot();
        try
        {
            CreateLegacyUserConfig(root, EvidenceFolderName, "<mine/>");
            CreateLegacyUserConfig(root, OtherEvidenceFolderName, "<not-mine/>");

            UserConfigFolderMigration.Run(root, EvidenceFolderName);

            var newRoot = Path.Combine(root, UserConfigFolderMigration.CurrentCompanyFolderName);
            Assert.True(Directory.Exists(Path.Combine(newRoot, EvidenceFolderName)));
            Assert.False(
                Directory.Exists(Path.Combine(newRoot, OtherEvidenceFolderName)),
                "Only this exe's own evidence folder must ever be migrated.");
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    /// <summary>
    /// m1 reasoning check: <see cref="UserConfigFolderMigration.TryGetOwnEvidenceFolderName"/> is safe to
    /// call from any process — it never throws regardless of what the current process's own
    /// <c>%LocalAppData%</c> evidence folder actually looks like (this test process is the xunit test
    /// host, not <c>DesktopOrganizer.UI.exe</c>, so the resolved company segment will not match
    /// <see cref="UserConfigFolderMigration.CurrentCompanyFolderName"/> and the method is expected to
    /// return <see langword="null"/> — the behavior this pins is "never throws", not a specific result).
    /// </summary>
    [Fact]
    public void TryGetOwnEvidenceFolderName_DoesNotThrow()
    {
        var exception = Record.Exception(() => UserConfigFolderMigration.TryGetOwnEvidenceFolderName());

        Assert.Null(exception);
    }

    private static string CreateTempRoot() => Path.Combine(Path.GetTempPath(), $"DOrgUserConfigMigration_{Guid.NewGuid():N}");

    /// <summary>Creates <c>{root}/DesktopOrganizer.UI/{evidenceFolderName}/{VersionFolderName}/user.config</c> with <paramref name="content"/>, returning its full path.</summary>
    private static string CreateLegacyUserConfig(string root, string evidenceFolderName, string content)
    {
        var evidenceDir = Path.Combine(root, UserConfigFolderMigration.LegacyCompanyFolderName, evidenceFolderName, VersionFolderName);
        Directory.CreateDirectory(evidenceDir);
        var userConfig = Path.Combine(evidenceDir, UserConfigFileName);
        File.WriteAllText(userConfig, content);
        return userConfig;
    }

    private static void SafeDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception)
        {
            // Best-effort cleanup only — a leftover temp directory does not fail the test.
        }
    }
}
