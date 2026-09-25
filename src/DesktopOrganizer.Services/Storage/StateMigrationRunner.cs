using System.Text.Json.Nodes;
using DesktopOrganizer.Services.Abstractions.Storage;

namespace DesktopOrganizer.Services.Storage;

/// <summary>
/// Applies the ordered chain of <see cref="ISettingsMigration"/> steps needed to bring a stored
/// document up to the current schema version (ADR-024 D-9).
/// </summary>
/// <remarks>
/// <para>
/// <b>Document shape accepted by <see cref="MigrateToCurrent"/>.</b> Unlike
/// <see cref="ISettingsMigration.Migrate"/> — which, per its own contract, only ever sees payload
/// keys — the <c>document</c> passed to this runner is the <em>full</em> document, envelope keys
/// included: it must carry a top-level integer <c>schemaVersion</c> key (this is how the runner
/// knows where in the chain to start), and may also carry <c>writtenAtUtc</c>/<c>appVersion</c> if
/// the caller has them (both are passed through completely untouched — no migration ever sees or
/// touches them). This runner uses <see cref="StateEnvelope"/> to draw that envelope/payload line —
/// <see cref="StateEnvelope.TryGetSchemaVersion"/> to read the starting version and
/// <see cref="StateEnvelope.StripEnvelope"/> to produce the payload-only document each
/// <see cref="ISettingsMigration.Migrate"/> call actually sees — rather than re-deriving the
/// reserved-key list itself, and re-attaches <c>writtenAtUtc</c>/<c>appVersion</c> (carried through
/// verbatim as the original <see cref="JsonNode"/>s, not re-parsed/re-typed) plus the updated
/// <c>schemaVersion</c> once the chain completes. The split exists so this type can be driven
/// directly off on-disk / golden-fixture documents (always envelope + payload together) while
/// individual <see cref="ISettingsMigration"/> implementations stay completely ignorant of the
/// envelope (ADR-024 D-1 / D-5).
/// </para>
/// <para>
/// <b><c>CurrentSchemaVersion</c>.</b> This runner deliberately takes <c>currentSchemaVersion</c> as
/// a plain parameter on every call rather than hardcoding or duplicating a constant of its own.
/// Callers (<c>JsonApplicationStateStore</c>) pass <see cref="StateEnvelope.CurrentSchemaVersion"/>.
/// </para>
/// </remarks>
internal sealed class StateMigrationRunner
{
    private readonly List<ISettingsMigration> _migrationsByFromVersion;

    /// <summary>
    /// Initializes a new instance of the <see cref="StateMigrationRunner"/> class, sorting
    /// <paramref name="migrations"/> by <see cref="ISettingsMigration.FromVersion"/> and validating
    /// that the chain is contiguous and single-step (each migration's
    /// <see cref="ISettingsMigration.ToVersion"/> is exactly its own <see cref="ISettingsMigration.FromVersion"/>
    /// + 1, and equals the next migration's <see cref="ISettingsMigration.FromVersion"/>). A malformed
    /// chain fails fast here, at DI composition time, rather than surfacing later as a confusing
    /// mid-migration gap — fail-closed, per ADR-024 D-9.
    /// </summary>
    /// <param name="migrations">The unordered set of migration steps to chain together.</param>
    /// <exception cref="InvalidOperationException">
    /// The registered migrations do not form a single, contiguous, gap-free, single-step chain.
    /// </exception>
    public StateMigrationRunner(IEnumerable<ISettingsMigration> migrations)
    {
        ArgumentNullException.ThrowIfNull(migrations);

        _migrationsByFromVersion = migrations.OrderBy(m => m.FromVersion).ToList();

        for (var i = 0; i < _migrationsByFromVersion.Count; i++)
        {
            var current = _migrationsByFromVersion[i];
            if (current.ToVersion != current.FromVersion + 1)
            {
                throw new InvalidOperationException(
                    $"Migration '{current.GetType().Name}' has ToVersion ({current.ToVersion}) that is not " +
                    $"exactly FromVersion + 1 ({current.FromVersion} + 1) — only single-step migrations are " +
                    "supported by this runner.");
            }

            if (i == 0)
            {
                continue;
            }

            var previous = _migrationsByFromVersion[i - 1];
            if (previous.FromVersion == current.FromVersion)
            {
                throw new InvalidOperationException(
                    $"Two migrations are registered for the same FromVersion ({current.FromVersion}): " +
                    $"'{previous.GetType().Name}' and '{current.GetType().Name}'.");
            }

            if (previous.ToVersion != current.FromVersion)
            {
                throw new InvalidOperationException(
                    $"Gap in the migration chain: '{previous.GetType().Name}' produces schema version " +
                    $"{previous.ToVersion}, but no registered migration has FromVersion == {previous.ToVersion} " +
                    $"(the next registered migration, '{current.GetType().Name}', starts at {current.FromVersion}).");
            }
        }
    }

