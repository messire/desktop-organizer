using DesktopOrganizer.Services.Diagnostics;

namespace DesktopOrganizer.UI.Lifecycle;

/// <summary>
/// Wraps the application's one, idempotent shutdown sequence (ADR-033 part 2, release-hardening plan
/// §2 Q2) — the exact steps <c>App.OnExit</c> used to run inline — so <c>App.OnExit</c>,
/// <c>Application.SessionEnding</c>/<c>Microsoft.Win32.SystemEvents.SessionEnding</c>, and a fatal
/// crash's own "save state" step (<c>CrashCoordinator</c>, Dispatcher/WinForms origin only) can all
/// trigger the very same steps without ever running them twice.
/// </summary>
/// <remarks>
/// Guarded by a single <see cref="Interlocked"/> flag: the first caller to reach <see cref="Run"/> —
/// whichever of <c>OnExit</c>, a <c>SessionEnding</c> handler, or a crash runs first — executes every
/// step, in order; every later call, including a genuinely concurrent one, is a silent no-op. Each step
/// is wrapped in its own <see langword="try"/>/<see langword="catch"/> (M7, release-hardening review): a
/// step's own "never throws" contract (e.g. <c>IApplicationStateService.SaveNow</c>) is a design intent,
/// not a guarantee this class may rely on at the one moment — process shutdown — where a single
/// misbehaving step (icons restore, save, log flush, dispose) must never stop the steps after it from
/// running.
/// </remarks>
public sealed class ShutdownSequence
{
    private readonly Action[] _steps;
    private int _ran;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShutdownSequence"/> class.
    /// </summary>
    /// <param name="steps">The steps to run, in order, the first time <see cref="Run"/> is called.</param>
    public ShutdownSequence(params Action[] steps) =>
        _steps = steps ?? throw new ArgumentNullException(nameof(steps));

    /// <summary>
    /// Runs every step passed to the constructor, in order, exactly once across the lifetime of this
    /// instance. Every call after the first — from any caller, on any thread — is a no-op.
    /// </summary>
    public void Run()
    {
        if (Interlocked.Exchange(ref _ran, 1) != 0) return;

        foreach (var step in _steps)
        {
            try
            {
                step();
            }
            catch (Exception ex)
            {
                // A single failing step (icons restore, save, log flush, dispose) must not abort the
                // rest of the shutdown sequence — log and keep going so every later step still runs.
                AppLog.Current.Error("ShutdownSequence", "StepFailed", ex);
            }
        }
    }
}
