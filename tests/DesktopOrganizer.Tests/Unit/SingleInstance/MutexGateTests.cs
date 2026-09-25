using DesktopOrganizer.Services.SingleInstance;
using DesktopOrganizer.Tests.Resources;

namespace DesktopOrganizer.Tests.Unit.SingleInstance;

/// <summary>
/// Unit tests for <see cref="MutexGate"/> (ADR-023 D-1/R-2/R-5) against real
/// <see cref="System.Threading.Mutex"/> kernel objects — no GUI involved, so these run under
/// <c>dotnet test</c> like any other unit test. Each test uses a unique <see cref="Guid"/>-suffixed
/// name so parallel test runs never collide.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class MutexGateTests
{
    /// <summary>
    /// Verifies that <see cref="MutexGate.TryAcquire"/> succeeds and marks the gate as owned when the
    /// underlying named mutex is free.
    /// </summary>
    [Fact]
    public void TryAcquire_FreeMutex_ReturnsTrueAndOwns()
    {
        using var gate = new MutexGate(UniqueName());

        Assert.True(gate.TryAcquire());
        Assert.True(gate.IsOwned);
    }

    /// <summary>
    /// Verifies that <see cref="MutexGate.TryAcquire"/> returns <see langword="false"/> and leaves the
    /// gate unowned when the underlying named mutex is already held by another thread.
    /// </summary>
    [Fact]
    public void TryAcquire_HeldByAnotherThread_ReturnsFalse()
    {
        var name = UniqueName();
        using var holderReady = new ManualResetEventSlim(false);
        using var releaseHolder = new ManualResetEventSlim(false);

        var holderThread = new Thread(() =>
        {
            using var holderMutex = new Mutex(initiallyOwned: false, name);
            holderMutex.WaitOne();
            holderReady.Set();
            releaseHolder.Wait();
            holderMutex.ReleaseMutex();
        })
        {
            IsBackground = true,
        };
        holderThread.Start();

        try
        {
            Assert.True(holderReady.Wait(TimeSpan.FromSeconds(2)));

            using var gate = new MutexGate(name);
            var acquired = gate.TryAcquire();

            Assert.False(acquired);
            Assert.False(gate.IsOwned);
        }
        finally
        {
            releaseHolder.Set();
            holderThread.Join(TimeSpan.FromSeconds(2));
        }
    }

    /// <summary>
    /// Verifies that <see cref="MutexGate.TryAcquire"/> succeeds, marks the gate as owned, and sets
    /// <see cref="MutexGate.WasAbandoned"/> when the underlying named mutex was abandoned by a thread
    /// that exited without releasing it (the same condition a killed primary process leaves behind).
    /// </summary>
    [Fact]
    public void TryAcquire_AbandonedByAnotherThread_ReturnsTrueAndOwns()
    {
        // A thread that acquires the mutex and exits without releasing is exactly what the OS calls
        // "abandoned" — the same condition a killed primary process leaves behind (ADR-023 R-2).
        var name = UniqueName();
        using var holderAcquired = new ManualResetEventSlim(false);

        // Handle to the holder's *OS* thread, and the holder's own Mutex — both published to this frame
        // through holderAcquired, whose Set/Wait pair also provides the required memory barrier.
        WaitHandle? holderOsThread = null;

        // The named kernel mutant lives only as long as some handle to it is open. If the holder thread's
        // Mutex were the only one, the GC could finalize it the moment the thread exits (its local goes
        // dead), destroying the kernel object — the MutexGate below would then *create a fresh, never
        // abandoned* mutant, acquire it cleanly and let this test pass without exercising the abandoned
        // path at all. Keeping the Mutex rooted in this frame until the assertions are done prevents that.
        Mutex? holderMutex = null;

        var holderThread = new Thread(() =>
        {
            holderOsThread = Kernel32.DuplicateCurrentThreadHandle();
            holderMutex = new Mutex(initiallyOwned: false, name);
            holderMutex.WaitOne();
            holderAcquired.Set();
        })
        {
            IsBackground = true,
        };
        holderThread.Start();

        try
        {
            Assert.True(holderAcquired.Wait(TimeSpan.FromSeconds(2)));

            // Deliberately NOT Thread.Join: Join completes once the *managed* thread has finished and been
            // detached, while the Win32 thread is still unwinding. The kernel abandons a dead thread's
            // mutexes inside KeTerminateThread, i.e. strictly after that point, so a zero-timeout probe
            // fired right after Join can still see the mutex as held-and-not-abandoned and return false.
            // Waiting on the thread's own kernel object is the synchronization point that does guarantee
            // the mutex has been abandoned.
            Assert.True(holderOsThread!.WaitOne(TimeSpan.FromSeconds(5)));

            using var gate = new MutexGate(name);
            var acquired = gate.TryAcquire();

            Assert.True(acquired);
            Assert.True(gate.IsOwned);
            Assert.True(gate.WasAbandoned);
        }
        finally
        {
            holderOsThread?.Dispose();
            holderMutex?.Dispose();
        }
    }

    /// <summary>
    /// Verifies that <see cref="MutexGate.Release"/> does not throw and leaves the gate unowned when
    /// called without a prior successful <see cref="MutexGate.TryAcquire"/>.
    /// </summary>
    [Fact]
    public void Release_NeverAcquired_DoesNotThrow_AndStaysNotOwned()
    {
        using var gate = new MutexGate(UniqueName());

        gate.Release();

        Assert.False(gate.IsOwned);
    }

    /// <summary>
    /// Verifies that calling <see cref="MutexGate.Release"/> twice does not throw and leaves the gate
    /// unowned.
    /// </summary>
    [Fact]
    public void Release_Twice_DoesNotThrow()
    {
        using var gate = new MutexGate(UniqueName());
        gate.TryAcquire();

        gate.Release();
        gate.Release();

        Assert.False(gate.IsOwned);
    }

    /// <summary>
    /// Verifies that calling <see cref="MutexGate.Dispose"/> twice, without ever acquiring the gate,
    /// does not throw.
    /// </summary>
    [Fact]
    public void Dispose_TwiceAndWithoutAcquire_DoesNotThrow()
    {
        var gate = new MutexGate(UniqueName());

        gate.Dispose();
        gate.Dispose();
    }

    private static string UniqueName() => $@"Local\DesktopOrganizer.Test.MutexGate.{Guid.NewGuid():N}";
}
