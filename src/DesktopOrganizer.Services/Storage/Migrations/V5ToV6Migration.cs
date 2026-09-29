using System.Text.Json.Nodes;
using DesktopOrganizer.Services.Abstractions.Storage;

namespace DesktopOrganizer.Services.Storage.Migrations;

/// <summary>
/// Schema v5 -&gt; v6 (ADR-043, docs/PLANS/page-display-names.md §2.3): reshapes <c>pages</c> from a
/// flat array of identifiers (<c>pages.ids[]</c>) to an ordered array of records carrying a stable
/// <c>id</c> plus an optional display <c>name</c> (<c>pages.items[{id,name?}]</c>) — pages get the same
/// "stable id + separate display name" shape panels already have (<c>PanelStateDto.Id</c> +
/// <c>Title</c>). Every other top-level payload key — <c>panels</c>, <c>layoutSnapshots</c>,
/// <c>systemIconsPanel</c>, and any key this migration does not know about — is copied through
/// unchanged via <see cref="JsonNode.DeepClone"/>; unlike <see cref="V4ToV5Migration"/>'s explicit
/// four-key allow-list, this migration copies every key except <c>pages</c> itself, since there is
/// nothing else to reshape at this schema step and an allow-list would risk silently dropping an
/// unrecognised key. In particular <c>panels[].pageId</c> and
/// <c>layoutSnapshots[].panels[].pageId</c> are untouched byte-for-byte — page ids are stable and
/// never renamed by this (or any) migration (docs/PLANS/page-display-names.md §2.1 "Alternatives",
/// option C, was rejected for exactly this reason).
/// </summary>
/// <remarks>
/// <para>
/// <b>Per-id mapping (docs/PLANS/page-display-names.md §2.3 step 3):</b> for every string entry
/// <c>s</c> of the old <c>pages.ids</c> array, <c>s == "default"</c> (ordinal) becomes
/// <c>{"id":"default"}</c> — no <c>name</c> key at all, not an explicit <c>null</c>, so the unnamed
/// default page keeps localizing itself (<c>Page_Default_DisplayName</c>) exactly as it did before
/// this migration ever ran. Every other id <c>s</c> becomes <c>{"id":s,"name":s}</c> — the user sees
/// literally the same string they saw before (the raw id), now stored as that page's data-not-code
/// display name instead of as the only thing labelling the page. A non-string or <c>null</c> entry is
/// skipped; a malformed <c>pages.ids</c> array producing zero entries yields <c>items: []</c>, which
/// <c>PagesStateMapper.FromDto</c> (one layer up, in <c>DesktopOrganizer.UI</c>) already treats the
/// same way an entirely-absent <c>pages</c> section is treated (falls back to a lone <c>default</c>
/// page) — this migration does not duplicate that fallback itself.
/// </para>
/// <para>
/// <b>Idempotent by construction.</b> If <c>pages</c> already carries an <c>items</c> array — i.e. is
/// already v6-shaped, the shape a hypothetical repeated migration run or an already-current document
/// mistakenly re-migrated would present — the whole <c>pages</c> object is copied through verbatim via
/// <see cref="JsonNode.DeepClone"/>, dropping only a co-present legacy <c>ids</c> key (the single
/// source of truth in v6 is <c>items</c>; <c>ids</c> must never coexist with it on disk). Every other
/// key of <c>pages</c> — most importantly <c>currentPageId</c>, and any future addition — passes
/// through unchanged either way, matching the <c>V4ToV5Migration</c>/<c>V3ToV4Migration</c> idempotency
/// discipline this migration chain already follows for every other step.
/// </para>
/// <para>
/// <b>Duplicate ids are not sanitized here</b> — same defensive stance as sibling migrations:
/// sanitizing on-disk data is <c>PagesStateMapper.FromDto</c>'s job (one layer up), not a migration's;
/// a migration that also sanitized would be a second place with its own chance to disagree with the
/// first (docs/PLANS/page-display-names.md §2.3 step 3, mirroring <c>V4ToV5Migration</c>'s own remarks
/// on not sanitizing unrecognised enum strings).
/// </para>
/// </remarks>
internal sealed class V5ToV6Migration : ISettingsMigration
{
    private const string DefaultPageId = "default";

    /// <inheritdoc />
    public int FromVersion => 5;

    /// <inheritdoc />
    public int ToVersion => 6;

    /// <inheritdoc />
    public JsonObject Migrate(JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var result = new JsonObject();

        foreach (var (key, value) in document)
        {
            if (key == "pages")
            {
                continue;
            }

            result[key] = value?.DeepClone();
        }

        // Rule 2 (docs/PLANS/page-display-names.md §2.3): pages absent, JSON null, or not an object at
        // all (e.g. a stray array/string/number from a hand-edited document) all mean the same thing —
        // no pages section survives into the v6 output. PagesStateMapper.FromDto / ApplicationStateService.Load
        // (one layer up, in DesktopOrganizer.UI) already default a wholly-absent pages section to a lone
        // "default" page; this migration must not invent that fallback itself, only preserve the
        // "absent" signal so that fallback still fires.
        if (document["pages"] is JsonObject v5Pages)
        {
            result["pages"] = MigratePages(v5Pages);
        }

        return result;
    }

    private static JsonObject MigratePages(JsonObject v5Pages)
    {
        var result = new JsonObject();

        if (v5Pages["items"] is JsonArray existingItems)
        {
            // Idempotent: already v6-shaped. Copy every key verbatim (including currentPageId and any
            // unknown future key) except a co-present legacy "ids", which must never coexist with the
            // single-source-of-truth "items" on disk.
            foreach (var (key, value) in v5Pages)
            {
                if (key is "ids")
                {
                    continue;
                }

                result[key] = key == "items" ? CloneItems(existingItems) : value?.DeepClone();
            }

            return result;
        }

        result["items"] = BuildItemsFromIds(v5Pages["ids"] as JsonArray);

        foreach (var (key, value) in v5Pages)
        {
            if (key is "ids" or "items")
            {
                continue;
            }

            result[key] = value?.DeepClone();
        }

        return result;
    }

    private static JsonArray CloneItems(JsonArray items)
    {
        var result = new JsonArray();
        foreach (var item in items)
        {
            result.Add(item?.DeepClone());
        }

        return result;
    }

    private static JsonArray BuildItemsFromIds(JsonArray? v5Ids)
    {
        var result = new JsonArray();
        if (v5Ids is null)
        {
            return result;
        }

        foreach (var node in v5Ids)
        {
            string? id;
            try
            {
                id = node?.GetValue<string>();
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException)
            {
                // Not actually a string (e.g. a stray number/object/bool) — skip, same defensive
                // stance V1ToV2Migration.MapEnumName takes for an unparsable value.
                continue;
            }

            if (id is null)
            {
                continue;
            }

            var item = new JsonObject { ["id"] = id };
            if (id != DefaultPageId)
            {
                item["name"] = id;
            }

            result.Add(item);
        }

        return result;
    }
}
