using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace DesktopOrganizer.Tests.Unit.Helpers;

/// <summary>
/// Regression tests for <see cref="StaTestRunner"/>'s dispatcher teardown.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="StaTestRunner"/> used to call <see cref="Dispatcher.InvokeShutdown"/> on its worker
/// thread's dispatcher unconditionally. When the test body it ran had been the first in the process to
/// construct <see cref="Application"/> (<c>PanelMetricsTests</c> does exactly that, and whether it got
/// there first depended purely on xUnit's class scheduling), that teardown left
/// <see cref="Application.Current"/> permanently non-null with a DEAD dispatcher — and a dead
/// dispatcher rethrows <see cref="System.Threading.Tasks.TaskCanceledException"/> at anyone who
/// marshals onto it, killing the test host when that "anyone" is a thread-pool timer
/// (<c>ChildWindowViewModel</c>'s liveness poll). That is the order-dependent
/// <c>Test Run Aborted</c> this class pins shut.
/// </para>
/// </remarks>
[Trait(UnitTests, "DesktopOrganizer.Tests")]
public class StaTestRunnerTests
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Deterministic in either scheduling order: this constructs the <see cref="Application"/> through
    /// <see cref="StaTestRunner"/> itself if nobody has yet (reproducing the exact old failure), and
    /// otherwise asserts the same invariant about whichever fixture did get there first.
    /// </summary>
    [Fact]
    public void Run_NeverLeavesApplicationCurrentWithADeadDispatcher()
    {
        StaTestRunner.Run(() => WpfApplicationHost.EnsureApplication());

        var app = Application.Current;
        Assert.NotNull(app);
        Assert.False(app!.Dispatcher.HasShutdownStarted, "Application.Current's dispatcher must still accept work.");
        Assert.False(app.Dispatcher.HasShutdownFinished, "Application.Current's dispatcher must still accept work.");
    }

    /// <summary>
    /// The owner exemption must stay an exemption: a worker thread that did NOT create
    /// <see cref="Application.Current"/> still releases its own lazily-created dispatcher (the hidden
    /// message-only HWND and thread message queue behind it) instead of leaking one per STA test.
    /// </summary>
    [Fact]
    public void Run_ShutsDownAWorkerDispatcherThatDoesNotOwnApplicationCurrent()
    {
        // Guarantee an Application owned by some OTHER thread first, so the worker below is definitely
        // in the non-owner branch whichever test class ran before this one.
        StaTestRunner.Run(() => WpfApplicationHost.EnsureApplication());

        Dispatcher? workerDispatcher = null;
        StaTestRunner.Run(() =>
        {
            // Touching any DependencyObject is what lazily creates the worker thread's dispatcher.
            _ = new Grid();
            workerDispatcher = Dispatcher.CurrentDispatcher;
        });

        Assert.NotNull(workerDispatcher);
        Assert.NotSame(Application.Current!.Dispatcher, workerDispatcher);

        // The shutdown runs on the worker thread as it unwinds, AFTER Run() has already released this
        // thread, so wait for it rather than assuming it has landed already.
        var shutDown = SpinWait.SpinUntil(() => workerDispatcher!.HasShutdownFinished, ShutdownTimeout);

        Assert.True(shutDown, "A non-owner STA worker must not leak its dispatcher.");
    }

    [Fact]
    public void Run_RethrowsTheTestBodysExceptionOnTheCallingThread()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => StaTestRunner.Run(() => throw new InvalidOperationException("boom")));

        Assert.Equal("boom", exception.Message);
    }

    // ── MINOR (P08 fix cycle 2): absorbed-exception observability ───────────────────────────────────

    /// <summary>
    /// Regression coverage for <see cref="StaTestRunner.AbsorbedExceptionCount"/>/
    /// <see cref="StaTestRunner.AssertNoAbsorbedExceptions"/> themselves — this codebase's own
    /// mechanism for making <c>StaTestRunner.PumpForever</c>'s "print a line to stderr" degrade
    /// observable to a test, rather than only to a human reading console output.
    /// <see cref="StaTestRunner.AbsorbedExceptionCount"/> is cumulative for the whole process (see its
    /// own remarks), so this only asserts it INCREASES, never that it equals an exact value — other
    /// tests before or after this one may leak their own callbacks onto the same pumped dispatcher.
    /// </summary>
    /// <remarks>
    /// Deliberately does NOT rely on <see cref="Application.Current"/>'s ambient dispatcher actually
    /// pumping: whether the process-wide owner pumps at all depends on whether <see cref="Run"/> is what
    /// made it, and some fixtures in this suite construct <see cref="Application"/> WITHOUT going
    /// through <see cref="Run"/> at all (see "a live but non-pumping owner" in
    /// <c>docs/Refactoring/Testing/sta-test-conventions.md</c>) — depending on unrelated test ordering
    /// across the whole assembly to decide which one got there first would make this test's own outcome
    /// order-dependent, exactly the flake class this file exists to prevent. Instead this test drives
    /// <c>PumpForever</c> directly (via reflection — it is <see langword="private"/>) on a dedicated,
    /// fully self-owned STA thread, and shuts that thread's dispatcher down itself afterward.
    /// </remarks>
    [Fact]
    public void AbsorbedExceptionCount_IncrementsWhenALeakedCallbackThrowsOnAPumpedDispatcher()
    {
        var before = StaTestRunner.AbsorbedExceptionCount;
        using var ready = new ManualResetEventSlim(initialState: false);
        Dispatcher? dispatcher = null;

        var pumpForever = typeof(StaTestRunner).GetMethod("PumpForever", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(nameof(StaTestRunner), "PumpForever");

        var thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();

            // Blocks in Dispatcher.Run() until InvokeShutdown() below — this is StaTestRunner's own
            // owner-thread pump, exercised directly rather than via Run() (see remarks).
            pumpForever.Invoke(null, [dispatcher]);
        })
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(ready.Wait(ShutdownTimeout), $"{nameof(AbsorbedExceptionCount_IncrementsWhenALeakedCallbackThrowsOnAPumpedDispatcher)}: the dedicated STA thread never reported its dispatcher.");

        try
        {
            dispatcher!.BeginInvoke(() =>
                throw new InvalidOperationException(
                    $"{nameof(StaTestRunnerTests)}: deliberate leaked-callback exception for {nameof(StaTestRunner.AbsorbedExceptionCount)} regression coverage."));

            var incremented = SpinWait.SpinUntil(() => StaTestRunner.AbsorbedExceptionCount > before, ShutdownTimeout);

            Assert.True(incremented, "PumpForever must count an exception it absorbs from a leaked background callback.");
            Assert.Throws<InvalidOperationException>(StaTestRunner.AssertNoAbsorbedExceptions);
        }
        finally
        {
            dispatcher!.InvokeShutdown();
            thread.Join(ShutdownTimeout);
        }
    }
}
