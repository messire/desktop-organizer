using DesktopOrganizer.Services.Enums;
using DesktopOrganizer.Services.Interfaces;
using DesktopOrganizer.Services.Providers;

namespace DesktopOrganizer.Services.Tools.IconLoading;

/// <summary>
/// Production <see cref="IRemotePathClassifier"/> (ADR-046 D-2, docs/PLANS/background-icon-loading.md
/// §5.2.4): UNC roots by pure string parsing, drive letters through a per-letter cached, time-bounded
/// <see cref="INativeApi.GetDriveKind"/>, and a <c>.lnk</c>'s raw stored target through
/// <see cref="INativeApi.TryGetShortcutRawTarget"/> (no <c>Resolve</c>, no network).
/// </summary>
/// <remarks>
/// <para>
/// <b>Root keys.</b> A UNC root keeps the path's own spelling of <c>\\server\share</c> (the breaker compares
/// ordinal-ignore-case); a drive root is the upper-case letter plus colon (<c>"Z:"</c>). <b>Known limitation
/// (review L5, deliberate):</b> a mapped letter and the UNC path it maps to — <c>Z:\x</c> and
/// <c>\\srv\share\x</c> after <c>net use Z: \\srv\share</c> — are two DIFFERENT circuit-breaker keys, so one
/// dead share can cost one slow call per spelling before both spellings are quarantined. Unifying them would
/// need <c>WNetGetConnection</c>/<c>QueryDosDevice</c> per letter (the former can itself go to the network)
/// for a case that costs at most one extra timeout per spelling per quarantine period.
/// </para>
/// <para>
/// <b>Drive letters (R7-14).</b> <c>GetDriveTypeW</c> on a disconnected or unreachable mapped letter can block
/// on the network. It therefore never runs on the caller's thread: a letter's query runs on its own dedicated
/// background thread (single-flight — at most one query per letter at a time), and the caller waits at most
/// the query bound (<see cref="DefaultDriveKindQueryTimeout"/>) for it. A letter whose query has not answered
/// in time is treated as REMOTE — the hanging query is itself the evidence — so its jobs go to the Remote lane,
/// where the per-root circuit breaker contains the cost, instead of each file on that letter pinning the
/// Local worker until the watchdog abandons it (a few of those disable the Local lane for the session).
/// Until the hanging query returns, later callers get the same "remote" answer without waiting again; its
/// real answer is then cached for <see cref="DriveKindCacheTtl"/>.
/// </para>
/// <para>
/// Only letters positively known to be local — <see cref="DriveKind.Fixed"/>, <see cref="DriveKind.Removable"/>,
/// <see cref="DriveKind.CdRom"/>, <see cref="DriveKind.RamDisk"/> — stay on the Local lane.
/// <see cref="DriveKind.Remote"/>, <see cref="DriveKind.NoRootDirectory"/> (e.g. a persistent mapping that was
/// not restored at logon: touching it makes the shell try to reconnect over the network),
/// <see cref="DriveKind.Unknown"/> and a failing native call are all routed Remote: the Remote lane is only
/// serialized and breaker-guarded, whereas a wrong "local" answer can cost the Local lane a worker.
/// </para>
/// </remarks>
internal sealed class RemotePathClassifier : IRemotePathClassifier
{
    /// <summary>How long a <c>GetDriveTypeW</c> answer is reused for a letter (a drive can be mapped/unmapped at runtime).</summary>
    internal static readonly TimeSpan DriveKindCacheTtl = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long a caller waits for a letter's <c>GetDriveTypeW</c> before treating the letter as remote
    /// (R7-14). A healthy answer takes microseconds; the bound only matters for a hanging query, and each
    /// hang costs it once per caller that arrives while the query is still starting to hang.
    /// </summary>
    internal static readonly TimeSpan DefaultDriveKindQueryTimeout = TimeSpan.FromMilliseconds(250);

    private readonly INativeApi _nativeApi;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _driveKindQueryTimeout;
    private readonly object _gate = new();

