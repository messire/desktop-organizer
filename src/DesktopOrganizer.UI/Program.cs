using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using DesktopOrganizer.Localization;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Abstractions.Storage;
using DesktopOrganizer.UI.Configuration;
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

    // English, printed to Console.Error only (never shown in a message box) so scripts/developers
    // reading the terminal always see the same text regardless of UI language (docs/PLANS/
    // localization.md §2.8/§0.2 — diagnostics and console output stay English). The message box shown
    // for the same failure uses the localized "Restore_LockUnavailable_Message" RESX key instead — see
    // RunRestoreStateBackup.
    private const string RestoreLockUnavailableMessage =
        "DesktopOrganizer could not restore application state: another running instance holds the storage lock. Close DesktopOrganizer and try again.";

    // ADR-024 D-8: printed once, to Console.Error only (no message box — a normal launch must not
    // gain a new modal dialog over a degraded-but-working session, see D-8's own rationale), when the
    // normal startup path's storage-lock acquire fails. Not localized — same rationale as
    // RestoreLockUnavailableMessage's console line above.
    private const string StorageLockUnavailableMessage =
        "DesktopOrganizer: could not acquire the application-state storage lock — running with saving disabled for this session.";

    // Mirrors StorageFileLock()'s production default (maxAttempts: 3) purely for the diagnostics
    // payload below; TryAcquire() itself does not report how many attempts it actually made.
    private const int StorageLockDefaultMaxAttempts = 3;

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
    /// see <see cref="RunRestoreStateBackup"/>).
    /// </summary>
    /// <param name="args">
    /// Command-line arguments. Unused by protocol <c>DO/1</c> itself (ADR-023 D-5) — the one flag this
    /// process reads directly is <c>--restore-state-backup</c> (ADR-024 §7 scenario C), checked before
    /// the single-instance handshake ever starts.
    /// </param>
    [STAThread]
    public static int Main(string[] args)
    {
        // M-4: everything below — DI container construction, resolving ISingleInstanceCoordinator (whose
        // factory constructs a real named Mutex and reads WindowsIdentity.GetCurrent()/
        // Process.GetCurrentProcess()), and the handshake itself — runs before any WPF Application or
        // diagnostics log exists. Unlike the WPF SDK's own auto-generated Main, nothing upstream of this
        // method catches an exception thrown here; without this guard the process would simply vanish
        // with no message and a CLR-default exit code.
        try
        {
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

        var storageLock = provider.GetRequiredService<IStorageLock>();
        if (!storageLock.TryAcquire())
        {
            Console.Error.WriteLine(RestoreLockUnavailableMessage);
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
            MessageBox.Show(localizedMessage, AppTitle, MessageBoxButton.OK, MessageBoxImage.Information);
            return 0;
        }

        Console.Error.WriteLine(result.Message);
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

    private static int RunSingleInstanceHandshakeAndApp(string[] args)
    {
        var services = new ServiceCollection();
        services.ConfigureServices();

        // Side-effect-free: every registration here is a singleton descriptor, none are eagerly
        // resolved by BuildServiceProvider() itself (ADR-023 §2.4).
        using var provider = services.BuildServiceProvider();

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
            Console.Error.WriteLine(StorageLockUnavailableMessage);
            provider.GetRequiredService<IDiagnosticsLog>().Write(
                "state",
                "StateStoreLockUnavailable",
                null,
                new KeyValuePair<string, object?>("attempts", StorageLockDefaultMaxAttempts));
        }

        var applicationState = provider.GetRequiredService<IApplicationStateService>();
        applicationState.Load();

        var app = new App(provider, coordinator, decision.Role);
        app.InitializeComponent();
        return app.Run();
    }
}
