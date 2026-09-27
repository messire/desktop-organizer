using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using DesktopOrganizer.Localization;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Abstractions.Storage;
using DesktopOrganizer.Services.Diagnostics;
using DesktopOrganizer.Services.Storage;
using DesktopOrganizer.UI.Configuration;
using DesktopOrganizer.UI.Lifecycle;
using DesktopOrganizer.UI.State;
using Microsoft.Extensions.DependencyInjection;

namespace DesktopOrganizer.UI;

/// <summary>
/// Process entry point (ADR-023 D-2). Runs the single-instance handshake — guard mutex + machine-global
/// named-pipe activation channel — before constructing the WPF <see cref="Application"/> at all, so a
/// process that turns out to be Secondary never runs <see cref="App.OnExit"/> and can never overwrite
/// the real Primary's persisted panel state with an empty/unloaded model (ADR-023 R-1, phase-3 plan
/// §0.1). This replaces the WPF SDK's own auto-generated <c>Main()</c> — see the
/// <c>&lt;StartupObject&gt;</c>/<c>&lt;ApplicationDefinition Remove="App.xaml"&gt;</c> pair in
/// <c>DesktopOrganizer.UI.csproj</c>.
/// </summary>
public static class Program
{
    private const string AppTitle = "DesktopOrganizer";

    // ADR-024 §7 rollback scenario C: a manual recovery switch, handled entirely before the single-
    // instance handshake / normal ApplicationStateService.Load() path — see RunRestoreStateBackup.
    // Not a localizable string — a command-line flag, always ASCII/English.
    private const string RestoreStateBackupFlag = "--restore-state-backup";

    // A second, unrelated one-shot recovery switch — see RunRestorePanelItems remarks. Wired into
    // installer/DesktopOrganizer.iss's [UninstallRun], run by the installer itself (headless, already
    // past InitializeUninstall's EnsureAppClosed) rather than typed by a user at a terminal, but the
    // flag is still plain ASCII/English like every other command-line switch this process reads.
    private const string RestorePanelItemsFlag = "--restore-panel-items";

    // English, printed to Console.Error only (never shown in a message box) so scripts/developers
    // reading the terminal always see the same text regardless of UI language (docs/PLANS/
    // localization.md §2.8/§0.2 — diagnostics and console output stay English). The message box shown
    // for the same failure uses the localized "Restore_LockUnavailable_Message" RESX key instead — see
    // RunRestoreStateBackup.
    private const string RestoreLockUnavailableMessage =
        "DesktopOrganizer could not restore application state: another running instance holds the storage lock. Close DesktopOrganizer and try again.";

    // Console-only (see RestoreLockUnavailableMessage remarks on why) — but unlike that switch,
    // RunRestorePanelItems never shows a message box for ANY outcome (see its own remarks: it runs
    // headless from the uninstaller, where nobody is present to dismiss one).
    private const string RestorePanelItemsLockUnavailableMessage =
        "DesktopOrganizer could not restore panel items: another running instance holds the storage lock. Nothing was moved.";

    // M1 (release-polish 1.0 code review): the exact string PanelItemsUninstallRestorer.RestoreAll()
    // itself would report for "no managed PanelItems storage folder found" — duplicated here so
    // RunRestorePanelItems can report the same outcome without constructing DI/AppLog/the storage lock
    // at all (see MINOR 3 remarks on RunRestorePanelItems).
    private const string RestorePanelItemsNothingToRestoreMessage =
        "No managed PanelItems storage folder found — nothing to restore.";

    // M1: printed (and best-effort logged) when RunRestorePanelItems itself throws — see its own
    // remarks on why this path must never let an exception reach Main's outer try/catch (which shows a
    // MessageBox that would hang a headless uninstall).
    private const string RestorePanelItemsUnexpectedFailureMessage =
        "DesktopOrganizer could not restore panel items: an unexpected error occurred. Nothing further was moved.";

