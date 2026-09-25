using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace DesktopOrganizer.Tests.Helpers;

/// <summary>
/// Minimal STA-thread test convention for this project (docs/PLANS/workspace-grid-00-baseline.md, P00
/// part D "инвентарь тестов": no unit test previously exercised a live WPF <c>UIElement</c>/<c>Panel</c>
/// instance's own <c>Measure</c>/<c>Arrange</c> — every existing WPF-touching test either reads static
/// <see cref="System.Windows.DependencyProperty"/> metadata with no instance at all
/// (<c>Unit.Controls.PanelChromeMetadataTests</c>) or operates on plain WPF value types
/// (<see cref="System.Windows.Size"/>/<see cref="System.Windows.Point"/>/<see cref="System.Windows.Rect"/>),
/// never a full <see cref="System.Windows.FrameworkElement"/>/<see cref="System.Windows.Controls.Panel"/>).
/// No third-party STA test package (e.g. <c>Xunit.StaFact</c>) is referenced by this project — this class
/// is a small, dependency-free stand-in: it runs <c>action</c> on a dedicated
/// <see cref="ApartmentState.STA"/> thread (the apartment WPF layout/visual code is documented to
/// require), joins it, and re-throws any exception on the CALLING (xUnit) thread with its original stack
/// trace preserved (<see cref="ExceptionDispatchInfo"/>) so a failure still surfaces as a normal, readable
/// test failure rather than being silently swallowed on the worker thread or reported as an unrelated
/// "thread did not complete" hang.
/// </summary>
/// <remarks>
/// P05 fix-cycle hardening (MINOR finding): three defensive properties this class did NOT have before —
/// (1) the worker thread is now a background thread (<see cref="Thread.IsBackground"/>), so a future test
/// body that (deliberately or accidentally) starts a <see cref="Dispatcher.Run"/> message pump and never
/// exits it can no longer keep the ENTIRE test-runner PROCESS alive after the run otherwise finishes; (2)
/// <see cref="Thread.Join()"/> now has a bounded timeout (<see cref="CompletionTimeout"/>) and throws a
/// diagnosable <see cref="TimeoutException"/> instead of blocking the calling (xUnit) thread forever if
/// the worker thread ever deadlocks; (3) any <see cref="Dispatcher"/> lazily created for the STA thread
/// during <c>action</c> (every <see cref="System.Windows.DependencyObject"/> touches
/// <see cref="Dispatcher.CurrentDispatcher"/> on first use, which creates one on demand if none exists
/// yet for the calling thread) is now explicitly shut down (<see cref="Dispatcher.InvokeShutdown"/>) in a
/// <see langword="finally"/> block before the thread exits, so its underlying resources (a hidden
/// message-only <c>HWND</c>, its thread-local message queue) are released deterministically rather than
/// left for the CLR/OS to reclaim only when the thread itself is torn down.
/// </remarks>
/// <remarks>
/// <para>
/// STA-flake fix (2026-09-22) — property (3) above has ONE exception it previously got wrong, and it
/// was the root cause of an intermittent, order-dependent <c>Test Run Aborted</c> with no failing test.
/// <see cref="Application"/> is a process-wide singleton that can never be un-constructed: once a test
/// body constructs one on THIS worker thread, <see cref="Application.Current"/> is permanently bound to
/// THIS thread's <see cref="Dispatcher"/> for the rest of the <c>dotnet test</c> process. Shutting that
/// dispatcher down on the way out (which the <see langword="finally"/> below used to do
/// unconditionally) leaves every later test running against an <see cref="Application.Current"/> whose
/// dispatcher is dead — and a dead dispatcher does not fail quietly: a cross-thread
/// <c>Dispatcher.Invoke</c> onto it rethrows <see cref="System.Threading.Tasks.TaskCanceledException"/>
/// at the CALLING thread, which for the thread-pool timers production ViewModels own
/// (<c>ChildWindowViewModel</c>'s 3-second liveness poll) is an unhandled exception that kills the test
/// host outright. Whether it happened at all depended purely on which WPF-touching test class xUnit
/// happened to schedule first, hence "sometimes".
/// </para>
/// <para>
/// So: when this worker thread turns out to own <see cref="Application.Current"/>, it now keeps its
/// dispatcher alive and genuinely pumping (<see cref="Dispatcher.Run"/>) for the rest of the process
/// instead of shutting it down — the same "one owner, and the owner never stops pumping" convention
/// <c>ResourceDictionaryMergeTests</c>/<c>ChildWindowContentSizingTests</c> already follow for their own
/// STA harnesses (docs/Refactoring/Testing/sta-test-conventions.md). The worker stays
/// <see cref="Thread.IsBackground"/>, so pumping forever still cannot hold the process open; and
/// <see cref="Run"/> no longer waits on <see cref="Thread.Join(TimeSpan)"/> (which that pump would
/// necessarily outlive) but on an explicit completion signal raised the moment the test body itself is
/// done.
/// </para>
/// </remarks>
public static class StaTestRunner
{
    /// <summary>
    /// How long <see cref="Run"/> waits for the STA worker's test body before giving up and throwing
    /// <see cref="TimeoutException"/> — generous enough that no legitimate WPF Measure/Arrange test body
    /// could ever legitimately need this long, but short enough that a genuine deadlock fails the test
    /// run promptly instead of hanging a CI job until its own outer timeout kills it.
    /// </summary>
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Runs <paramref name="action"/> to completion on a new STA thread, then re-throws on the calling
    /// thread if it threw.
    /// </summary>
    /// <param name="action">The WPF-touching test body to run under an STA apartment.</param>
    /// <exception cref="TimeoutException">
    /// The worker thread did not complete within <see cref="CompletionTimeout"/> — see this class's remarks.
    /// </exception>
    public static void Run(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        Exception? captured = null;
        using var completed = new ManualResetEventSlim(initialState: false);

        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
            finally
            {
                // Release the calling (xUnit) thread the moment the test body itself is done — BEFORE
                // the cleanup below, which in the Application-owner case never returns at all.
                //
                // Guarded because the ONE path that reaches here with `completed` already disposed is
                // the CompletionTimeout path: Run() has given up, thrown, and its `using` has run while
                // this worker was still inside action(). An ObjectDisposedException escaping this
                // `finally` would be an unhandled exception on a non-UI thread — i.e. it would convert
                // an already-diagnosable TimeoutException into a process kill.
                try
                {
                    completed.Set();
                }
                catch (ObjectDisposedException)
                {
                    // Run() timed out and moved on; nobody is waiting for this signal any more.
                }
            }

            Cleanup();
        })
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!completed.Wait(CompletionTimeout))
            throw new TimeoutException($"{nameof(StaTestRunner)}.{nameof(Run)}: STA worker thread did not complete within {CompletionTimeout}.");

        if (captured is not null)
            ExceptionDispatchInfo.Capture(captured).Throw();
    }

    /// <summary>
    /// <see cref="Run(Action)"/>'s value-returning counterpart — runs <paramref name="func"/> to
    /// completion on a new STA thread and returns its result, with every guarantee <see cref="Run(Action)"/>
    /// itself makes (owner-pumps-forever-with-an-absorber, non-owner shuts its own dispatcher down,
    /// exceptions re-thrown on the calling thread, bounded completion timeout).
    /// </summary>
    /// <remarks>
    /// Added (P08 fix cycle 2, M-2) specifically so classes that need a VALUE back out of their STA-thread
    /// body — several fixtures across this project (<c>ResourceDictionaryMergeTests</c>,
    /// <c>ChildWindowInitializeTests</c>, <c>ChildWindowContentSizingTests</c>,
    /// <c>ChildWindowSizingGridTests</c>, <c>ResizeOverlayGeometryTests</c>) had each hand-rolled their own
    /// near-identical private <c>RunOnStaThread&lt;T&gt;</c> for exactly this reason — can share this one
    /// implementation instead of duplicating the STA-thread machinery, AND, more importantly, gain the
    /// exception-absorbing <c>PumpForever</c> guard for free whenever their worker ends up owning
    /// <see cref="System.Windows.Application.Current"/>. None of the hand-rolled duplicates called
    /// <see cref="System.Windows.Threading.Dispatcher.Run()"/> with any
    /// <see cref="System.Windows.Threading.Dispatcher.UnhandledException"/> handler at all when they turned
    /// out to be the owner — a leaked callback throwing on THAT thread was an unguarded process kill,
    /// exactly the class of bug rule 2 is about, just reached through a different STA harness than the
    /// original case study.
    /// </remarks>
    /// <typeparam name="T">The STA-thread body's result type.</typeparam>
    /// <param name="func">The WPF-touching test body to run under an STA apartment.</param>
    /// <returns><paramref name="func"/>'s return value.</returns>
    public static T Run<T>(Func<T> func)
    {
        ArgumentNullException.ThrowIfNull(func);

        var result = default(T);

        // Explicit Action cast is load-bearing, not stylistic: `() => result = func()` is an assignment
        // expression, which is convertible to BOTH `Action` (the value is discarded) and `Func<T>` (the
        // value is used) — left ambiguous, overload resolution picked THIS SAME `Run<T>(Func<T>)` overload
        // over the intended `Run(Action)`, recursing into itself until a stack overflow crashed the whole
        // test host (confirmed empirically). The cast pins the target overload unambiguously.
        Run((Action)(() => result = func()));
        return result!;
    }

    /// <summary>
    /// Disposes of whatever <see cref="Dispatcher"/> the worker thread lazily acquired, on the worker
    /// thread itself, once its test body has finished. Runs after <c>completed.Set()</c>, so nothing it
    /// does (including never returning) can affect the test's own outcome.
    /// </summary>
    private static void Cleanup()
    {
        // FromThread, not CurrentDispatcher (which would CREATE one just to shut it down again), so a
        // test body that never touched a DependencyObject at all doesn't pay for a Dispatcher it never
        // needed.
        var dispatcher = Dispatcher.FromThread(Thread.CurrentThread);
        if (dispatcher is null) return;

        // The one case where shutting down is WRONG: the test body constructed the process-wide
        // Application on this thread, so this dispatcher is now Application.Current's dispatcher
        // forever. Shutting it down would poison every later cross-thread marshal in the process —
        // see this class's remarks for the full failure mode. Keep pumping instead, exactly like a
        // real WPF app's UI thread; IsBackground keeps that from holding the process open.
        if (ReferenceEquals(Application.Current?.Dispatcher, dispatcher))
        {
            PumpForever(dispatcher);
            return;
        }

        // P05 fix-cycle 2 NIT: this cleanup is wrapped in try/catch — an exception escaping the Thread
        // delegate is an unhandled exception on a non-UI thread, which for .NET (Core) tears down the
        // ENTIRE test process immediately rather than failing just the one test whose action() already
        // completed (successfully or not) by this point. Best-effort only: if InvokeShutdown itself
        // throws, the dispatcher's underlying HWND/message queue is still reclaimed once this
        // (background) thread terminates — nothing observable is lost by swallowing it here.
        try
        {
            dispatcher.InvokeShutdown();
        }
        catch (Exception shutdownException)
        {
            // Best-effort cleanup only — see the remark above. Only the exception's type name is
            // recorded (never Message/StackTrace), matching this codebase's own privacy
            // convention for swallowed exceptions (e.g. PanelResizeController.OnResizeDragDelta).
            Console.Error.WriteLine(
                $"{nameof(StaTestRunner)}: ignoring {shutdownException.GetType().Name} from Dispatcher.InvokeShutdown().");
        }
    }

    /// <summary>
    /// Runs the <see cref="Application.Current"/>-owning thread's message pump for the rest of the
    /// process, absorbing anything that throws out of an operation dispatched onto it.
    /// </summary>
    /// <param name="dispatcher">This thread's dispatcher — the one <see cref="Application.Current"/> is bound to.</param>
    /// <remarks>
    /// The work that lands here is, by construction, never a test's own assertion path: every
    /// <see cref="Run"/> body has already finished and had its exceptions re-thrown on the xUnit thread
    /// before this pump starts. What reaches it instead is background work from objects earlier tests
    /// left alive — <c>ChildWindowViewModel</c> liveness ticks,
    /// <c>SystemIconsPanelViewModel.OnRecycleBinChanged</c>'s <c>BeginInvoke</c> — observing torn-down
    /// state. WPF re-throws an unhandled <see cref="DispatcherUnhandledExceptionEventArgs"/> out of
    /// <see cref="Dispatcher.Run"/> itself, which on this non-xUnit thread would kill the whole test
    /// host. Marking it handled turns that process kill into a printed line, which is strictly the
    /// better failure mode for a leaked-object callback nobody is asserting on.
    /// </remarks>
    private static void PumpForever(Dispatcher dispatcher)
    {
        dispatcher.UnhandledException += (_, args) =>
        {
            args.Handled = true;
            Interlocked.Increment(ref _absorbedExceptionCount);

            // Type name only, never Message/StackTrace — this codebase's convention for swallowed
            // exceptions (e.g. PanelResizeController.OnResizeDragDelta).
            Console.Error.WriteLine(
                $"{nameof(StaTestRunner)}: absorbed {args.Exception.GetType().Name} from a leaked background callback on the Application dispatcher.");
        };

        Dispatcher.Run();
    }

    // ── Absorbed-exception observability (MINOR, P08 fix cycle 2) ───────────────────────────────────

    /// <summary>
    /// Count of exceptions <see cref="PumpForever"/> has absorbed from a leaked background callback,
    /// cumulative for the lifetime of the <c>dotnet test</c> process (never reset — the
    /// <see cref="Application"/>-owning dispatcher it pumps for lives for the rest of the process too, per
    /// this class's own remarks).
    /// </summary>
    private static int _absorbedExceptionCount;

    /// <summary>Read-only view of <see cref="_absorbedExceptionCount"/>, for tests/manual inspection.</summary>
    public static int AbsorbedExceptionCount => Volatile.Read(ref _absorbedExceptionCount);

    /// <summary>
    /// Throws if <see cref="PumpForever"/> has absorbed any exception so far. NOT wired to run
    /// automatically after every <see cref="Run"/> — a leaked callback observed here was, by
    /// construction (see <see cref="PumpForever"/>'s remarks), left alive by some EARLIER test, so
    /// asserting this unconditionally after each <see cref="Run"/> would blame whichever unrelated test
    /// happens to run next for a defect that is not its own — precisely the "kill the messenger" failure
    /// mode <c>docs/Refactoring/Testing/sta-test-conventions.md</c>'s case study is about. Intended for
    /// deliberate, explicit use instead: a debugging session investigating a suspected leak, or a future
    /// dedicated end-of-suite fixture once one exists for this test host.
    /// </summary>
    public static void AssertNoAbsorbedExceptions()
    {
        var count = AbsorbedExceptionCount;
        if (count > 0)
        {
            throw new InvalidOperationException(
                $"{nameof(StaTestRunner)} absorbed {count} exception(s) from a leaked background callback " +
                "on the Application dispatcher since this process started — see stderr for the exception " +
                "type name(s).");
        }
    }
}
