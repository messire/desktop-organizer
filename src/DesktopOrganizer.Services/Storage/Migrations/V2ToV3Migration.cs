using System.Text.Json.Nodes;
using DesktopOrganizer.Services.Abstractions.Storage;

namespace DesktopOrganizer.Services.Storage.Migrations;

/// <summary>
/// Schema v2 -&gt; v3 (ADR-024 D-9): groups each panel's flat property set into
/// <c>geometry</c>/<c>appearance</c>/<c>content</c> sub-objects, the shape PHASE-4's target format
/// specifies. Applied identically to every panel in the top-level <c>panels</c> array and to every
/// panel inside <c>layoutSnapshots[].panels</c> (ADR-024 R-11) via the shared
/// <see cref="GroupPanel"/> helper, so a panel captured in a snapshot and a live panel end up with
/// byte-for-byte the same shape.
/// </summary>
/// <remarks>
/// <para>
/// <b>Missing fields get the UI-layer panel model's own defaults, not made-up ones.</b> A v2 panel
/// is not expected to be missing properties in practice — v1 -&gt; v2 carries every property
/// through — but this migration is defensive about it anyway, reusing exactly the default values
/// <c>ChildWindowModel</c>'s own field initializers use (see the <c>Default*</c> constants below),
/// so a partially-formed document migrates to the same panel shape a brand-new
/// <c>ChildWindowModel</c> would produce. The defaults are duplicated here as literal constants,
/// deliberately: <c>DesktopOrganizer.Services</c> does not (and must not) reference
/// <c>DesktopOrganizer.UI</c> — see ADR-024 D-3 on the layering this store respects.
/// </para>
/// <para>
/// <b><c>windowState</c> becomes a string here, not in v1 -&gt; v2.</b> Unlike <c>viewMode</c> /
/// <c>contentFilter</c> / <c>sortMode</c> (already converted to strings in schema v2, ADR-024
/// R-10), <c>System.Windows.WindowState</c> stayed numeric through v2 — grouping it into
/// <c>geometry</c> here is the natural point to also convert it, since PHASE-4's target v3 shape
/// (<c>"windowState":"Normal"</c>) requires a string, and the same historical-freeze concern that
/// drove R-10 applies here too: <see cref="WindowStateNamesAtV2"/> is fixed at the schema-v2-era
/// member layout of <c>System.Windows.WindowState</c>, not read off the live enum.
/// </para>
/// </remarks>
internal sealed class V2ToV3Migration : ISettingsMigration
{
    // ChildWindowModel field-initializer defaults, duplicated here deliberately — see the type-level
    // remarks on why DesktopOrganizer.Services cannot reference DesktopOrganizer.UI to read them off
    // the real type.
    private const string DefaultTitle = "";
    private const string DefaultBackgroundColor = "#1E1E1E";
    private const double DefaultBackgroundOpacity = 0.8;
    private const string DefaultTextColor = "#DDDDDD";
    private const double DefaultIconSize = 48;
    private const string DefaultViewMode = "List";
    private const string DefaultContentFilter = "ShowAll";
    private const string DefaultSortMode = "Manual";
    private const bool DefaultIsVisible = true;
    private const string DefaultWindowState = "Normal";
    private const bool DefaultIsRolledUp = false;
    private const string DefaultPageId = "default";
    private const int DefaultGeometryComponent = 0;

    // Historical v2 numeric -> name mapping for System.Windows.WindowState, frozen the same way as
    // V1ToV2Migration's enum tables — see the type-level remarks. Do NOT change when the live enum
    // changes.
    private static readonly IReadOnlyDictionary<int, string> WindowStateNamesAtV2 = new Dictionary<int, string>
    {
        [0] = "Normal",
        [1] = "Minimized",
        [2] = "Maximized",
    };

    /// <inheritdoc />
    public int FromVersion => 2;

    /// <inheritdoc />
    public int ToVersion => 3;

    /// <inheritdoc />
    public JsonObject Migrate(JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var result = new JsonObject
        {
            ["panels"] = MigratePanels(document["panels"] as JsonArray),
        };

        if (document["pages"] is JsonObject pages)
        {
            result["pages"] = pages.DeepClone();
        }

        if (document["layoutSnapshots"] is JsonArray snapshots)
        {
            result["layoutSnapshots"] = MigrateLayoutSnapshots(snapshots);
        }

        if (document["systemIconsPanel"] is JsonObject systemIconsPanel)
        {
            result["systemIconsPanel"] = systemIconsPanel.DeepClone();
        }

        return result;
    }

    private static JsonArray MigratePanels(JsonArray? v2Panels)
    {
        var result = new JsonArray();
        if (v2Panels is null)
        {
            return result;
        }

        foreach (var node in v2Panels)
        {
            if (node is JsonObject panel)
            {
                result.Add(GroupPanel(panel));
            }
        }

        return result;
    }

