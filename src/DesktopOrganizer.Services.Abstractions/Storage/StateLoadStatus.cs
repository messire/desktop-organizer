namespace DesktopOrganizer.Services.Abstractions.Storage;

/// <summary>
/// Which rung of the read/recovery ladder (ADR-024 D-7) produced a <see cref="StateLoadResult"/>.
/// The ladder is consulted strictly in the order the members are declared here.
/// </summary>
public enum StateLoadStatus
{
    /// <summary>
    /// The primary file parsed, its <c>schemaVersion</c> was less than or equal to the version this
    /// build understands, and any needed migrations completed successfully.
    /// </summary>
    Loaded,

    /// <summary>
    /// The primary file was missing or unreadable/corrupt, but a valid <c>.bak</c> existed. The
    /// backup was loaded (and migrated, if needed) and the primary was immediately rewritten from
    /// it so a subsequent load doesn't need to fall back again. If the primary was corrupt (as
    /// opposed to merely missing), it was moved to quarantine first — see
    /// <see cref="StateLoadResult.QuarantinePath"/>.
    /// </summary>
    RecoveredFromBackup,

    /// <summary>
    /// Neither the primary nor <c>.bak</c> was usable. Both were moved to quarantine (see
    /// <see cref="StateLoadResult.QuarantinePath"/>) and an empty structural state was returned.
    /// Writing is still allowed — the next <see cref="IApplicationStateStore.Save"/> call starts a
    /// fresh document. Deliberately does <b>not</b> fall back to legacy import (ADR-024 D-7 step 4):
    /// once a JSON document has ever existed, legacy <c>user.config</c> values are frozen and stale,
    /// and resurrecting them would silently replace current state with month-old data.
    /// </summary>
    Quarantined,

    /// <summary>
    /// Neither the primary nor <c>.bak</c> file existed, but the legacy <c>user.config</c> contained
    /// non-default structural state (see <see cref="ILegacyStateImporter"/>). That state was
    /// imported as schema v1 and migrated up to the current schema version. The legacy
    /// <c>user.config</c> keys are left untouched on disk (ADR-024 D-10 — "frozen", not deleted).
    /// </summary>
    ImportedFromLegacy,

    /// <summary>
    /// No JSON store existed and legacy <c>user.config</c> was empty or exactly the application's
    /// own defaults (e.g. a single "default" page, no panels). A brand-new, empty structural state
    /// was returned.
    /// </summary>
    FirstRun,

    /// <summary>
    /// The primary file parsed, but its <c>schemaVersion</c> is <em>greater</em> than the version
    /// this build understands — this build is older than whatever last wrote the file. Per ADR-024
    /// D-7 step 7, the document is neither migrated downward nor touched in any way:
    /// <see cref="StateLoadResult.Payload"/> is an empty structural state (must not be mistaken for
    /// "nothing configured yet" — the real data is still on disk, just not something this build can
    /// safely read), and <see cref="IApplicationStateStore.IsWritable"/> becomes
    /// <see langword="false"/> for the remainder of the process, so every subsequent
    /// <see cref="IApplicationStateStore.Save"/> call returns
    /// <see cref="StateSaveResult.SkippedReadOnly"/> rather than ever overwriting a document this
    /// build cannot fully understand. This is half of the rollback story (ADR-024 §7): running an
    /// older build against a newer on-disk schema must be a safe, inert no-op, never a silent
    /// downgrade of the user's data.
    /// </summary>
    NewerSchema,
}
