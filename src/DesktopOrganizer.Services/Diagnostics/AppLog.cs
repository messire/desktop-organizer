using DesktopOrganizer.Services.Abstractions;

namespace DesktopOrganizer.Services.Diagnostics;

/// <summary>
/// Process-wide ambient access point for <see cref="IAppLog"/> (ADR-033 part 1), for the two kinds of
/// call site that cannot receive it through constructor injection: static helpers, and code that runs
/// before <c>Program.Main</c> has built the DI container at all. Every DI-resolvable class should prefer
/// an optional <c>IAppLog? appLog = null</c> constructor parameter with a <c>_appLog ?? AppLog.Current</c>
/// fallback instead of reaching for this directly — see the package-B plan's ctor-injection rule.
/// </summary>
/// <remarks>
/// <see cref="Current"/> defaults to <see cref="NullAppLog.Instance"/> until <see cref="Initialize"/> is
/// called exactly once, from <c>Program.Main</c>, with the real DI-resolved <see cref="IAppLog"/> — itself
/// a thin wrapper that only materializes the underlying JSONL sink on its first actual write (ADR-023
/// R-12): merely calling <see cref="Initialize"/> here never touches the file system.
/// </remarks>
public static class AppLog
{
    private static IAppLog _current = NullAppLog.Instance;
    private static int _initialized;

    /// <summary>
    /// The current process-wide <see cref="IAppLog"/> — <see cref="NullAppLog.Instance"/> until
    /// <see cref="Initialize"/> has run.
    /// </summary>
    public static IAppLog Current => Volatile.Read(ref _current);

    /// <summary>
    /// Sets <see cref="Current"/> to <paramref name="log"/>. Only the first call has any effect — every
    /// subsequent call (e.g. a future refactor that accidentally calls this twice) is a silent no-op, so
    /// a late/duplicate call can never swap out the log an earlier part of startup is already using.
    /// </summary>
    /// <param name="log">The real, DI-resolved <see cref="IAppLog"/> to install.</param>
    public static void Initialize(IAppLog log)
    {
        ArgumentNullException.ThrowIfNull(log);

        if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
        Volatile.Write(ref _current, log);
    }

    /// <summary>
    /// Test-only: resets <see cref="Current"/> back to <see cref="NullAppLog.Instance"/> and clears the
    /// one-time <see cref="Initialize"/> guard. This type is a process-wide static, so any test that
    /// calls <see cref="Initialize"/> must call this afterwards (e.g. in a <c>finally</c> block) — without
    /// it, that test would permanently change what every other test in the same run observes from
    /// <see cref="Current"/>, since <see cref="Initialize"/>'s "first call wins" contract has no other way
    /// to be undone.
    /// </summary>
    internal static void ResetForTests()
    {
        Volatile.Write(ref _current, NullAppLog.Instance);
        Volatile.Write(ref _initialized, 0);
    }
}
