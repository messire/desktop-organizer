using System.Text.Json.Nodes;
using DesktopOrganizer.Services.Storage;
using DesktopOrganizer.Services.Storage.Migrations;
using DesktopOrganizer.Tests.Helpers;

namespace DesktopOrganizer.Tests.Unit.Storage.Migrations;

/// <summary>
/// Unit tests for <see cref="V4ToV5Migration"/>: additive <c>geometry.geometryMode</c>/
/// <c>geometry.widthSpanUnits</c>/<c>geometry.heightSpanUnits</c>/<c>geometry.sizingMode</c> and
/// <c>appearance.iconLayoutPolicy</c> (ADR-028/ADR-029, docs/PLANS/workspace-grid-migration.md),
/// idempotency, safe defaults, and identical shaping between the top-level <c>panels</c> array and
/// <c>layoutSnapshots[].panels</c> (ADR-024 R-11).
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class V4ToV5MigrationTests
{
    private static readonly V4ToV5Migration Migration = new();

    /// <summary>
    /// Verifies that a v4 panel with no grid/sizing/icon-layout fields migrates to
    /// <c>geometryMode=LegacyFreeform</c>, <c>sizingMode=Manual</c>, <c>iconLayoutPolicy=LegacyStretch</c>,
    /// with the additive span-unit fields left absent rather than defaulted to a value (ADR-028/ADR-029).
    /// </summary>
    [Fact]
    public void Migrate_MissingGeometryFields_DefaultsToLegacyFreeformManualLegacyStretch()
    {
        var v4 = new JsonObject { ["panels"] = new JsonArray(FullPanel("panel-1")) };

        var v5 = Migration.Migrate(v4);
        var panel = v5["panels"]![0]!;
        var geometry = panel["geometry"]!.AsObject();
        var appearance = panel["appearance"]!.AsObject();

        Assert.Equal("LegacyFreeform", geometry["geometryMode"]!.GetValue<string>());
        Assert.Equal("Manual", geometry["sizingMode"]!.GetValue<string>());
        Assert.Equal("LegacyStretch", appearance["iconLayoutPolicy"]!.GetValue<string>());
        Assert.False(geometry.ContainsKey("widthSpanUnits"));
        Assert.False(geometry.ContainsKey("heightSpanUnits"));
    }

    /// <summary>
    /// Verifies that <see cref="V4ToV5Migration"/> preserves the existing x/y/width/height and other
    /// geometry fields verbatim, since this migration is purely additive.
    /// </summary>
    [Fact]
    public void Migrate_PreservesExistingXYWidthHeightVerbatim()
    {
        var v4 = new JsonObject { ["panels"] = new JsonArray(FullPanel("panel-1")) };

        var v5 = Migration.Migrate(v4);
        var geometry = v5["panels"]![0]!["geometry"]!;

        Assert.Equal(100, geometry["x"]!.GetValue<int>());
        Assert.Equal(200, geometry["y"]!.GetValue<int>());
        Assert.Equal(300, geometry["width"]!.GetValue<int>());
        Assert.Equal(400, geometry["height"]!.GetValue<int>());
        Assert.Equal("\\\\.\\DISPLAY1", geometry["monitorDeviceId"]!.GetValue<string>());
        Assert.Equal("Maximized", geometry["windowState"]!.GetValue<string>());
    }

    /// <summary>
    /// Verifies that <see cref="V4ToV5Migration"/> does not touch the existing <c>appearance.iconSize</c> value.
    /// </summary>
    [Fact]
    public void Migrate_DoesNotTouchIconSizeOrDefaultIconSize()
    {
        var panel = FullPanel("panel-1");
        panel["appearance"]!["iconSize"] = 32.0101;
        var v4 = new JsonObject { ["panels"] = new JsonArray(panel) };

        var v5 = Migration.Migrate(v4);
        var appearance = v5["panels"]![0]!["appearance"]!;

        Assert.Equal(32.0101, appearance["iconSize"]!.GetValue<double>());
    }

    /// <summary>
    /// Verifies that <see cref="V4ToV5Migration"/> is idempotent: a panel that already has grid/sizing/
    /// icon-layout values keeps them exactly as-is, and running the migration a second time on its own
    /// v5 output is a true no-op.
    /// </summary>
    [Fact]
    public void Migrate_IsIdempotent_AlreadyMigratedGeometryAndAppearanceValuesAreNeverOverwritten()
    {
        var panel = FullPanel("panel-1");
        panel["geometry"]!["geometryMode"] = "GridAligned";
        panel["geometry"]!["widthSpanUnits"] = 20;
        panel["geometry"]!["heightSpanUnits"] = 15;
        panel["geometry"]!["sizingMode"] = "AutoContent";
        panel["appearance"]!["iconLayoutPolicy"] = "FixedGap";
        var v4 = new JsonObject { ["panels"] = new JsonArray(panel) };

        var v5 = Migration.Migrate(v4);
        var geometry = v5["panels"]![0]!["geometry"]!;
        var appearance = v5["panels"]![0]!["appearance"]!;

        Assert.Equal("GridAligned", geometry["geometryMode"]!.GetValue<string>());
        Assert.Equal(20, geometry["widthSpanUnits"]!.GetValue<int>());
        Assert.Equal(15, geometry["heightSpanUnits"]!.GetValue<int>());
        Assert.Equal("AutoContent", geometry["sizingMode"]!.GetValue<string>());
        Assert.Equal("FixedGap", appearance["iconLayoutPolicy"]!.GetValue<string>());

        // Running the migration a second time on its own output must be a true no-op.
        var v5Again = Migration.Migrate(v5);
        JsonAssert.Equal(v5, v5Again);
    }

    /// <summary>
    /// Verifies that an already-present, unrecognised <c>geometryMode</c> string value is preserved
    /// verbatim by the migration (idempotency requires not sanitizing it) and never throws; the safe
    /// fallback to a default happens one layer up, at <c>PanelStateMapper.FromDto</c>.
    /// </summary>
    [Fact]
    public void Migrate_UnknownEnumStringValue_IsPreservedVerbatim_SafeFallbackHappensAtDeserialization()
    {
        // The migration itself is not responsible for sanitizing an already-present, unrecognised
        // value (idempotency requires preserving whatever is already there verbatim) — the safe
        // fallback to a default happens one layer up, at PanelStateMapper.FromDto via the
        // Parser.ParseOrXxx helpers (DesktopOrganizer.UI.Models), covered by PanelStateMapperTests.
        // This test only proves the migration does not throw and does not touch the (bogus) value.
        var panel = FullPanel("panel-1");
        panel["geometry"]!["geometryMode"] = "TotallyUnknownValue";
        var v4 = new JsonObject { ["panels"] = new JsonArray(panel) };

        var exception = Record.Exception(() => Migration.Migrate(v4));

        Assert.Null(exception);
        var v5 = Migration.Migrate(v4);
        Assert.Equal("TotallyUnknownValue", v5["panels"]![0]!["geometry"]!["geometryMode"]!.GetValue<string>());
    }

    /// <summary>
    /// Verifies that a panel's <c>id</c>/<c>title</c>/<c>pageId</c>/<c>content</c> fields pass through
    /// <see cref="V4ToV5Migration"/> unchanged.
    /// </summary>
    [Fact]
    public void Migrate_TopLevelPanelKeysAndContent_PassThroughUnchanged()
    {
        var v4 = new JsonObject { ["panels"] = new JsonArray(FullPanel("panel-1")) };

        var v5 = Migration.Migrate(v4);
        var panel = v5["panels"]![0]!;

        Assert.Equal("panel-1", panel["id"]!.GetValue<string>());
        Assert.Equal("Downloads", panel["title"]!.GetValue<string>());
        Assert.Equal("default", panel["pageId"]!.GetValue<string>());

        var content = panel["content"]!;
        Assert.Equal("Grid", content["viewMode"]!.GetValue<string>());
        Assert.Equal("C:\\Downloads", content["sourceFolderPath"]!.GetValue<string>());
    }

    /// <summary>
    /// Verifies that a v4 panel with no <c>geometry</c> or <c>appearance</c> object at all still
    /// migrates to safe defaults (<c>LegacyFreeform</c>/<c>Manual</c>/<c>LegacyStretch</c>).
    /// </summary>
    [Fact]
    public void Migrate_PanelMissingGeometryOrAppearanceObject_StillProducesSafeDefaults()
    {
        var sparsePanel = new JsonObject { ["id"] = "sparse-1" };
        var v4 = new JsonObject { ["panels"] = new JsonArray(sparsePanel) };

        var v5 = Migration.Migrate(v4);
        var panel = v5["panels"]![0]!;

        Assert.Equal("LegacyFreeform", panel["geometry"]!["geometryMode"]!.GetValue<string>());
        Assert.Equal("Manual", panel["geometry"]!["sizingMode"]!.GetValue<string>());
        Assert.Equal("LegacyStretch", panel["appearance"]!["iconLayoutPolicy"]!.GetValue<string>());
    }

    /// <summary>
    /// Verifies that migrating the same panel content produces an identical shape whether it appears at
    /// the top-level <c>panels</c> array or nested inside a <c>layoutSnapshots[].panels</c> entry (ADR-024 R-11).
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
    /// Verifies that <see cref="V4ToV5Migration"/> passes the <c>pages</c> section through unchanged.
    /// </summary>
    [Fact]
    public void Migrate_Pages_PassesThroughUnchanged()
    {
        var pages = Parse("""{ "ids": ["default", "work"], "currentPageId": "work" }""");
        var v4 = new JsonObject { ["pages"] = pages };

        var v5 = Migration.Migrate(v4);

        JsonAssert.Equal(pages, v5["pages"]);
    }

    /// <summary>
    /// Verifies that <see cref="V4ToV5Migration"/> passes the <c>systemIconsPanel</c> section through unchanged.
    /// </summary>
    [Fact]
    public void Migrate_SystemIconsPanel_PassesThroughUnchanged()
    {
        var sip = Parse("""{ "isEnabled": true, "showOnAllVirtualDesktops": false, "iconSize": "Large", "anchorVirtualDesktopId": null, "items": [] }""");
        var v4 = new JsonObject { ["systemIconsPanel"] = sip };

        var v5 = Migration.Migrate(v4);

        JsonAssert.Equal(sip, v5["systemIconsPanel"]);
    }

    /// <summary>
    /// Verifies that a v4 document with no <c>panels</c> array migrates to an empty <c>panels</c> array.
    /// </summary>
    [Fact]
    public void Migrate_MissingPanelsArray_ProducesEmptyPanelsArray()
    {
        var v5 = Migration.Migrate(new JsonObject());

        Assert.Empty(v5["panels"]!.AsArray());
    }

    /// <summary>
    /// Verifies that <see cref="V4ToV5Migration"/> never produces a top-level key reserved for the
    /// state envelope (<see cref="StateEnvelope.IsReservedKey"/>).
    /// </summary>
    [Fact]
    public void Migrate_NeverProducesAReservedEnvelopeKey()
    {
        var v4 = new JsonObject { ["panels"] = new JsonArray(FullPanel("panel-1")) };

        var v5 = Migration.Migrate(v4);

        Assert.All(v5.Select(kv => kv.Key), key => Assert.False(StateEnvelope.IsReservedKey(key)));
    }

    private static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();

    private static JsonObject FullPanel(string id) => new()
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
            ["accentColor"] = "#6B7280",
            ["surfaceOpacity"] = 0.8,
            ["surfaceStyle"] = "Ambient",
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
