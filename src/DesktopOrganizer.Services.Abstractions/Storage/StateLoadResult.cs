using System.Text.Json.Nodes;

namespace DesktopOrganizer.Services.Abstractions.Storage;

/// <summary>
/// Result of a single <see cref="IApplicationStateStore.Load"/> call.
/// </summary>
/// <param name="Status">Which rung of the recovery ladder (ADR-024 D-7) produced this result.</param>
/// <param name="Payload">
/// The structural state as plain payload keys — envelope keys (<c>schemaVersion</c>,
/// <c>writtenAtUtc</c>, <c>appVersion</c>) are stripped — already migrated up to the current schema
/// version, <b>except</b> when <paramref name="Status"/> is
/// <see cref="StateLoadStatus.NewerSchema"/>, in which case this is an empty structural state; see
/// the remarks on that member for why it must not be treated as "nothing configured yet".
/// </param>
/// <param name="SchemaVersionOnDisk">
/// The <c>schemaVersion</c> found in the document that was actually loaded, before migration. <c>0</c>
/// when nothing existed on disk (<see cref="StateLoadStatus.FirstRun"/>); <c>1</c> when the payload
/// came from <see cref="ILegacyStateImporter"/> (<see cref="StateLoadStatus.ImportedFromLegacy"/>
/// always starts from the synthetic legacy schema version, 1).
/// </param>
/// <param name="MigrationsApplied">
/// How many <see cref="ISettingsMigration"/> steps ran to bring the document up to the current
/// schema version. <c>0</c> when the document was already at the current version — in particular, a
/// second <see cref="IApplicationStateStore.Load"/> against an already-migrated file must report
/// <c>0</c> here; migrations never reapply to a document already at the current version.
/// </param>
/// <param name="QuarantinePath">
/// The path a corrupt file was moved to under <c>application-state.corrupt-{yyyyMMdd-HHmmss}.json</c>,
/// when <paramref name="Status"/> is <see cref="StateLoadStatus.RecoveredFromBackup"/> (the bad
/// primary was quarantined) or <see cref="StateLoadStatus.Quarantined"/> (primary and/or backup
/// quarantined). <see langword="null"/> for every other status.
/// </param>
public sealed record StateLoadResult(
    StateLoadStatus Status,
    JsonObject Payload,
    int SchemaVersionOnDisk,
    int MigrationsApplied,
    string? QuarantinePath);
