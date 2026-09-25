using System.Text.Json.Nodes;
using DesktopOrganizer.Services.Abstractions.Storage;

namespace DesktopOrganizer.Services.Storage.Migrations;

/// <summary>
/// Schema v3 -&gt; v4 (ADR-025, docs/PLANS/phase-5-ambient-accent-ui.md §1 D-5): replaces each panel's
/// <c>appearance.backgroundColor</c>/<c>appearance.backgroundOpacity</c> pair with
/// <c>appearance.accentColor</c>/<c>appearance.surfaceOpacity</c>/<c>appearance.surfaceStyle</c> — the
/// panel's fill colour is no longer user-chosen (it becomes a neutral, token-driven surface); what the
/// user picked survives only as a semantic accent. Applied identically to every panel in the
/// top-level <c>panels</c> array and to every panel inside <c>layoutSnapshots[].panels</c> (ADR-024
/// R-11) via the shared <see cref="MapAppearance"/> helper, so a panel captured in a snapshot and a
/// live panel end up with byte-for-byte the same shape — never duplicate this mapping at the two call
/// sites.
/// </summary>
/// <remarks>
/// <para>
/// <b>The <c>backgroundColor == "#1E1E1E"</c> -&gt; <c>accentColor == "#6B7280"</c> branch is the one
/// lossy step in this migration.</b> <c>#1E1E1E</c> is schema v1-v3's historical default panel fill —
/// not a colour any user consciously chose — so carrying it forward as an accent would leave every
/// panel that was never customised with an almost-black indicator. Any other <c>backgroundColor</c>
/// value is assumed to be a deliberate user choice (e.g. a category colour) and moves to
/// <c>accentColor</c> verbatim, preserving that panel's visual identity as its new accent.
/// </para>
/// <para>
/// <b>Missing fields get the UI-layer panel model's own defaults, not made-up ones</b> — same
/// defensive stance as <see cref="V2ToV3Migration"/>: a v3 panel is not expected to be missing
/// <c>appearance</c> properties in practice, but this migration reuses exactly the default values
/// <c>ChildWindowModel</c>'s own field initializers use (see the <c>Default*</c> constants below), so
/// a partially-formed document migrates to the same panel shape a brand-new <c>ChildWindowModel</c>
/// would produce. The defaults (including the legacy background default the lossy branch above
/// compares against) are duplicated here as literal constants, deliberately:
/// <c>DesktopOrganizer.Services</c> does not (and must not) reference <c>DesktopOrganizer.UI</c> — see
/// ADR-024 D-3 on the layering this store respects. In particular <c>DefaultAccentColor</c> mirrors
/// <c>Theming.AccentPalette.Neutral</c> and <c>DefaultSurfaceStyle</c> mirrors
/// <c>PanelSurfaceStyle.Ambient</c>'s serialized name — both UI-layer types this migration cannot see.
/// </para>
/// <para>
/// <c>backgroundColor</c>/<c>backgroundOpacity</c> never appear in this migration's output — they are
/// dead schema-v3 keys, fully replaced by <c>accentColor</c>/<c>surfaceOpacity</c>/<c>surfaceStyle</c>.
/// <c>textColor</c> and <c>iconSize</c> pass through unchanged (still per-panel overrides — see
/// ADR-025 D-4 / backlog DO-013).
/// </para>
/// </remarks>
internal sealed class V3ToV4Migration : ISettingsMigration
{
    // ChildWindowModel field-initializer defaults, duplicated here deliberately — see the type-level
    // remarks on why DesktopOrganizer.Services cannot reference DesktopOrganizer.UI to read them off
    // the real type.
    private const string LegacyDefaultBackgroundColor = "#1E1E1E";
    private const double DefaultBackgroundOpacity = 0.8;
    private const string DefaultAccentColor = "#6B7280"; // Theming.AccentPalette.Neutral
    private const string DefaultSurfaceStyle = "Ambient"; // PanelSurfaceStyle.Ambient
    private const string DefaultTextColor = "#DDDDDD";
    private const double DefaultIconSize = 48;

    /// <inheritdoc />
    public int FromVersion => 3;

    /// <inheritdoc />
    public int ToVersion => 4;

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

    private static JsonArray MigratePanels(JsonArray? v3Panels)
    {
        var result = new JsonArray();
        if (v3Panels is null)
        {
            return result;
        }

        foreach (var node in v3Panels)
        {
            if (node is JsonObject panel)
            {
                result.Add(MigratePanel(panel));
            }
        }

        return result;
    }

