using System.Text.Json.Nodes;

namespace DesktopOrganizer.Services.Storage;

/// <summary>
/// Reads and writes the four reserved envelope keys of the versioned JSON application-state
/// document (ADR-024 D-5: <c>schemaVersion</c>, <c>writtenAtUtc</c>, <c>appVersion</c>, and since v7
/// <c>schemaRevision</c> — see the Additive revisions amendment), and draws
/// the line between those and everything else, which is payload. Every other component in
/// <c>Storage</c> — <see cref="AtomicJsonFileWriter"/>, <see cref="JsonApplicationStateStore"/>, the
/// migration chain (ADR-024 D-9) — goes through this type rather than hard-coding the reserved key
/// names a second time.
/// </summary>
/// <remarks>
/// The envelope is flat, not nested (ADR-024 D-5): reserved keys sit at the same top level as
/// payload keys in the on-disk document, e.g. <c>{"schemaVersion":3,"writtenAtUtc":"...","appVersion":"...","panels":[],...}</c>.
/// A migration (<see cref="Abstractions.Storage.ISettingsMigration"/>) must never introduce a
/// payload key that collides with a reserved one — see <see cref="IsReservedKey"/>, which the
/// planned <c>SchemaContractTests</c> asserts against directly.
/// </remarks>
internal static class StateEnvelope
{
    /// <summary>
    /// The schema version this build writes and fully understands. Golden fixtures and migrations
    /// (ADR-024 D-9) exist to bring any older on-disk document up to exactly this version; a document
    /// newer than this is never migrated downward (D-7 rung 7).
    /// </summary>
    /// <remarks>
    /// v6 (ADR-043, docs/PLANS/page-display-names.md): <c>pages</c> gains a stable page id + separate
    /// display <c>name</c> per page (<c>pages.items[{id,name?}]</c> replaces <c>pages.ids[]</c>) — see
    /// <see cref="Migrations.V5ToV6Migration"/>. Purely a <c>pages</c>-section reshape; every other
    /// payload key (<c>panels</c>/<c>layoutSnapshots</c>/<c>systemIconsPanel</c>), including every
    /// <c>pageId</c> reference inside them, is unaffected.
    /// <para>
    /// v7 (ADR-024 Amendment "Additive revisions", docs/PLANS/schema-additive-revision-v7.md): the payload
    /// is identical to v6 (<see cref="Migrations.V6ToV7Migration"/> is an identity migration). The bump is the
    /// "line in the sand" that introduces <c>schemaRevision</c> and the unknown-field round-trip: every build
    /// able to write v7 is revision-safe, every older (revision-unsafe) writer is cut off by rung 7.
    /// </para>
    /// </remarks>
    public const int CurrentSchemaVersion = 7;

    /// <summary>
    /// The additive revision (minor) this build writes and fully understands within
    /// <see cref="CurrentSchemaVersion"/>. A document with the same major but a higher revision is opened
    /// writable (unknown fields are preserved on save); the revision written back is
    /// <c>max(revisionOnDisk, CurrentSchemaRevision)</c> — a monotonic high-water mark.
    /// </summary>
    public const int CurrentSchemaRevision = 0;

    /// <summary>
    /// The synthetic schema version legacy <c>user.config</c> data is treated as being "at" once
    /// imported (ADR-024 D-9 — v1 is a literal transcription of the four legacy writers, D-7 rung 5).
    /// </summary>
    public const int LegacySchemaVersion = 1;

    private const string SchemaVersionKey = "schemaVersion";
    private const string WrittenAtUtcKey = "writtenAtUtc";
    private const string AppVersionKey = "appVersion";
    private const string SchemaRevisionKey = "schemaRevision";

    private static readonly string[] ReservedKeysArray = { SchemaVersionKey, WrittenAtUtcKey, AppVersionKey, SchemaRevisionKey };

    /// <summary>The reserved envelope key names, in no particular order.</summary>
    public static IReadOnlyCollection<string> ReservedKeys => ReservedKeysArray;

    /// <summary>Whether <paramref name="key"/> is one of the four reserved envelope keys.</summary>
    /// <param name="key">The key to test.</param>
    public static bool IsReservedKey(string key) =>
        key is SchemaVersionKey or WrittenAtUtcKey or AppVersionKey or SchemaRevisionKey;