    // Guarded by _gate.
    private readonly Dictionary<char, DriveKindEntry> _driveKinds = [];
    private readonly Dictionary<char, Task<DriveKind>> _pendingQueries = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="RemotePathClassifier"/> class on the real clock.
    /// </summary>
    /// <param name="nativeApi">Native façade (<c>GetDriveTypeW</c>, <c>IShellLinkW</c>).</param>
    public RemotePathClassifier(INativeApi nativeApi)
        : this(nativeApi, TimeProvider.System, DefaultDriveKindQueryTimeout)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RemotePathClassifier"/> class with an explicit clock
    /// for the drive-kind cache TTL and an explicit drive-kind query bound (tests).
    /// </summary>
    /// <param name="nativeApi">Native façade.</param>
    /// <param name="timeProvider">Clock for <see cref="DriveKindCacheTtl"/>.</param>
    /// <param name="driveKindQueryTimeout">How long a caller waits for a letter's <c>GetDriveTypeW</c> before treating the letter as remote.</param>
    internal RemotePathClassifier(INativeApi nativeApi, TimeProvider timeProvider, TimeSpan driveKindQueryTimeout)
    {
        _nativeApi = nativeApi;
        _timeProvider = timeProvider;
        _driveKindQueryTimeout = driveKindQueryTimeout;
    }

    /// <inheritdoc />
    public string? GetOwnRemoteRoot(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;

        if (IconProvider.IsUncPath(path)) return TryGetUncRoot(path);

        if (TryGetDriveLetter(path) is not { } letter) return null;

        // "Z:" and the \\srv\share it maps to are two different keys — see the class remarks (L5).
        return IsKnownLocalLetter(letter) ? null : $"{letter}:";
    }

    /// <inheritdoc />
    public string? GetShortcutTargetRemoteRoot(string shortcutPath)
    {
        if (string.IsNullOrEmpty(shortcutPath)) return null;

        string? target;
        try
        {
            if (!_nativeApi.TryGetShortcutRawTarget(shortcutPath, out target)) return null;
        }
        catch (Exception)
        {
            // INativeApi documents "never throws"; a misbehaving implementation must still not fail the job:
            // an unclassifiable link stays on the Local lane, where the watchdog still protects it (R7-15).
            return null;
        }

        return GetOwnRemoteRoot(target.Trim());
    }

    /// <summary>
    /// <c>\\server\share</c> for a UNC path (also <c>//server/share</c>, <c>\\?\UNC\server\share</c> and
    /// <c>\\.\UNC\server\share</c>); <c>\\server</c> when no share segment follows; <see langword="null"/> when
    /// the server segment is empty.
    /// </summary>
    /// <param name="path">A path <see cref="IconProvider.IsUncPath"/> accepted.</param>
    /// <returns>The root key, or <see langword="null"/>.</returns>
    internal static string? TryGetUncRoot(string path)
    {
        var start = path.Length >= 4 && (path[2] == '?' || path[2] == '.') && IsSeparator(path[3])
            ? 8 // "\\?\UNC\" / "\\.\UNC\"
            : 2; // "\\"

        if (start >= path.Length) return null;

        var serverEnd = IndexOfSeparator(path, start);
        var server = serverEnd < 0 ? path[start..] : path[start..serverEnd];
        if (server.Length == 0) return null;

        if (serverEnd < 0) return $@"\\{server}";

        var shareEnd = IndexOfSeparator(path, serverEnd + 1);
        var share = shareEnd < 0 ? path[(serverEnd + 1)..] : path[(serverEnd + 1)..shareEnd];

        return share.Length == 0 ? $@"\\{server}" : $@"\\{server}\{share}";
    }

    /// <summary>
    /// The upper-case drive letter of <c>X:...</c>, <c>\\?\X:...</c> or <c>\\.\X:...</c>; otherwise
    /// <see langword="null"/> (relative paths, volume GUID paths, other device paths).
    /// </summary>
    /// <param name="path">The path to inspect.</param>
    /// <returns>The drive letter, or <see langword="null"/>.</returns>
    internal static char? TryGetDriveLetter(string path)
    {
        if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':') return char.ToUpperInvariant(path[0]);