    // ADR-024 D-8: printed once, to Console.Error only (no message box — a normal launch must not
    // gain a new modal dialog over a degraded-but-working session, see D-8's own rationale), when the
    // normal startup path's storage-lock acquire fails. Not localized — same rationale as
    // RestoreLockUnavailableMessage's console line above.
    private const string StorageLockUnavailableMessage =
        "DesktopOrganizer: could not acquire the application-state storage lock — running with saving disabled for this session.";

    // Mirrors StorageFileLock()'s production default (maxAttempts: 3) purely for the diagnostics
    // payload below; TryAcquire() itself does not report how many attempts it actually made.
    private const int StorageLockDefaultMaxAttempts = 3;

    // --restore-panel-items exit codes — distinct from --restore-state-backup's (0/2) and from the
    // single-instance handshake's (0/1/3/4), documented alongside them on Main's own remarks below.
    private const int RestorePanelItemsLockUnavailableExitCode = 6;
    private const int RestorePanelItemsPartialFailureExitCode = 7;

    // M1: a new, documented exit code — see Main's remarks — for an unexpected exception inside
    // RunRestorePanelItems that its own try/catch had to swallow instead of letting it reach Main's
    // outer catch (whose MessageBox would hang a headless `runhidden waituntilterminated` uninstall).
    private const int RestorePanelItemsUnexpectedFailureExitCode = 8;