    /// <summary>
    /// Wraps <paramref name="payload"/> (payload keys only — must not contain any reserved key; see
    /// <see cref="IsReservedKey"/>) in a fresh envelope carrying <paramref name="schemaVersion"/>,
    /// <paramref name="writtenAtUtc"/>, <paramref name="appVersion"/>, and <paramref name="schemaRevision"/>. Returns a brand-new
    /// <see cref="JsonObject"/> — <paramref name="payload"/> itself is not mutated, and its nodes are
    /// deep-cloned (a <see cref="JsonNode"/> can only ever be attached under one parent at a time).
    /// </summary>
    /// <param name="payload">The payload-only object to wrap; must not contain any reserved key.</param>
    /// <param name="schemaVersion">The schema version to record in the envelope.</param>
    /// <param name="writtenAtUtc">The write timestamp to record in the envelope.</param>
    /// <param name="appVersion">The application version to record in the envelope.</param>
    /// <param name="schemaRevision">The additive schema revision to record in the envelope (high-water mark).</param>
    public static JsonObject Wrap(JsonObject payload, int schemaVersion, DateTimeOffset writtenAtUtc, string appVersion, int schemaRevision)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(appVersion);

        var envelope = new JsonObject
        {
            [SchemaVersionKey] = schemaVersion,
            [WrittenAtUtcKey] = writtenAtUtc.ToString("O"),
            [AppVersionKey] = appVersion,
            [SchemaRevisionKey] = schemaRevision,
        };

        foreach (var (key, value) in payload)
        {
            if (IsReservedKey(key))
                throw new ArgumentException($"Payload must not contain reserved envelope key '{key}'.", nameof(payload));

            envelope[key] = CloneNode(value);
        }

        return envelope;
    }

    /// <summary>
    /// Returns a new <see cref="JsonObject"/> containing every key of <paramref name="document"/>
    /// except the four reserved envelope keys — i.e. payload only.
    /// </summary>
    /// <param name="document">The full envelope document to strip.</param>
    public static JsonObject StripEnvelope(JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var payload = new JsonObject();
        foreach (var (key, value) in document)
        {
            if (IsReservedKey(key)) continue;
            payload[key] = CloneNode(value);
        }

        return payload;
    }

    /// <summary>
    /// Attempts to read <c>schemaVersion</c> from <paramref name="document"/> as an integer.
    /// <see langword="false"/> if the key is missing, not present as a top-level property, or not a
    /// JSON number that fits in an <see cref="int"/> — any of which makes the document count as
    /// corrupt for the purposes of the read/recovery ladder (ADR-024 D-7).
    /// </summary>
    /// <param name="document">The envelope document to read from.</param>
    /// <param name="schemaVersion">The decoded schema version when this method returns <see langword="true"/>.</param>
    public static bool TryGetSchemaVersion(JsonObject document, out int schemaVersion)
    {
        ArgumentNullException.ThrowIfNull(document);

        schemaVersion = 0;
        if (!document.TryGetPropertyValue(SchemaVersionKey, out var node) || node is null) return false;

        try
        {
            schemaVersion = node.GetValue<int>();
            return true;
        }
        catch (Exception)
        {
            // Wrong JSON kind (string/array/object) or out of int range — treat as corrupt/invalid,
            // never throw out of a read-path helper.
            return false;
        }
    }

    /// <summary>
    /// Reads <c>schemaRevision</c> from <paramref name="document"/> tolerantly: a missing key, a non-integer
    /// value, or a negative number all count as revision <c>0</c> (never corrupt, never quarantined).
    /// </summary>
    /// <param name="document">The envelope document to read from.</param>
    /// <param name="schemaRevision">The decoded revision, or <c>0</c>.</param>
    /// <returns><see langword="true"/> when a well-formed, non-negative revision was present; <see langword="false"/> when the value was missing or malformed (and treated as <c>0</c>).</returns>
    public static bool TryGetSchemaRevision(JsonObject document, out int schemaRevision)
    {
        ArgumentNullException.ThrowIfNull(document);

        schemaRevision = 0;
        if (!document.TryGetPropertyValue(SchemaRevisionKey, out var node) || node is null) return false;

        try
        {
            var value = node.GetValue<int>();
            if (value < 0) return false;

            schemaRevision = value;
            return true;
        }
        catch (Exception)
        {
            // Wrong JSON kind or out of int range — treated as revision 0, never thrown out of a read path.
            return false;
        }
    }

    /// <summary>Whether <paramref name="document"/> carries a well-formed <c>schemaVersion</c> (see <see cref="TryGetSchemaVersion"/>).</summary>
    /// <param name="document">The envelope-wrapped state document to inspect.</param>
    public static bool HasValidSchemaVersion(JsonObject document) => TryGetSchemaVersion(document, out _);

    /// <summary>
    /// Deep-clones a <see cref="JsonNode"/> by round-tripping it through text. <see cref="JsonNode"/>
    /// instances can only be attached under a single parent at a time, so building a new envelope (or
    /// stripping one) around nodes taken from an existing <see cref="JsonObject"/> requires a copy
    /// rather than a direct re-parent.
    /// </summary>
    private static JsonNode? CloneNode(JsonNode? node) => node is null ? null : JsonNode.Parse(node.ToJsonString());
}
