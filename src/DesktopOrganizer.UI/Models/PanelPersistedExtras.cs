using System.Text.Json;

namespace DesktopOrganizer.UI.Models;

/// <summary>
/// Forward-compatibility carrier attached to a <see cref="ChildWindowModel"/>: the persisted fields of the panel
/// that this build does not understand (unknown JSON keys at the four panel levels, and unrecognised enum strings).
/// The UI never reads it; it exists only so <c>PanelStateMapper</c> can write everything back unchanged on save
/// (ADR-024 Amendment "Additive revisions", docs/PLANS/schema-additive-revision-v7.md §2.2).
/// </summary>
/// <remarks>
/// Immutable, no change notification. It lives on the model rather than in a side table keyed by panel id because
/// layout snapshots keep panels as serialized <see cref="ChildWindowModel"/> JSON and a snapshot restore assigns
/// fresh ids — a property on the model survives both round-trips automatically.
/// </remarks>
public sealed class PanelPersistedExtras
{
    /// <summary>Unknown keys of the panel object itself (<c>panels[]</c>).</summary>
    public IReadOnlyDictionary<string, JsonElement>? Panel { get; init; }

    /// <summary>Unknown keys of <c>panels[].geometry</c>.</summary>
    public IReadOnlyDictionary<string, JsonElement>? Geometry { get; init; }

    /// <summary>Unknown keys of <c>panels[].appearance</c>.</summary>
    public IReadOnlyDictionary<string, JsonElement>? Appearance { get; init; }

    /// <summary>Unknown keys of <c>panels[].content</c>.</summary>
    public IReadOnlyDictionary<string, JsonElement>? Content { get; init; }

    /// <summary>
    /// Unrecognised enum strings, keyed by the field path (e.g. <c>geometry.windowState</c>,
    /// <c>appearance.surfaceStyle</c>).
    /// </summary>
    public IReadOnlyDictionary<string, PreservedEnumValue>? Enums { get; init; }

    /// <summary>Total number of unknown JSON keys across the four panel levels (diagnostics counter).</summary>
    internal int UnknownKeyCount =>
        (Panel?.Count ?? 0) + (Geometry?.Count ?? 0) + (Appearance?.Count ?? 0) + (Content?.Count ?? 0);

    /// <summary>Number of unrecognised enum strings carried (diagnostics counter).</summary>
    internal int UnknownEnumCount => Enums?.Count ?? 0;

    /// <summary>Creates a carrier, or <see langword="null"/> when there is nothing to carry.</summary>
    /// <param name="panel">Unknown keys of the panel object.</param>
    /// <param name="geometry">Unknown keys of the geometry object.</param>
    /// <param name="appearance">Unknown keys of the appearance object.</param>
    /// <param name="content">Unknown keys of the content object.</param>
    /// <param name="enums">Unrecognised enum strings by field path.</param>
    internal static PanelPersistedExtras? TryCreate(
        IReadOnlyDictionary<string, JsonElement>? panel,
        IReadOnlyDictionary<string, JsonElement>? geometry,
        IReadOnlyDictionary<string, JsonElement>? appearance,
        IReadOnlyDictionary<string, JsonElement>? content,
        IReadOnlyDictionary<string, PreservedEnumValue>? enums)
    {
        panel = NullIfEmpty(panel);
        geometry = NullIfEmpty(geometry);
        appearance = NullIfEmpty(appearance);
        content = NullIfEmpty(content);
        enums = NullIfEmpty(enums);

        if (panel is null && geometry is null && appearance is null && content is null && enums is null)
        {
            return null;
        }

        return new PanelPersistedExtras
        {
            Panel = panel,
            Geometry = geometry,
            Appearance = appearance,
            Content = content,
            Enums = enums,
        };
    }

    private static IReadOnlyDictionary<TKey, TValue>? NullIfEmpty<TKey, TValue>(IReadOnlyDictionary<TKey, TValue>? source)
        where TKey : notnull
        => source is { Count: > 0 } ? new Dictionary<TKey, TValue>(source) : null;
}
