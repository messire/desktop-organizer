using System.IO;
using DesktopOrganizer.Services.Storage;
using DesktopOrganizer.Tests.Helpers;

namespace DesktopOrganizer.Tests.Unit.Storage;

/// <summary>
/// Unit tests for <see cref="StorageFileLock"/> (ADR-024 D-8, closing ADR-023 R-15). Exercises the
/// real <see cref="FileShare.None"/> collision directly: two <see cref="StorageFileLock"/> instances
/// pointed at the same lock path within this single test process collide exactly the way two OS
/// processes would, since <see cref="FileShare.None"/> is enforced per open handle, not per process —
/// see the type's XML-doc remarks. Uses a real temp directory per test, cleaned up in a
/// <c>finally</c> — same convention as <c>AtomicJsonFileWriterTests</c>/<c>PanelItemStorageServiceTests</c>.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class StorageFileLockTests
{
    // Zero delay: these tests assert the *outcome* of the retry schedule, not its timing, so there is
    // no reason to actually wait out 450 ms of real time per test.
    private static readonly TimeSpan NoDelay = TimeSpan.Zero;

    private static (StateStorePaths Paths, string Root) BuildPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DOrgStorageLock_{Guid.NewGuid():N}");
        return (new StateStorePaths(root), root);
    }

    private static void SafeDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; a still-locked handle from a test failure shouldn't mask the real
            // assertion failure.
        }
    }

    private static StorageFileLock BuildLock(StateStorePaths paths, int maxAttempts = 3) =>
        new(paths, maxAttempts, NoDelay);

    [Fact]
    public void TryAcquire_NoOtherHolder_Succeeds_AndSetsIsHeld()
    {
        var (paths, root) = BuildPaths();
        try
        {
            using var @lock = BuildLock(paths);

            Assert.False(@lock.IsHeld);
            var acquired = @lock.TryAcquire();

            Assert.True(acquired);
            Assert.True(@lock.IsHeld);
            Assert.True(File.Exists(paths.LockFilePath));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void TryAcquire_SecondInstanceSamePath_FailsWhileFirstHolds()
    {
        var (paths, root) = BuildPaths();
        try
        {
            using var first = BuildLock(paths);
            using var second = BuildLock(paths);

            Assert.True(first.TryAcquire());
            var secondAcquired = second.TryAcquire();

            Assert.False(secondAcquired);
            Assert.False(second.IsHeld);
            Assert.True(first.IsHeld, "The original holder must be unaffected by a failed competing acquire.");
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void TryAcquire_AfterFirstDisposes_SecondSucceeds()
    {
        var (paths, root) = BuildPaths();
        try
        {
            var first = BuildLock(paths);
            using var second = BuildLock(paths);

            Assert.True(first.TryAcquire());
            Assert.False(second.TryAcquire());

            first.Dispose();
            var secondAcquiredAfterRelease = second.TryAcquire();

            Assert.True(secondAcquiredAfterRelease);
            Assert.True(second.IsHeld);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Dispose_ReleasesHandle_IsIdempotent()
    {
        var (paths, root) = BuildPaths();
        try
        {
            var @lock = BuildLock(paths);
            Assert.True(@lock.TryAcquire());

            @lock.Dispose();
            Assert.False(@lock.IsHeld);

            // Idempotent: a second Dispose() must not throw.
            var exception = Record.Exception(() => @lock.Dispose());
            Assert.Null(exception);
            Assert.False(@lock.IsHeld);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void TryAcquire_CalledTwiceOnSameHeldInstance_IsIdempotent_ReturnsTrue()
    {
        var (paths, root) = BuildPaths();
        try
        {
            using var @lock = BuildLock(paths);

            Assert.True(@lock.TryAcquire());
            var second = @lock.TryAcquire();

            Assert.True(second);
            Assert.True(@lock.IsHeld);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void TryAcquire_AfterDispose_NeverReacquires_ReturnsFalse()
    {
        var (paths, root) = BuildPaths();
        try
        {
            var @lock = BuildLock(paths);
            Assert.True(@lock.TryAcquire());
            @lock.Dispose();

            var reacquired = @lock.TryAcquire();

            Assert.False(reacquired);
            Assert.False(@lock.IsHeld);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void IsHeld_DefaultsFalse_BeforeAnyAcquireAttempt()
    {
        var (paths, root) = BuildPaths();
        try
        {
            using var @lock = BuildLock(paths);

            Assert.False(@lock.IsHeld);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void TryAcquire_MissingStorageRoot_CreatesDirectoryAndSucceeds()
    {
        var (paths, root) = BuildPaths();
        try
        {
            Assert.False(Directory.Exists(root));
            using var @lock = BuildLock(paths);

            var acquired = @lock.TryAcquire();

            Assert.True(acquired);
            Assert.True(File.Exists(paths.LockFilePath));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }
}
