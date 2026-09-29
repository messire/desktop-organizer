using System.Text.Json.Nodes;
using DesktopOrganizer.Services.Storage;
using DesktopOrganizer.Services.Storage.Migrations;
using DesktopOrganizer.Tests.Helpers;

namespace DesktopOrganizer.Tests.Unit.Storage.Migrations;

/// <summary>
/// Unit tests for <see cref="V5ToV6Migration"/>: <c>pages.ids[]</c> -&gt; <c>pages.items[{id,name?}]</c>
/// (ADR-043, docs/PLANS/page-display-names.md §2.3), idempotency, safe defaults for corrupted/hand-edited
/// data, and byte-for-byte preservation of every other top-level key.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class V5ToV6MigrationTests
{
    private static readonly V5ToV6Migration Migration = new();

    /// <summary>Verifies that the <c>"default"</c> page migrates with no <c>name</c> key at all (not an explicit <c>null</c>).</summary>
    [Fact]
    public void Migrate_DefaultPage_HasNoNameKey()
    {
        var v5 = new JsonObject { ["pages"] = Parse("""{ "ids": ["default"], "currentPageId": "default" }""") };

        var v6 = Migration.Migrate(v5);
        var item = v6["pages"]!["items"]![0]!.AsObject();

        Assert.Equal("default", item["id"]!.GetValue<string>());
        Assert.False(item.ContainsKey("name"));
    }

    /// <summary>
    /// Verifies that every non-<c>"default"</c> id — including ids with spaces and Cyrillic characters —
    /// migrates to <c>{"id":s,"name":s}</c>, so the user sees exactly the same string as before.
    /// </summary>
    /// <param name="id">The non-default page id under test.</param>
    [Theory]
    [InlineData("work")]
    [InlineData("Page 2")]
    [InlineData("Работа")]
    public void Migrate_NonDefaultPages_NameEqualsId(string id)
    {
        var v5 = new JsonObject
        {
            ["pages"] = new JsonObject
            {
                ["ids"] = new JsonArray("default", id),
                ["currentPageId"] = "default",
            },
        };

        var v6 = Migration.Migrate(v5);
        var items = v6["pages"]!["items"]!.AsArray();
        var item = items[1]!.AsObject();

        Assert.Equal(id, item["id"]!.GetValue<string>());
        Assert.Equal(id, item["name"]!.GetValue<string>());
    }

    /// <summary>Verifies that the order of ids and <c>currentPageId</c> are preserved verbatim.</summary>
    [Fact]
    public void Migrate_PreservesOrderAndCurrentPageId()
    {
        var v5 = new JsonObject
        {
            ["pages"] = new JsonObject
            {
                ["ids"] = new JsonArray("default", "b", "a"),
                ["currentPageId"] = "a",
            },
        };

        var v6 = Migration.Migrate(v5);
        var items = v6["pages"]!["items"]!.AsArray();

        Assert.Equal(["default", "b", "a"], items.Select(i => i!["id"]!.GetValue<string>()));
        Assert.Equal("a", v6["pages"]!["currentPageId"]!.GetValue<string>());
    }

    /// <summary>Verifies that the legacy <c>ids</c> key never survives into the v6 output.</summary>
    [Fact]
    public void Migrate_DropsIdsKey_WritesItems()
    {
        var v5 = new JsonObject
        {
            ["pages"] = new JsonObject { ["ids"] = new JsonArray("default"), ["currentPageId"] = "default" },
        };

        var v6 = Migration.Migrate(v5);
        var pages = v6["pages"]!.AsObject();

        Assert.False(pages.ContainsKey("ids"));
        Assert.True(pages.ContainsKey("items"));
    }

    /// <summary>
    /// Verifies that a <c>pages</c> object already shaped like v6 (carrying <c>items</c>) is copied
    /// through verbatim, and that running the migration a second time on its own output is a true no-op.
    /// </summary>
    [Fact]
    public void Migrate_IsIdempotent_WhenItemsAlreadyPresent()
    {
        var v5 = new JsonObject
        {
            ["pages"] = Parse("""{ "items": [{ "id": "default" }, { "id": "work", "name": "Work" }], "currentPageId": "work" }"""),
        };

        var v6 = Migration.Migrate(v5);
        JsonAssert.Equal(v5["pages"], v6["pages"]);

        var v6Again = Migration.Migrate(v6);
        JsonAssert.Equal(v6, v6Again);
    }

    /// <summary>
    /// Verifies that when both <c>items</c> and a stray legacy <c>ids</c> are present on the same
    /// <c>pages</c> object, <c>items</c> wins verbatim and <c>ids</c> is dropped.
    /// </summary>
    [Fact]
    public void Migrate_ItemsAndIdsBothPresent_ItemsWins_IdsDropped()
    {
        var v5 = new JsonObject
        {
            ["pages"] = new JsonObject
            {
                ["items"] = new JsonArray(new JsonObject { ["id"] = "default" }),
                ["ids"] = new JsonArray("default", "stale"),
                ["currentPageId"] = "default",
            },
        };

        var v6 = Migration.Migrate(v5);
        var pages = v6["pages"]!.AsObject();

        Assert.False(pages.ContainsKey("ids"));
        var items = pages["items"]!.AsArray();
        Assert.Single(items);
        Assert.Equal("default", items[0]!["id"]!.GetValue<string>());
    }

    /// <summary>Verifies that a non-string or <c>null</c> entry in <c>ids</c> is skipped rather than throwing.</summary>
    [Fact]
    public void Migrate_SkipsNonStringAndNullIds()
    {
        var v5 = new JsonObject
        {
            ["pages"] = new JsonObject
            {
                ["ids"] = new JsonArray("default", JsonValue.Create(42), null, "work"),
                ["currentPageId"] = "default",
            },
        };

        var v6 = Migration.Migrate(v5);
        var items = v6["pages"]!["items"]!.AsArray();

        Assert.Equal(["default", "work"], items.Select(i => i!["id"]!.GetValue<string>()));
    }

    /// <summary>
    /// Verifies that duplicate ids in the legacy <c>ids</c> array are preserved verbatim (as two separate
    /// items) rather than deduplicated — sanitizing on load is <c>PagesStateMapper.FromDto</c>'s job, not
    /// this migration's.
    /// </summary>
    [Fact]
    public void Migrate_DuplicateIds_PreservedVerbatim()
    {
        var v5 = new JsonObject
        {
            ["pages"] = new JsonObject { ["ids"] = new JsonArray("default", "work", "work"), ["currentPageId"] = "default" },
        };

        var v6 = Migration.Migrate(v5);
        var items = v6["pages"]!["items"]!.AsArray();

        Assert.Equal(3, items.Count);
        Assert.Equal(["default", "work", "work"], items.Select(i => i!["id"]!.GetValue<string>()));
    }

    /// <summary>Verifies that a document with no <c>pages</c> key at all stays without one after migration.</summary>
    [Fact]
    public void Migrate_PagesMissing_StaysMissing()
    {
        var v5 = new JsonObject { ["panels"] = new JsonArray() };

        var v6 = Migration.Migrate(v5);

        Assert.False(v6.ContainsKey("pages"));
    }

    /// <summary>Verifies that a <c>pages</c> value that is not a JSON object (e.g. an array) is dropped, not migrated.</summary>
    [Fact]
    public void Migrate_PagesNotObject_Dropped()
    {
        var v5 = new JsonObject { ["pages"] = new JsonArray("default") };

        var v6 = Migration.Migrate(v5);

        Assert.False(v6.ContainsKey("pages"));
    }

    /// <summary>Verifies that a <c>pages</c> object with no <c>ids</c> (and no <c>items</c>) migrates to an empty <c>items</c> array.</summary>
    [Fact]
    public void Migrate_IdsMissing_EmptyItems()
    {
        var v5 = new JsonObject { ["pages"] = new JsonObject { ["currentPageId"] = "default" } };

        var v6 = Migration.Migrate(v5);
        var pages = v6["pages"]!.AsObject();

        Assert.Empty(pages["items"]!.AsArray());
        Assert.Equal("default", pages["currentPageId"]!.GetValue<string>());
    }

    /// <summary>Verifies that the entire <c>panels</c> array — including every <c>pageId</c> and v5 geometry/appearance field — passes through byte-for-byte.</summary>
    [Fact]
    public void Migrate_PanelsUnchanged_ByteForByte()
    {
        var panels = new JsonArray(FullPanel("panel-1", "work"), FullPanel("panel-2", "default"));
        var v5 = new JsonObject { ["panels"] = panels.DeepClone(), ["pages"] = Parse("""{ "ids": ["default", "work"], "currentPageId": "default" }""") };

        var v6 = Migration.Migrate(v5);

        JsonAssert.Equal(panels, v6["panels"]);
    }

    /// <summary>Verifies that <c>layoutSnapshots[].panels[].pageId</c> and every other snapshot field pass through byte-for-byte.</summary>
    [Fact]
    public void Migrate_LayoutSnapshotsUnchanged_ByteForByte()
    {
        var snapshots = new JsonArray(
            new JsonObject
            {
                ["id"] = "snap-1",
                ["name"] = "Snap",
                ["createdAt"] = "2026-01-01T00:00:00",
                ["panels"] = new JsonArray(FullPanel("panel-1", "work")),
            });
        var v5 = new JsonObject { ["layoutSnapshots"] = snapshots.DeepClone() };

        var v6 = Migration.Migrate(v5);

        JsonAssert.Equal(snapshots, v6["layoutSnapshots"]);
    }

    /// <summary>Verifies that <c>systemIconsPanel</c> and an unrecognised top-level key both pass through byte-for-byte.</summary>
    [Fact]
    public void Migrate_SystemIconsPanelAndUnknownTopLevelKeys_Preserved()
    {
        var sip = Parse("""{ "isEnabled": true, "showOnAllVirtualDesktops": false, "iconSize": "Large", "anchorVirtualDesktopId": null, "items": [] }""");
        var v5 = new JsonObject
        {
            ["systemIconsPanel"] = sip.DeepClone(),
            ["someFutureKey"] = "keep-me",
        };

        var v6 = Migration.Migrate(v5);

        JsonAssert.Equal(sip, v6["systemIconsPanel"]);
        Assert.Equal("keep-me", v6["someFutureKey"]!.GetValue<string>());
    }

    /// <summary>Verifies that <see cref="V5ToV6Migration"/> never mutates the input document.</summary>
    [Fact]
    public void Migrate_DoesNotMutateInput()
    {
        var v5 = new JsonObject
        {
            ["pages"] = new JsonObject { ["ids"] = new JsonArray("default", "work"), ["currentPageId"] = "work" },
        };
        var before = v5.DeepClone();

        Migration.Migrate(v5);

        JsonAssert.Equal(before, v5);
    }

    /// <summary>Verifies <see cref="V5ToV6Migration.FromVersion"/>/<see cref="V5ToV6Migration.ToVersion"/>.</summary>
    [Fact]
    public void FromVersion_Is5_ToVersion_Is6()
    {
        Assert.Equal(5, Migration.FromVersion);
        Assert.Equal(6, Migration.ToVersion);
    }

    private static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();

    private static JsonObject FullPanel(string id, string pageId) => new()
    {
        ["id"] = id,
        ["title"] = "Downloads",
        ["pageId"] = pageId,
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
            ["geometryMode"] = "LegacyFreeform",
            ["sizingMode"] = "Manual",
        },
        ["appearance"] = new JsonObject
        {
            ["accentColor"] = "#6B7280",
            ["surfaceOpacity"] = 0.8,
            ["surfaceStyle"] = "Ambient",
            ["textColor"] = "#DDDDDD",
            ["iconSize"] = 48,
            ["iconLayoutPolicy"] = "LegacyStretch",
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
