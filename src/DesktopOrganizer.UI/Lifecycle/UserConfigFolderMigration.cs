using System.Configuration;
using System.IO;
using DesktopOrganizer.Services.Diagnostics;

namespace DesktopOrganizer.UI.Lifecycle;

/// <summary>
/// One-time startup migration (2026-09-28, explicit user approval — see CLAUDE.md's frozen
/// settings-contour note and <c>DesktopOrganizer.UI.csproj</c>'s <c>&lt;Company&gt;</c> comment) for the
/// per-user <c>Settings.settings</c> <c>user.config</c> folder that moves when
/// <see cref="System.Reflection.AssemblyCompanyAttribute"/> changes from the pinned
/// <c>"DesktopOrganizer.UI"</c> to <c>Directory.Build.props</c>'s solution-wide <c>"khagendev"</c>:
/// System.Configuration keys that folder as
/// <c>%LocalAppData%\&lt;Company&gt;\&lt;exe&gt;_Url_&lt;hash&gt;\&lt;AssemblyVersion&gt;\user.config</c>,
/// and only the <c>&lt;Company&gt;</c> segment changes here — the exe name, evidence hash and pinned
/// <c>AssemblyVersion</c> (<c>Directory.Build.props</c>) are all unchanged.
/// </summary>
/// <remarks>
/// <para>
/// Must run before anything in this process reads <c>Properties.Settings.Default</c> — the earliest
/// reader is <c>Program.TryReadLanguagePreference</c>, called right after the culture bootstrap starts —
/// so <see cref="Run()"/> is invoked at the very top of <c>Program.Main</c>, ahead of the WinForms
/// exception bridge and the culture bootstrap, but after the <c>--restore-panel-items</c> flag has
/// already returned: that headless uninstall path never reads <c>user.config</c> at all, so running this
/// ahead of it would be pure overhead on every uninstall.
/// </para>
/// <para>
/// <b>Algorithm (code-review hardening, 2026-09-28):</b> <see cref="Run()"/> resolves this exe's own
/// evidence folder name (the <c>&lt;exe&gt;_Url_&lt;hash&gt;</c> segment System.Configuration hashes for
/// <i>this one</i> exe location — one such folder per distinct exe location the assembly has ever run
/// from, not always exactly one) via <see cref="TryGetOwnEvidenceFolderName"/>, without touching
/// <c>Properties.Settings.Default</c> or any generated <see cref="ApplicationSettingsBase"/> instance —
/// see that method's remarks. Only that one folder is ever migrated; sibling evidence folders left behind
/// under the old root by some other exe location are never touched. If the resolved path's
/// <c>&lt;Company&gt;</c> segment is not <see cref="CurrentCompanyFolderName"/> (a dev build overriding
/// <c>&lt;Company&gt;</c> locally, or a future rename that needs its own new migration step), migration is
/// skipped entirely.
/// </para>
/// <para>
/// The only "done" signal is <see cref="MarkerFileName"/> under the new root — checked first, before any
/// other I/O — so a half-completed copy is never mistaken for a finished migration. When there is work to
/// do, the source tree is copied into a staging folder whose name does not match a real evidence folder
/// (<see cref="StagingFolderPrefix"/> + the evidence folder name), verifying each file's length and
/// deleting a mismatched copy immediately; only once every file has copied and verified cleanly is the
/// staging folder atomically renamed (<see cref="Directory.Move(string, string)"/>) onto the real evidence
/// folder name. If the final folder already exists without a marker (a previous run copied everything but
/// crashed before writing the marker, or before the atomic rename above existed), only the files still
/// missing from it are copied — files already present are trusted as-is rather than re-verified. The
/// marker is written only when the whole operation reports zero failures; otherwise no marker is written,
/// so the next launch retries from wherever it left off. The old tree is never deleted; it is left behind
/// as a de facto backup.
/// </para>
/// <para>
/// <see cref="Run()"/> itself is wrapped in a named <see cref="Mutex"/> (<see cref="MutexName"/>) so two
/// processes starting for the first time at the same moment never race on the same copy — the loser waits
/// up to <see cref="MutexWaitTimeout"/> and, if it still could not acquire the mutex by then, skips its
/// own migration attempt entirely for this launch (no marker is written, so a later launch retries). An
/// <see cref="AbandonedMutexException"/> (a prior holder crashed while holding it) is treated as a normal
/// acquire: the marker-first, staging-folder algorithm above is safe to resume or restart regardless of
/// what state an abandoned holder left behind.
/// </para>
/// <para>
/// Every outcome that involved actual copy work — completed, or incomplete and left for the next launch
/// to retry — is appended to a <see cref="MigrationLogFileName"/> file under the new root
/// (<see cref="AppLog"/> is still <see cref="NullAppLog"/> this early in startup, so nothing written
/// through it is actually persisted yet, but the plain text log file is). The common "nothing to migrate"
/// case (no old evidence folder at all, or already migrated) is a silent no-op that does not create the
/// new root or write anything, so a fresh machine and every ordinary subsequent launch stay exactly that
/// cheap. A one-line summary of a completed-or-incomplete outcome is also queued in
/// <see cref="PendingLogSummary"/> for <c>Program.Main</c> to hand to <see cref="AppLog.Current"/> once it
/// has been initialized, so it also reaches the structured diagnostics log for a normal session.
/// </para>
/// <para>
/// Exception-safe throughout, once past argument validation: a null <c>localAppDataRoot</c> or
/// <c>evidenceFolderName</c> argument to <see cref="Run(string, string)"/> is a programmer error and still throws
/// <see cref="ArgumentNullException"/> immediately, by design — everything else, every per-file/per-
/// directory operation and the mutex wait itself, is individually best-effort and reported to
/// <see cref="AppLog.Current"/>/the migration log, so one unreadable/locked file or a contested mutex never
/// aborts the rest of the copy or startup itself. A user who hits a failure here still starts with default
/// settings rather than a crashed process.
/// </para>
/// </remarks>
internal static class UserConfigFolderMigration
{
    /// <summary>The pinned <c>&lt;Company&gt;</c> this exe's <c>user.config</c> used to live under.</summary>
    internal const string LegacyCompanyFolderName = "DesktopOrganizer.UI";

