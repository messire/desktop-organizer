using System.Text.Json.Nodes;

namespace DesktopOrganizer.Services.Abstractions.Storage;

/// <summary>
/// A single step in the schema migration chain (ADR-024 D-9), taking a document at
/// <see cref="FromVersion"/> and producing one at <see cref="ToVersion"/>.
/// </summary>
/// <remarks>
/// Operates purely on <see cref="JsonObject"/>, never on today's CLR types (ADR-024 D-1): a
/// migration's job is to read a document written by code that may no longer exist in the current
/// build, so deserializing it into current DTOs first ("deserialize-and-pray") is exactly the
/// failure mode this contract avoids — a renamed or removed CLR property would silently drop data
/// during deserialization, before the migration ever got a chance to see it. Implementations
/// rebuild the JSON structure directly (rename/regroup/retype keys, e.g. unpacking a JSON-string
/// field into a real nested object) and must leave every non-reserved top-level key as payload — see
/// the envelope/payload split in ADR-024 D-5. A migration must never introduce a payload key that
/// collides with a reserved envelope key (<c>schemaVersion</c>, <c>writtenAtUtc</c>,
/// <c>appVersion</c>).
/// </remarks>
public interface ISettingsMigration
{
    /// <summary>The schema version this migration accepts as input.</summary>
    int FromVersion { get; }

    /// <summary>
    /// The schema version this migration produces as output. Always <see cref="FromVersion"/> + 1 —
    /// the migration runner chains single-step migrations in order rather than allowing one
    /// migration to skip versions, so a gap in the chain can be detected deterministically.
    /// </summary>
    int ToVersion { get; }

    /// <summary>
    /// Transforms <paramref name="document"/> (payload keys only, no envelope keys) from
    /// <see cref="FromVersion"/> to <see cref="ToVersion"/>. Should throw rather than return a
    /// partially-transformed document if the input cannot be migrated — the migration runner treats
    /// a thrown exception as "this document could not be migrated" and discards the attempt without
    /// ever writing it to disk (fail-closed).
    /// </summary>
    /// <param name="document">The document at <see cref="FromVersion"/>.</param>
    /// <returns>The document at <see cref="ToVersion"/>.</returns>
    JsonObject Migrate(JsonObject document);
}
