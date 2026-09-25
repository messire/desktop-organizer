using System.IO;
using System.Text.Json.Nodes;
using DesktopOrganizer.Services.Abstractions.Storage;
using DesktopOrganizer.Services.Storage;
using DesktopOrganizer.Services.Storage.Migrations;
using DesktopOrganizer.Tests.Helpers;

namespace DesktopOrganizer.Tests.Unit.Storage;

/// <summary>
/// Golden-fixture tests (ADR-024 D-9 DoD): the real migration chain, driven end-to-end against
/// full realistic documents at every schema version, must reproduce exactly the fixed, checked-in
/// expected output — see <c>Resources/Storage/golden/README.md</c> for the immutability rule these
/// fixtures follow.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class GoldenMigrationTests
{
    private static readonly string GoldenDirectory = Path.Combine("Resources", "Storage", "golden");

    /// <summary>
    /// Verifies that migrating the golden <c>v1-full.json</c> fixture through the full V1-&gt;V5 chain
    /// applies exactly 4 migrations and matches the checked-in <c>expected-current-from-v1.json</c> fixture.
    /// </summary>
    [Fact]
    public void V1Full_MigratesToCurrent_MatchesExpected()
    {
        var input = LoadFixture("v1-full.json");
        var expected = LoadFixture("expected-current-from-v1.json");

        var (actual, applied) = CreateRunner().MigrateToCurrent(input, StateEnvelope.CurrentSchemaVersion);

        Assert.Equal(4, applied);
        JsonAssert.Equal(expected, actual);
    }

    /// <summary>
    /// Verifies that migrating the golden <c>v2-full.json</c> fixture through the V2-&gt;V5 chain applies
    /// exactly 3 migrations and matches the checked-in <c>expected-current-from-v2.json</c> fixture.
    /// </summary>
    [Fact]
    public void V2Full_MigratesToCurrent_MatchesExpected()
    {
        var input = LoadFixture("v2-full.json");
        var expected = LoadFixture("expected-current-from-v2.json");

        var (actual, applied) = CreateRunner().MigrateToCurrent(input, StateEnvelope.CurrentSchemaVersion);

        Assert.Equal(3, applied);
        JsonAssert.Equal(expected, actual);
    }

    /// <summary>
    /// Verifies that migrating the golden <c>v3-full.json</c> fixture through the V3-&gt;V5 chain applies
    /// exactly 2 migrations and matches the checked-in <c>expected-current-from-v3.json</c> fixture.
    /// </summary>
    [Fact]
    public void V3Full_MigratesToCurrent_MatchesExpected()
    {
        var input = LoadFixture("v3-full.json");
        var expected = LoadFixture("expected-current-from-v3.json");

        var (actual, applied) = CreateRunner().MigrateToCurrent(input, StateEnvelope.CurrentSchemaVersion);

        Assert.Equal(2, applied);
        JsonAssert.Equal(expected, actual);
    }

    /// <summary>
    /// Verifies that migrating the golden <c>v4-full.json</c> fixture through the V4-&gt;V5 migration
    /// applies exactly 1 migration and matches the checked-in <c>expected-current-from-v4.json</c> fixture.
    /// </summary>
    [Fact]
    public void V4Full_MigratesToCurrent_MatchesExpected()
    {
        var input = LoadFixture("v4-full.json");
        var expected = LoadFixture("expected-current-from-v4.json");

        var (actual, applied) = CreateRunner().MigrateToCurrent(input, StateEnvelope.CurrentSchemaVersion);

        Assert.Equal(1, applied);
        JsonAssert.Equal(expected, actual);
    }

    /// <summary>
    /// Verifies that migrating the golden <c>v5-full.json</c> fixture (already at the current schema)
    /// applies zero migrations and returns the document unchanged.
    /// </summary>
    [Fact]
    public void V5Full_AlreadyAtCurrentSchema_IsIdentity()
    {
        var input = LoadFixture("v5-full.json");

        var (actual, applied) = CreateRunner().MigrateToCurrent(input, StateEnvelope.CurrentSchemaVersion);

        Assert.Equal(0, applied);
        JsonAssert.Equal(input, actual);
    }

    /// <summary>
    /// Verifies that running the full migration chain against the already-current golden <c>v5-full.json</c>
    /// fixture twice in a row (the shape a restore-then-restore-again cycle would produce) yields
    /// byte-for-byte identical results both times (docs/fix-prompts/06-fit-content.md DoD).
    /// </summary>
    [Fact]
    public void V5Full_MigratedTwice_IsByteForByteIdentical_TwoRestoreCyclesAgree()
    {
        // docs/fix-prompts/06-fit-content.md DoD: "snapshots after two restore cycles are identical."
        // Applied here at the schema-migration level: running the full chain against an already-current
        // document twice in a row (the shape a restore-then-restore-again cycle would produce) must
        // yield byte-for-byte identical JsonObject shapes both times — not just "no exception".
        var input = LoadFixture("v5-full.json");

        var (firstPass, _) = CreateRunner().MigrateToCurrent(input, StateEnvelope.CurrentSchemaVersion);
        var (secondPass, _) = CreateRunner().MigrateToCurrent(firstPass, StateEnvelope.CurrentSchemaVersion);

        JsonAssert.Equal(firstPass, secondPass);
    }

    private static StateMigrationRunner CreateRunner() => new(new ISettingsMigration[]
    {
        new V1ToV2Migration(),
        new V2ToV3Migration(),
        new V3ToV4Migration(),
        new V4ToV5Migration(),
    });

    private static JsonObject LoadFixture(string fileName)
    {
        var path = Path.Combine(GoldenDirectory, fileName);
        var text = File.ReadAllText(path);
        return JsonNode.Parse(text)!.AsObject();
    }
}
