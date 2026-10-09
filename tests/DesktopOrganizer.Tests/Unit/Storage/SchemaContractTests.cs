using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DesktopOrganizer.Core;
using DesktopOrganizer.Services.Abstractions.Storage;
using DesktopOrganizer.Services.Storage;
using DesktopOrganizer.Services.Storage.Migrations;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.State;
using DesktopOrganizer.UI.State.Dto;

namespace DesktopOrganizer.Tests.Unit.Storage;

/// <summary>
/// The key contract test against ADR-024 D-1's risk (R-9): the payload keys the real migration chain
/// (<c>DesktopOrganizer.Services.Storage.Migrations</c>) produces must match, name-for-name and
/// structure-for-structure, the keys <see cref="ApplicationStateDocumentDto"/>'s serialization
/// (<c>DesktopOrganizer.UI.State</c>, via <see cref="StateJson"/>) produces. The two sides are
/// written and maintained independently — D-1's whole tradeoff (schema names existing in two places)
/// is only safe because this test exists and runs on every change to either side.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class SchemaContractTests
{
    private static readonly string GoldenDirectory = Path.Combine("Resources", "Storage", "golden");

    /// <summary>
    /// Verifies ADR-024 D-1's central risk (R-9): the payload key paths the real migration chain
    /// produces from a fully-populated v1 fixture match, name-for-name and structure-for-structure, the
    /// key paths <see cref="ApplicationStateDocumentDto"/>'s own JSON serialization produces.
    /// </summary>
    [Fact]
    public void MigratedV1Document_KeyShape_MatchesApplicationStateDocumentDtoSerialization()
    {
        var v1Document = LoadFixture("v1-full.json");
        var runner = new StateMigrationRunner(new ISettingsMigration[] { new V1ToV2Migration(), new V2ToV3Migration(), new V3ToV4Migration(), new V4ToV5Migration(), new V5ToV6Migration(), new V6ToV7Migration() });
        var (migrated, applied) = runner.MigrateToCurrent(v1Document, StateEnvelope.CurrentSchemaVersion);
        Assert.Equal(6, applied);

        var migratedPayload = StateEnvelope.StripEnvelope(migrated);
        var migratedKeyPaths = CollectKeyPaths(migratedPayload);

        var dtoNode = (JsonObject)JsonSerializer.SerializeToNode(BuildFullyPopulatedDocument(), StateJson.Options)!;
        var dtoKeyPaths = CollectKeyPaths(dtoNode);

        Assert.Equal(dtoKeyPaths, migratedKeyPaths);
    }

    /// <summary>
    /// Verifies that migrating a fully-populated v1 fixture through <see cref="V1ToV2Migration"/> never
    /// produces a top-level key reserved for the state envelope (<see cref="StateEnvelope.IsReservedKey"/>).
    /// </summary>
    [Fact]
    public void V1ToV2Migration_NeverProducesAReservedEnvelopeKey()
    {
        var v1Document = LoadFixture("v1-full.json");
        var v1Payload = StateEnvelope.StripEnvelope(v1Document);

        var v2Payload = new V1ToV2Migration().Migrate(v1Payload);

        Assert.All(v2Payload.Select(kv => kv.Key), key => Assert.False(StateEnvelope.IsReservedKey(key)));
    }

    /// <summary>
    /// Verifies that migrating a fully-populated v2 fixture through <see cref="V2ToV3Migration"/> never
    /// produces a reserved envelope key.
    /// </summary>
    [Fact]
    public void V2ToV3Migration_NeverProducesAReservedEnvelopeKey()
    {
        var v2Document = LoadFixture("v2-full.json");
        var v2Payload = StateEnvelope.StripEnvelope(v2Document);

        var v3Payload = new V2ToV3Migration().Migrate(v2Payload);

        Assert.All(v3Payload.Select(kv => kv.Key), key => Assert.False(StateEnvelope.IsReservedKey(key)));
    }

    /// <summary>
    /// Verifies that migrating a fully-populated v3 fixture through <see cref="V3ToV4Migration"/> never
    /// produces a reserved envelope key.
    /// </summary>
    [Fact]
    public void V3ToV4Migration_NeverProducesAReservedEnvelopeKey()
    {
        var v3Document = LoadFixture("v3-full.json");
        var v3Payload = StateEnvelope.StripEnvelope(v3Document);

        var v4Payload = new V3ToV4Migration().Migrate(v3Payload);

        Assert.All(v4Payload.Select(kv => kv.Key), key => Assert.False(StateEnvelope.IsReservedKey(key)));
    }

    /// <summary>
    /// Verifies that migrating a fully-populated v4 fixture through <see cref="V4ToV5Migration"/> never
    /// produces a reserved envelope key.
    /// </summary>
    [Fact]
    public void V4ToV5Migration_NeverProducesAReservedEnvelopeKey()
    {
        var v4Document = LoadFixture("v4-full.json");
        var v4Payload = StateEnvelope.StripEnvelope(v4Document);

        var v5Payload = new V4ToV5Migration().Migrate(v4Payload);

        Assert.All(v5Payload.Select(kv => kv.Key), key => Assert.False(StateEnvelope.IsReservedKey(key)));
    }

    /// <summary>
    /// Verifies that migrating a fully-populated v5 fixture through <see cref="V5ToV6Migration"/> never
    /// produces a reserved envelope key.
    /// </summary>
    [Fact]
    public void V5ToV6Migration_NeverProducesAReservedEnvelopeKey()
    {
        var v5Document = LoadFixture("v5-full.json");
        var v5Payload = StateEnvelope.StripEnvelope(v5Document);

        var v6Payload = new V5ToV6Migration().Migrate(v5Payload);

        Assert.All(v6Payload.Select(kv => kv.Key), key => Assert.False(StateEnvelope.IsReservedKey(key)));
    }

    /// <summary>
    /// Verifies that migrating a fully-populated v6 fixture through <see cref="V6ToV7Migration"/> never
    /// produces a reserved envelope key (in particular not <c>schemaRevision</c>, which only
    /// <see cref="StateEnvelope.Wrap"/> writes).
    /// </summary>
    [Fact]
    public void V6ToV7Migration_NeverProducesAReservedEnvelopeKey()
    {
        var v6Document = LoadFixture("v6-full.json");
        var v6Payload = StateEnvelope.StripEnvelope(v6Document);

        var v7Payload = new V6ToV7Migration().Migrate(v6Payload);

        Assert.All(v7Payload.Select(kv => kv.Key), key => Assert.False(StateEnvelope.IsReservedKey(key)));
        Assert.Contains("schemaRevision", StateEnvelope.ReservedKeys);
        Assert.Equal(4, StateEnvelope.ReservedKeys.Count);
    }

    /// <summary>
    /// Gate for every future DTO (ADR-024 Amendment "Additive revisions"): each DTO under
    /// <c>DesktopOrganizer.UI.State.Dto</c> must have exactly one <see cref="JsonExtensionDataAttribute"/> property
    /// of type <c>Dictionary&lt;string, JsonElement&gt;</c>, otherwise a field added by a newer revision would be
    /// silently dropped at that level on the next save.
    /// </summary>
    [Fact]
    public void AllDtos_HaveExactlyOneJsonExtensionDataProperty()
    {
        var dtoTypes = GetDtoTypes();
        Assert.Equal(10, dtoTypes.Count);

        foreach (var type in dtoTypes)
        {
            var extensionProperties = type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetCustomAttribute<JsonExtensionDataAttribute>() is not null)
                .ToList();

            var property = Assert.Single(extensionProperties);
            Assert.Equal(typeof(Dictionary<string, JsonElement>), property.PropertyType);
        }
    }

    /// <summary>
    /// Gate: no DTO property has an enum type — enum-valued fields are plain strings parsed tolerantly by the
    /// mappers, so an unknown member written by a newer build can never throw during deserialization.
    /// </summary>
    [Fact]
    public void NoDtoProperty_IsEnumTyped()
    {
        foreach (var type in GetDtoTypes())
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                Assert.False(propertyType.IsEnum, $"{type.Name}.{property.Name} is enum-typed ({propertyType.Name}).");
            }
        }
    }

    /// <summary>
    /// Bumping <see cref="StateEnvelope.CurrentSchemaRevision"/> without checking in the golden fixture for the new
    /// revision (<c>v7-r{N}-full.json</c>) fails here: one fixture per revision, 0..current.
    /// </summary>
    [Fact]
    public void RevisionFixtures_ExistForEveryRevisionUpToCurrent()
    {
        for (var revision = 0; revision <= StateEnvelope.CurrentSchemaRevision; revision++)
        {
            var fixture = LoadFixture($"v{StateEnvelope.CurrentSchemaVersion}-r{revision}-full.json");
            Assert.True(StateEnvelope.TryGetSchemaVersion(fixture, out var version));
            Assert.Equal(StateEnvelope.CurrentSchemaVersion, version);
            Assert.True(StateEnvelope.TryGetSchemaRevision(fixture, out var fixtureRevision));
            Assert.Equal(revision, fixtureRevision);
        }
    }

    /// <summary>
    /// NIT fix (fix-cycle 1, docs/PLANS/workspace-grid-migration.md §10.5): the same literal default
    /// value (<c>"LegacyFreeform"</c>/<c>"Manual"</c>/<c>"LegacyStretch"</c>) is necessarily duplicated
    /// in two places that cannot share a single constant — <c>V4ToV5Migration</c> (Services layer,
    /// <see cref="JsonNode"/>-only per ADR-024 D-1, cannot reference the UI-layer enum/DTO types) and
    /// <see cref="PanelGeometryDto"/>/<see cref="PanelAppearanceDto"/>'s own C# field initializers (UI
    /// layer). <c>MigratedV1Document_KeyShape_MatchesApplicationStateDocumentDtoSerialization</c> above
    /// only compares key <em>paths</em>, not values, so it would not catch the two literals drifting
    /// apart. This test closes that gap directly: migrate a v4 document whose panel never had any of
    /// the five new fields, and assert the migration's output values equal the DTOs' own defaults
    /// (never a second hardcoded copy of the literal in this test) — so if either side changes without
    /// the other, this fails instead of both silently disagreeing forever.
    /// </summary>
    [Fact]
    public void V4ToV5Migration_DefaultValues_MatchApplicationStateDocumentDtoDefaults()
    {
        var v4Document = LoadFixture("v4-full.json");
        var v4Payload = StateEnvelope.StripEnvelope(v4Document);

        var v5Payload = new V4ToV5Migration().Migrate(v4Payload);

        var expectedGeometry = new PanelGeometryDto();
        var expectedAppearance = new PanelAppearanceDto();

        foreach (var panelNode in v5Payload["panels"]!.AsArray())
        {
            var panel = panelNode!.AsObject();
            Assert.Equal(expectedGeometry.GeometryMode, panel["geometry"]!["geometryMode"]!.GetValue<string>());
            Assert.Equal(expectedGeometry.SizingMode, panel["geometry"]!["sizingMode"]!.GetValue<string>());
            Assert.Equal(expectedAppearance.IconLayoutPolicy, panel["appearance"]!["iconLayoutPolicy"]!.GetValue<string>());

            // §2/§10.2 of the plan: widthSpanUnits/heightSpanUnits must stay absent (never an explicit
            // null) for a migrated (necessarily LegacyFreeform) panel.
            Assert.False(panel["geometry"]!.AsObject().ContainsKey("widthSpanUnits"));
            Assert.False(panel["geometry"]!.AsObject().ContainsKey("heightSpanUnits"));
        }
    }

    /// <summary>
    /// Recursively collects every payload key path present in <paramref name="node"/>, normalizing
    /// array indices to a single <c>[]</c> segment (so a fixture with two panels and a DTO instance
    /// with one panel compare as identical shapes) and dotted for nested objects.
    /// </summary>
    private static SortedSet<string> CollectKeyPaths(JsonNode? node)
    {
        var result = new SortedSet<string>(StringComparer.Ordinal);
        CollectKeyPaths(node, string.Empty, result);
        return result;
    }

    private static void CollectKeyPaths(JsonNode? node, string prefix, SortedSet<string> result)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    var path = prefix.Length == 0 ? key : $"{prefix}.{key}";
                    result.Add(path);
                    CollectKeyPaths(value, path, result);
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    CollectKeyPaths(item, prefix + "[]", result);
                }

                break;
        }
    }

    private static List<Type> GetDtoTypes() =>
        typeof(ApplicationStateDocumentDto).Assembly
            .GetTypes()
            .Where(t => t.Namespace == typeof(ApplicationStateDocumentDto).Namespace && t is { IsClass: true, IsAbstract: false } && !t.IsNested)
            .Where(t => t.GetCustomAttribute<System.Runtime.CompilerServices.CompilerGeneratedAttribute>() is null)
            .ToList();

    private static JsonObject LoadFixture(string fileName)
    {
        var path = Path.Combine(GoldenDirectory, fileName);
        var text = File.ReadAllText(path);
        return JsonNode.Parse(text)!.AsObject();
    }

    /// <summary>
    /// A document with every optional/nullable field populated with a non-null value, so every key
    /// the schema can produce actually appears once — a null-valued property would otherwise be
    /// omitted by <see cref="StateJson"/>'s <c>WhenWritingNull</c> and silently vanish from the
    /// comparison on the DTO side only.
    /// </summary>
    private static ApplicationStateDocumentDto BuildFullyPopulatedDocument() => new()
    {
        Panels = [BuildFullyPopulatedPanel()],

        // A non-empty Name (rather than the unnamed "default" page) so the "pages.items[].name" key
        // path actually appears on the DTO side too — the migrated-v1 side (§8.2, ADR-043) has one:
        // v1-full.json's "work" page migrates all the way to {"id":"work","name":"work"}. WhenWritingNull
        // would otherwise omit that path here, only on the DTO side, and make the two sides silently
        // disagree on which paths are compared (this test's own central point).
        Pages = new PagesStateDto { Items = [new PageStateDto { Id = "work", Name = "Work" }], CurrentPageId = "default" },
        LayoutSnapshots =
        [
            new LayoutSnapshotStateDto
            {
                Id = "snap-1",
                Name = "Snapshot",
                CreatedAt = DateTime.UtcNow,
                Panels = [BuildFullyPopulatedPanel()],
            },
        ],
        SystemIconsPanel = new SystemIconsPanelStateDto
        {
            IsEnabled = true,
            ShowOnAllVirtualDesktops = true,
            IconSize = "Medium",
            AnchorVirtualDesktopId = Guid.NewGuid().ToString(),
            Items = [new SystemIconsPanelItemStateDto { Kind = nameof(SystemIconKind.RecycleBin), IsVisible = true, Order = 0 }],
        },
    };

    private static PanelStateDto BuildFullyPopulatedPanel() => new()
    {
        Id = Guid.NewGuid(),
        Title = "Panel",
        PageId = "default",
        Geometry = new PanelGeometryDto
        {
            X = 1,
            Y = 2,
            Width = 3,
            Height = 4,
            MonitorDeviceId = "\\\\.\\DISPLAY1",
            WindowState = "Normal",
            IsRolledUp = false,
            IsVisible = true,
        },
        Appearance = new PanelAppearanceDto
        {
            AccentColor = "#6B7280",
            SurfaceOpacity = 0.8,
            SurfaceStyle = "Ambient",
            TextColor = "#DDDDDD",
            IconSize = 48,
        },
        Content = new PanelContentDto
        {
            ViewMode = "List",
            ContentFilter = "ShowAll",
            SortMode = "Manual",
            SourceFolderPath = "C:\\Source",
            FilePaths = ["C:\\a.txt"],
        },
    };
}
