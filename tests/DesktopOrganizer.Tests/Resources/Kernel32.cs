using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DesktopOrganizer.Tests.Resources;

/// <summary>
/// Provides access to kernel32.dll functions for testing purposes.
/// This static partial class exists for tests that must synchronize with a real OS object rather than
/// with the CLR's managed projection of it.
/// </summary>
public static partial class Kernel32
{
    /// <summary>
    /// <c>DuplicateHandle</c> option: the duplicate gets the same access rights as the source handle,
    /// making the <c>dwDesiredAccess</c> argument ignored.
    /// </summary>
    private const uint DUPLICATE_SAME_ACCESS = 0x00000002;

    /// <summary>
    /// Returns a real, waitable handle to the OS thread that calls this method.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists:</b> <see cref="Thread.Join()"/> synchronizes with the end of the thread's
    /// <i>managed</i> execution — once the delegate returns and the CLR detaches the thread, the managed
    /// <see cref="Thread"/> reports <see cref="ThreadState.Stopped"/> and <c>Join</c> completes, while the
    /// underlying Win32 thread is still unwinding and has not yet called <c>ExitThread</c>. Kernel state
    /// that is released only at real thread termination — most notably abandoning any mutex the thread
    /// still owns, which the kernel does inside <c>KeTerminateThread</c> just before it signals the thread
    /// object — is therefore <i>not</i> guaranteed to be visible after <c>Join</c> returns. Waiting on the
    /// thread's own kernel object is the only synchronization point that does guarantee it.
    /// </para>
    /// <para>
    /// <c>GetCurrentThread</c> returns a pseudo-handle that is only meaningful on the calling thread, so it
    /// must be duplicated into a real handle before another thread can wait on it.
    /// </para>
    /// </remarks>
    /// <returns>
    /// A <see cref="WaitHandle"/> that becomes signalled when the calling OS thread has terminated. The
    /// caller owns it and must dispose it.
    /// </returns>
    /// <exception cref="InvalidOperationException">The handle could not be duplicated.</exception>
    public static WaitHandle DuplicateCurrentThreadHandle()
    {
        var process = GetCurrentProcess();

        if (!DuplicateHandle(process, GetCurrentThread(), process, out var duplicate, 0, false, DUPLICATE_SAME_ACCESS))
        {
            throw new InvalidOperationException(
                $"DuplicateHandle failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }

        return new OsThreadWaitHandle(new SafeWaitHandle(duplicate, ownsHandle: true));
    }

    /// <summary>
    /// Retrieves a pseudo-handle for the calling thread.
    /// </summary>
    /// <returns>A pseudo-handle, valid only in the context of the calling thread.</returns>
    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentThread();

    /// <summary>
    /// Retrieves a pseudo-handle for the current process.
    /// </summary>
    /// <returns>A pseudo-handle for the current process.</returns>
    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();

    /// <summary>
    /// Duplicates an object handle.
    /// </summary>
    /// <param name="hSourceProcessHandle">A handle to the process with the handle to duplicate.</param>
    /// <param name="hSourceHandle">The handle to duplicate.</param>
    /// <param name="hTargetProcessHandle">A handle to the process that is to receive the duplicate.</param>
    /// <param name="lpTargetHandle">Receives the duplicated handle.</param>
    /// <param name="dwDesiredAccess">Access for the new handle; ignored with <see cref="DUPLICATE_SAME_ACCESS"/>.</param>
    /// <param name="bInheritHandle">Whether the duplicate is inheritable.</param>
    /// <param name="dwOptions">Duplication options.</param>
    /// <returns>
    /// <c>true</c> if the function succeeds; otherwise, <c>false</c>. Use <c>Marshal.GetLastWin32Error</c>
    /// to get more error information.
    /// </returns>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DuplicateHandle(
        IntPtr hSourceProcessHandle,
        IntPtr hSourceHandle,
        IntPtr hTargetProcessHandle,
        out IntPtr lpTargetHandle,
        uint dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
        uint dwOptions);

    /// <summary>
    /// Minimal <see cref="WaitHandle"/> over a kernel thread object — <see cref="WaitHandle"/> itself is
    /// abstract and none of the built-in subclasses model a thread.
    /// </summary>
    private sealed class OsThreadWaitHandle : WaitHandle
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="OsThreadWaitHandle"/> class.
        /// </summary>
        /// <param name="handle">An owned, waitable handle to an OS thread.</param>
        public OsThreadWaitHandle(SafeWaitHandle handle) => SafeWaitHandle = handle;
    }
}
