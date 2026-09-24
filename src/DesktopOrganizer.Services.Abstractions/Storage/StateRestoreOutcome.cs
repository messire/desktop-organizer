namespace DesktopOrganizer.Services.Abstractions.Storage;

/// <summary>
/// Coarse-grained outcome of a single <see cref="IStateRestoreCommand.Restore"/> call (ADR-024 §7,
/// rollback scenario C — the <c>--restore-state-backup</c> command-line switch).
/// </summary>
public enum StateRestoreOutcome
{
    /// <summary>
    /// A candidate (<c>.bak</c>, or failing that the newest <c>application-state.corrupt-*.json</c>)
    /// was found, parsed, migrated to the current schema version, and atomically became the new
    /// primary document. Any previous primary file was quarantined, never deleted.
    /// </summary>
    Restored,

    /// <summary>
    /// Neither a <c>.bak</c> file nor any quarantine file exists under the storage directory —
    /// nothing to restore from. Nothing on disk was touched.
    /// </summary>
    NoCandidateFound,

    /// <summary>
    /// A candidate file was found but failed validation (not valid JSON, no valid
    /// <c>schemaVersion</c>, or a migration step failed/the chain has a gap). Per ADR-024 §7 scenario
    /// C, no destructive action is ever taken without a validated candidate — the on-disk primary
    /// (if any) is left exactly as it was.
    /// </summary>
    CandidateInvalid,

    /// <summary>
    /// The candidate validated successfully, but quarantining the existing bad primary and/or
    /// writing the new one failed (disk full, permissions, held handle). Best-effort: the command
    /// never leaves the storage directory with both an old and a new primary — see
    /// <see cref="IStateRestoreCommand"/> remarks.
    /// </summary>
    Failed,
}