    /// <summary>
    /// The <c>&lt;Company&gt;</c> this exe's <c>user.config</c> lives under from now on (
    /// <c>Directory.Build.props</c>'s <c>&lt;Company&gt;</c> at the time of this migration). Deliberately
    /// a separate literal, not a read of <c>Directory.Build.props</c> or the running assembly's own
    /// <see cref="System.Reflection.AssemblyCompanyAttribute"/>: this migration is tied to this one
    /// historical rename event, not to whatever the solution-wide company happens to say in the future —
    /// a later rename needs its own new migration step, not a change to this constant.
    /// </summary>
    internal const string CurrentCompanyFolderName = "khagendev";

    /// <summary>Diagnostics marker file name written directly under the new root once migration completes.</summary>
    internal const string MarkerFileName = "migrated-from-DesktopOrganizer.UI.txt";

    /// <summary>Plain-text outcome log appended under the new root whenever <see cref="Run()"/>/<see cref="Run(string, string)"/> actually has copy work to report (see the type-level remarks).</summary>
    internal const string MigrationLogFileName = "migration.log";

    /// <summary>
    /// Folder name prefix for the temporary staging copy — deliberately not shaped like a real
    /// <c>&lt;exe&gt;_Url_&lt;hash&gt;</c> evidence folder, so an interrupted staging copy is never mistaken
    /// for one and is easy to spot as leftover junk.
    /// </summary>
    private const string StagingFolderPrefix = ".migrating-";

