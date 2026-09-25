using System.Text.Json.Nodes;
using DesktopOrganizer.Services.Storage.Migrations;
using DesktopOrganizer.Tests.Helpers;

namespace DesktopOrganizer.Tests.Unit.Storage.Migrations;

/// <summary>
/// Unit tests for <see cref="V1ToV2Migration"/>: PascalCase -&gt; camelCase, numeric enums -&gt;
/// their frozen v1-era names, and <c>PanelJsonItems</c> unpacking (ADR-024 D-9, R-10, R-11).
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class V1ToV2MigrationTests
{
    private static readonly V1ToV2Migration Migration = new();

    /// <summary>
    /// Verifies that <see cref="V1ToV2Migration"/> maps a numeric legacy <c>ViewMode</c> value to its
    /// frozen v1-era enum name.
    /// </summary>
    /// <param name="numeric">The legacy numeric <c>ViewMode</c> value.</param>
    /// <param name="expectedName">The frozen v1-era enum name it must map to.</param>
    [Theory]
    [InlineData(0, "List")]
    [InlineData(1, "Grid")]
    public void Migrate_ViewMode_MapsNumericToFrozenV1Name(int numeric, string expectedName)
    {
        var v1 = BuildDocumentWithSinglePanel($$"""{"ViewMode":{{numeric}},"ContentFilter":0,"SortMode":0}""");

        var v2 = Migration.Migrate(v1);

        Assert.Equal(expectedName, v2["panels"]![0]!["viewMode"]!.GetValue<string>());
    }

    /// <summary>
    /// Verifies that <see cref="V1ToV2Migration"/> maps a numeric legacy <c>ContentFilter</c> value to
    /// its frozen v1-era enum name.
    /// </summary>
    /// <param name="numeric">The legacy numeric <c>ContentFilter</c> value.</param>
    /// <param name="expectedName">The frozen v1-era enum name it must map to.</param>
    [Theory]
    [InlineData(0, "ShowAll")]
    [InlineData(1, "HideAll")]
    [InlineData(2, "HideShortcuts")]
    [InlineData(3, "FoldersOnly")]
    public void Migrate_ContentFilter_MapsNumericToFrozenV1Name(int numeric, string expectedName)
    {
        var v1 = BuildDocumentWithSinglePanel($$"""{"ViewMode":0,"ContentFilter":{{numeric}},"SortMode":0}""");

        var v2 = Migration.Migrate(v1);

        Assert.Equal(expectedName, v2["panels"]![0]!["contentFilter"]!.GetValue<string>());
    }

    /// <summary>
    /// Verifies that <see cref="V1ToV2Migration"/> maps a numeric legacy <c>SortMode</c> value to its
    /// frozen v1-era enum name.
    /// </summary>
    /// <param name="numeric">The legacy numeric <c>SortMode</c> value.</param>
    /// <param name="expectedName">The frozen v1-era enum name it must map to.</param>
    [Theory]
    [InlineData(0, "Manual")]
    [InlineData(1, "Name")]
    public void Migrate_SortMode_MapsNumericToFrozenV1Name(int numeric, string expectedName)
    {
        var v1 = BuildDocumentWithSinglePanel($$"""{"ViewMode":0,"ContentFilter":0,"SortMode":{{numeric}}}""");

        var v2 = Migration.Migrate(v1);

        Assert.Equal(expectedName, v2["panels"]![0]!["sortMode"]!.GetValue<string>());
    }

    /// <summary>
    /// Verifies that an out-of-range numeric enum value for <c>ViewMode</c>/<c>ContentFilter</c>/<c>SortMode</c>
    /// defaults to the index-zero enum name rather than throwing.
    /// </summary>
    [Fact]
    public void Migrate_UnknownEnumValue_DefaultsToIndexZeroName()
    {
        var v1 = BuildDocumentWithSinglePanel("""{"ViewMode":99,"ContentFilter":99,"SortMode":99}""");

        var v2 = Migration.Migrate(v1);

        var panel = v2["panels"]![0]!;
        Assert.Equal("List", panel["viewMode"]!.GetValue<string>());
        Assert.Equal("ShowAll", panel["contentFilter"]!.GetValue<string>());
        Assert.Equal("Manual", panel["sortMode"]!.GetValue<string>());
    }

    /// <summary>
    /// Verifies that <see cref="V1ToV2Migration"/> renames every PascalCase v1 panel field to its
    /// camelCase v2 equivalent while preserving values, including a null <c>sourceFolderPath</c> key
    /// that must still be present.
    /// </summary>
    [Fact]
    public void Migrate_PanelFields_RenamedToCamelCase()
    {
        var v1 = Parse("""
        {
          "panels": [
            {
              "Id": "11111111-1111-1111-1111-111111111111",
              "Title": "Downloads",
              "BackgroundColor": "#1E1E1E",
              "BackgroundOpacity": 0.8,
              "TextColor": "#DDDDDD",
              "IconSize": 48,
              "ViewMode": 0,
              "ContentFilter": 0,
              "SortMode": 0,
              "MonitorDeviceId": "\\\\.\\DISPLAY1",
              "Position": { "X": 1, "Y": 2, "IsEmpty": false },
              "Size": { "Width": 3, "Height": 4, "IsEmpty": false },
              "IsVisible": true,
              "WindowState": 0,
              "IsRolledUp": false,
              "PageId": "default",
              "SourceFolderPath": null,
              "FilePaths": ["a.txt"]
            }
          ]
        }
        """);

        var v2 = Migration.Migrate(v1);
        var panel = v2["panels"]![0]!;

        Assert.Equal("11111111-1111-1111-1111-111111111111", panel["id"]!.GetValue<string>());
        Assert.Equal("Downloads", panel["title"]!.GetValue<string>());
        Assert.Equal("#1E1E1E", panel["backgroundColor"]!.GetValue<string>());
        Assert.Equal(0.8, panel["backgroundOpacity"]!.GetValue<double>());
        Assert.Equal("#DDDDDD", panel["textColor"]!.GetValue<string>());
        Assert.Equal(48, panel["iconSize"]!.GetValue<int>());
        Assert.Equal("\\\\.\\DISPLAY1", panel["monitorDeviceId"]!.GetValue<string>());
        Assert.Equal(1, panel["position"]!["x"]!.GetValue<int>());
        Assert.Equal(2, panel["position"]!["y"]!.GetValue<int>());
        Assert.Equal(3, panel["size"]!["width"]!.GetValue<int>());
        Assert.Equal(4, panel["size"]!["height"]!.GetValue<int>());
        Assert.True(panel["isVisible"]!.GetValue<bool>());
        Assert.Equal(0, panel["windowState"]!.GetValue<int>());
        Assert.False(panel["isRolledUp"]!.GetValue<bool>());
        Assert.Equal("default", panel["pageId"]!.GetValue<string>());
        Assert.Null(panel["sourceFolderPath"]);
        Assert.True(panel.AsObject().ContainsKey("sourceFolderPath"));
        Assert.Equal(["a.txt"], panel["filePaths"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    /// <summary>
    /// Verifies that <see cref="V1ToV2Migration"/> renames the <c>pages</c> object's PascalCase fields
    /// (<c>Ids</c>, <c>CurrentPageId</c>) to camelCase while preserving values.
    /// </summary>
    [Fact]
    public void Migrate_Pages_RenamedToCamelCase()
    {
        var v1 = Parse("""{ "pages": { "Ids": ["default", "work"], "CurrentPageId": "work" } }""");

        var v2 = Migration.Migrate(v1);

        Assert.Equal(["default", "work"], v2["pages"]!["ids"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("work", v2["pages"]!["currentPageId"]!.GetValue<string>());
    }

    /// <summary>
    /// Verifies that a v1 document with no <c>pages</c> section migrates to a single "default" page.
    /// </summary>
    [Fact]
    public void Migrate_MissingPages_DefaultsToSingleDefaultPage()
    {
        var v1 = Parse("{}");

        var v2 = Migration.Migrate(v1);

        Assert.Equal(["default"], v2["pages"]!["ids"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("default", v2["pages"]!["currentPageId"]!.GetValue<string>());
    }

    /// <summary>
    /// Verifies that <see cref="V1ToV2Migration"/> renames the <c>systemIconsPanel</c> object's
    /// PascalCase fields, including its nested <c>Items</c> array, to camelCase while preserving values.
    /// </summary>
    [Fact]
    public void Migrate_SystemIconsPanel_RenamedToCamelCase()
    {
        var v1 = Parse("""
        {
          "systemIconsPanel": {
            "IsEnabled": true,
            "ShowOnAllVirtualDesktops": false,
            "IconSize": "Large",
            "AnchorVirtualDesktopId": "55555555-5555-5555-5555-555555555555",
            "Items": [ { "Kind": "RecycleBin", "IsVisible": true, "Order": 0 } ]
          }
        }
        """);

        var v2 = Migration.Migrate(v1);
        var sip = v2["systemIconsPanel"]!;

        Assert.True(sip["isEnabled"]!.GetValue<bool>());
        Assert.False(sip["showOnAllVirtualDesktops"]!.GetValue<bool>());
        Assert.Equal("Large", sip["iconSize"]!.GetValue<string>());
        Assert.Equal("55555555-5555-5555-5555-555555555555", sip["anchorVirtualDesktopId"]!.GetValue<string>());
        var item = sip["items"]![0]!;
        Assert.Equal("RecycleBin", item["kind"]!.GetValue<string>());
        Assert.True(item["isVisible"]!.GetValue<bool>());
        Assert.Equal(0, item["order"]!.GetValue<int>());
    }

    /// <summary>
    /// Verifies that a v1 document with no <c>systemIconsPanel</c> section stays without that key after migration.
    /// </summary>
    [Fact]
    public void Migrate_MissingSystemIconsPanel_StaysAbsent()
    {
        var v1 = Parse("{}");

        var v2 = Migration.Migrate(v1);

        Assert.False(v2.ContainsKey("systemIconsPanel"));
    }

    /// <summary>
    /// Verifies that <see cref="V1ToV2Migration"/> unpacks a layout snapshot's <c>PanelJsonItems</c>
    /// (an array of JSON-encoded strings) into real, camelCase-migrated panel objects under <c>panels</c>.
    /// </summary>
    [Fact]
    public void Migrate_PanelJsonItems_UnpacksJsonStringsIntoRealObjects()
    {
        var panelJson = """{"Id":"aaaa","Title":"Snap Panel","BackgroundColor":"#1E1E1E","BackgroundOpacity":0.8,"TextColor":"#DDDDDD","IconSize":48,"ViewMode":1,"ContentFilter":0,"SortMode":0,"MonitorDeviceId":null,"Position":{"X":1,"Y":1,"IsEmpty":false},"Size":{"Width":1,"Height":1,"IsEmpty":false},"IsVisible":true,"WindowState":0,"IsRolledUp":false,"PageId":"default","SourceFolderPath":null,"FilePaths":[]}""";
        var escaped = panelJson.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var v1 = Parse($$"""
        {
          "layoutSnapshots": [
            { "Id": "s1", "Name": "Snap 1", "CreatedAt": "2026-01-01T00:00:00", "PanelJsonItems": ["{{escaped}}"] }
          ]
        }
        """);

        var v2 = Migration.Migrate(v1);

        var snapshot = v2["layoutSnapshots"]![0]!;
        Assert.Equal("s1", snapshot["id"]!.GetValue<string>());
        Assert.Equal("Snap 1", snapshot["name"]!.GetValue<string>());
        var panels = snapshot["panels"]!.AsArray();
        Assert.Single(panels);
        Assert.Equal("aaaa", panels[0]!["id"]!.GetValue<string>());
        Assert.Equal("Grid", panels[0]!["viewMode"]!.GetValue<string>());
        Assert.IsType<JsonObject>(panels[0]);
    }

    /// <summary>
    /// Verifies that a layout snapshot whose <c>PanelJsonItems</c> contains one unparsable JSON string
    /// is dropped entirely, while other, healthy snapshots in the same document still migrate normally.
    /// </summary>
    [Fact]
    public void Migrate_SnapshotWithCorruptPanelJsonItemEntry_DropsWholeSnapshot_OthersSurvive()
    {
        var goodPanelJson = """{"Id":"good","Title":"Good","BackgroundColor":"#1E1E1E","BackgroundOpacity":0.8,"TextColor":"#DDDDDD","IconSize":48,"ViewMode":0,"ContentFilter":0,"SortMode":0,"MonitorDeviceId":null,"Position":{"X":0,"Y":0,"IsEmpty":true},"Size":{"Width":0,"Height":0,"IsEmpty":true},"IsVisible":true,"WindowState":0,"IsRolledUp":false,"PageId":"default","SourceFolderPath":null,"FilePaths":[]}""";
        var goodEscaped = goodPanelJson.Replace("\\", "\\\\").Replace("\"", "\\\"");

        var v1 = Parse($$"""
        {
          "layoutSnapshots": [
            { "Id": "broken", "Name": "Broken Snapshot", "CreatedAt": "2026-01-01T00:00:00", "PanelJsonItems": ["this is not valid json at all"] },
            { "Id": "healthy", "Name": "Healthy Snapshot", "CreatedAt": "2026-01-02T00:00:00", "PanelJsonItems": ["{{goodEscaped}}"] }
          ]
        }
        """);

        var v2 = Migration.Migrate(v1);

        var snapshots = v2["layoutSnapshots"]!.AsArray();
        Assert.Single(snapshots);
        Assert.Equal("healthy", snapshots[0]!["id"]!.GetValue<string>());
    }

    /// <summary>
    /// Verifies that a v1 document with no <c>panels</c> or <c>layoutSnapshots</c> sections migrates
    /// both to empty arrays rather than leaving them absent or throwing.
    /// </summary>
    [Fact]
    public void Migrate_MissingPanelsAndLayoutSnapshots_DefaultToEmptyArrays()
    {
        var v1 = Parse("{}");

        var v2 = Migration.Migrate(v1);

        Assert.Empty(v2["panels"]!.AsArray());
        Assert.Empty(v2["layoutSnapshots"]!.AsArray());
    }

    private static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();

    private static JsonObject BuildDocumentWithSinglePanel(string panelEnumFieldsJson)
    {
        var enumFields = Parse(panelEnumFieldsJson);
        var panel = new JsonObject
        {
            ["Id"] = "id-1",
            ["Title"] = "Panel",
            ["BackgroundColor"] = "#1E1E1E",
            ["BackgroundOpacity"] = 0.8,
            ["TextColor"] = "#DDDDDD",
            ["IconSize"] = 48,
            ["ViewMode"] = enumFields["ViewMode"]!.DeepClone(),
            ["ContentFilter"] = enumFields["ContentFilter"]!.DeepClone(),
            ["SortMode"] = enumFields["SortMode"]!.DeepClone(),
            ["MonitorDeviceId"] = null,
            ["Position"] = new JsonObject { ["X"] = 0, ["Y"] = 0, ["IsEmpty"] = true },
            ["Size"] = new JsonObject { ["Width"] = 0, ["Height"] = 0, ["IsEmpty"] = true },
            ["IsVisible"] = true,
            ["WindowState"] = 0,
            ["IsRolledUp"] = false,
            ["PageId"] = "default",
            ["SourceFolderPath"] = null,
            ["FilePaths"] = new JsonArray(),
        };

        return new JsonObject { ["panels"] = new JsonArray(panel) };
    }
}
