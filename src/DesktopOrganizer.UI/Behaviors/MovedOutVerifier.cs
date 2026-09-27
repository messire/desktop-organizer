using System.IO;
using DesktopOrganizer.UI.Services;

namespace DesktopOrganizer.UI.Behaviors;

/// <summary>
/// Confirms whether a panel item that just took part in an external drag-out (Explorer/the desktop,
/// ADR-036, release-hardening-1.0.md §E) actually left its original location, by re-checking existence
/// at three delays after the drag completed (0 ms, +500 ms, +1500 ms — 0/500/2000 ms since the drag
/// ended) instead of trusting the OLE drop effect <see cref="System.Windows.DragDrop.DoDragDrop"/>
/// returns: Explorer's own optimized move frequently reports <see cref="System.Windows.DragDropEffects.None"/>
/// even on a successful move, and an async/deferred shell drop can complete after
/// <see cref="System.Windows.DragDrop.DoDragDrop"/> has already returned. The source path is never
/// touched by this class — only observed.
/// </summary>
/// <remarks>
/// Stops re-checking (leaves the item exactly as it was, in the panel) the moment a check finds the
/// path still exists — covering a copy (Ctrl-drag), a cancelled drag (Esc), or a drop that landed
/// somewhere that doesn't actually move the file. Only when EVERY scheduled check finds the path gone
/// is <c>onConfirmedMoved</c> invoked, exactly once.
/// <para>
/// M1 (release-hardening 1.0 review): each <see cref="Schedule"/> call gets its OWN <see cref="IDebounceTimer"/>
/// (one <see cref="Func{IDebounceTimer}"/> call per <see cref="Schedule"/>, not per delayed step within it)
/// — a panel can drag more than one item out in quick succession, and a single SHARED timer reused across
/// overlapping <see cref="Schedule"/> calls would have its still-pending +500/+2000 ms check for the
/// FIRST item silently cancelled the moment <see cref="IDebounceTimer.Start"/> re-armed it for the
/// SECOND item's own delayed check (<see cref="IDebounceTimer.Start"/>'s own "last Start wins" contract)
/// — starving the first item's verification, potentially leaving a moved-out item stuck in the panel.
/// Every timer handed out this way is tracked in <see cref="_activeTimers"/> until its owning
/// <see cref="Schedule"/> chain finishes (confirmed moved, or survived every check), so <see cref="CancelAll"/>
/// can stop every STILL-PENDING one at once (e.g. from <c>ChildWindow.OnClosing</c>, so a delayed callback
/// never fires against a panel that is about to be disposed).
/// </para>
/// </remarks>
public sealed class MovedOutVerifier
{
    /// <summary>
    /// Delay before each successive check, relative to the PREVIOUS check (not to the start) — so the
    /// three checks land at 0 ms, +500 ms, +1500 ms after scheduling began, i.e. 0/500/2000 ms in total.
    /// </summary>
    private static readonly TimeSpan[] Delays =
    [
        TimeSpan.Zero,
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromMilliseconds(1500),
    ];

    private readonly Func<IDebounceTimer?> _timerProvider;
    private readonly Func<string, bool> _pathExists;

    // M1: every timer currently backing a still-in-flight Schedule() chain — see this type's remarks.
    // Removed as soon as its own chain finishes (Check() below, on every exit path), so this only ever
    // holds timers with a genuinely pending delayed check.
    private readonly List<IDebounceTimer> _activeTimers = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="MovedOutVerifier"/> class.
    /// </summary>
    /// <param name="timerProvider">
    /// Resolves a FRESH <see cref="IDebounceTimer"/> for each <see cref="Schedule"/> call (M1 — never a
    /// single instance shared across overlapping gestures; see this type's remarks) — when it returns
    /// <see langword="null"/> (no UI dispatcher available), the corresponding check runs immediately
    /// instead of being silently dropped.
    /// </param>
    /// <param name="pathExists">
    /// Test seam: the existence probe for a scheduled check. Defaults to
    /// <see cref="File.Exists(string)"/> OR <see cref="Directory.Exists(string)"/>.
    /// </param>
    public MovedOutVerifier(Func<IDebounceTimer?> timerProvider, Func<string, bool>? pathExists = null)
    {
        _timerProvider = timerProvider;
        _pathExists = pathExists ?? DefaultPathExists;
    }

    /// <summary>
    /// Schedules the 0/500/2000 ms existence-check sequence for <paramref name="path"/>.
    /// <paramref name="onConfirmedMoved"/> fires exactly once, only if every scheduled check finds the
    /// path gone; a check that finds it still present stops the sequence early without ever calling it.
    /// Independent of, and never cancels, any other in-flight <see cref="Schedule"/> call (M1).
    /// </summary>
    /// <param name="path">The panel item's original (pre-drag) path to watch.</param>
    /// <param name="onConfirmedMoved">Invoked once the path is confirmed gone at every check.</param>
    public void Schedule(string path, Action onConfirmedMoved) => RunCheck(path, stepIndex: 0, onConfirmedMoved, timer: null);

    /// <summary>
    /// Stops and forgets every currently in-flight <see cref="Schedule"/> chain's timer (M1) — used by
    /// <c>ChildWindow.OnClosing</c> so no delayed check can ever fire against a panel that is about to be
    /// disposed. A no-op call site's own <c>onConfirmedMoved</c> callback simply never runs for whichever
    /// chains were still pending; already-finished chains are unaffected (they were never tracked here in
    /// the first place, see <see cref="_activeTimers"/>'s own remarks).
    /// </summary>
    public void CancelAll()
    {
        foreach (var timer in _activeTimers) timer.Stop();
        _activeTimers.Clear();
    }

    private static bool DefaultPathExists(string path) => File.Exists(path) || Directory.Exists(path);

    private void RunCheck(string path, int stepIndex, Action onConfirmedMoved, IDebounceTimer? timer)
    {
        // Runs this step's actual existence probe, given whichever timer (if any) this chain has
        // resolved so far — `usedTimer` is exactly what this step's own Start callback (below) closed
        // over, never the stale outer `timer` parameter, so the correct instance is untracked/reused.
        void Check(IDebounceTimer? usedTimer)
        {
            // This step's own delay (if any) has now elapsed — its timer no longer has a pending
            // callback, whether this chain ends here or continues to the next step below.
            if (usedTimer is not null) _activeTimers.Remove(usedTimer);

            if (!_pathExists(path))
            {
                onConfirmedMoved();
                return;
            }

            var next = stepIndex + 1;
            if (next < Delays.Length) RunCheck(path, next, onConfirmedMoved, usedTimer);

            // else: the path survived every scheduled check — it stays in the panel, no-op.
        }

        var delay = Delays[stepIndex];
        if (delay == TimeSpan.Zero)
        {
            Check(timer);
            return;
        }

        // M1: one timer per Schedule() call, resolved (and tracked) the first time this chain actually
        // needs one — every later step of the SAME chain reuses it (steps within one chain are strictly
        // sequential, never concurrent, so reusing it there is safe); a DIFFERENT, concurrently in-flight
        // Schedule() chain always gets its own fresh instance from _timerProvider.
        var chainTimer = timer ?? _timerProvider();
        if (chainTimer is null)
        {
            Check(null); // no dispatcher available — degrade to an immediate check rather than dropping it.
            return;
        }

        if (!_activeTimers.Contains(chainTimer)) _activeTimers.Add(chainTimer);
        chainTimer.Start(delay, () => Check(chainTimer));
    }
}