    /// <summary>
    /// Entry point. Exit codes (consumed by <c>tools/lifecycle/Invoke-SingleInstanceStress.ps1</c>,
    /// ADR-023 §6.2): <c>0</c> — this process ran as Primary and exited normally, a Secondary process
    /// successfully activated the existing Primary's window, or <c>--restore-state-backup</c> completed
    /// successfully; <c>3</c> — the single-instance handshake failed (ADR-023 D-7: an unresponsive
    /// Primary, or activation could not be delivered — no writable second instance is ever started);
    /// <c>4</c> — a Primary exists, but in a different Windows session (ADR-023 D-4, Fast User
    /// Switching); <c>1</c> — an unhandled exception was thrown before/during the handshake itself
    /// (M-4), e.g. the guard mutex could not be opened because another principal already owns an object
    /// of that name (<see cref="UnauthorizedAccessException"/>/
    /// <see cref="System.Threading.WaitHandleCannotBeOpenedException"/>); <c>2</c> —
    /// <c>--restore-state-backup</c> was requested but did not restore anything (no candidate found, the
    /// candidate failed validation, the storage lock could not be acquired, or the write itself failed —
    /// see <see cref="RunRestoreStateBackup"/>); <c>5</c> — a fatal unhandled exception was caught by
    /// <see cref="DesktopOrganizer.UI.Lifecycle.CrashCoordinator"/> on the UI thread and the crash
    /// sequence ran to completion (ADR-033 part 2, <see cref="DesktopOrganizer.UI.Lifecycle.ExitCodes.Crashed"/>);
    /// a fatal exception on a genuine background thread instead ends the process via the CLR's own
    /// unmanaged-exception termination, with no code this method controls; <c>6</c> —
    /// <c>--restore-panel-items</c> was requested but the storage lock could not be acquired (another
    /// running instance holds it) — nothing was moved; <c>7</c> — <c>--restore-panel-items</c> ran but
    /// one or more items could not be moved and remain in managed storage (see
    /// <see cref="RunRestorePanelItems"/>) — every item it could move was still moved; <c>8</c> —
    /// <c>--restore-panel-items</c> threw an unexpected exception, caught entirely inside
    /// <see cref="RunRestorePanelItems"/>'s own try/catch (M1, release-polish 1.0 code review) so it
    /// never reaches this method's own message-box-showing catch below, which would otherwise hang a
    /// headless <c>runhidden waituntilterminated</c> uninstall with nobody present to dismiss it.
    /// </summary>
    /// <param name="args">
    /// Command-line arguments. Unused by protocol <c>DO/1</c> itself (ADR-023 D-5) — the two flags this
    /// process reads directly are <c>--restore-state-backup</c> (ADR-024 §7 scenario C) and
    /// <c>--restore-panel-items</c> (see <see cref="RunRestorePanelItems"/>), both checked before the
    /// single-instance handshake ever starts.
    /// </param>
    [STAThread]
    public static int Main(string[] args)
    {
        // ADR-033 part 2 (crash safety, release-hardening plan §2): installed as early as possible —
        // before the single-instance handshake, before the DI container, before any WPF Application
        // exists. CrashRouting.Handler is still null at this point (App's constructor sets it); every
        // one of these three delegates tolerates that (see CrashRouting's own remarks) and simply drops
        // a crash that manages to happen in the narrow window before App is constructed, the same way
        // the outer try/catch below already covers that window with its own message box.
        //
        // TaskScheduler.UnobservedTaskException is deliberately NOT routed through CrashCoordinator —
        // it is by design non-fatal (.NET's own default behavior: the process survives regardless of
        // this handler), so this only records it and marks it observed.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                CrashRouting.Handler?.Invoke(ex, CrashOrigin.Background);
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Current.Error("Program", "UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        // M-4: everything below — the WinForms exception bridge, DI container construction, resolving
        // ISingleInstanceCoordinator (whose factory constructs a real named Mutex and reads
        // WindowsIdentity.GetCurrent()/Process.GetCurrentProcess()), and the handshake itself — runs
        // before any WPF Application or diagnostics log exists. Unlike the WPF SDK's own auto-generated
        // Main, nothing upstream of this method catches an exception thrown here; without this guard the
        // process would simply vanish with no message and a CLR-default exit code.
        try
        {
            // M1 (release-polish 1.0 code review): dispatched before anything else in this method —
            // ahead of the WinForms exception bridge and the culture/user.config bootstrap below, not
            // just ahead of the single-instance handshake. This flag is only ever set by the installer's
            // headless, English-only [UninstallRun] entry (see RunRestorePanelItems remarks); it needs
            // none of the WinForms bridge, WPF culture, or user.config reads that follow, and
            // RunRestorePanelItems's own try/catch guarantees this never throws back into this method's
            // message-box-showing catch below (which would hang that headless uninstall).
            if (args.Any(a => string.Equals(a, RestorePanelItemsFlag, StringComparison.OrdinalIgnoreCase)))
                return RunRestorePanelItems();

            // 2026-09-28 (explicit user approval, see CLAUDE.md's frozen settings-contour note and
            // DesktopOrganizer.UI.csproj's <Company> comment): one-time, idempotent, exception-safe
            // migration of the per-user user.config folder from the old pinned Company
            // ("DesktopOrganizer.UI") to the new one ("khagendev"). Must run before the very first read
            // of Properties.Settings.Default anywhere below — TryReadLanguagePreference() a few lines
            // down is the earliest one — but after the RestorePanelItemsFlag check above: that headless
            // uninstall path never reads user.config at all, so running this ahead of it would be pure
            // overhead on every uninstall.
            UserConfigFolderMigration.Run();

            // Inside the guard (release-hardening review L7): Install touches WinForms' global
            // Application state, and a throw from it must still end in the startup-failed message box
            // below rather than an unhandled exception with no message at all.
            WinFormsExceptionBridge.Install(ex => CrashRouting.Handler?.Invoke(ex, CrashOrigin.WinForms));

            // docs/PLANS/localization.md §2.5 step 1 (finding 5): touch SystemCulture before anything
            // below ever assigns CultureInfo.CurrentUICulture, so its static field-initializer snapshot
            // is the real, unmodified Windows UI culture — what "follow Windows" (SupportedLanguages.
            // FollowSystem) resolves against later, including after a manual language switch back to
            // "as in Windows" in a later session.
            _ = SystemCulture.OriginalUiCulture;

            var preference = TryReadLanguagePreference();
            var culture = SupportedLanguages.Resolve(preference, SystemCulture.OriginalUiCulture);
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.CurrentUICulture = culture;

            // Handled entirely on its own, before the single-instance handshake and before
            // ApplicationStateService.Load() ever run (ADR-024 §7 scenario C) — this is a one-shot
            // recovery tool, not a normal application launch. No WPF Application is ever constructed on
            // this path.
            if (args.Any(a => string.Equals(a, RestoreStateBackupFlag, StringComparison.OrdinalIgnoreCase)))
                return RunRestoreStateBackup();

            return RunSingleInstanceHandshakeAndApp(args);
        }
        catch (Exception ex)
        {
            // M-4: if the exception happened before the culture bootstrap above ran to completion,
            // CultureInfo.CurrentUICulture is still whatever Windows started the process with — an
            // accepted degradation (docs/PLANS/localization.md §2.5 step 4).
            MessageBox.Show(
                LocalizationResources.Format("Startup_Failed_Format", CultureInfo.CurrentUICulture, ex.Message),
                AppTitle,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return 1;
        }
    }

    /// <summary>
    /// Reads <see cref="Properties.Settings.Default"/>'s <c>Language</c> user setting (docs/PLANS/
    /// localization.md §2.3/§2.5 step 2), tolerating a corrupt/missing <c>user.config</c>: any exception
    /// reading it — most notably <see cref="System.Configuration.ConfigurationErrorsException"/> —
    /// degrades to <see cref="SupportedLanguages.FollowSystem"/> rather than ever failing startup.
    /// </summary>
    private static string TryReadLanguagePreference()
    {
        try
        {
            return Properties.Settings.Default.Language;
        }
        catch (Exception)
        {
            return SupportedLanguages.FollowSystem;
        }
    }

    /// <summary>
    /// Implements the <c>--restore-state-backup</c> command-line switch (ADR-024 §7 rollback scenario
    /// C). Acquires the same advisory storage lock <c>Program.Main</c>'s normal path uses (ADR-024 D-8)
    /// before touching any file — running this concurrently with a live, writing DesktopOrganizer
    /// instance would be exactly the two-writer hazard the lock exists to prevent — then delegates the
    /// actual recovery to <see cref="IStateRestoreCommand"/>. Both success and failure are reported to
    /// the console (this is meant to be run from a terminal, always in English — see
    /// <see cref="LocalizeRestoreResult"/>'s remarks) and via a message box, localized to
    /// <see cref="CultureInfo.CurrentUICulture"/> (in case it wasn't run from a terminal).
    /// </summary>
    private static int RunRestoreStateBackup()
    {
        var services = new ServiceCollection();
        services.ConfigureServices();

        using var provider = services.BuildServiceProvider();

        AppLog.Initialize(provider.GetRequiredService<IAppLog>());

        var storageLock = provider.GetRequiredService<IStorageLock>();
        if (!storageLock.TryAcquire())
        {
            Console.Error.WriteLine(RestoreLockUnavailableMessage); // cli-output
            AppLog.Current.Warn("Program", "RestoreStateBackupLockUnavailable");
            MessageBox.Show(
                LocalizationResources.GetString("Restore_LockUnavailable_Message", CultureInfo.CurrentUICulture),
                AppTitle,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return 2;
        }

        var restoreCommand = provider.GetRequiredService<IStateRestoreCommand>();
        var result = restoreCommand.Restore();
        var localizedMessage = LocalizeRestoreResult(result, CultureInfo.CurrentUICulture);

        if (result.Outcome == StateRestoreOutcome.Restored)
        {
            Console.WriteLine(result.Message);
            AppLog.Current.Info("Program", "RestoreStateBackupRestored", new KeyValuePair<string, object?>("message", result.Message));
            MessageBox.Show(localizedMessage, AppTitle, MessageBoxButton.OK, MessageBoxImage.Information);
            return 0;
        }

        Console.Error.WriteLine(result.Message); // cli-output
        AppLog.Current.Warn(
            "Program",
            "RestoreStateBackupNotRestored",
            new KeyValuePair<string, object?>("outcome", result.Outcome.ToString()),
            new KeyValuePair<string, object?>("message", result.Message));
        MessageBox.Show(localizedMessage, AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
        return 2;
    }

    /// <summary>
    /// Maps a <see cref="StateRestoreResult"/> to the localized text shown in
    /// <see cref="RunRestoreStateBackup"/>'s message box (docs/PLANS/localization.md §2.8). The
    /// <see cref="StateRestoreResult.Message"/> string itself is never localized and is always printed
    /// to the console verbatim, in English — it is meant for scripts/developers reading the terminal,
    /// not the end user (CLAUDE.md: diagnostics stay English); this method only supplies the
    /// user-facing message box wording, folding that same English text in as a "Details" suffix for the
    /// outcomes that carry one.
    /// </summary>
    /// <param name="result">The outcome of a single <see cref="IStateRestoreCommand.Restore"/> call.</param>
    /// <param name="culture">The culture to localize the message box text into.</param>
    private static string LocalizeRestoreResult(StateRestoreResult result, CultureInfo culture)
    {
        var candidateFileName = result.CandidatePath is null ? null : Path.GetFileName(result.CandidatePath);

        return result.Outcome switch
        {
            StateRestoreOutcome.Restored when result.QuarantinedPrimaryPath is not null =>
                LocalizationResources.Format(
                    "Restore_RestoredQuarantined_Format",
                    culture,
                    candidateFileName,
                    Path.GetFileName(result.QuarantinedPrimaryPath)),
            StateRestoreOutcome.Restored =>
                LocalizationResources.Format("Restore_Restored_Format", culture, candidateFileName),
            StateRestoreOutcome.NoCandidateFound =>
                LocalizationResources.GetString("Restore_NoCandidate_Message", culture),
            StateRestoreOutcome.CandidateInvalid =>
                LocalizationResources.Format("Restore_CandidateInvalid_Format", culture, candidateFileName, result.Message),
            StateRestoreOutcome.Failed =>
                LocalizationResources.Format("Restore_Failed_Format", culture, candidateFileName, result.Message),
            _ =>
                LocalizationResources.Format("Restore_Unknown_Format", culture, result.Outcome, result.Message),
        };
    }

    /// <summary>
    /// Implements the <c>--restore-panel-items</c> command-line switch: the uninstall-time counterpart
    /// to <see cref="IPanelItemStorageService"/>'s live Move semantics (see
    /// <see cref="IPanelItemsUninstallRestorer"/> remarks). Wired into
    /// <c>installer/DesktopOrganizer.iss</c>'s <c>[UninstallRun]</c>, invoked by the uninstaller itself
    /// — headless, after <c>InitializeUninstall</c>'s <c>EnsureAppClosed</c> has already made sure no
    /// instance of the app is running — strictly before <c>{app}</c>'s files (and, separately, this
    /// command's own target folder) are removed.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="RunRestoreStateBackup"/>, this never shows a <see cref="MessageBox"/> for any
    /// outcome: it is invoked non-interactively by the uninstaller (typically with
    /// <c>Flags: runhidden waituntilterminated</c>), where a modal dialog would have nobody present to
    /// dismiss it and would simply hang the uninstall. Every outcome is instead reported only to the
    /// console (English, same rationale as <see cref="RestoreLockUnavailableMessage"/>) and the
    /// diagnostics log, via a deterministic exit code (see <see cref="Main"/>'s own remarks).
    /// <para>
    /// Acquires the same advisory storage lock <see cref="RunRestoreStateBackup"/> and the normal
    /// startup path use (ADR-024 D-8) before touching any file: running this concurrently with a live,
    /// writing DesktopOrganizer instance would be exactly the two-writer hazard the lock exists to
    /// prevent (though in practice <c>EnsureAppClosed</c> should already guarantee no such instance
    /// exists by the time the uninstaller reaches this switch).
    /// </para>
    /// <para>
    /// MINOR 3 (release-polish 1.0 code review): before any of that, checks whether the managed
    /// <c>PanelItems</c> folder even exists — resolved the same side-effect-free way
    /// <see cref="DesktopOrganizer.Services.Tools.PanelItemsUninstallRestorer"/>'s own production
    /// constructor does, via <see cref="AppStorageRoot.Default"/> — and returns immediately if not. On a
    /// machine where the app never ran (or a second uninstall re-run after a clean first one), this
    /// avoids building the DI container / initializing <see cref="IAppLog"/> / creating
    /// <c>Logs\</c>/<c>State\</c> purely to report "nothing to restore".
    /// </para>
    /// <para>
    /// M1 (release-polish 1.0 code review): the entire body below the existence check runs inside its
    /// own try/catch. An exception here must never propagate to <see cref="Main"/>'s outer catch — that
    /// catch shows a <see cref="MessageBox"/>, which on this headless uninstall path has nobody present
    /// to dismiss it and would hang the uninstall indefinitely.
    /// </para>
    /// </remarks>
    private static int RunRestorePanelItems()
    {
        // MINOR 3: mirrors PanelItemsUninstallRestorer's own `Directory.Exists(_storageRoot)` guard, but
        // ahead of DI/AppLog/the storage lock — Path.Combine/AppStorageRoot.Default and Directory.Exists
        // are the only two calls made here, neither of which creates anything.
        var panelItemsRoot = Path.Combine(AppStorageRoot.Default, "PanelItems");
        if (!Directory.Exists(panelItemsRoot))
        {
            Console.WriteLine(RestorePanelItemsNothingToRestoreMessage); // cli-output
            return 0;
        }

        try
        {
            var services = new ServiceCollection();
            services.ConfigureServices();

            using var provider = services.BuildServiceProvider();

            AppLog.Initialize(provider.GetRequiredService<IAppLog>());

            var storageLock = provider.GetRequiredService<IStorageLock>();
            if (!storageLock.TryAcquire())
            {
                Console.Error.WriteLine(RestorePanelItemsLockUnavailableMessage); // cli-output
                AppLog.Current.Warn("Program", "RestorePanelItemsLockUnavailable");
                return RestorePanelItemsLockUnavailableExitCode;
            }

            var restorer = provider.GetRequiredService<IPanelItemsUninstallRestorer>();
            var result = restorer.RestoreAll();

            if (result.Success)
            {
                Console.WriteLine(result.Message); // cli-output
                AppLog.Current.Info(
                    "Program",
                    "RestorePanelItemsCompleted",
                    new KeyValuePair<string, object?>("restored", result.RestoredCount));
                return 0;
            }

            Console.Error.WriteLine(result.Message); // cli-output
            AppLog.Current.Warn(
                "Program",
                "RestorePanelItemsCompletedWithFailures",
                new KeyValuePair<string, object?>("restored", result.RestoredCount),
                new KeyValuePair<string, object?>("failed", result.FailedCount));
            return RestorePanelItemsPartialFailureExitCode;
        }
        catch (Exception ex)
        {
            // M1: best-effort only — AppLog.Current defaults to NullAppLog.Instance until Initialize()
            // runs, so this is always safe to call even if the exception happened before DI/AppLog were
            // ever constructed above.
            Console.Error.WriteLine($"{RestorePanelItemsUnexpectedFailureMessage} {ex.Message}"); // cli-output
            AppLog.Current.Error("Program", "RestorePanelItemsUnexpectedFailure", ex);
            return RestorePanelItemsUnexpectedFailureExitCode;
        }
    }

    private static int RunSingleInstanceHandshakeAndApp(string[] args)
    {
        var services = new ServiceCollection();
        services.ConfigureServices();

        // Side-effect-free: every registration here is a singleton descriptor, none are eagerly
        // resolved by BuildServiceProvider() itself (ADR-023 §2.4).
        using var provider = services.BuildServiceProvider();

        AppLog.Initialize(provider.GetRequiredService<IAppLog>());

        // Code-review hardening (2026-09-28): UserConfigFolderMigration ran before AppLog existed, so any
        // outcome it had to report went only to its own migration.log file. Hand that one-line summary to
        // the now-initialized structured log too, so it also shows up in a normal session's diagnostics.
        if (UserConfigFolderMigration.PendingLogSummary is { } userConfigMigrationSummary)
        {
            AppLog.Current.Info(
                "Program",
                "UserConfigFolderMigrationSummary",
                new KeyValuePair<string, object?>("summary", userConfigMigrationSummary));
        }

        var coordinator = provider.GetRequiredService<ISingleInstanceCoordinator>();

        // Bounded (<=3s total, ADR-023 D-6) and never throws — safe to call unconditionally here, on
        // this process's single STA thread, before anything else ever touches the guard mutex
        // (thread-affinity, ADR-023 R-5).
        var decision = coordinator.Start();

        switch (decision.Action)
        {
            case SingleInstanceStartupAction.ExitAfterActivation:
                // A running Primary accepted our activation request and will show its own window —
                // nothing left for this process to do. Disposing `provider` below (via the `using`)
                // disposes the coordinator, releasing this process's handles on both mutexes.
                return 0;

            case SingleInstanceStartupAction.ExitOtherSession:
                MessageBox.Show(
                    LocalizationResources.GetString("Startup_OtherSession_Message", CultureInfo.CurrentUICulture),
                    AppTitle,
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return 4;

            case SingleInstanceStartupAction.ExitActivationFailed:
                MessageBox.Show(
                    LocalizationResources.GetString("Startup_ActivationFailed_Message", CultureInfo.CurrentUICulture),
                    AppTitle,
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return 3;
        }

        // decision.Action == RunAsPrimary from here on — the switch above exhausts every other member
        // of SingleInstanceStartupAction (ADR-023 D-4/D-5/D-7).
        //
        // TryBeginServing() is idempotent by contract (ISingleInstanceCoordinator): if Start()'s own loop
        // already got the server running, this is a no-op that returns true. If instead Start() resolved
        // to a *degraded* Primary (ADR-023 D-4/M-2: the guard was acquired but the pipe server itself
        // could not come up for a reason other than another process's competing server — AclFailed or
        // Faulted, as opposed to PipeBusy) this retries once more; that retry's own result no longer
        // gates startup, though — SingleInstanceStartupDecisionTable's contract is "acquired ⇒
        // RunAsPrimary regardless of serverStarted" (ADR-023 D-4), so a degraded Primary still runs, just
        // without a working activation channel (already recorded via the ActivationServerUnavailable
        // diagnostics event; a second failed retry here logs it again, which is harmless). The one case
        // that genuinely means "activation is impossible" — another session's primary already owns the
        // single pipe instance (PipeBusy) — is handled entirely inside Start() itself, which releases the
        // guard and resolves to ExitOtherSession/ExitActivationFailed instead of ever reaching this line.
        coordinator.TryBeginServing();

        // ADR-024 D-11 ordering, verbatim: storage lock -> ApplicationStateService.Load() -> new App(...).
        // Both run strictly before any component capable of calling IApplicationStateService.SaveNow()/
        // RequestSave() exists — App itself, and every one of the four consumers it eventually resolves
        // (WindowManager, PageManagerService, LayoutSnapshotService, SystemIconsPanelManager), are all
        // constructed later, inside app.Run() -> StartApp. A failed TryAcquire() degrades to a read-only
        // session (IApplicationStateStore.IsWritable stays false) rather than aborting startup — this
        // process already passed the single-instance handshake above and is the confirmed Primary.
        var storageLock = provider.GetRequiredService<IStorageLock>();
        if (!storageLock.TryAcquire())
        {
            // ADR-024 D-8: a failed acquire degrades this confirmed-Primary process to a read-only
            // session (IApplicationStateStore.IsWritable stays false, every Save() call becomes a
            // no-op) rather than aborting startup — killing the process over a stuck lock file would
            // be worse than running without persistence. Logged exactly once, here; every later
            // Save() call re-checks IsWritable silently, without logging again.
            Console.Error.WriteLine(StorageLockUnavailableMessage); // cli-output
            provider.GetRequiredService<IDiagnosticsLog>().Write(
                "state",
                "StateStoreLockUnavailable",
                null,
                new KeyValuePair<string, object?>("attempts", StorageLockDefaultMaxAttempts));
            AppLog.Current.Warn(
                "Program",
                "StorageLockUnavailable",
                new KeyValuePair<string, object?>("attempts", StorageLockDefaultMaxAttempts));
        }

        var applicationState = provider.GetRequiredService<IApplicationStateService>();
        applicationState.Load();

        var app = new App(provider, coordinator, decision.Role);
        app.InitializeComponent();
        return app.Run();
    }
}
