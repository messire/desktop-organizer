using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
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

    [Fact]
    public void MigratedV1Document_KeyShape_MatchesApplicationStateDocumentDtoSerialization()
    {
        var v1Document = LoadFixture("v1-full.json");
        var runner = new StateMigrationRunner(new ISettingsMigration[] { new V1ToV2Migration(), new V2ToV3Migration(), new V3ToV4Migration(), new V4ToV5Migration() });
        var (migrated, applied) = runner.MigrateToCurrent(v1Document, StateEnvelope.CurrentSchemaVersion);
        Assert.Equal(4, applied);

        var migratedPayload = StateEnvelope.StripEnvelope(migrated);
        var migratedKeyPaths = CollectKeyPaths(migratedPayload);

        var dtoNode = (JsonObject)JsonSerializer.SerializeToNode(BuildFullyPopulatedDocument(), StateJson.Options)!;
        var dtoKeyPaths = CollectKeyPaths(dtoNode);

        Assert.Equal(dtoKeyPaths, migratedKeyPaths);
    }

    [Fact]
    public void V1ToV2Migration_NeverProducesAReservedEnvelopeKey()
    {
        var v1Document = LoadFixture("v1-full.json");
        var v1Payload = StateEnvelope.StripEnvelope(v1Document);

        var v2Payload = new V1ToV2Migration().Migrate(v1Payload);

        Assert.All(v2Payload.Select(kv => kv.Key), key => Assert.False(StateEnvelope.IsReservedKey(key)));
    }

    [Fact]
    public void V2ToV3Migration_NeverProducesAReservedEnvelopeKey()
    {
        var v2Document = LoadFixture("v2-full.json");
        var v2Payload = StateEnvelope.StripEnvelope(v2Document);

        var v3Payload = new V2ToV3Migration().Migrate(v2Payload);

        Assert.All(v3Payload.Select(kv => kv.Key), key => Assert.False(StateEnvelope.IsReservedKey(key)));
    }

    [Fact]
    public void V3ToV4Migration_NeverProducesAReservedEnvelopeKey()
    {
        var v3Document = LoadFixture("v3-full.json");
        var v3Payload = StateEnvelope.StripEnvelope(v3Document);

        var v4Payload = new V3ToV4Migration().Migrate(v3Payload);

        Assert.All(v4Payload.Select(kv => kv.Key), key => Assert.False(StateEnvelope.IsReservedKey(key)));
    }

    [Fact]
    public void V4ToV5Migration_NeverProducesAReservedEnvelopeKey()
    {
        var v4Document = LoadFixture("v4-full.json");
        var v4Payload = StateEnvelope.StripEnvelope(v4Document);

        var v5Payload = new V4ToV5Migration().Migrate(v4Payload);

        Assert.All(v5Payload.Select(kv => kv.Key), key => Assert.False(StateEnvelope.IsReservedKey(key)));
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
        Pages = new PagesStateDto { Ids = ["default"], CurrentPageId = "default" },
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
            Items = [new SystemIconsPanelItemStateDto { Kind = SystemIconKind.RecycleBin, IsVisible = true, Order = 0 }],
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
            WindowState = WindowState.Normal,
            IsRolledUp = false,
            IsVisible = true,
        },
        Appearance = new PanelAppearanceDto
        {
            AccentColor = "#6B7280",
            SurfaceOpacity = 0.8,
            SurfaceStyle = PanelSurfaceStyle.Ambient,
            TextColor = "#DDDDDD",
            IconSize = 48,
        },
        Content = new PanelContentDto
        {
            ViewMode = PanelViewMode.List,
            ContentFilter = PanelContentFilter.ShowAll,
            SortMode = PanelSortMode.Manual,
            SourceFolderPath = "C:\\Source",
            FilePaths = ["C:\\a.txt"],
        },
    };
}
