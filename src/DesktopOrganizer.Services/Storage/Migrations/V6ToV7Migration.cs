using System.Text.Json.Nodes;
using DesktopOrganizer.Services.Abstractions.Storage;

namespace DesktopOrganizer.Services.Storage.Migrations;

/// <summary>
/// Schema v6 -&gt; v7 (ADR-024 Amendment "Additive revisions", docs/PLANS/schema-additive-revision-v7.md §4.1):
/// an <b>identity</b> migration. The payload is byte-for-byte the v6 payload; the major bump exists only
/// to introduce the <c>schemaRevision</c> envelope key and to cut off every revision-unsafe writer
/// (builds that neither round-trip unknown fields nor tolerate unknown enum values) via rung 7.
/// </summary>
/// <remarks>
/// <para>
/// <b>No <c>schemaRevision</c> here.</b> <see cref="ISettingsMigration"/> only sees the payload and must
/// never produce a reserved envelope key; <c>schemaRevision</c> is written by
/// <see cref="StateEnvelope.Wrap"/> when the store persists the migrated document (revision 0).
/// </para>
/// <para>
/// <b>Idempotent by construction</b> (a deep copy of every key, applying it twice yields the same
/// document) and <b>copy-all</b>: unknown top-level keys survive via <see cref="MigrationJson.CloneAllExcept"/>.
/// </para>
/// </remarks>
internal sealed class V6ToV7Migration : ISettingsMigration
{
    /// <inheritdoc />
    public int FromVersion => 6;

    /// <inheritdoc />
    public int ToVersion => 7;

    /// <inheritdoc />
    public JsonObject Migrate(JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);

        return MigrationJson.CloneAllExcept(document);
    }
}