    private static JsonArray MigrateLayoutSnapshots(JsonArray v2Snapshots)
    {
        var result = new JsonArray();

        foreach (var node in v2Snapshots)
        {
            if (node is not JsonObject snapshot)
            {
                continue;
            }

            var groupedSnapshot = new JsonObject();
            if (snapshot.ContainsKey("id"))
            {
                groupedSnapshot["id"] = snapshot["id"]?.DeepClone();
            }

            if (snapshot.ContainsKey("name"))
            {
                groupedSnapshot["name"] = snapshot["name"]?.DeepClone();
            }

            if (snapshot.ContainsKey("createdAt"))
            {
                groupedSnapshot["createdAt"] = snapshot["createdAt"]?.DeepClone();
            }

            groupedSnapshot["panels"] = MigratePanels(snapshot["panels"] as JsonArray);
            result.Add(groupedSnapshot);
        }

        return result;
    }

    /// <summary>
    /// Groups one v2 panel's flat properties into v3's <c>geometry</c>/<c>appearance</c>/<c>content</c>
    /// shape. Shared verbatim between the top-level <c>panels</c> array and every
    /// <c>layoutSnapshots[].panels</c> entry (ADR-024 R-11) — never duplicate this logic at the two
    /// call sites.
    /// </summary>
    private static JsonObject GroupPanel(JsonObject v2Panel)
    {
        var position = v2Panel["position"] as JsonObject;
        var size = v2Panel["size"] as JsonObject;

        var geometry = new JsonObject
        {
            ["x"] = GetNestedOrDefault(position, "x", DefaultGeometryComponent),
            ["y"] = GetNestedOrDefault(position, "y", DefaultGeometryComponent),
            ["width"] = GetNestedOrDefault(size, "width", DefaultGeometryComponent),
            ["height"] = GetNestedOrDefault(size, "height", DefaultGeometryComponent),
            ["monitorDeviceId"] = GetOrNull(v2Panel, "monitorDeviceId"),
            ["windowState"] = MapWindowState(v2Panel["windowState"]),
            ["isRolledUp"] = GetOrDefault(v2Panel, "isRolledUp", DefaultIsRolledUp),
            ["isVisible"] = GetOrDefault(v2Panel, "isVisible", DefaultIsVisible),
        };

        var appearance = new JsonObject
        {
            ["backgroundColor"] = GetOrDefault(v2Panel, "backgroundColor", DefaultBackgroundColor),
            ["backgroundOpacity"] = GetOrDefault(v2Panel, "backgroundOpacity", DefaultBackgroundOpacity),
            ["textColor"] = GetOrDefault(v2Panel, "textColor", DefaultTextColor),
            ["iconSize"] = GetOrDefault(v2Panel, "iconSize", DefaultIconSize),
        };

        var content = new JsonObject
        {
            ["viewMode"] = GetOrDefault(v2Panel, "viewMode", DefaultViewMode),
            ["contentFilter"] = GetOrDefault(v2Panel, "contentFilter", DefaultContentFilter),
            ["sortMode"] = GetOrDefault(v2Panel, "sortMode", DefaultSortMode),
            ["sourceFolderPath"] = GetOrNull(v2Panel, "sourceFolderPath"),
            ["filePaths"] = GetOrDefaultArray(v2Panel, "filePaths"),
        };

        return new JsonObject
        {
            ["id"] = GetOrNull(v2Panel, "id"),
            ["title"] = GetOrDefault(v2Panel, "title", DefaultTitle),
            ["pageId"] = GetOrDefault(v2Panel, "pageId", DefaultPageId),
            ["geometry"] = geometry,
            ["appearance"] = appearance,
            ["content"] = content,
        };
    }

    private static JsonNode MapWindowState(JsonNode? numericNode)
    {
        if (numericNode is not null)
        {
            try
            {
                var numeric = numericNode.GetValue<int>();
                if (WindowStateNamesAtV2.TryGetValue(numeric, out var name))
                {
                    return JsonValue.Create(name)!;
                }
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException)
            {
                // Fall through to the default below.
            }
        }

        return JsonValue.Create(DefaultWindowState)!;
    }

    private static JsonNode? GetOrNull(JsonObject obj, string key)
        => obj.TryGetPropertyValue(key, out var value) ? value?.DeepClone() : null;

    private static JsonNode GetOrDefault(JsonObject obj, string key, string defaultValue)
        => obj.TryGetPropertyValue(key, out var value) && value is not null
            ? value.DeepClone()!
            : JsonValue.Create(defaultValue)!;

    private static JsonNode GetOrDefault(JsonObject obj, string key, double defaultValue)
        => obj.TryGetPropertyValue(key, out var value) && value is not null
            ? value.DeepClone()!
            : JsonValue.Create(defaultValue)!;

    private static JsonNode GetOrDefault(JsonObject obj, string key, bool defaultValue)
        => obj.TryGetPropertyValue(key, out var value) && value is not null
            ? value.DeepClone()!
            : JsonValue.Create(defaultValue)!;

    private static JsonNode GetOrDefaultArray(JsonObject obj, string key)
        => obj.TryGetPropertyValue(key, out var value) && value is JsonArray
            ? value.DeepClone()!
            : new JsonArray();

    private static JsonNode GetNestedOrDefault(JsonObject? nested, string key, int defaultValue)
        => nested is not null && nested.TryGetPropertyValue(key, out var value) && value is not null
            ? value.DeepClone()!
            : JsonValue.Create(defaultValue)!;
}