        if (path.Length >= 6
            && IsSeparator(path[0]) && IsSeparator(path[1])
            && (path[2] == '?' || path[2] == '.') && IsSeparator(path[3])
            && char.IsAsciiLetter(path[4]) && path[5] == ':')
        {
            return char.ToUpperInvariant(path[4]);
        }

        return null;
    }

    /// <summary>
    /// Test seam: the still-running <c>GetDriveTypeW</c> query for <paramref name="letter"/>, or a completed
    /// task when none is pending — lets a test wait for a released query's answer to reach the cache without
    /// sleeping.
    /// </summary>
    /// <param name="letter">Upper-case drive letter.</param>
    /// <returns>The pending query, or a completed task.</returns>
    internal Task GetPendingDriveKindQuery(char letter)
    {
        lock (_gate)
        {
            return _pendingQueries.TryGetValue(letter, out var query) ? query : Task.CompletedTask;
        }
    }

    private static bool IsSeparator(char c) => c is '\\' or '/';

    private static int IndexOfSeparator(string path, int start)
    {
        for (var i = start; i < path.Length; i++)
        {
            if (IsSeparator(path[i])) return i;
        }

        return -1;
    }

    private static bool IsLocalKind(DriveKind kind)
        => kind is DriveKind.Fixed or DriveKind.Removable or DriveKind.CdRom or DriveKind.RamDisk;

    /// <summary>
    /// Whether <paramref name="letter"/> is known — from the cache, or from a query answering within the query
    /// bound — to be a local drive kind. Anything else (remote, no root, unknown, still pending) is not.
    /// </summary>
    /// <param name="letter">Upper-case drive letter.</param>
    /// <returns><see langword="true"/> to keep the job on the Local lane.</returns>
    private bool IsKnownLocalLetter(char letter)
    {
        Task<DriveKind> query;
        lock (_gate)
        {
            if (_driveKinds.TryGetValue(letter, out var cached)
                && _timeProvider.GetElapsedTime(cached.Timestamp) < DriveKindCacheTtl)
            {
                return IsLocalKind(cached.Kind);
            }

            // Single-flight: while a query for this letter is out, every caller shares it.
            if (!_pendingQueries.TryGetValue(letter, out var pending))
            {
                pending = StartDriveKindQuery(letter);
                _pendingQueries[letter] = pending;
            }

            query = pending;
        }

        // Bounded wait, outside the lock. The query body never throws (it catches everything), so Wait only
        // reports "done in time" or "not yet".
        return query.Wait(_driveKindQueryTimeout) && IsLocalKind(query.Result);
    }

    /// <summary>
    /// Starts <c>GetDriveTypeW</c> for <paramref name="letter"/> on a dedicated background thread
    /// (<see cref="TaskCreationOptions.LongRunning"/>): a query hanging on the network must not hold a
    /// thread-pool thread. On return it caches the answer and clears the pending entry.
    /// </summary>
    /// <param name="letter">Upper-case drive letter.</param>
    /// <returns>The running query.</returns>
    private Task<DriveKind> StartDriveKindQuery(char letter)
        => Task.Factory.StartNew(
            () =>
            {
                DriveKind kind;
                try
                {
                    kind = _nativeApi.GetDriveKind($@"{letter}:\");
                }
                catch (Exception)
                {
                    // INativeApi documents "never throws"; if it does anyway the letter is unclassifiable (remote).
                    kind = DriveKind.Unknown;
                }

                lock (_gate)
                {
                    // Unknown is cached too: re-asking every job for a letter the OS cannot classify buys nothing.
                    _driveKinds[letter] = new DriveKindEntry(kind, _timeProvider.GetTimestamp());
                    _pendingQueries.Remove(letter);
                }

                return kind;
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    private readonly record struct DriveKindEntry(DriveKind Kind, long Timestamp);
}