    private static JsonArray MigrateLayoutSnapshots(JsonArray v3Snapshots)
    {
        var result = new JsonArray();

        foreach (var node in v3Snapshots)
        {
            if (node is not JsonObject snapshot)
            {
                continue;
            }

            var migratedSnapshot = new JsonObject();
            if (snapshot.ContainsKey("id"))
            {
                migratedSnapshot["id"] = snapshot["id"]?.DeepClone();
            }

            if (snapshot.ContainsKey("name"))
            {
                migratedSnapshot["name"] = snapshot["name"]?.DeepClone();
            }

            if (snapshot.ContainsKey("createdAt"))
            {
                migratedSnapshot["createdAt"] = snapshot["createdAt"]?.DeepClone();
            }

            migratedSnapshot["panels"] = MigratePanels(snapshot["panels"] as JsonArray);
            result.Add(migratedSnapshot);
        }

        return result;
    }

    /// <summary>
    /// Migrates one v3 panel: every top-level key (<c>id</c>/<c>title</c>/<c>pageId</c>/
    /// <c>geometry</c>/<c>content</c>) passes through unchanged, and only <c>appearance</c> is
    /// re-shaped via <see cref="MapAppearance"/>. Shared verbatim between the top-level <c>panels</c>
    /// array and every <c>layoutSnapshots[].panels</c> entry (ADR-024 R-11) — never duplicate this
    /// logic at the two call sites.
    /// </summary>
    private static JsonObject MigratePanel(JsonObject v3Panel)
    {
        var result = new JsonObject();

        foreach (var (key, value) in v3Panel)
        {
            if (key == "appearance")
            {
                continue;
            }

            result[key] = value?.DeepClone();
        }

        result["appearance"] = MapAppearance(v3Panel["appearance"] as JsonObject);

        return result;
    }

    /// <summary>
    /// Maps a v3 <c>appearance</c> object to its v4 shape (see the type-level remarks for the full
    /// rule table). Shared by every panel this migration touches, top-level or inside a layout
    /// snapshot — never duplicated.
    /// </summary>
    /// <remarks>
    /// Every field that simply passes through (<c>backgroundOpacity</c> -&gt; <c>surfaceOpacity</c>,
    /// <c>textColor</c>, <c>iconSize</c>, and <c>backgroundColor</c> when it carries over verbatim as
    /// <c>accentColor</c>) is preserved via <c>GetNodeOrDefault</c> — a <see cref="JsonNode.DeepClone"/>
    /// of the original node, not a round-trip through a CLR <see langword="double"/>/<see langword="string"/>
    /// and back. <c>JsonValue.Create(double)</c> reformats e.g. the JSON literal <c>1.0</c> as
    /// <c>1</c>, which would make golden-fixture byte-for-byte comparisons fail on a value that never
    /// actually changed.
    /// </remarks>
    private static JsonObject MapAppearance(JsonObject? v3Appearance)
    {
        var backgroundColorValue = GetString(v3Appearance, "backgroundColor", LegacyDefaultBackgroundColor);
        var isLegacyDefaultBackground = backgroundColorValue == LegacyDefaultBackgroundColor;

        var accentColor = isLegacyDefaultBackground
            ? JsonValue.Create(DefaultAccentColor)!
            : GetNodeOrDefault(v3Appearance, "backgroundColor", DefaultAccentColor);

        return new JsonObject
        {
            ["accentColor"] = accentColor,
            ["surfaceOpacity"] = GetNodeOrDefault(v3Appearance, "backgroundOpacity", DefaultBackgroundOpacity),
            ["surfaceStyle"] = JsonValue.Create(DefaultSurfaceStyle),
            ["textColor"] = GetNodeOrDefault(v3Appearance, "textColor", DefaultTextColor),
            ["iconSize"] = GetNodeOrDefault(v3Appearance, "iconSize", DefaultIconSize),
        };
    }

    private static string GetString(JsonObject? obj, string key, string defaultValue)
    {
        if (obj is null || !obj.TryGetPropertyValue(key, out var value) || value is null)
        {
            return defaultValue;
        }

        try
        {
            return value.GetValue<string>();
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException)
        {
            return defaultValue;
        }
    }

    private static JsonNode GetNodeOrDefault(JsonObject? obj, string key, string defaultValue)
        => obj is not null && obj.TryGetPropertyValue(key, out var value) && value is not null
            ? value.DeepClone()!
            : JsonValue.Create(defaultValue)!;

    private static JsonNode GetNodeOrDefault(JsonObject? obj, string key, double defaultValue)
        => obj is not null && obj.TryGetPropertyValue(key, out var value) && value is not null
            ? value.DeepClone()!
            : JsonValue.Create(defaultValue)!;
}