    /// <summary>
    /// Name of the machine-local (session-scoped, <c>Local\</c> namespace — this migration only ever needs
    /// to serialize processes running in the same interactive session against the same per-user
    /// <c>%LocalAppData%</c>) named mutex guarding the copy below against two processes starting for the
    /// first time at the same moment (M2, code-review hardening).
    /// </summary>
    private const string MutexName = @"Local\DesktopOrganizer.UserConfigFolderMigration";

    private const string LogSource = "UserConfigFolderMigration";

    /// <summary>Maximum time <see cref="Run(string, string)"/> waits to acquire <see cref="MutexName"/> before giving up on this launch.</summary>
    private static readonly TimeSpan MutexWaitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// One-line, English, non-localized summary of the most recent <see cref="Run()"/> call's outcome —
    /// queued for <c>Program.Main</c> to hand to <see cref="AppLog.Current"/> right after it initializes
    /// the real log, since <see cref="AppLog.Current"/> is still <see cref="NullAppLog"/> while this class
    /// runs. <see langword="null"/> when there was nothing worth reporting there (already migrated in an
    /// earlier launch, or there was no old install to migrate from at all) — the full detail always still
    /// goes to <see cref="MigrationLogFileName"/> regardless.
    /// </summary>
    internal static string? PendingLogSummary { get; private set; }

    /// <summary>
    /// Runs the migration against the real <c>%LocalAppData%</c> and this exe's own resolved evidence
    /// folder. See the type-level remarks for the full algorithm; never throws.
    /// </summary>
    internal static void Run()
    {
        var localAppDataRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var newRoot = Path.Combine(localAppDataRoot, CurrentCompanyFolderName);

        // Fast path every launch after the first successfully-completed migration takes: skips
        // resolving the evidence folder name (which calls into System.Configuration) and creating the
        // mutex entirely. RunCore below re-checks this same marker as the authoritative gate once the
        // mutex is held, so this early check only needs to be an optimization, not a race-free guard.
        if (File.Exists(Path.Combine(newRoot, MarkerFileName)))
        {
            PendingLogSummary = null;
            return;
        }

        var evidenceFolderName = TryGetOwnEvidenceFolderName();
        if (evidenceFolderName is null)
        {
            // Not running under CurrentCompanyFolderName (a dev build overriding <Company> locally), or
            // System.Configuration itself could not resolve a path — either way there is no known folder
            // to migrate.
            PendingLogSummary = null;
            return;
        }

        Run(localAppDataRoot, evidenceFolderName);
    }

