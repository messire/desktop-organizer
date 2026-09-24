using System.Text.Json.Nodes;
using DesktopOrganizer.Services.Abstractions.Storage;

namespace DesktopOrganizer.Services.Storage.Migrations;

/// <summary>
/// Schema v4 -&gt; v5 (ADR-028 workspace grid + ADR-029 icon layout policy,
/// docs/PLANS/workspace-grid-migration.md): adds four additive fields to every panel's existing
/// <c>geometry</c> object (<c>geometryMode</c>/<c>widthSpanUnits</c>/<c>heightSpanUnits</c>/
/// <c>sizingMode</c>) and one additive field to every panel's existing <c>appearance</c> object
/// (<c>iconLayoutPolicy</c>). Unlike <see cref="V3ToV4Migration"/>'s appearance restructuring, this
/// migration is <b>purely additive</b> — no existing key of <c>geometry</c>/<c>appearance</c> (in
/// particular <c>x</c>/<c>y</c>/<c>width</c>/<c>height</c>) is removed, renamed, or reshaped. Applied
/// identically to the top-level <c>panels</c> array and to every panel inside
/// <c>layoutSnapshots[].panels</c> (ADR-024 R-11), via the shared <see cref="MigratePanel"/> helper —
/// never duplicate this mapping at the two call sites.
/// </summary>
/// <remarks>
/// <para>
/// <b>Existing <c>x</c>/<c>y</c>/<c>width</c>/<c>height</c> are never touched</b> —
/// docs/PLANS/workspace-grid-migration.md §2 "Важно про X/Y/Width/Height": every pre-existing key of
/// <c>geometry</c>/<c>appearance</c> is copied verbatim (<see cref="JsonNode.DeepClone"/>); only the
/// five new keys are appended when absent.
/// </para>
/// <para>
/// <b>Defaults mirror every existing panel's actual pre-ADR-028/029 behavior</b>, not made-up values:
/// <c>geometryMode = "LegacyFreeform"</c> (byte-for-byte today's free positioning, ADR-028),
/// <c>sizingMode = "Manual"</c> (height was never passively re-derived from content before this
/// feature existed, ADR-029 Decision 7), <c>iconLayoutPolicy = "LegacyStretch"</c> (today's ADR-018
/// stretch-gap math, ADR-029 Decision 6). <c>widthSpanUnits</c>/<c>heightSpanUnits</c> are
/// deliberately left <b>absent</b> for every migrated panel: they are only ever meaningful once
/// <c>geometryMode == "GridAligned"</c>, which no panel migrated from v4 ever is — writing them as
/// <see langword="null"/> would still be a key present with a null value, which is not what "not
/// applicable" should look like on disk (<c>StateJson.Options</c> omits null-valued DTO properties on
/// the write side for exactly this shape of field — the migration mirrors that by never adding the
/// key at all).
/// </para>
/// <para>
/// <b>Idempotent by construction</b> (docs/PLANS/workspace-grid-migration.md §5): if any of the five
/// new keys is already present on a <c>geometry</c>/<c>appearance</c> object (e.g. a hypothetical
/// repeated migration run, or a document produced by a build that already wrote v5-shaped data before
/// its <c>schemaVersion</c> bump landed), its existing value is preserved verbatim, never overwritten
/// with the default — the same defensive style <see cref="V1ToV2Migration"/>/<see cref="V2ToV3Migration"/>/
/// <see cref="V3ToV4Migration"/> already use.
/// </para>
/// <para>
/// <b>Unrecognised/corrupted enum string values are not this migration's job to sanitize.</b> If a
/// <c>geometry</c>/<c>appearance</c> object already carries e.g. <c>"geometryMode": "Bogus"</c>, this
/// migration copies it through verbatim (idempotency, above) — a safe fallback to the default happens
/// one layer up, at <c>DesktopOrganizer.UI.State.PanelStateMapper.FromDto</c>, via
/// <c>PanelGeometryModeParser</c>/<c>PanelSizingModeParser</c>/<c>IconLayoutPolicyParser</c> (all
/// case-insensitive, all never throw) — see docs/PLANS/workspace-grid-migration.md §8
/// <c>Migrate_UnknownEnumValue_FallsBackToDefault</c>. Sanitizing here too would just be a second,
/// redundant place doing the same job with a second chance to disagree with the first.
/// </para>
/// <para>
/// This migration does not touch <c>appearance.iconSize</c> at all — see
/// docs/PLANS/workspace-grid-migration.md §7 (the fractional persisted <c>IconSize</c> finding, F-11,
/// is deliberately out of scope: rounding it here would destroy diagnostic evidence before its cause
/// is understood).
/// </para>
/// </remarks>
internal sealed class V4ToV5Migration : ISettingsMigration
{
    private const string DefaultGeometryMode = "LegacyFreeform";
    private const string DefaultSizingMode = "Manual";
    private const string DefaultIconLayoutPolicy = "LegacyStretch";

    /// <inheritdoc />
    public int FromVersion => 4;

    /// <inheritdoc />
    public int ToVersion => 5;

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

    private static JsonArray MigratePanels(JsonArray? v4Panels)
    {
        var result = new JsonArray();
        if (v4Panels is null)
        {
            return result;
        }

        foreach (var node in v4Panels)
        {
            if (node is JsonObject panel)
            {
                result.Add(MigratePanel(panel));
            }
        }

        return result;
    }

    private static JsonArray MigrateLayoutSnapshots(JsonArray v4Snapshots)
    {
        var result = new JsonArray();

        foreach (var node in v4Snapshots)
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
    /// Migrates one v4 panel: every top-level key (<c>id</c>/<c>title</c>/<c>pageId</c>/<c>content</c>)
    /// passes through unchanged; only <c>geometry</c> and <c>appearance</c> get the new additive keys
    /// via <see cref="MigrateGeometry"/>/<see cref="MigrateAppearance"/>. Shared verbatim between the
    /// top-level <c>panels</c> array and every <c>layoutSnapshots[].panels</c> entry (ADR-024 R-11).
    /// </summary>
    private static JsonObject MigratePanel(JsonObject v4Panel)
    {
        var result = new JsonObject();

        foreach (var (key, value) in v4Panel)
        {
            if (key is "geometry" or "appearance")
            {
                continue;
            }

            result[key] = value?.DeepClone();
        }

        result["geometry"] = MigrateGeometry(v4Panel["geometry"] as JsonObject);
        result["appearance"] = MigrateAppearance(v4Panel["appearance"] as JsonObject);

        return result;
    }

    private static JsonObject MigrateGeometry(JsonObject? v4Geometry)
    {
        var result = new JsonObject();

        if (v4Geometry is not null)
        {
            foreach (var (key, value) in v4Geometry)
            {
                result[key] = value?.DeepClone();
            }
        }

        if (!result.ContainsKey("geometryMode"))
        {
            result["geometryMode"] = DefaultGeometryMode;
        }

        // widthSpanUnits/heightSpanUnits intentionally stay absent when missing — see the type-level
        // remarks: null means "not a GridAligned panel", never written as an explicit null.
        if (!result.ContainsKey("sizingMode"))
        {
            result["sizingMode"] = DefaultSizingMode;
        }

        return result;
    }

    private static JsonObject MigrateAppearance(JsonObject? v4Appearance)
    {
        var result = new JsonObject();

        if (v4Appearance is not null)
        {
            foreach (var (key, value) in v4Appearance)
            {
                result[key] = value?.DeepClone();
            }
        }

        if (!result.ContainsKey("iconLayoutPolicy"))
        {
            result["iconLayoutPolicy"] = DefaultIconLayoutPolicy;
        }

        return result;
    }
}
