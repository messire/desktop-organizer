namespace DesktopOrganizer.Services.Abstractions.Storage;

/// <summary>
/// A stage in the atomic write protocol used by <see cref="IApplicationStateStore.Save"/>
/// implementations (ADR-024 D-6: serialize to a temp file → fsync → validate by re-parsing →
/// atomic replace of the primary file). Identifies where a save failed, both for the
/// crash-injection test seam implementations use internally to simulate a process death at each
/// step, and for <see cref="StateSaveOutcome.FailureStage"/> reported back to callers of
/// <see cref="IApplicationStateStore.Save"/>.
/// </summary>
/// <remarks>
/// This single enum is deliberately shared between the internal test seam and the public
/// <see cref="StateSaveOutcome"/> contract rather than duplicated into two parallel vocabularies:
/// a test asserting "the writer failed after fsync but before validation" and a test asserting
/// "<see cref="IApplicationStateStore.Save"/> reported failure after fsync but before validation"
/// must be talking about the exact same value, or the two would silently drift apart over time.
/// <para>
/// The order below is the order of the write protocol itself: a real failure is always reported at
/// the stage that was in progress, or had just completed, when the failure occurred — never at a
/// later stage than was actually reached.
/// </para>
/// </remarks>
public enum StateWriteStage
{
    /// <summary>
    /// The payload was serialized and written to the <c>application-state.json.tmp-{guid}</c> file,
    /// before <see cref="System.IO.FileStream.Flush(bool)"/> is called.
    /// </summary>
    AfterTempWrite,

    /// <summary>The temp file was flushed to disk (a real fsync, <c>flushToDisk: true</c>) and its handle closed.</summary>
    AfterFlush,

    /// <summary>
    /// The temp file was re-opened and re-parsed as JSON to confirm it is well-formed and carries a
    /// valid <c>schemaVersion</c> before it is allowed to become the primary file.
    /// </summary>
    AfterValidate,

    /// <summary>
    /// Validation succeeded; about to perform the atomic <see cref="System.IO.File.Replace(string, string, string)"/>
    /// (or <see cref="System.IO.File.Move(string, string)"/> when there is no existing primary file yet).
    /// </summary>
    BeforeReplace,

    /// <summary>
    /// The atomic replace/move onto the primary file completed on disk; the in-memory committed
    /// state (used for the no-op guard) is about to be updated to match.
    /// </summary>
    AfterReplace,
}
