using System.IO;
using System.Text.Json.Nodes;
using DesktopOrganizer.Services.Abstractions.Storage;
using DesktopOrganizer.Services.Storage;

namespace DesktopOrganizer.Tests.Unit.Storage;

/// <summary>
/// Unit tests for <see cref="StateMigrationRunner"/>: ordering, fail-closed gap detection,
/// exception propagation, idempotency, and the defensive downgrade guard (ADR-024 D-9).
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class StateMigrationRunnerTests
{
    private sealed class FakeMigration : ISettingsMigration
    {
        private readonly Func<JsonObject, JsonObject> _transform;

        public FakeMigration(int from, int to, Func<JsonObject, JsonObject> transform)
        {
            FromVersion = from;
            ToVersion = to;
            _transform = transform;
        }

        public int FromVersion { get; }

        public int ToVersion { get; }

        public JsonObject Migrate(JsonObject document) => _transform(document);
    }

    private static FakeMigration StepMarker(int from, int to) =>
        new(from, to, doc =>
        {
            var steps = doc["steps"] as JsonArray ?? new JsonArray();
            steps.Add($"{from}to{to}");
            doc["steps"] = steps;
            return doc;
        });

    private static JsonObject DocumentAtVersion(int schemaVersion) =>
        new()
        {
            ["schemaVersion"] = schemaVersion,
            ["writtenAtUtc"] = "2026-01-01T00:00:00.0000000+00:00",
            ["appVersion"] = "1.2.3",
            ["marker"] = "unchanged",
        };

    [Fact]
    public void MigrateToCurrent_AppliesChainInOrder()
    {
        var runner = new StateMigrationRunner(new ISettingsMigration[]
        {
            StepMarker(1, 2),
            StepMarker(2, 3),
        });

        var document = DocumentAtVersion(1);

        var (migrated, applied) = runner.MigrateToCurrent(document, 3);

        Assert.Equal(2, applied);
        Assert.Equal(3, migrated["schemaVersion"]!.GetValue<int>());
        var steps = Assert.IsType<JsonArray>(migrated["steps"]);
        Assert.Equal(["1to2", "2to3"], steps.Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public void MigrateToCurrent_RegistrationOrderDoesNotMatter_StillAppliesByFromVersion()
    {
        // Registered out of order on purpose: the runner must sort by FromVersion itself.
        var runner = new StateMigrationRunner(new ISettingsMigration[]
        {
            StepMarker(2, 3),
            StepMarker(1, 2),
        });

        var (migrated, applied) = runner.MigrateToCurrent(DocumentAtVersion(1), 3);

        Assert.Equal(2, applied);
        var steps = Assert.IsType<JsonArray>(migrated["steps"]);
        Assert.Equal(["1to2", "2to3"], steps.Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public void MigrateToCurrent_PreservesWrittenAtUtcAndAppVersionVerbatim()
    {
        var runner = new StateMigrationRunner(new ISettingsMigration[] { StepMarker(1, 2) });

        var (migrated, _) = runner.MigrateToCurrent(DocumentAtVersion(1), 2);

        Assert.Equal("2026-01-01T00:00:00.0000000+00:00", migrated["writtenAtUtc"]!.GetValue<string>());
        Assert.Equal("1.2.3", migrated["appVersion"]!.GetValue<string>());
    }

    [Fact]
    public void MigrateToCurrent_Idempotent_AlreadyAtCurrent_ReturnsZeroAppliedAndUnchangedDocument()
    {
        var runner = new StateMigrationRunner(new ISettingsMigration[] { StepMarker(1, 2), StepMarker(2, 3) });

        var document = DocumentAtVersion(3);

        var (result, applied) = runner.MigrateToCurrent(document, 3);

        Assert.Equal(0, applied);
        Assert.Same(document, result);
        Assert.Equal("unchanged", result["marker"]!.GetValue<string>());
    }

    [Fact]
    public void MigrateToCurrent_GapInChain_AtRuntime_ThrowsFailClosed()
    {
        // Only 1->2 is registered; asking to reach schema 3 needs a 2->3 step that does not exist.
        var runner = new StateMigrationRunner(new ISettingsMigration[] { StepMarker(1, 2) });

        var ex = Assert.Throws<InvalidOperationException>(() => runner.MigrateToCurrent(DocumentAtVersion(1), 3));
        Assert.Contains("2", ex.Message);
    }

    [Fact]
    public void Constructor_GapBetweenRegisteredMigrations_ThrowsFailClosed()
    {
        // 1->2 and 3->4 never connect at version 2/3 — this must be caught eagerly, at construction.
        Assert.Throws<InvalidOperationException>(() => new StateMigrationRunner(new ISettingsMigration[]
        {
            StepMarker(1, 2),
            StepMarker(3, 4),
        }));
    }

    [Fact]
    public void Constructor_DuplicateFromVersion_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => new StateMigrationRunner(new ISettingsMigration[]
        {
            StepMarker(1, 2),
            new FakeMigration(1, 2, d => d),
        }));
    }

    [Fact]
    public void Constructor_NonSingleStepMigration_Throws()
    {
        // ToVersion must be exactly FromVersion + 1 — a migration claiming to skip a version is rejected.
        Assert.Throws<InvalidOperationException>(() => new StateMigrationRunner(new ISettingsMigration[]
        {
            new FakeMigration(1, 3, d => d),
        }));
    }

    [Fact]
    public void MigrateToCurrent_ExceptionInsideMigration_PropagatesAndIsNotSwallowed()
    {
        var throwing = new FakeMigration(1, 2, _ => throw new InvalidDataException("boom"));
        var runner = new StateMigrationRunner(new ISettingsMigration[] { throwing });

        var ex = Assert.Throws<InvalidDataException>(() => runner.MigrateToCurrent(DocumentAtVersion(1), 2));
        Assert.Equal("boom", ex.Message);
    }

    [Fact]
    public void MigrateToCurrent_Downgrade_NewerThanCurrent_ThrowsDefensive()
    {
        var runner = new StateMigrationRunner(Array.Empty<ISettingsMigration>());

        var ex = Assert.Throws<InvalidOperationException>(() => runner.MigrateToCurrent(DocumentAtVersion(5), 3));
        Assert.Contains("newer", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MigrateToCurrent_MissingSchemaVersion_Throws()
    {
        var runner = new StateMigrationRunner(Array.Empty<ISettingsMigration>());
        var document = new JsonObject { ["panels"] = new JsonArray() };

        Assert.Throws<InvalidOperationException>(() => runner.MigrateToCurrent(document, 3));
    }
}
