using DesktopOrganizer.UI.Services;

namespace DesktopOrganizer.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="SaveScheduler"/> — the debounce/flush primitive backing
/// <see cref="WindowManager.SaveWindowsState"/>/<see cref="WindowManager.SaveWindowsStateNow"/>
/// (Phase 7 / Block C, docs/PLANS/phase-7-performance-hardening.md).
/// </summary>
/// <remarks>
/// Uses <see cref="FakeDebounceTimer"/> instead of a real <see cref="System.Windows.Threading.DispatcherTimer"/>
/// so the deferred callback can be triggered deterministically — no timing/races involved.
/// </remarks>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class SaveSchedulerTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(500);

    [Fact]
    public void Request_MultipleQuickCalls_DoNotSaveUntilTimerFires()
    {
        // Arrange
        var timer = new FakeDebounceTimer();
        var saveCount = 0;
        var scheduler = new SaveScheduler(() => saveCount++, () => timer, Window);

        // Act — a burst of calls within the debounce window.
        scheduler.Request();
        scheduler.Request();
        scheduler.Request();

        // Assert — nothing has actually saved yet; only the timer has been (re)armed.
        Assert.Equal(0, saveCount);
        Assert.Equal(3, timer.StartCount);
    }

    [Fact]
    public void Request_MultipleQuickCalls_ResultInExactlyOneSave_WhenTimerFinallyFires()
    {
        // Arrange
        var timer = new FakeDebounceTimer();
        var saveCount = 0;
        var scheduler = new SaveScheduler(() => saveCount++, () => timer, Window);

        // Act — burst of calls collapses into whatever callback the timer holds last, then that
        // single callback fires once (simulating the debounce window finally elapsing).
        scheduler.Request();
        scheduler.Request();
        scheduler.Request();
        timer.Fire();

        // Assert
        Assert.Equal(1, saveCount);
    }

    [Fact]
    public void Request_NoTimerAvailable_SavesImmediatelyAndSynchronously()
    {
        // Arrange — timerProvider returning null simulates no UI dispatcher being available
        // (e.g. Application.Current is null in a headless host, or shutdown already tore it down).
        var saveCount = 0;
        var scheduler = new SaveScheduler(() => saveCount++, () => null, Window);

        // Act
        scheduler.Request();

        // Assert — degrades to an immediate synchronous save instead of silently losing the request.
        Assert.Equal(1, saveCount);
    }

    [Fact]
    public void Flush_CancelsPendingTimer_AndSavesImmediately()
    {
        // Arrange
        var timer = new FakeDebounceTimer();
        var saveCount = 0;
        var scheduler = new SaveScheduler(() => saveCount++, () => timer, Window);
        scheduler.Request();
        Assert.Equal(0, saveCount); // sanity: nothing saved yet

        // Act
        scheduler.Flush();

        // Assert
        Assert.Equal(1, saveCount);
        Assert.Equal(1, timer.StopCount);
    }

    [Fact]
    public void Flush_CancelsPendingTimer_SoALateTimerFireDoesNotDoubleSave()
    {
        // Arrange
        var timer = new FakeDebounceTimer();
        var saveCount = 0;
        var scheduler = new SaveScheduler(() => saveCount++, () => timer, Window);
        scheduler.Request();

        // Act
        scheduler.Flush();
        timer.Fire(); // simulates a real DispatcherTimer tick that was already queued before Stop()

        // Assert — Flush's own synchronous save is the only one; the (cancelled) timer callback
        // was cleared by Stop() and does nothing when "fired" afterwards.
        Assert.Equal(1, saveCount);
    }

    [Fact]
    public void Flush_NoPendingRequest_StillSavesSynchronously()
    {
        // Arrange — Flush must be safe to call even when nothing is pending (e.g. app exit with no
        // prior debounced request in flight).
        var timer = new FakeDebounceTimer();
        var saveCount = 0;
        var scheduler = new SaveScheduler(() => saveCount++, () => timer, Window);

        // Act
        scheduler.Flush();

        // Assert
        Assert.Equal(1, saveCount);
    }

    [Fact]
    public void Flush_NoTimerAvailable_SavesImmediately_DoesNotThrow()
    {
        // Arrange
        var saveCount = 0;
        var scheduler = new SaveScheduler(() => saveCount++, () => null, Window);

        // Act
        var exception = Record.Exception(scheduler.Flush);

        // Assert
        Assert.Null(exception);
        Assert.Equal(1, saveCount);
    }

    /// <summary>
    /// Minimal <see cref="IDebounceTimer"/> test double: mirrors the real
    /// <see cref="DispatcherDebounceTimer"/>'s "last Start() wins" semantics (each call replaces the
    /// previously-armed callback rather than queuing a second one) without any actual timing.
    /// </summary>
    private sealed class FakeDebounceTimer : IDebounceTimer
    {
        private Action? _callback;

        public int StartCount { get; private set; }

        public int StopCount { get; private set; }

        public void Start(TimeSpan window, Action callback)
        {
            StartCount++;
            _callback = callback;
        }

        public void Stop()
        {
            StopCount++;
            _callback = null;
        }

        /// <summary>Simulates the debounce window elapsing: invokes and clears the armed callback, if any.</summary>
        public void Fire()
        {
            var callback = _callback;
            _callback = null;
            callback?.Invoke();
        }
    }
}
