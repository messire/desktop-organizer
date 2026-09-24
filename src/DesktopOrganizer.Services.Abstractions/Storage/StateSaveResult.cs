namespace DesktopOrganizer.Services.Abstractions.Storage;

/// <summary>
/// Coarse-grained outcome of a single <see cref="IApplicationStateStore.Save"/> call. Always
/// returned wrapped in a <see cref="StateSaveOutcome"/>, since a plain enum cannot also carry
/// <em>where</em> a <see cref="Failed"/> attempt failed — see <see cref="StateSaveOutcome"/>.
/// </summary>
public enum StateSaveResult
{
    /// <summary>The payload was serialized, flushed, validated, and atomically became the new committed primary file.</summary>
    Saved,

    /// <summary>
    /// The serialized payload is byte-for-byte identical to what is already committed (the no-op
    /// guard carried over from <c>WindowManager._lastSavedJson</c>, ADR-024 §0.4) — nothing was
    /// written to disk.
    /// </summary>
    SkippedNoChange,

    /// <summary>
    /// <see cref="IApplicationStateStore.IsWritable"/> is <see langword="false"/> — either the
    /// advisory storage lock could not be acquired for this process (<see cref="IStorageLock"/>),
    /// or a prior <see cref="IApplicationStateStore.Load"/> returned
    /// <see cref="StateLoadStatus.NewerSchema"/> and writing has been disabled for the rest of the
    /// session. The store is in a degraded, read-only session and never attempted to write.
    /// </summary>
    SkippedReadOnly,

    /// <summary>
    /// Rejected by the write barrier: <see cref="IApplicationStateStore.Save"/> was called before
    /// the first successful <see cref="IApplicationStateStore.Load"/> completed in this process. See
    /// the write-barrier invariant documented on <see cref="IApplicationStateStore"/>.
    /// </summary>
    SkippedBeforeLoad,

    /// <summary>
    /// The write protocol was attempted but did not complete successfully. See
    /// <see cref="StateSaveOutcome.FailureStage"/> for where in the protocol it failed. Committed
    /// state on disk and in memory is guaranteed unchanged — see the remarks on
    /// <see cref="IApplicationStateStore.Save"/>.
    /// </summary>
    Failed,
}