    /// <summary>
    /// Runs the migration against <paramref name="localAppDataRoot"/> and <paramref name="evidenceFolderName"/>
    /// instead of the real environment and this exe's resolved evidence folder — the entry point tests use,
    /// with a disposable temp directory standing in for <c>%LocalAppData%</c> and a literal evidence folder
    /// name standing in for <see cref="TryGetOwnEvidenceFolderName"/>'s result. Guarded by the same named
    /// mutex (<see cref="MutexName"/>) <see cref="Run()"/> uses in production (M2) — two concurrent calls
    /// against the same root are serialized, never interleaved. Never throws once past argument
    /// validation: a null argument is a programmer error and still throws <see cref="ArgumentNullException"/>
    /// immediately; every I/O failure below that point, including the mutex wait itself timing out, is
    /// best-effort and reported to <see cref="AppLog.Current"/>/<see cref="MigrationLogFileName"/> instead.
    /// </summary>
    /// <param name="localAppDataRoot">The per-user app-data root both the old and new company folders live directly under.</param>
    /// <param name="evidenceFolderName">The single <c>&lt;exe&gt;_Url_&lt;hash&gt;</c> evidence folder to migrate.</param>
    internal static void Run(string localAppDataRoot, string evidenceFolderName)
    {
        ArgumentNullException.ThrowIfNull(localAppDataRoot);
        ArgumentNullException.ThrowIfNull(evidenceFolderName);

        using var mutex = new Mutex(initiallyOwned: false, MutexName, out _);
        bool acquired;
        try
        {
            acquired = mutex.WaitOne(MutexWaitTimeout);
        }
        catch (AbandonedMutexException)
        {
            // A prior holder crashed/exited without releasing — the mutex is nonetheless now ours. The
            // marker-first, staging-folder algorithm below is safe to resume or restart regardless of
            // what state an abandoned holder left behind (at worst: a stray staging folder, cleaned up
            // by CopyViaStagingFolder itself before it starts copying).
            acquired = true;
        }

        if (!acquired)
        {
            // Another process is migrating right now — skip this launch entirely; no marker is written,
            // so the next launch retries.
            PendingLogSummary = null;
            return;
        }

        try
        {
            RunCore(localAppDataRoot, evidenceFolderName);
        }
        catch (Exception ex)
        {
            AppLog.Current.Error(
                LogSource,
                "MigrationFailedUnexpectedly",
                ex,
                new KeyValuePair<string, object?>("localAppDataRoot", localAppDataRoot),
                new KeyValuePair<string, object?>("evidenceFolderName", evidenceFolderName));
            TryAppendMigrationLog(
                Path.Combine(localAppDataRoot, CurrentCompanyFolderName),
                $"FAILED unexpectedly: {ex.GetType().Name}: {ex.Message}");
            PendingLogSummary = "User config migration failed unexpectedly — will retry next launch.";
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    /// <summary>
    /// Resolves this exe's own <c>&lt;exe&gt;_Url_&lt;hash&gt;</c> evidence folder name without ever
    /// touching <c>Properties.Settings.Default</c> or constructing any generated
    /// <see cref="ApplicationSettingsBase"/> instance: <see cref="ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel)"/>
    /// with <see cref="ConfigurationUserLevel.PerUserRoamingAndLocal"/> is the same lower-level API
    /// <c>LocalFileSettingsProvider</c> itself calls internally to locate the local user config file — it
    /// only computes where that file would live from the current process's assembly identity and evidence
    /// (a hash of the exe's own codebase location), it does not read, parse, or instantiate anything from
    /// the file's contents, so nothing here can trigger loading <c>Properties.Settings.Default</c> or any
    /// other <see cref="ApplicationSettingsBase"/>-derived settings class.
    /// </summary>
    /// <returns>
    /// The evidence folder name (e.g. <c>DesktopOrganizer.UI_Url_abc123def456</c>), or <see langword="null"/>
    /// if the resolved path's <c>&lt;Company&gt;</c> segment is not <see cref="CurrentCompanyFolderName"/>
    /// (a dev build overriding <c>&lt;Company&gt;</c> locally, or a future rename needing its own new
    /// migration step), or if resolution itself failed for any reason.
    /// </returns>
    internal static string? TryGetOwnEvidenceFolderName()
    {
        try
        {
            var configuration = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.PerUserRoamingAndLocal);
            var userConfigPath = configuration.FilePath;
            if (string.IsNullOrEmpty(userConfigPath)) return null;

            // userConfigPath shape: <root>\<Company>\<exe>_Url_<hash>\<AssemblyVersion>\user.config
            var versionFolder = Path.GetDirectoryName(userConfigPath);
            var evidenceFolder = versionFolder is null ? null : Path.GetDirectoryName(versionFolder);
            if (string.IsNullOrEmpty(evidenceFolder)) return null;

            var companyFolder = Path.GetDirectoryName(evidenceFolder);
            var companyFolderName = companyFolder is null ? null : Path.GetFileName(companyFolder);
            if (!string.Equals(companyFolderName, CurrentCompanyFolderName, StringComparison.Ordinal))
                return null;

            return Path.GetFileName(evidenceFolder);
        }
        catch (Exception)
        {
            // Best-effort only — System.Configuration is not expected to throw here, but this method must
            // never propagate a failure back into Run(): "could not resolve" and "nothing to migrate" are
            // handled identically by the caller.
            return null;
        }
    }

    private static void RunCore(string localAppDataRoot, string evidenceFolderName)
    {
        var oldRoot = Path.Combine(localAppDataRoot, LegacyCompanyFolderName);
        var newRoot = Path.Combine(localAppDataRoot, CurrentCompanyFolderName);
        var markerPath = Path.Combine(newRoot, MarkerFileName);

        // The only "done" signal — checked first, before any other I/O (M1).
        if (File.Exists(markerPath))
        {
            PendingLogSummary = null;
            return;
        }

        var sourceFolder = Path.Combine(oldRoot, evidenceFolderName);
        if (!Directory.Exists(sourceFolder))
        {
            // Fresh machine, or the old install never wrote settings for this exact exe location — a
            // complete no-op that must not even create the new root (skipped logging here on purpose:
            // this is the common case on every fresh install and would otherwise write a migration.log
            // and create %LocalAppData%\khagendev\ on every single launch that never had anything to
            // migrate in the first place).
            PendingLogSummary = null;
            return;
        }

        Directory.CreateDirectory(newRoot);
        var finalFolder = Path.Combine(newRoot, evidenceFolderName);

        var outcome = Directory.Exists(finalFolder)
            ? CopyMissingFilesBestEffort(sourceFolder, finalFolder)
            : CopyViaStagingFolder(sourceFolder, newRoot, evidenceFolderName, finalFolder);

        if (outcome.FailedCount == 0)
        {
            WriteMarker(newRoot, oldRoot);
            TryAppendMigrationLog(
                newRoot,
                $"DONE: copied {outcome.CopiedCount} file(s), 0 failed. source={sourceFolder} dest={finalFolder}");
            PendingLogSummary =
                $"User config migrated from '{LegacyCompanyFolderName}' to '{CurrentCompanyFolderName}' ({outcome.CopiedCount} file(s)).";
        }
        else
        {
            TryAppendMigrationLog(
                newRoot,
                $"INCOMPLETE: copied {outcome.CopiedCount} file(s), {outcome.FailedCount} failed — no marker written, will retry next launch. source={sourceFolder} dest={finalFolder}");
            PendingLogSummary =
                $"User config migration incomplete: {outcome.CopiedCount} copied, {outcome.FailedCount} failed — will retry next launch.";
        }
    }

    /// <summary>
    /// Fresh-migration path: copies <paramref name="sourceRoot"/> into a staging folder under
    /// <paramref name="newRoot"/> (deleting any stray staging leftover from a previous crashed attempt
    /// first), verifying every file, and — only if every file copied and verified cleanly — atomically
    /// renames the staging folder onto <paramref name="finalFolder"/>. On any failure, the staging folder
    /// is deleted and nothing is ever exposed under <paramref name="finalFolder"/>'s name: the next launch
    /// retries the whole copy from scratch.
    /// </summary>
    private static CopyOutcome CopyViaStagingFolder(string sourceRoot, string newRoot, string evidenceFolderName, string finalFolder)
    {
        var stagingFolder = Path.Combine(newRoot, StagingFolderPrefix + evidenceFolderName);
        TryDeleteDirectoryBestEffort(stagingFolder);

        var outcome = CopyDirectoryBestEffort(sourceRoot, stagingFolder);

        if (outcome.FailedCount == 0)
        {
            try
            {
                Directory.Move(stagingFolder, finalFolder);
                return outcome;
            }
            catch (Exception ex)
            {
                AppLog.Current.Error(
                    LogSource,
                    "StagingMoveFailed",
                    ex,
                    new KeyValuePair<string, object?>("staging", stagingFolder),
                    new KeyValuePair<string, object?>("dest", finalFolder));
                TryDeleteDirectoryBestEffort(stagingFolder);
                return outcome with { FailedCount = outcome.FailedCount + 1 };
            }
        }

        // Incomplete copy — never expose it under the final name.
        TryDeleteDirectoryBestEffort(stagingFolder);
        return outcome;
    }

    /// <summary>
    /// Resume path: <paramref name="destRoot"/> already exists without a marker (a previous run copied
    /// everything but crashed before writing the marker or moving the staging folder). Copies only the
    /// files still missing from it (<c>overwrite: false</c>) — every file already present under
    /// <paramref name="destRoot"/> is trusted as-is rather than re-verified.
    /// </summary>
    private static CopyOutcome CopyMissingFilesBestEffort(string sourceRoot, string destRoot)
    {
        if (!TryEnumerateSourceTree(sourceRoot, out var subdirectories, out var files)) return new CopyOutcome(0, 1);

        CreateSubdirectories(sourceRoot, destRoot, subdirectories);

        var copied = 0;
        var failed = 0;
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(sourceRoot, file);
            var destFile = Path.Combine(destRoot, relative);
            if (File.Exists(destFile)) continue; // already there from an earlier attempt — trusted as-is

            if (CopyFileBestEffort(file, destFile)) copied++;
            else failed++;
        }

        return new CopyOutcome(copied, failed);
    }

