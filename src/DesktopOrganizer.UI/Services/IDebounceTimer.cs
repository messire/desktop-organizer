namespace DesktopOrganizer.UI.Services;

/// <summary>
/// Abstraction over the one-shot "fire after N milliseconds of silence" timer used by
/// <see cref="SaveScheduler"/>. Exists purely so tests can trigger the deferred callback
/// deterministically instead of racing a real timer.
/// </summary>
public interface IDebounceTimer
{
    /// <summary>
    /// (Re)starts the debounce window: <paramref name="callback"/> fires once, after
    /// <paramref name="window"/> elapses with no further calls to <see cref="Start"/>. Calling this
    /// again before the window elapses cancels the previous callback and restarts the window with the
    /// new one — this is what collapses a burst of calls into a single eventual callback.
    /// </summary>
    /// <param name="window">How long to wait, from this call, before firing.</param>
    /// <param name="callback">The callback to invoke once the window elapses.</param>
    void Start(TimeSpan window, Action callback);

    /// <summary>
    /// Cancels any pending callback scheduled by <see cref="Start"/>. No-op if nothing is pending.
    /// </summary>
    void Stop();
}
