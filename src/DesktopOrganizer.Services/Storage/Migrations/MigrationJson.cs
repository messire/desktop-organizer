using System.Text.Json.Nodes;

namespace DesktopOrganizer.Services.Storage.Migrations;

/// <summary>
/// Shared helpers for <see cref="Abstractions.Storage.ISettingsMigration"/> implementations
/// (ADR-024 Amendment "Additive revisions", docs/PLANS/schema-additive-revision-v7.md §4.1 / F4).
/// </summary>
internal static class MigrationJson
{
    /// <summary>
    /// Returns a new <see cref="JsonObject"/> holding a deep clone of every key of
    /// <paramref name="source"/> except <paramref name="reshapedKeys"/> — the keys the calling migration
    /// rebuilds itself. This is the "copy everything except what is explicitly reshaped" rule that is
    /// mandatory for every migration from v6 onward: an allow-list of known keys (the historical
    /// <see cref="V4ToV5Migration"/> anti-pattern) would silently drop any key written by a newer
    /// additive revision, which the unknown-field round-trip exists to preserve.
    /// </summary>
    /// <param name="source">The payload object to clone.</param>
    /// <param name="reshapedKeys">Top-level keys that the migration reshapes and therefore must not be copied.</param>
    /// <returns>A new object; <paramref name="source"/> is not mutated and shares no nodes with the result.</returns>
    public static JsonObject CloneAllExcept(JsonObject source, params string[] reshapedKeys)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(reshapedKeys);

        var result = new JsonObject();
        foreach (var (key, value) in source)
        {
            if (Array.IndexOf(reshapedKeys, key) >= 0)
            {
                continue;
            }

            result[key] = value?.DeepClone();
        }

        return result;
    }
}