    /// <summary>
    /// Recursively copies <paramref name="sourceRoot"/> to <paramref name="destRoot"/> (which must not
    /// already exist as a real folder — the staging/resume callers above decide that), verifying each
    /// copied file's length against its source and deleting a mismatched copy immediately. Best-effort at
    /// every level: a failure enumerating a subdirectory, or copying/verifying one file, is logged and
    /// counted as failed rather than aborting the rest of the tree — every other file this call can still
    /// copy, it does. Reparse points (junctions/symlinks) are skipped entirely rather than followed.
    /// </summary>
    private static CopyOutcome CopyDirectoryBestEffort(string sourceRoot, string destRoot)
    {
        Directory.CreateDirectory(destRoot);

        if (!TryEnumerateSourceTree(sourceRoot, out var subdirectories, out var files)) return new CopyOutcome(0, 1);

        CreateSubdirectories(sourceRoot, destRoot, subdirectories);

        var copied = 0;
        var failed = 0;
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(sourceRoot, file);
            if (CopyFileBestEffort(file, Path.Combine(destRoot, relative))) copied++;
            else failed++;
        }

        return new CopyOutcome(copied, failed);
    }

    /// <summary>
    /// Lists every subdirectory and file under <paramref name="sourceRoot"/>, recursively, excluding
    /// reparse points (junctions/symlinks — never followed). Returns <see langword="false"/> (with both
    /// outputs empty) if enumeration itself failed, e.g. a permissions error on the root.
    /// </summary>
    private static bool TryEnumerateSourceTree(string sourceRoot, out string[] subdirectories, out string[] files)
    {
        try
        {
            subdirectories = FilterOutReparsePoints(Directory.GetDirectories(sourceRoot, "*", SearchOption.AllDirectories));
            files = FilterOutReparsePoints(Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories));
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Current.Warn(
                LogSource,
                "SourceTreeEnumerationFailed",
                new KeyValuePair<string, object?>("source", sourceRoot),
                new KeyValuePair<string, object?>("exception", ex.Message));
            subdirectories = [];
            files = [];
            return false;
        }
    }

    /// <summary>Recreates every subdirectory under <paramref name="destRoot"/>, including empty ones.</summary>
    private static void CreateSubdirectories(string sourceRoot, string destRoot, string[] subdirectories)
    {
        foreach (var directory in subdirectories)
        {
            try
            {
                var relative = Path.GetRelativePath(sourceRoot, directory);
                Directory.CreateDirectory(Path.Combine(destRoot, relative));
            }
            catch (Exception ex)
            {
                AppLog.Current.Warn(
                    LogSource,
                    "SubdirectoryCreateFailed",
                    new KeyValuePair<string, object?>("directory", directory),
                    new KeyValuePair<string, object?>("exception", ex.Message));
            }
        }
    }

    /// <summary>Filters out any path whose <see cref="FileAttributes.ReparsePoint"/> bit is set, or that vanished/became inaccessible mid-check.</summary>
    private static string[] FilterOutReparsePoints(string[] paths)
    {
        var filtered = new List<string>(paths.Length);
        foreach (var path in paths)
        {
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
            }
            catch (Exception)
            {
                continue; // vanished mid-enumeration or inaccessible — best-effort, just skip it
            }

            filtered.Add(path);
        }

        return [.. filtered];
    }

    /// <summary>
    /// Copies a single file and verifies the destination's length matches the source, deleting the
    /// destination immediately if it does not. Never throws: any failure is reported to
    /// <see cref="AppLog.Current"/> and reported as <see langword="false"/> — the source file is never
    /// deleted by this migration regardless of outcome.
    /// </summary>
    private static bool CopyFileBestEffort(string sourceFile, string destFile)
    {
        try
        {
            var destDir = Path.GetDirectoryName(destFile);
            if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);

            File.Copy(sourceFile, destFile, overwrite: false);

            var sourceLength = new FileInfo(sourceFile).Length;
            var destLength = new FileInfo(destFile).Length;
            if (sourceLength != destLength)
            {
                AppLog.Current.Warn(
                    LogSource,
                    "CopyLengthMismatch",
                    new KeyValuePair<string, object?>("source", sourceFile),
                    new KeyValuePair<string, object?>("sourceLength", sourceLength),
                    new KeyValuePair<string, object?>("destLength", destLength));
                TryDeleteFileBestEffort(destFile);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            AppLog.Current.Error(
                LogSource,
                "CopyFileFailed",
                ex,
                new KeyValuePair<string, object?>("source", sourceFile),
                new KeyValuePair<string, object?>("dest", destFile));
            return false;
        }
    }

    private static void TryDeleteFileBestEffort(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception)
        {
            // Best-effort cleanup only — a leftover mismatched file does not fail the migration.
        }
    }

    private static void TryDeleteDirectoryBestEffort(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception)
        {
            // Best-effort cleanup only — a leftover staging directory does not fail the migration.
        }
    }

    /// <summary>
    /// Best-effort diagnostics marker, written once directly under <paramref name="newRoot"/> after a
    /// completed migration, recording where it copied from and when (UTC).
    /// </summary>
    private static void WriteMarker(string newRoot, string oldRoot)
    {
        try
        {
            var markerPath = Path.Combine(newRoot, MarkerFileName);
            var contents =
                $"Migrated from: {oldRoot}{Environment.NewLine}" +
                $"Migrated at (UTC): {DateTimeOffset.UtcNow:O}{Environment.NewLine}";
            File.WriteAllText(markerPath, contents);
        }
        catch (Exception ex)
        {
            AppLog.Current.Error(
                LogSource,
                "MarkerWriteFailed",
                ex,
                new KeyValuePair<string, object?>("newRoot", newRoot));
        }
    }

    /// <summary>
    /// Best-effort append of a single timestamped line to <see cref="MigrationLogFileName"/> under
    /// <paramref name="newRoot"/> — the durable record of every outcome this class reaches, since
    /// <see cref="AppLog.Current"/> is still <see cref="NullAppLog"/> this early in startup.
    /// </summary>
    private static void TryAppendMigrationLog(string newRoot, string line)
    {
        try
        {
            Directory.CreateDirectory(newRoot);
            var path = Path.Combine(newRoot, MigrationLogFileName);
            File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {line}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Best-effort diagnostics only — logging itself must never fail the migration.
        }
    }

    private readonly record struct CopyOutcome(int CopiedCount, int FailedCount);
}
