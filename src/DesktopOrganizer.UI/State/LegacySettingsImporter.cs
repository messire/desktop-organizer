using System.Text.Json;
using System.Text.Json.Nodes;
using DesktopOrganizer.Services.Abstractions.Storage;
using DesktopOrganizer.UI.Services;

namespace DesktopOrganizer.UI.State;

/// <summary>
/// Reads the five structural keys still living in the legacy <c>user.config</c>
/// (<see cref="IAppSettings.ChildWindowsState"/>, <see cref="IAppSettings.Pages"/>,
/// <see cref="IAppSettings.CurrentPage"/>, <see cref="IAppSettings.LayoutSnapshots"/>,
/// <see cref="IAppSettings.SystemIconsPanel"/>) and assembles them into a schema-v1 document
/// (ADR-024 §1 D-9) for <c>IApplicationStateStore.Load</c> to migrate up from.
/// </summary>
/// <remarks>
/// This is a deliberate exception to "logic lives in Services": <see cref="IAppSettings"/> — the
/// thin wrapper around <c>Properties.Settings.Default</c> — lives in <c>DesktopOrganizer.UI</c>, not
/// in a Services project, so an importer that reads it must live here too (ADR-024 §2.3).
/// <para>
/// v1 is a <b>dословная transcription</b> of what each of the four existing writers
/// (<c>WindowManager</c>, <c>PageManagerService</c>, <c>LayoutSnapshotService</c>,
/// <c>SystemIconsPanelManager</c>) already serializes today — PascalCase property names, numeric
/// panel enums, <c>Point</c>/<c>Size</c> as <c>{X,Y,IsEmpty}</c>, exactly as-is. No normalization
/// happens here; that is the job of the v1→v2 migration.
/// </para>
/// </remarks>
internal sealed class LegacySettingsImporter : ILegacyStateImporter
{
    private readonly IAppSettings _appSettings;

    /// <summary>
    /// Initializes a new instance of the <see cref="LegacySettingsImporter"/> class.
    /// </summary>
    /// <param name="appSettings">The legacy settings wrapper this importer reads its five structural keys from.</param>
    public LegacySettingsImporter(IAppSettings appSettings)
    {
        _appSettings = appSettings;
    }

    /// <inheritdoc />
    public JsonObject? TryImport()
    {
        // The five IAppSettings members below are [Obsolete] (ADR-024 D-10: legacy, frozen, read-only)
        // — this importer is their one deliberate, sanctioned reader.
#pragma warning disable CS0618 // Type or member is obsolete
        var panels = ParseArrayOrDefault(_appSettings.ChildWindowsState, out var panelsHasData);
        var (pagesIds, pagesHasData) = ParsePagesOrDefault(_appSettings.Pages);
        var layoutSnapshots = ParseArrayOrDefault(_appSettings.LayoutSnapshots, out var snapshotsHasData);
        var systemIconsPanel = ParseObjectOrDefault(_appSettings.SystemIconsPanel, out var systemIconsHasData);

        // ADR-024 D-7 step 6 vs step 5: this is FirstRun, not an import, only when every one of the
        // four keys that decide the question (§ prompt — CurrentPage is structurally tied to Pages
        // and never inspected on its own) is empty or exactly the application's own default. A key
        // that failed to parse (garbage JSON) still counts as "has data" here — it proves something
        // was once written — even though its own contribution to the v1 document below degrades to
        // that key's empty default (the corrupt block does not block the rest of the import).
        if (!panelsHasData && !pagesHasData && !snapshotsHasData && !systemIconsHasData)
        {
            return null;
        }

        var currentPageId = string.IsNullOrWhiteSpace(_appSettings.CurrentPage)
            ? "default"
            : _appSettings.CurrentPage;
#pragma warning restore CS0618

        var pages = new JsonObject
        {
            ["Ids"] = pagesIds,
            ["CurrentPageId"] = currentPageId,
        };

        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["writtenAtUtc"] = DateTimeOffset.UtcNow.ToString("O"),
            ["appVersion"] = ResolveAppVersion(),
            ["panels"] = panels,
            ["pages"] = pages,
            ["layoutSnapshots"] = layoutSnapshots,
            ["systemIconsPanel"] = systemIconsPanel,
        };
    }

    /// <summary>
    /// Parses <paramref name="raw"/> as a JSON array, verbatim. <paramref name="hasData"/> is
    /// <see langword="true"/> when the raw string is neither blank nor an empty array (<c>"[]"</c>)
    /// nor parses to zero elements — including when it is present but fails to parse at all (a
    /// corrupt-but-non-empty legacy value still signals "this key was once written to").
    /// </summary>
    private static JsonArray ParseArrayOrDefault(string? raw, out bool hasData)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            hasData = false;
            return [];
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(raw);
        }
        catch (JsonException)
        {
            // Corrupt, but non-blank: this key was written to at some point, just not readable now.
            // Never let the exception escape — this importer must never throw (see the interface's
            // documented contract).
            hasData = true;
            return [];
        }

        if (parsed is not JsonArray array || array.Count == 0)
        {
            hasData = false;
            return [];
        }

        hasData = true;
        return array;
    }

    /// <summary>
    /// Parses <paramref name="raw"/> as a JSON object, verbatim. <paramref name="hasData"/> mirrors
    /// <see cref="ParseArrayOrDefault"/>: blank counts as default, a corrupt-but-non-blank value
    /// counts as data (degrading to an empty object here), and a successfully parsed object with at
    /// least one member counts as data.
    /// </summary>
    private static JsonObject ParseObjectOrDefault(string? raw, out bool hasData)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            hasData = false;
            return [];
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(raw);
        }
        catch (JsonException)
        {
            hasData = true;
            return [];
        }

        if (parsed is not JsonObject obj || obj.Count == 0)
        {
            hasData = false;
            return [];
        }

        hasData = true;
        return obj;
    }

    /// <summary>
    /// Parses the legacy <c>Pages</c> key into the list of page ids used by v1's
    /// <c>pages.Ids</c>. The returned <c>HasData</c> is <see langword="true"/> unless the raw value is
    /// blank or parses to exactly the application's own default, a single <c>"default"</c> entry — a
    /// corrupt-but-non-blank value counts as data and degrades to <c>["default"]</c> below.
    /// </summary>
    /// <param name="raw">The legacy <c>Pages</c> setting's raw string value.</param>
    private static (JsonArray Ids, bool HasData) ParsePagesOrDefault(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return (["default"], false);
        }

        List<string>? pages;
        try
        {
            pages = JsonSerializer.Deserialize<List<string>>(raw);
        }
        catch (JsonException)
        {
            return (["default"], true);
        }

        if (pages is null || pages.Count == 0)
        {
            return (["default"], false);
        }

        if (pages.Count == 1 && pages[0] == "default")
        {
            return (["default"], false);
        }

        var ids = new JsonArray();
        foreach (var page in pages)
        {
            ids.Add(JsonValue.Create(page));
        }

        return (ids, true);
    }

    private static string ResolveAppVersion()
    {
        // Informational only (ADR-024 D-5) — never consulted for decisions, only recorded. Falls back
        // to a fixed literal rather than throwing if the entry assembly's informational version is
        // unavailable (e.g. under a test host).
        var version = System.Reflection.Assembly.GetExecutingAssembly()
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;

        return string.IsNullOrWhiteSpace(version) ? "unknown" : version;
    }
}
