using System.Text.Json.Nodes;
using DesktopOrganizer.Services.Storage.Migrations;
using DesktopOrganizer.Tests.Helpers;

namespace DesktopOrganizer.Tests.Unit.Storage.Migrations;

/// <summary>
/// Unit tests for <see cref="V2ToV3Migration"/>: grouping into
/// <c>geometry</c>/<c>appearance</c>/<c>content</c>, defaults matching
/// <c>ChildWindowModel</c>'s own field initializers, and identical shaping between the top-level
/// <c>panels</c> array and <c>layoutSnapshots[].panels</c> (ADR-024 D-9, R-11).
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class V2ToV3MigrationTests
{
    private static readonly V2ToV3Migration Migration = new();

    /// <summary>
    /// Verifies that <see cref="V2ToV3Migration"/> groups a fully-populated flat v2 panel's fields into
    /// the v3 <c>geometry</c>/<c>appearance</c>/<c>content</c> sub-objects, dropping the transient
    /// <c>isEmpty</c> flag along the way.
    /// </summary>
    [Fact]
    public void Migrate_FullPanel_GroupsIntoGeometryAppearanceContent()
    {
        var v2 = new JsonObject { ["panels"] = new JsonArray(FullPanel("panel-1")) };

        var v3 = Migration.Migrate(v2);
        var panel = v3["panels"]![0]!;

        Assert.Equal("panel-1", panel["id"]!.GetValue<string>());
        Assert.Equal("Downloads", panel["title"]!.GetValue<string>());
        Assert.Equal("default", panel["pageId"]!.GetValue<string>());

        var geometry = panel["geometry"]!;
        Assert.Equal(100, geometry["x"]!.GetValue<int>());
        Assert.Equal(200, geometry["y"]!.GetValue<int>());
        Assert.Equal(300, geometry["width"]!.GetValue<int>());
        Assert.Equal(400, geometry["height"]!.GetValue<int>());
        Assert.Equal("\\\\.\\DISPLAY1", geometry["monitorDeviceId"]!.GetValue<string>());
        Assert.Equal("Maximized", geometry["windowState"]!.GetValue<string>());
        Assert.False(geometry["isRolledUp"]!.GetValue<bool>());
        Assert.True(geometry["isVisible"]!.GetValue<bool>());
        Assert.False(geometry.AsObject().ContainsKey("isEmpty"));

        var appearance = panel["appearance"]!;
        Assert.Equal("#1E1E1E", appearance["backgroundColor"]!.GetValue<string>());
        Assert.Equal(0.8, appearance["backgroundOpacity"]!.GetValue<double>());
        Assert.Equal("#DDDDDD", appearance["textColor"]!.GetValue<string>());
        Assert.Equal(48, appearance["iconSize"]!.GetValue<int>());

        var content = panel["content"]!;
        Assert.Equal("Grid", content["viewMode"]!.GetValue<string>());
        Assert.Equal("HideShortcuts", content["contentFilter"]!.GetValue<string>());
        Assert.Equal("Name", content["sortMode"]!.GetValue<string>());
        Assert.Equal("C:\\Downloads", content["sourceFolderPath"]!.GetValue<string>());
        Assert.Equal(["a.txt"], content["filePaths"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    /// <summary>
    /// Verifies that <see cref="V2ToV3Migration"/> maps a numeric legacy <c>windowState</c> value to its
    /// enum name inside the new <c>geometry</c> sub-object.
    /// </summary>
    /// <param name="numeric">The legacy numeric <c>windowState</c> value.</param>
    /// <param name="expectedName">The enum name it must map to.</param>
    [Theory]
    [InlineData(0, "Normal")]
    [InlineData(1, "Minimized")]
    [InlineData(2, "Maximized")]
    public void Migrate_WindowState_MapsNumericToName(int numeric, string expectedName)
    {
        var panel = FullPanel("panel-1");
        panel["windowState"] = numeric;
        var v2 = new JsonObject { ["panels"] = new JsonArray(panel) };

        var v3 = Migration.Migrate(v2);

        Assert.Equal(expectedName, v3["panels"]![0]!["geometry"]!["windowState"]!.GetValue<string>());
    }

    /// <summary>
    /// Verifies that an out-of-range numeric <c>windowState</c> value defaults to "Normal" rather than throwing.
    /// </summary>
    [Fact]
    public void Migrate_UnknownWindowStateValue_DefaultsToNormal()
    {
        var panel = FullPanel("panel-1");
        panel["windowState"] = 99;
        var v2 = new JsonObject { ["panels"] = new JsonArray(panel) };

        var v3 = Migration.Migrate(v2);

        Assert.Equal("Normal", v3["panels"]![0]!["geometry"]!["windowState"]!.GetValue<string>());
    }

    /// <summary>
    /// Verifies that a v2 panel missing every optional field migrates using the same default values as
    /// <c>ChildWindowModel</c>'s own field initializers.
    /// </summary>
    [Fact]
    public void Migrate_PanelMissingEveryOptionalField_UsesChildWindowModelDefaults()
    {
        var sparsePanel = new JsonObject { ["id"] = "sparse-1" };
        var v2 = new JsonObject { ["panels"] = new JsonArray(sparsePanel) };

        var v3 = Migration.Migrate(v2);
        var panel = v3["panels"]![0]!;

        Assert.Equal("sparse-1", panel["id"]!.GetValue<string>());
        Assert.Equal(string.Empty, panel["title"]!.GetValue<string>());
        Assert.Equal("default", panel["pageId"]!.GetValue<string>());

        var geometry = panel["geometry"]!;
        Assert.Equal(0, geometry["x"]!.GetValue<int>());
        Assert.Equal(0, geometry["y"]!.GetValue<int>());
        Assert.Equal(0, geometry["width"]!.GetValue<int>());
        Assert.Equal(0, geometry["height"]!.GetValue<int>());
        Assert.Null(geometry["monitorDeviceId"]);
        Assert.Equal("Normal", geometry["windowState"]!.GetValue<string>());
        Assert.False(geometry["isRolledUp"]!.GetValue<bool>());
        Assert.True(geometry["isVisible"]!.GetValue<bool>());

        var appearance = panel["appearance"]!;
        Assert.Equal("#1E1E1E", appearance["backgroundColor"]!.GetValue<string>());
        Assert.Equal(0.8, appearance["backgroundOpacity"]!.GetValue<double>());
        Assert.Equal("#DDDDDD", appearance["textColor"]!.GetValue<string>());
        Assert.Equal(48, appearance["iconSize"]!.GetValue<double>());

        var content = panel["content"]!;
        Assert.Equal("List", content["viewMode"]!.GetValue<string>());
        Assert.Equal("ShowAll", content["contentFilter"]!.GetValue<string>());
        Assert.Equal("Manual", content["sortMode"]!.GetValue<string>());
        Assert.Null(content["sourceFolderPath"]);
        Assert.Empty(content["filePaths"]!.AsArray());
    }

    /// <summary>
    /// Verifies that migrating the same panel content produces an identical shape whether it appears at
    /// the top-level <c>panels</c> array or nested inside a <c>layoutSnapshots[].panels</c> entry.
    /// </summary>
    [Fact]
    public void Migrate_PanelInsideLayoutSnapshot_GetsIdenticalShapeToTopLevelPanel()
    {
        var panel = FullPanel("shared-panel");
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
    /// Verifies that <see cref="V2ToV3Migration"/> passes the <c>pages</c> section through unchanged.
    /// </summary>
    [Fact]
    public void Migrate_Pages_PassesThroughUnchanged()
    {
        var pages = Parse("""{ "ids": ["default", "work"], "currentPageId": "work" }""");
        var v2 = new JsonObject { ["pages"] = pages };

        var v3 = Migration.Migrate(v2);

        JsonAssert.Equal(pages, v3["pages"]);
    }

    /// <summary>
    /// Verifies that <see cref="V2ToV3Migration"/> passes the <c>systemIconsPanel</c> section through unchanged.
    /// </summary>
    [Fact]
    public void Migrate_SystemIconsPanel_PassesThroughUnchanged()
    {
        var sip = Parse("""{ "isEnabled": true, "showOnAllVirtualDesktops": false, "iconSize": "Large", "anchorVirtualDesktopId": null, "items": [] }""");
        var v2 = new JsonObject { ["systemIconsPanel"] = sip };

        var v3 = Migration.Migrate(v2);

        JsonAssert.Equal(sip, v3["systemIconsPanel"]);
    }

    /// <summary>
    /// Verifies that a v2 document with no <c>panels</c> array migrates to an empty <c>panels</c> array.
    /// </summary>
    [Fact]
    public void Migrate_MissingPanelsArray_ProducesEmptyPanelsArray()
    {
        var v3 = Migration.Migrate(new JsonObject());

        Assert.Empty(v3["panels"]!.AsArray());
    }

    private static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();

    private static JsonObject FullPanel(string id) => new()
    {
        ["id"] = id,
        ["title"] = "Downloads",
        ["backgroundColor"] = "#1E1E1E",
        ["backgroundOpacity"] = 0.8,
        ["textColor"] = "#DDDDDD",
        ["iconSize"] = 48,
        ["viewMode"] = "Grid",
        ["contentFilter"] = "HideShortcuts",
        ["sortMode"] = "Name",
        ["monitorDeviceId"] = "\\\\.\\DISPLAY1",
        ["position"] = new JsonObject { ["x"] = 100, ["y"] = 200, ["isEmpty"] = false },
        ["size"] = new JsonObject { ["width"] = 300, ["height"] = 400, ["isEmpty"] = false },
        ["isVisible"] = true,
        ["windowState"] = 2,
        ["isRolledUp"] = false,
        ["pageId"] = "default",
        ["sourceFolderPath"] = "C:\\Downloads",
        ["filePaths"] = new JsonArray("a.txt"),
    };
}
