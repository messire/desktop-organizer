using System.Text.Json;
using System.Text.Json.Nodes;
using DesktopOrganizer.Services.Abstractions.Storage;

namespace DesktopOrganizer.Services.Storage.Migrations;

/// <summary>
/// Schema v1 -&gt; v2 (ADR-024 D-9): unpacks the JSON-string-in-JSON <c>PanelJsonItems</c> field
/// inside every layout snapshot into real, typed <c>panels</c> objects, and unifies the JSON
/// dialect across the whole document — PascalCase -&gt; camelCase, numeric enum values -&gt; their
/// v1-era member names — replacing the two incompatible dialects the four legacy writers each used
/// (ADR-024 §0.1).
/// </summary>
/// <remarks>
/// <para>
/// <b>Enum member names are hardcoded, not read off today's enum (ADR-024 R-10).</b> The UI-layer
/// <c>PanelViewMode</c>, <c>PanelContentFilter</c> and <c>PanelSortMode</c> enums were all
/// serialized by v1 as their bare numeric underlying value (<c>System.Text.Json</c>'s default for
/// an enum with no <c>JsonStringEnumConverter</c> registered). Mapping those numbers back to names
/// by calling <c>((SomeEnum)value).ToString()</c> against the <em>current</em>, live enum type would
/// only be correct for as long as nobody ever inserts a member in the middle of one of these three
/// enums between releases — exactly the "silent reinterpretation" failure mode ADR-024 R-10 names.
/// Instead, the number-&gt;name tables below are a <b>frozen historical snapshot</b>, fixed at the
/// moment this migration was written (2026-09-19) and permanently associated with schema v1, wholly
/// independent of whatever these enums look like in the assembly that runs this migration years
/// from now. Do not "simplify" these tables by reading them off the live enum type, and do not edit
/// them when the live enums change — add a new migration instead.
/// </para>
/// <para>
/// <b>A snapshot with any unparsable <c>PanelJsonItems</c> entry is dropped whole, not
/// truncated.</b> A layout snapshot is restored all-or-nothing — the panel restore path removes
/// every current panel first and only then recreates panels from the snapshot — so silently
/// restoring a snapshot missing one of its panels would be a worse outcome than the snapshot simply
/// not surviving the migration at all. Only the corrupt snapshot is dropped; every other,
/// well-formed snapshot in the document — and the rest of the document itself — is unaffected.
/// </para>
/// </remarks>
internal sealed class V1ToV2Migration : ISettingsMigration
{
    // Historical v1 numeric -> name mapping for DesktopOrganizer.UI.Models.PanelViewMode. Frozen —
    // see the type-level remarks. Do NOT change when the live enum changes.
    private static readonly IReadOnlyDictionary<int, string> ViewModeNamesAtV1 = new Dictionary<int, string>
    {
        [0] = "List",
        [1] = "Grid",
    };

    // Historical v1 numeric -> name mapping for DesktopOrganizer.UI.Models.PanelContentFilter.
    private static readonly IReadOnlyDictionary<int, string> ContentFilterNamesAtV1 = new Dictionary<int, string>
    {
        [0] = "ShowAll",
        [1] = "HideAll",
        [2] = "HideShortcuts",
        [3] = "FoldersOnly",
    };

    // Historical v1 numeric -> name mapping for DesktopOrganizer.UI.Models.PanelSortMode.
    private static readonly IReadOnlyDictionary<int, string> SortModeNamesAtV1 = new Dictionary<int, string>
    {
        [0] = "Manual",
        [1] = "Name",
    };

    /// <inheritdoc />
    public int FromVersion => 1;

    /// <inheritdoc />
    public int ToVersion => 2;

    /// <inheritdoc />
    public JsonObject Migrate(JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var result = new JsonObject
        {
            ["panels"] = MigratePanels(document["panels"] as JsonArray),
            ["pages"] = MigratePages(document["pages"] as JsonObject),
            ["layoutSnapshots"] = MigrateLayoutSnapshots(document["layoutSnapshots"] as JsonArray),
        };

        if (document["systemIconsPanel"] is JsonObject systemIconsPanel)
        {
            result["systemIconsPanel"] = MigrateSystemIconsPanel(systemIconsPanel);
        }

        return result;
    }

    private static JsonArray MigratePanels(JsonArray? v1Panels)
    {
        var result = new JsonArray();
        if (v1Panels is null)
        {
            return result;
        }

        foreach (var node in v1Panels)
        {
            if (node is JsonObject panel)
            {
                result.Add(MigratePanel(panel));
            }
        }

        return result;
    }

