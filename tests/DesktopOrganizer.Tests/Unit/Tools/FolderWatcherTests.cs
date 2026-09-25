using System.IO;
using DesktopOrganizer.Services.Tools;

namespace DesktopOrganizer.Tests.Unit.Tools;

/// <summary>
/// Unit tests for <see cref="FolderWatcher"/> — filter logic and debounce behaviour.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class FolderWatcherTests
{
    // ── ShouldIgnore ────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="FolderWatcher.ShouldIgnore"/> returns true for a <c>desktop.ini</c> path.
    /// </summary>
    [Fact]
    public void ShouldIgnore_DesktopIni_ReturnsTrue()
    {
        var result = FolderWatcher.ShouldIgnore(@"C:\Temp\desktop.ini");
        Assert.True(result);
    }

    /// <summary>
    /// Verifies that <see cref="FolderWatcher.ShouldIgnore"/> matches <c>desktop.ini</c> case-insensitively.
    /// </summary>
    [Fact]
    public void ShouldIgnore_DesktopIni_CaseInsensitive_ReturnsTrue()
    {
        var result = FolderWatcher.ShouldIgnore(@"C:\Temp\Desktop.INI");
        Assert.True(result);
    }

    /// <summary>
    /// Verifies that <see cref="FolderWatcher.ShouldIgnore"/> returns true for a <c>thumbs.db</c> path.
    /// </summary>
    [Fact]
    public void ShouldIgnore_ThumbsDb_ReturnsTrue()
    {
        var result = FolderWatcher.ShouldIgnore(@"C:\Temp\thumbs.db");
        Assert.True(result);
    }

    /// <summary>
    /// Verifies that <see cref="FolderWatcher.ShouldIgnore"/> matches <c>thumbs.db</c> case-insensitively.
    /// </summary>
    [Fact]
    public void ShouldIgnore_ThumbsDb_CaseInsensitive_ReturnsTrue()
    {
        var result = FolderWatcher.ShouldIgnore(@"C:\Temp\Thumbs.DB");
        Assert.True(result);
    }

    /// <summary>
    /// Verifies that <see cref="FolderWatcher.ShouldIgnore"/> returns true for a file with the hidden
    /// attribute set, using a real temp file so <see cref="File.GetAttributes(string)"/> reflects it.
    /// </summary>
    [Fact]
    public void ShouldIgnore_HiddenFile_ReturnsTrue()
    {
        // Arrange — create a real hidden temp file so GetAttributes works.
        var path = Path.Combine(Path.GetTempPath(), $"do_hidden_{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "hidden");
        File.SetAttributes(path, FileAttributes.Hidden);
        try
        {
            // Act
            var result = FolderWatcher.ShouldIgnore(path);

            // Assert
            Assert.True(result);
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
    }

    /// <summary>
    /// Verifies that <see cref="FolderWatcher.ShouldIgnore"/> returns false for a regular, non-hidden file.
    /// </summary>
    [Fact]
    public void ShouldIgnore_RegularFile_ReturnsFalse()
    {
        // Arrange — create a real non-hidden temp file.
        var path = Path.Combine(Path.GetTempPath(), $"do_regular_{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "regular");
        try
        {
            // Act
            var result = FolderWatcher.ShouldIgnore(path);

            // Assert
            Assert.False(result);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Verifies that <see cref="FolderWatcher.ShouldIgnore"/> returns false for a path that no longer
    /// exists on disk, so a Deleted event is never suppressed.
    /// </summary>
    [Fact]
    public void ShouldIgnore_DeletedFile_ReturnsFalse()
    {
        // A file that no longer exists should not be suppressed (Deleted event).
        var path = @"C:\NonExistentPath\ghost_file.txt";
        var result = FolderWatcher.ShouldIgnore(path);
        Assert.False(result);
    }

    // ── Debounce — integration tests with real FileSystemWatcher ────────────────

    /// <summary>
    /// Verifies that <see cref="FolderWatcher.Changed"/> fires after the debounce window once a real
    /// file system change occurs, against a real <see cref="FileSystemWatcher"/>.
    /// </summary>
    [Fact]
    public void FolderChanged_IsRaised_AfterDebounceWindow()
    {
        // Arrange
        var tempDir = CreateTempDirectory();
        try
        {
            using var watcher = new FolderWatcher();
            var raised = new ManualResetEventSlim(false);
            watcher.Changed += (_, _) => raised.Set();

            watcher.Start(tempDir);

            // Act — create a file to trigger FSW
            File.WriteAllText(Path.Combine(tempDir, "file.txt"), "hello");

            // Assert — event must arrive within 2 seconds (debounce is ~300 ms)
            var fired = raised.Wait(TimeSpan.FromSeconds(2));
            Assert.True(fired, "Changed event was not raised within the timeout.");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>
    /// Verifies that several rapid file system changes within the debounce window collapse into
    /// exactly one <see cref="FolderWatcher.Changed"/> event.
    /// </summary>
    [Fact]
    public void FolderChanged_NotRaised_DuringDebounceWindow()
    {
        // Arrange
        var tempDir = CreateTempDirectory();
        try
        {
            using var watcher = new FolderWatcher();
            var eventCount = 0;
            var firstFired = new ManualResetEventSlim(false);

            watcher.Changed += (_, _) =>
            {
                Interlocked.Increment(ref eventCount);
                firstFired.Set();
            };

            watcher.Start(tempDir);

            // Act — fire several FSW events within the 300 ms debounce window
            for (int i = 0; i < 5; i++)
            {
                File.WriteAllText(Path.Combine(tempDir, $"file_{i}.txt"), "x");
                Thread.Sleep(30); // well within debounce
            }

            // Wait for the debounced event to fire
            firstFired.Wait(TimeSpan.FromSeconds(2));

            // Give extra time to ensure no additional events arrive
            Thread.Sleep(600);

            // Assert — all rapid events should have been collapsed into exactly one
            Assert.Equal(1, eventCount);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // ── OnDebounceElapsed subscriber isolation (Task 2, sta-test-conventions.md "known remaining hazards") ──

    /// <summary>
    /// Verifies that a <see cref="FolderWatcher.Changed"/> subscriber that throws never crashes the
    /// timer callback thread or the process, and that a subsequent, separate debounce window still
    /// delivers the event to other, well-behaved subscribers.
    /// </summary>
    [Fact]
    public void Changed_SubscriberThrows_TimerKeepsRunning_NextEventStillDelivered()
    {
        // Arrange
        var tempDir = CreateTempDirectory();
        try
        {
            using var watcher = new FolderWatcher();
            var goodSubscriberCalls = 0;
            var secondCallSignal = new ManualResetEventSlim(false);

            // A throwing subscriber used to be an unhandled exception on the Timer callback thread —
            // which would have killed this entire test process, not just failed this one test. If this
            // regresses, the whole run aborts rather than this Assert failing cleanly.
            watcher.Changed += (_, _) => throw new InvalidOperationException(
                "Simulated subscriber bug — must not crash the process or stop delivery to other subscribers.");

            watcher.Changed += (_, _) =>
            {
                if (Interlocked.Increment(ref goodSubscriberCalls) >= 2)
                    secondCallSignal.Set();
            };

            watcher.Start(tempDir);

            // Act — two changes far enough apart to land in two separate debounce windows (debounce is
            // 300ms; sleeping comfortably past it mirrors FolderChanged_NotRaised_DuringDebounceWindow's
            // own convention above).
            File.WriteAllText(Path.Combine(tempDir, "first.txt"), "a");
            Thread.Sleep(500);
            File.WriteAllText(Path.Combine(tempDir, "second.txt"), "b");

            // Assert
            Assert.True(secondCallSignal.Wait(TimeSpan.FromSeconds(5)), "The well-behaved subscriber never received the second event — the throwing subscriber may have stopped the debounce timer or the watcher itself.");
            Assert.Equal(2, Volatile.Read(ref goodSubscriberCalls));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>
    /// Verifies that <see cref="FolderWatcher.Stop"/> can acquire its internal lock immediately and
    /// return promptly even while a <see cref="FolderWatcher.Changed"/> subscriber is still blocking on
    /// the timer-callback thread, rather than deadlocking against it.
    /// </summary>
    [Fact]
    public void Stop_WhileSubscriberIsStillRunningOnTheTimerThread_DoesNotDeadlock()
    {
        // Arrange
        var tempDir = CreateTempDirectory();
        using var releaseHandler = new ManualResetEventSlim(false);
        try
        {
            using var watcher = new FolderWatcher();
            var handlerEntered = new ManualResetEventSlim(false);

            // Stands in for ChildWindowViewModel.OnFolderChanged's UiDispatch.TryInvoke: a subscriber
            // that blocks the timer-callback thread until some other thread (in production, the UI
            // thread's dispatcher) lets it proceed.
            watcher.Changed += (_, _) =>
            {
                handlerEntered.Set();
                releaseHandler.Wait(TimeSpan.FromSeconds(5));
            };

            watcher.Start(tempDir);
            File.WriteAllText(Path.Combine(tempDir, "trigger.txt"), "x");

            Assert.True(handlerEntered.Wait(TimeSpan.FromSeconds(5)), "Changed handler never ran.");

            // Act — Stop() must be able to take _debounceLock immediately: with the pre-fix code (the
            // subscriber invoked from inside `lock (_debounceLock)`), this thread would never signal
            // completion within the timeout because Stop() would block on the same lock the timer
            // thread still holds while stuck in the subscriber above. A raw Thread + signal (rather than
            // Task.Run().Wait()) avoids blocking on a Task in a test method (xUnit1031).
            var stopCompleted = new ManualResetEventSlim(false);
            new Thread(() =>
            {
                watcher.Stop();
                stopCompleted.Set();
            })
            { IsBackground = true }.Start();

            var completedInTime = stopCompleted.Wait(TimeSpan.FromSeconds(2));

            // Assert
            Assert.True(completedInTime, "Stop() did not return within 2s — it deadlocked against a subscriber still running on the timer thread.");
        }
        finally
        {
            // Unblock the still-parked timer-thread subscriber immediately instead of letting it idle
            // out its own 5s Wait budget after this test has already moved on.
            releaseHandler.Set();
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    private static string CreateTempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"do_fw_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
