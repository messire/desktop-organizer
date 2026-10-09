using System.IO;
using System.Text.Json.Nodes;
using DesktopOrganizer.Services.Storage;
using DesktopOrganizer.Services.Storage.Migrations;
using DesktopOrganizer.Tests.Helpers;

namespace DesktopOrganizer.Tests.Unit.Storage.Migrations;

/// <summary>
/// Unit tests for <see cref="V6ToV7Migration"/> (ADR-024 Amendment "Additive revisions",
/// docs/PLANS/schema-additive-revision-v7.md §4.1): an identity migration — the payload is untouched, it is idempotent,
/// it keeps unknown keys at every level, and it never produces a reserved envelope key (the revision is written by
/// <see cref="StateEnvelope.Wrap"/>, not by a migration).
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class V6ToV7MigrationTests
{
    private static readonly V6ToV7Migration Migration = new();

    /// <summary>Verifies the migration declares the 6 -&gt; 7 step.</summary>
    [Fact]
    public void Versions_AreSixToSeven()
    {
        Assert.Equal(6, Migration.FromVersion);
        Assert.Equal(7, Migration.ToVersion);
    }

    /// <summary>Verifies that the payload comes out structurally identical to what went in.</summary>
    [Fact]
    public void Migrate_FullV6Payload_IsIdentity()
    {
        var payload = StateEnvelope.StripEnvelope(LoadFixture("v6-full.json"));

        var migrated = Migration.Migrate(payload);

        JsonAssert.Equal(payload, migrated);
    }

    /// <summary>Verifies that applying the migration twice yields the same document as applying it once.</summary>
    [Fact]
    public void Migrate_IsIdempotent()
    {
        var payload = StateEnvelope.StripEnvelope(LoadFixture("v6-full.json"));

        var once = Migration.Migrate(payload);
        var twice = Migration.Migrate(once);

        JsonAssert.Equal(once, twice);
    }

    /// <summary>Verifies that the result is a deep copy: mutating it does not touch the input.</summary>
    [Fact]
    public void Migrate_ReturnsDeepCopy_InputNotMutatedOrShared()
    {
        var payload = StateEnvelope.StripEnvelope(LoadFixture("v6-full.json"));
        var before = payload.ToJsonString();

        var migrated = Migration.Migrate(payload);
        migrated["panels"]!.AsArray().Clear();
        migrated["unrelated"] = 1;

        Assert.Equal(before, payload.ToJsonString());
    }

    /// <summary>Verifies that unknown keys (including nulls and nested objects) survive at the top level and below.</summary>
    [Fact]
    public void Migrate_PreservesUnknownKeys()
    {
        var payload = StateEnvelope.StripEnvelope(LoadFixture("v6-full.json"));
        payload["futureRoot"] = new JsonObject { ["a"] = new JsonArray(1, 2, 3), ["n"] = null };
        payload["panels"]![0]!["geometry"]!["futureGeometry"] = "keep";

        var migrated = Migration.Migrate(payload);

        JsonAssert.Equal(payload, migrated);
        Assert.Equal("keep", migrated["panels"]![0]!["geometry"]!["futureGeometry"]!.GetValue<string>());
        Assert.True(migrated["futureRoot"]!.AsObject().ContainsKey("n"));
    }

    /// <summary>Verifies that the migration does not create <c>schemaRevision</c> (or any other reserved key).</summary>
    [Fact]
    public void Migrate_DoesNotCreateAnyReservedEnvelopeKey()
    {
        var payload = StateEnvelope.StripEnvelope(LoadFixture("v6-full.json"));

        var migrated = Migration.Migrate(payload);

        Assert.DoesNotContain(migrated, kv => StateEnvelope.IsReservedKey(kv.Key));
    }

    /// <summary>Verifies that an empty payload stays empty.</summary>
    [Fact]
    public void Migrate_EmptyPayload_StaysEmpty()
    {
        Assert.Empty(Migration.Migrate(new JsonObject()));
    }

    private static JsonObject LoadFixture(string fileName)
    {
        var path = Path.Combine("Resources", "Storage", "golden", fileName);
        return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    }
}