    private static JsonObject MigratePanel(JsonObject v1Panel)
    {
        var v2Panel = new JsonObject();

        CopyIfPresent(v1Panel, "Id", v2Panel, "id");
        CopyIfPresent(v1Panel, "Title", v2Panel, "title");
        CopyIfPresent(v1Panel, "BackgroundColor", v2Panel, "backgroundColor");
        CopyIfPresent(v1Panel, "BackgroundOpacity", v2Panel, "backgroundOpacity");
        CopyIfPresent(v1Panel, "TextColor", v2Panel, "textColor");
        CopyIfPresent(v1Panel, "IconSize", v2Panel, "iconSize");
        v2Panel["viewMode"] = MapEnumName(v1Panel["ViewMode"], ViewModeNamesAtV1);
        v2Panel["contentFilter"] = MapEnumName(v1Panel["ContentFilter"], ContentFilterNamesAtV1);
        v2Panel["sortMode"] = MapEnumName(v1Panel["SortMode"], SortModeNamesAtV1);
        CopyIfPresent(v1Panel, "MonitorDeviceId", v2Panel, "monitorDeviceId");
        v2Panel["position"] = MigrateNestedGeometry(v1Panel["Position"] as JsonObject, "X", "x", "Y", "y");
        v2Panel["size"] = MigrateNestedGeometry(v1Panel["Size"] as JsonObject, "Width", "width", "Height", "height");
        CopyIfPresent(v1Panel, "IsVisible", v2Panel, "isVisible");
        CopyIfPresent(v1Panel, "WindowState", v2Panel, "windowState");
        CopyIfPresent(v1Panel, "IsRolledUp", v2Panel, "isRolledUp");
        CopyIfPresent(v1Panel, "PageId", v2Panel, "pageId");
        CopyIfPresent(v1Panel, "SourceFolderPath", v2Panel, "sourceFolderPath");
        CopyIfPresent(v1Panel, "FilePaths", v2Panel, "filePaths");

        return v2Panel;
    }

    /// <summary>
    /// Renames a v1 <c>Position</c> ({X,Y,IsEmpty}) or <c>Size</c> ({Width,Height,IsEmpty}) nested
    /// object to camelCase, carrying through whichever of the two named fields (plus <c>IsEmpty</c>,
    /// if present) are actually there. This is a rename only — the fields themselves are not
    /// regrouped or dropped until v2 -&gt; v3 (<c>V2ToV3Migration</c>), which is the migration that
    /// actually restructures geometry per ADR-024 D-9.
    /// </summary>
    private static JsonObject MigrateNestedGeometry(
        JsonObject? v1Nested, string firstSourceKey, string firstTargetKey, string secondSourceKey, string secondTargetKey)
    {
        var result = new JsonObject();
        if (v1Nested is null)
        {
            return result;
        }

        CopyIfPresent(v1Nested, firstSourceKey, result, firstTargetKey);
        CopyIfPresent(v1Nested, secondSourceKey, result, secondTargetKey);
        CopyIfPresent(v1Nested, "IsEmpty", result, "isEmpty");
        return result;
    }

    private static JsonObject MigratePages(JsonObject? v1Pages)
    {
        var result = new JsonObject();
        if (v1Pages is null)
        {
            result["ids"] = new JsonArray(JsonValue.Create("default"));
            result["currentPageId"] = JsonValue.Create("default");
            return result;
        }

        CopyIfPresent(v1Pages, "Ids", result, "ids");
        CopyIfPresent(v1Pages, "CurrentPageId", result, "currentPageId");
        return result;
    }

    private static JsonArray MigrateLayoutSnapshots(JsonArray? v1Snapshots)
    {
        var result = new JsonArray();
        if (v1Snapshots is null)
        {
            return result;
        }

        foreach (var node in v1Snapshots)
        {
            if (node is not JsonObject snapshot)
            {
                continue;
            }

            var migrated = MigrateSnapshot(snapshot);
            if (migrated is not null)
            {
                result.Add(migrated);
            }

            // else: the whole snapshot is dropped — see the type-level remarks on why a partial
            // snapshot is worse than a missing one. The rest of the document, including every other
            // snapshot, is unaffected.
        }

        return result;
    }

