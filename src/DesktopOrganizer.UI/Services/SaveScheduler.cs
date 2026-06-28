namespace DesktopOrganizer.UI.Services;

/// <summary>
/// Debounces frequent "please persist state" requests into a single deferred execution, with a
/// synchronous immediate-write escape hatch for structural changes and application shutdown.
/// </summary>
/// <remarks>
/// Used by <see cref="WindowManager"/> to implement the two-level save policy from
/// <c>docs/PLANS/phase-7-performance-hardening.md</c> (Block C): frequent UI actions (move, resize,
/// roll-up, filter/view changes, file removal) call <see cref="Request"/>, which collapses any burst
/// of calls inside the debounce window into a single write; structural changes (panel create/delete,
/// snapshot restore, app exit) call <see cref="Flush"/>, which writes synchronously right away so the
/// last user action is never lost.
///
/// The debounce mechanism itself is abstracted behind <see cref="IDebounceTimer"/> (resolved via
/// <paramref name="timerProvider"/> on every call, not cached at construction) so that:
/// <list type="bullet">
/// <item>tests can trigger the deferred callback deterministically via a fake timer instead of racing
/// a real one;</item>
/// <item>when no timer is available (<paramref name="timerProvider"/> returns <see langword="null"/> —
/// e.g. <c>Application.Current</c> is <see langword="null"/> in a headless/test host, or shutdown has
/// already torn the dispatcher down), <see cref="Request"/> degrades to an immediate synchronous save
/// instead of silently losing the request.</item>
/// </list>
/// </remarks>
public sealed class SaveScheduler
{
    private readonly Action _save;
    private readonly Func<IDebounceTimer?> _timerProvider;
    private readonly TimeSpan _window;

    /// <summary>
    /// Initializes a new instance of the <see cref="SaveScheduler"/> class.
    /// </summary>
    /// <param name="save">The actual persistence work to (eventually) run.</param>
    /// <param name="timerProvider">
    /// Resolves the <see cref="IDebounceTimer"/> to use, evaluated on every <see cref="Request"/>/
    /// <see cref="Flush"/> call. May return <see langword="null"/> to force an immediate synchronous
    /// save (e.g. no UI dispatcher is available).
    /// </param>
    /// <param name="window">The debounce window: how long to wait, after the last <see cref="Request"/>, before actually saving.</param>
    public SaveScheduler(Action save, Func<IDebounceTimer?> timerProvider, TimeSpan window)
    {
        _save = save;
        _timerProvider = timerProvider;
        _window = window;
    }

    /// <summary>
    /// Requests a deferred save. Any further call within <see cref="_window"/> collapses into the
    /// same eventual, single save. Degrades to an immediate synchronous save when no timer is
    /// available.
    /// </summary>
    public void Request()
    {
        var timer = _timerProvider();
        if (timer is null)
        {
            _save();
            return;
        }

        timer.Start(_window, _save);
    }

    /// <summary>
    /// Cancels any pending deferred save and performs one synchronously, right now. Safe to call even
    /// when nothing is pending — used by structural changes and shutdown, which must not rely on
    /// whether a debounced request happens to already be in flight.
    /// </summary>
    public void Flush()
    {
        _timerProvider()?.Stop();
        _save();
    }
}