    /// <summary>
    /// Applies however many migrations are needed to bring <paramref name="document"/> from its own
    /// <c>schemaVersion</c> up to <paramref name="currentSchemaVersion"/>.
    /// </summary>
    /// <param name="document">
    /// The full document, envelope keys included — see the type-level remarks for the exact shape
    /// expected.
    /// </param>
    /// <param name="currentSchemaVersion">
    /// The schema version this build understands and writes. See the type-level remarks about
    /// <c>StateEnvelope.CurrentSchemaVersion</c>.
    /// </param>
    /// <returns>
    /// The migrated document (again envelope + payload together, <c>schemaVersion</c> updated to
    /// <paramref name="currentSchemaVersion"/>) and how many migration steps actually ran. Reports
    /// <c>0</c> and returns <paramref name="document"/> unchanged when it is already at
    /// <paramref name="currentSchemaVersion"/> — idempotent; migrations never reapply to a document
    /// already at the current version.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="document"/> has no valid <c>schemaVersion</c>, the chain has a gap at the
    /// version it needs next (fail-closed — never guesses or skips a step), or
    /// <paramref name="document"/>'s <c>schemaVersion</c> is greater than
    /// <paramref name="currentSchemaVersion"/> — a downgrade. The latter is a defensive-only path:
    /// by contract this case is meant to be intercepted upstream, before this runner is ever called,
    /// as <c>StateLoadStatus.NewerSchema</c> (ADR-024 D-7 step 7).
    /// </exception>
    public (JsonObject Document, int MigrationsApplied) MigrateToCurrent(JsonObject document, int currentSchemaVersion)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (!StateEnvelope.TryGetSchemaVersion(document, out var documentVersion))
        {
            throw new InvalidOperationException(
                "Document has no valid top-level 'schemaVersion' key — cannot determine which migrations to apply.");
        }

        if (documentVersion == currentSchemaVersion)
        {
            return (document, 0);
        }

        if (documentVersion > currentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"Document schema version ({documentVersion}) is newer than the current schema version " +
                $"({currentSchemaVersion}) this build understands. This is a defensive-only path: callers " +
                "must intercept this case as StateLoadStatus.NewerSchema before ever calling " +
                "MigrateToCurrent (ADR-024 D-7 step 7) — migrating downward is never attempted here.");
        }

        // Carried through verbatim (not re-parsed/re-typed) — this runner only ever changes
        // schemaVersion; writtenAtUtc/appVersion are none of any migration's business.
        var writtenAtUtc = document["writtenAtUtc"]?.DeepClone();
        var appVersion = document["appVersion"]?.DeepClone();

        var payload = StateEnvelope.StripEnvelope(document);

        var version = documentVersion;
        var migrationsApplied = 0;

        while (version != currentSchemaVersion)
        {
            var migration = _migrationsByFromVersion.FirstOrDefault(m => m.FromVersion == version);
            if (migration is null)
            {
                throw new InvalidOperationException(
                    $"No migration registered for schema version {version} — gap in the migration chain. " +
                    $"Cannot bring the document up to schema version {currentSchemaVersion}.");
            }

            payload = migration.Migrate(payload);
            version = migration.ToVersion;
            migrationsApplied++;
        }

        payload["schemaVersion"] = version;
        if (writtenAtUtc is not null)
        {
            payload["writtenAtUtc"] = writtenAtUtc;
        }

        if (appVersion is not null)
        {
            payload["appVersion"] = appVersion;
        }

        return (payload, migrationsApplied);
    }
}