    /// <summary>
    /// Migrates one v1 layout snapshot, unpacking each of its <c>PanelJsonItems</c> JSON-string
    /// entries into a real panel object. Returns <see langword="null"/> — signalling "drop this
    /// snapshot" to <see cref="MigrateLayoutSnapshots"/> — if any entry is missing, not a string, or
    /// not parsable/well-formed JSON.
    /// </summary>
    private static JsonObject? MigrateSnapshot(JsonObject v1Snapshot)
    {
        var panels = new JsonArray();

        if (v1Snapshot["PanelJsonItems"] is JsonArray panelJsonItems)
        {
            foreach (var itemNode in panelJsonItems)
            {
                string? raw;
                try
                {
                    raw = itemNode?.GetValue<string>();
                }
                catch (InvalidOperationException)
                {
                    return null;
                }

                if (string.IsNullOrEmpty(raw))
                {
                    return null;
                }

                JsonNode? parsed;
                try
                {
                    parsed = JsonNode.Parse(raw);
                }
                catch (JsonException)
                {
                    return null;
                }

                if (parsed is not JsonObject panelObject)
                {
                    return null;
                }

                panels.Add(MigratePanel(panelObject));
            }
        }

        var result = new JsonObject();
        CopyIfPresent(v1Snapshot, "Id", result, "id");
        CopyIfPresent(v1Snapshot, "Name", result, "name");
        CopyIfPresent(v1Snapshot, "CreatedAt", result, "createdAt");
        result["panels"] = panels;
        return result;
    }

    private static JsonObject MigrateSystemIconsPanel(JsonObject v1SystemIconsPanel)
    {
        var result = new JsonObject();
        CopyIfPresent(v1SystemIconsPanel, "IsEnabled", result, "isEnabled");
        CopyIfPresent(v1SystemIconsPanel, "ShowOnAllVirtualDesktops", result, "showOnAllVirtualDesktops");
        CopyIfPresent(v1SystemIconsPanel, "IconSize", result, "iconSize");
        CopyIfPresent(v1SystemIconsPanel, "AnchorVirtualDesktopId", result, "anchorVirtualDesktopId");

        if (v1SystemIconsPanel["Items"] is JsonArray items)
        {
            var migratedItems = new JsonArray();
            foreach (var itemNode in items)
            {
                if (itemNode is not JsonObject item)
                {
                    continue;
                }

                var migratedItem = new JsonObject();
                CopyIfPresent(item, "Kind", migratedItem, "kind");
                CopyIfPresent(item, "IsVisible", migratedItem, "isVisible");
                CopyIfPresent(item, "Order", migratedItem, "order");
                migratedItems.Add(migratedItem);
            }

            result["items"] = migratedItems;
        }

        return result;
    }

    /// <summary>
    /// Copies <paramref name="sourceKey"/> from <paramref name="source"/> to
    /// <paramref name="targetKey"/> on <paramref name="target"/> — renaming only, value untouched —
    /// but only if <paramref name="sourceKey"/> actually exists on <paramref name="source"/>
    /// (including when it is explicitly JSON <c>null</c>, which System.Text.Json's default
    /// serialization writes for every reference-typed property, e.g. a panel with no
    /// <c>SourceFolderPath</c>). A key that is genuinely absent stays absent on the target too,
    /// rather than being invented as an explicit <c>null</c>.
    /// </summary>
    private static void CopyIfPresent(JsonObject source, string sourceKey, JsonObject target, string targetKey)
    {
        if (!source.ContainsKey(sourceKey))
        {
            return;
        }

        target[targetKey] = source[sourceKey]?.DeepClone();
    }

    /// <summary>
    /// Maps a v1 numeric enum value to its frozen v1-era member name (see the type-level remarks on
    /// <see cref="ViewModeNamesAtV1"/> and friends). A missing property or an out-of-range/unparsable
    /// numeric value both degrade to the enum's default member (index 0) — matching
    /// <c>System.Text.Json</c>'s own "missing property deserializes to <see langword="default"/>"
    /// behavior for these particular enums, rather than failing the whole panel over one bad value.
    /// </summary>
    private static JsonValue MapEnumName(JsonNode? numericNode, IReadOnlyDictionary<int, string> namesAtV1)
    {
        if (numericNode is not null)
        {
            try
            {
                var numeric = numericNode.GetValue<int>();
                if (namesAtV1.TryGetValue(numeric, out var name))
                {
                    return JsonValue.Create(name)!;
                }
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException)
            {
                // Fall through to the default below.
            }
        }

        return JsonValue.Create(namesAtV1[0])!;
    }
}
