using System.Text.Json.Nodes;

namespace DesktopOrganizer.Services.Abstractions.Storage;

/// <summary>
/// Owns the single on-disk application-state document (the versioned JSON store, ADR-024) that
/// replaces the four independent, ad hoc writers that used to each serialize a slice of structural
/// state into <c>user.config</c> — panels, pages, layout snapshots, and the system icons panel (see
/// ADR-024 §0.1). All four now share one document, one atomic write protocol, and one recovery
/// ladder on read, instead of four different JSON dialects inside one XML container.
/// </summary>
/// <remarks>
/// <b>Payload shape is <see cref="JsonObject"/>, never a CLR type.</b> Migrations and this store
/// operate purely on JSON (ADR-024 D-1) — deserializing a legacy or older-schema document straight
/// into today's CLR types ("deserialize-and-pray") is exactly the failure mode this contract exists
/// to avoid, since the code that wrote an older document may no longer exist and a renamed/removed
/// property would silently drop data during deserialization, before a migration ever got a chance
/// to see it. Typed access happens exactly once, at the UI layer, after <see cref="Load"/> has
/// returned an already-migrated, current-schema-version payload.
/// <para>
/// <b>Single load per process, executable write barrier (ADR-024 D-11).</b> <see cref="Load"/> is
/// meant to be called exactly once per process, before any component capable of calling
/// <see cref="Save"/> exists (see the intended <c>Program.Main</c> ordering in ADR-024 D-11).
/// Implementations must reject any <see cref="Save"/> call that happens before the first successful
/// <see cref="Load"/> with <see cref="StateSaveResult.SkippedBeforeLoad"/> rather than silently
/// succeeding. This barrier is an executable invariant, not just documentation: a save that slips in
/// before load completes would silently overwrite a just-migrated document with an empty or stale
/// one — the single biggest risk this phase introduces (ADR-024 R-3).
/// </para>
/// <para>
/// <b>Synchronous by design (ADR-024 D-2).</b> This contract is intentionally not
/// <see cref="System.Threading.Tasks.Task"/>-based: <c>App.OnExit</c> must finish writing before the
/// DI container is disposed, and an async API there would only become sync-over-async on the one
/// path where losing data matters most. "Don't freeze the UI" is already solved by the debounce in
/// front of <see cref="Save"/> (ADR-012's <c>SaveScheduler</c>), not by making the I/O itself async.
/// </para>
/// </remarks>
public interface IApplicationStateStore
{
    /// <summary>
    /// Loads the current application-state document, running the full recovery ladder (ADR-024 D-7:
    /// primary → backup → quarantine → legacy import → first run) and the migration chain up to the
    /// current schema version. Never throws; every failure mode is represented by a
    /// <see cref="StateLoadStatus"/> instead of an exception. Must be called, and complete, before
    /// the first call to <see cref="Save"/> in this process — see the write-barrier remarks on this
    /// type.
    /// </summary>
    StateLoadResult Load();

    /// <summary>
    /// Wraps <paramref name="payload"/> in the current-schema-version envelope (schema version,
    /// write timestamp, app version) and attempts to make it the new committed state via the atomic
    /// write protocol (ADR-024 D-6: temp file → fsync → validate by re-parsing → atomic replace of
    /// the primary file, with the previous primary preserved as <c>.bak</c>).
    /// </summary>
    /// <remarks>
    /// <b>A failed write must never move the committed state.</b> If any step of the write protocol
    /// fails, the in-memory "last successfully committed" snapshot — used both for the no-op guard
    /// (<see cref="StateSaveResult.SkippedNoChange"/>) and as the baseline the next call diffs
    /// against — must be left exactly as it was before this call, never updated to reflect a payload
    /// that failed to land on disk. Updating it optimistically would make the loss permanent: the
    /// next <see cref="Save"/> call would see "nothing changed since last commit" and skip writing
    /// entirely, even though nothing was ever actually written. This is the same class of bug
    /// previously seen in <c>PanelItemStorageService</c>'s manifest cache
    /// (<c>EnsureManifestCacheUnlocked</c>/<c>WriteManifestUnlocked</c>) and must not be reintroduced
    /// here.
    /// </remarks>
    /// <param name="payload">
    /// The current-schema-version structural state as plain payload keys — no envelope keys.
    /// Reserved envelope keys (<c>schemaVersion</c>, <c>writtenAtUtc</c>, <c>appVersion</c>) are
    /// added by the implementation and must not appear as top-level keys in <paramref name="payload"/>.
    /// </param>
    StateSaveOutcome Save(JsonObject payload);

    /// <summary>
    /// Whether this store can currently write. <see langword="false"/> when either the advisory
    /// storage lock could not be acquired for this process (<see cref="IStorageLock"/>) or a prior
    /// <see cref="Load"/> call returned <see cref="StateLoadStatus.NewerSchema"/> and writing has
    /// been disabled for the rest of the session. While <see langword="false"/>, every
    /// <see cref="Save"/> call returns <see cref="StateSaveResult.SkippedReadOnly"/> without touching
    /// disk. <see cref="Load"/> itself is unaffected by this flag and always attempts to read.
    /// </summary>
    bool IsWritable { get; }

    /// <summary>
    /// Preserves the current committed primary document by copying it (not moving it — the primary
    /// stays exactly as it is and remains the committed state) to a timestamped quarantine file
    /// (<c>application-state.corrupt-{ts}.json</c>), before the caller's very next <see cref="Save"/>
    /// call could overwrite it with a document that reflects less than everything <see cref="Load"/>
    /// handed back (ADR-024 finalization C-1). The store-level quarantine performed as part of
    /// <see cref="Load"/>'s recovery ladder only fires when the on-disk document fails to
    /// <em>parse</em> — it has no way to know that a caller successfully parsed/migrated the document
    /// and then, at the UI layer, silently dropped part of it (e.g.
    /// <c>WindowManager.LoadWindowsState</c> skipping a panel whose construction threw). This method
    /// is that missing signal.
    /// </summary>
    /// <remarks>
    /// Idempotent per successful <see cref="Load"/> call: only the first call after a given
    /// <see cref="Load"/> actually copies anything — later calls (even with a different
    /// <paramref name="reason"/>) are no-ops, since the only snapshot worth preserving is the very
    /// first, most-complete one, taken before anything has had a chance to overwrite the primary. A
    /// no-op (never throws) if <see cref="Load"/> has not yet completed in this process, or if no
    /// primary file exists yet to preserve (e.g. a fresh install, still <c>FirstRun</c>).
    /// </remarks>
    /// <param name="reason">
    /// A short, closed-set diagnostic token describing why the caller believes the document it's
    /// about to persist reflects less than the full document <see cref="Load"/> returned (e.g.
    /// <c>"PartialPanelLoad"</c>). Recorded verbatim on the <c>StateQuarantined</c> diagnostics event.
    /// </param>
    void QuarantineLoadedDocument(string reason);
}
