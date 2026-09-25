using System.Text.Json.Nodes;
using DesktopOrganizer.Services.Storage;
using DesktopOrganizer.Services.Storage.Migrations;
using DesktopOrganizer.Tests.Helpers;

namespace DesktopOrganizer.Tests.Unit.Storage.Migrations;

/// <summary>
/// Unit tests for <see cref="V3ToV4Migration"/>: replacing <c>appearance.backgroundColor</c>/
/// <c>backgroundOpacity</c> with <c>appearance.accentColor</c>/<c>surfaceOpacity</c>/
/// <c>surfaceStyle</c> (ADR-025 §1 D-5), including the lossy <c>#1E1E1E</c> -&gt; <c>#6B7280</c> branch,
/// and identical shaping between the top-level <c>panels</c> array and
/// <c>layoutSnapshots[].panels</c> (ADR-024 R-11).
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class V3ToV4MigrationTests
{
    private static readonly V3ToV4Migration Migration = new();

    /// <summary>
    /// Verifies that the default legacy <c>#1E1E1E</c> background color migrates to the neutral
    /// <c>#6B7280</c> accent color, the lossy branch of <see cref="V3ToV4Migration"/> (ADR-025 §1 D-5).
    /// </summary>
    [Fact]
    public void Migrate_DefaultLegacyBackgroundColor_MapsToNeutralAccent()
    {
        var v3 = new JsonObject { ["panels"] = new JsonArray(FullPanel("panel-1", "#1E1E1E", 0.8)) };

        var v4 = Migration.Migrate(v3);
        var appearance = v4["panels"]![0]!["appearance"]!;

        Assert.Equal("#6B7280", appearance["accentColor"]!.GetValue<string>());
        Assert.Equal(0.8, appearance["surfaceOpacity"]!.GetValue<double>());
        Assert.Equal("Ambient", appearance["surfaceStyle"]!.GetValue<string>());
    }

    /// <summary>
    /// Verifies that a non-default (user-chosen) legacy background color carries over verbatim as the
    /// new accent color, along with its opacity.
    /// </summary>
    [Fact]
    public void Migrate_UserChosenBackgroundColor_CarriesOverVerbatimAsAccent()
    {
        var v3 = new JsonObject { ["panels"] = new JsonArray(FullPanel("panel-1", "#A855F7", 0.65)) };

        var v4 = Migration.Migrate(v3);
        var appearance = v4["panels"]![0]!["appearance"]!;

        Assert.Equal("#A855F7", appearance["accentColor"]!.GetValue<string>());
        Assert.Equal(0.65, appearance["surfaceOpacity"]!.GetValue<double>());
    }

    /// <summary>
    /// Verifies that every migrated panel's <c>appearance.surfaceStyle</c> is always "Ambient",
    /// regardless of the panel's legacy background color or opacity input.
    /// </summary>
    [Fact]
    public void Migrate_SurfaceStyle_IsAlwaysAmbientRegardlessOfInput()
    {
        var v3 = new JsonObject
        {
            ["panels"] = new JsonArray(
                FullPanel("panel-1", "#1E1E1E", 0.8),
                FullPanel("panel-2", "#333333", 1.0)),
        };

        var v4 = Migration.Migrate(v3);

        Assert.All(v4["panels"]!.AsArray(), panel => Assert.Equal("Ambient", panel!["appearance"]!["surfaceStyle"]!.GetValue<string>()));
    }

    /// <summary>
    /// Verifies that <c>appearance.textColor</c> and <c>appearance.iconSize</c> pass through the
    /// migration verbatim, unaffected by the background-color-to-accent-color remapping.
    /// </summary>
    [Fact]
    public void Migrate_TextColorAndIconSize_PassThroughVerbatim()
    {
        var panel = FullPanel("panel-1");
        panel["appearance"]!["textColor"] = "#FFFFFF";
        panel["appearance"]!["iconSize"] = 32;
        var v3 = new JsonObject { ["panels"] = new JsonArray(panel) };

        var v4 = Migration.Migrate(v3);
        var appearance = v4["panels"]![0]!["appearance"]!;

        Assert.Equal("#FFFFFF", appearance["textColor"]!.GetValue<string>());
        Assert.Equal(32, appearance["iconSize"]!.GetValue<int>());
    }

    /// <summary>
    /// Verifies that the migrated <c>appearance</c> object never retains the old
    /// <c>backgroundColor</c>/<c>backgroundOpacity</c> keys once replaced by accent/surface fields.
    /// </summary>
    [Fact]
    public void Migrate_AppearanceOutput_NeverContainsBackgroundColorOrBackgroundOpacity()
    {
        var v3 = new JsonObject { ["panels"] = new JsonArray(FullPanel("panel-1")) };

        var v4 = Migration.Migrate(v3);
        var appearance = v4["panels"]![0]!["appearance"]!.AsObject();

        Assert.False(appearance.ContainsKey("backgroundColor"));
        Assert.False(appearance.ContainsKey("backgroundOpacity"));
    }

    /// <summary>
    /// Verifies that a panel's <c>id</c>/<c>title</c>/<c>pageId</c>/<c>geometry</c>/<c>content</c>
    /// fields pass through <see cref="V3ToV4Migration"/> unchanged; only <c>appearance</c> is remapped.
    /// </summary>
    [Fact]
    public void Migrate_TopLevelPanelKeys_PassThroughUnchangedExceptAppearance()
    {
        var v3 = new JsonObject { ["panels"] = new JsonArray(FullPanel("panel-1")) };

        var v4 = Migration.Migrate(v3);
        var panel = v4["panels"]![0]!;

        Assert.Equal("panel-1", panel["id"]!.GetValue<string>());
        Assert.Equal("Downloads", panel["title"]!.GetValue<string>());
        Assert.Equal("default", panel["pageId"]!.GetValue<string>());

        var geometry = panel["geometry"]!;
        Assert.Equal(100, geometry["x"]!.GetValue<int>());
        Assert.Equal("Maximized", geometry["windowState"]!.GetValue<string>());

        var content = panel["content"]!;
        Assert.Equal("Grid", content["viewMode"]!.GetValue<string>());
        Assert.Equal("C:\\Downloads", content["sourceFolderPath"]!.GetValue<string>());
    }

    /// <summary>
    /// Verifies that a v3 panel with no <c>appearance</c> object at all migrates using the same default
    /// accent/surface/text/icon values as <c>ChildWindowModel</c>'s own field initializers.
    /// </summary>
    [Fact]
    public void Migrate_PanelMissingAppearanceObject_UsesChildWindowModelDefaults()
    {
        var sparsePanel = new JsonObject { ["id"] = "sparse-1" };
        var v3 = new JsonObject { ["panels"] = new JsonArray(sparsePanel) };

        var v4 = Migration.Migrate(v3);
        var appearance = v4["panels"]![0]!["appearance"]!;

        Assert.Equal("#6B7280", appearance["accentColor"]!.GetValue<string>());
        Assert.Equal(0.8, appearance["surfaceOpacity"]!.GetValue<double>());
        Assert.Equal("Ambient", appearance["surfaceStyle"]!.GetValue<string>());
        Assert.Equal("#DDDDDD", appearance["textColor"]!.GetValue<string>());
        Assert.Equal(48, appearance["iconSize"]!.GetValue<double>());
    }

    /// <summary>
    /// Verifies that migrating the same panel content produces an identical shape whether it appears at
    /// the top-level <c>panels</c> array or nested inside a <c>layoutSnapshots[].panels</c> entry (ADR-024 R-11).
    /// </summary>
    [Fact]
    public void Migrate_PanelInsideLayoutSnapshot_GetsIdenticalShapeToTopLevelPanel()
    {
        var panel = FullPanel("shared-panel", "#A855F7", 0.65);
        var topLevelInput = new JsonObject { ["panels"] = new JsonArray(panel.DeepClone()) };
        var snapshotInput = new JsonObject
        {
            ["layoutSnapshots"] = new JsonArray(
                new JsonObject
                {
                    ["id"] = "snap-1",
                    ["name"] = "Snap",
                    ["createdAt"] = "2026-01-01T00:00:00",
                    ["panels"] = new JsonArray(panel.DeepClone()),
                }),
        };

        var topLevelResult = Migration.Migrate(topLevelInput)["panels"]![0];
        var snapshotResult = Migration.Migrate(snapshotInput)["layoutSnapshots"]![0]!["panels"]![0];

        JsonAssert.Equal(topLevelResult, snapshotResult);
    }

    /// <summary>
    /// Verifies that <see cref="V3ToV4Migration"/> passes the <c>pages</c> section through unchanged.
    /// </summary>
    [Fact]
    public void Migrate_Pages_PassesThroughUnchanged()
    {
        var pages = Parse("""{ "ids": ["default", "work"], "currentPageId": "work" }""");
        var v3 = new JsonObject { ["pages"] = pages };

        var v4 = Migration.Migrate(v3);

        JsonAssert.Equal(pages, v4["pages"]);
    }

    /// <summary>
    /// Verifies that <see cref="V3ToV4Migration"/> passes the <c>systemIconsPanel</c> section through unchanged.
    /// </summary>
    [Fact]
    public void Migrate_SystemIconsPanel_PassesThroughUnchanged()
    {
        var sip = Parse("""{ "isEnabled": true, "showOnAllVirtualDesktops": false, "iconSize": "Large", "anchorVirtualDesktopId": null, "items": [] }""");
        var v3 = new JsonObject { ["systemIconsPanel"] = sip };

        var v4 = Migration.Migrate(v3);

        JsonAssert.Equal(sip, v4["systemIconsPanel"]);
    }

    /// <summary>
    /// Verifies that a v3 document with no <c>panels</c> array migrates to an empty <c>panels</c> array.
    /// </summary>
    [Fact]
    public void Migrate_MissingPanelsArray_ProducesEmptyPanelsArray()
    {
        var v4 = Migration.Migrate(new JsonObject());

        Assert.Empty(v4["panels"]!.AsArray());
    }

    /// <summary>
    /// Verifies that <see cref="V3ToV4Migration"/> never produces a top-level key reserved for the
    /// state envelope (<see cref="StateEnvelope.IsReservedKey"/>).
    /// </summary>
    [Fact]
    public void Migrate_NeverProducesAReservedEnvelopeKey()
    {
        var v3 = new JsonObject { ["panels"] = new JsonArray(FullPanel("panel-1")) };

        var v4 = Migration.Migrate(v3);

        Assert.All(v4.Select(kv => kv.Key), key => Assert.False(StateEnvelope.IsReservedKey(key)));
    }

    private static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();

    private static JsonObject FullPanel(string id, string backgroundColor = "#1E1E1E", double backgroundOpacity = 0.8) => new()
    {
        ["id"] = id,
        ["title"] = "Downloads",
        ["pageId"] = "default",
        ["geometry"] = new JsonObject
        {
            ["x"] = 100,
            ["y"] = 200,
            ["width"] = 300,
            ["height"] = 400,
            ["monitorDeviceId"] = "\\\\.\\DISPLAY1",
            ["windowState"] = "Maximized",
            ["isRolledUp"] = false,
            ["isVisible"] = true,
        },
        ["appearance"] = new JsonObject
        {
            ["backgroundColor"] = backgroundColor,
            ["backgroundOpacity"] = backgroundOpacity,
            ["textColor"] = "#DDDDDD",
            ["iconSize"] = 48,
        },
        ["content"] = new JsonObject
        {
            ["viewMode"] = "Grid",
            ["contentFilter"] = "HideShortcuts",
            ["sortMode"] = "Name",
            ["sourceFolderPath"] = "C:\\Downloads",
            ["filePaths"] = new JsonArray("a.txt"),
        },
    };
}
