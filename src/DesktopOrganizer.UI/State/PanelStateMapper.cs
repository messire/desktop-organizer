using System.Text.Json;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.State.Dto;
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;
using WindowState = System.Windows.WindowState;

namespace DesktopOrganizer.UI.State;

/// <summary>
/// The single place <see cref="ChildWindowModel"/> (a UI type — it drags in
/// <see cref="System.Windows.WindowState"/> and <see cref="System.Drawing.Point"/>/
/// <see cref="System.Drawing.Size"/>) meets the storage schema's <see cref="PanelStateDto"/>
/// (ADR-024 D-3/§2.3). Used both for the live panel set and for panels captured inside a
/// <see cref="Dto.LayoutSnapshotStateDto"/> (ADR-024 R-11 — the same shape, the same mapper, no
/// second copy of this logic).
/// </summary>
/// <remarks>
/// <see cref="ChildWindowModel.FileItems"/> is deliberately never touched here: it is the runtime-only
/// projection of <see cref="ChildWindowModel.FilePaths"/> (already <c>[JsonIgnore]</c> on the model
/// itself) and is rebuilt from disk after a panel is created, not persisted.
/// <para>
/// <b>Forward compatibility (ADR-024 Amendment "Additive revisions").</b> Unknown JSON keys at the four panel
/// levels and unrecognised enum strings are carried on <see cref="ChildWindowModel.PersistedExtras"/> from
/// <see cref="FromDto"/> to <see cref="ToDto"/>, so a panel edited by this build keeps every field a newer build
/// wrote. An unrecognised enum string degrades to the documented default on the model (never a quarantine) and is
/// written back as-is while the model still holds that default.
/// </para>
/// </remarks>
internal static class PanelStateMapper
{
    private const string WindowStateKey = "geometry.windowState";
    private const string GeometryModeKey = "geometry.geometryMode";
    private const string SizingModeKey = "geometry.sizingMode";
    private const string SurfaceStyleKey = "appearance.surfaceStyle";
    private const string IconLayoutPolicyKey = "appearance.iconLayoutPolicy";
    private const string ViewModeKey = "content.viewMode";
    private const string ContentFilterKey = "content.contentFilter";
    private const string SortModeKey = "content.sortMode";

    /// <summary>Maps a live <see cref="ChildWindowModel"/> to its schema-v3 <see cref="PanelStateDto"/>.</summary>
    /// <param name="model">The panel model to map.</param>
    /// <param name="includeUnknownFields">
    /// <see langword="true"/> (default) writes back the model's <see cref="ChildWindowModel.PersistedExtras"/>;
    /// <see langword="false"/> maps known fields only — used when the result is a brand-new entity
    /// (<see cref="PanelCloneBuilder"/>).
    /// </param>
    public static PanelStateDto ToDto(ChildWindowModel model, bool includeUnknownFields = true)
    {
        ArgumentNullException.ThrowIfNull(model);

        var extras = includeUnknownFields ? model.PersistedExtras : null;

        // Spans are written for GridAligned, and also whenever geometryMode is written back as a preserved raw
        // string this build does not know (a future mode that may rely on spans): dropping them would silently
        // zero a newer build's data on a round trip through this one.
        var geometryModeOut = WriteEnum(model.GeometryMode, extras, GeometryModeKey);
        var keepSpans = model.GeometryMode == PanelGeometryMode.GridAligned
                        || !string.Equals(geometryModeOut, model.GeometryMode.ToString(), StringComparison.Ordinal);

        return new PanelStateDto
        {
            Extras = CopyOrNull(extras?.Panel),
            Id = model.Id,
            Title = model.Title,
            PageId = model.PageId,
            Geometry = new PanelGeometryDto
            {
                X = model.Position.X,
                Y = model.Position.Y,
                Width = model.Size.Width,
                Height = model.Size.Height,
                MonitorDeviceId = model.MonitorDeviceId,
                WindowState = WriteEnum(model.WindowState, extras, WindowStateKey),
                IsRolledUp = model.IsRolledUp,
                IsVisible = model.IsVisible,
                GeometryMode = geometryModeOut,

                // MINOR fix (fix-cycle 1, docs/PLANS/workspace-grid-migration.md §10.5): spans are only
                // meaningful for GridAligned (§2 of the plan) — a LegacyFreeform panel that happens to
                // still carry a stale span pair (e.g. the user switched it back from GridAligned) must
                // not persist them, or a future reader could mistake "has spans" for "is GridAligned".
                // Exception: an unknown geometryMode preserved verbatim (see keepSpans) keeps its spans.
                WidthSpanUnits = keepSpans ? model.WidthSpanUnits : null,
                HeightSpanUnits = keepSpans ? model.HeightSpanUnits : null,
                SizingMode = WriteEnum(model.SizingMode, extras, SizingModeKey),
                Extras = CopyOrNull(extras?.Geometry),
            },
            Appearance = new PanelAppearanceDto
            {
                AccentColor = model.AccentColor,
                SurfaceOpacity = model.SurfaceOpacity,
                SurfaceStyle = WriteEnum(model.SurfaceStyle, extras, SurfaceStyleKey),
                TextColor = model.TextColor,
                IconSize = model.IconSize,
                IconLayoutPolicy = WriteEnum(model.IconLayoutPolicy, extras, IconLayoutPolicyKey),
                Extras = CopyOrNull(extras?.Appearance),
            },
            Content = new PanelContentDto
            {
                ViewMode = WriteEnum(model.ViewMode, extras, ViewModeKey),
                ContentFilter = WriteEnum(model.ContentFilter, extras, ContentFilterKey),
                SortMode = WriteEnum(model.SortMode, extras, SortModeKey),
                SourceFolderPath = model.SourceFolderPath,
                FilePaths = [.. model.FilePaths],
                Extras = CopyOrNull(extras?.Content),
            },
        };
    }

    /// <summary>Maps a schema-v3 <see cref="PanelStateDto"/> back to a fresh <see cref="ChildWindowModel"/>.</summary>
    /// <param name="dto">The persisted panel DTO to map.</param>
    public static ChildWindowModel FromDto(PanelStateDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        var geometry = dto.Geometry ?? new PanelGeometryDto();
        var appearance = dto.Appearance ?? new PanelAppearanceDto();
        var content = dto.Content ?? new PanelContentDto();

        Dictionary<string, PreservedEnumValue>? preserved = null;

        var geometryMode = PanelGeometryModeParser.ParseOrLegacyFreeform(geometry.GeometryMode);
        Preserve(geometry.GeometryMode, geometryMode, PanelGeometryMode.LegacyFreeform, GeometryModeKey, ref preserved);
        var widthSpanUnits = geometry.WidthSpanUnits;
        var heightSpanUnits = geometry.HeightSpanUnits;

        // MAJOR-1(a) fix-cycle 1 (docs/PLANS/workspace-grid-migration.md §10.5): a GridAligned panel
        // without a valid (>= 1 on both axes) span pair is not actually aligned to anything on disk —
        // this on-disk shape is reachable today (schema v5 files written by App.xaml.cs/
        // MainWindowViewModel before this fix-cycle populated GeometryMode from
        // IAppSettings.DefaultPanelGeometryMode without ever setting the spans, and any hand-edited/
        // corrupted document can claim it too). Degrade to LegacyFreeform (spans cleared) instead of
        // trusting an inconsistent on-disk value: rendering/interaction code that branches on
        // GeometryMode must never see GridAligned paired with a missing/degenerate span, and this is
        // the single choke point every panel load passes through.
        if (geometryMode == PanelGeometryMode.GridAligned && !(widthSpanUnits is > 0 && heightSpanUnits is > 0))
        {
            geometryMode = PanelGeometryMode.LegacyFreeform;
            widthSpanUnits = null;
            heightSpanUnits = null;
        }

        return new ChildWindowModel
        {
            Id = dto.Id,
            Title = dto.Title,
            PageId = dto.PageId,
            Position = new Point(geometry.X, geometry.Y),
            Size = new Size(geometry.Width, geometry.Height),
            MonitorDeviceId = geometry.MonitorDeviceId,
            WindowState = ReadEnum(geometry.WindowState, WindowState.Normal, WindowStateKey, ref preserved),
            IsRolledUp = geometry.IsRolledUp,
            IsVisible = geometry.IsVisible,
            GeometryMode = geometryMode,
            WidthSpanUnits = widthSpanUnits,
            HeightSpanUnits = heightSpanUnits,
            SizingMode = ParseSizingMode(geometry.SizingMode, ref preserved),
            AccentColor = appearance.AccentColor,
            SurfaceOpacity = appearance.SurfaceOpacity,
            SurfaceStyle = ReadEnum(appearance.SurfaceStyle, PanelSurfaceStyle.Ambient, SurfaceStyleKey, ref preserved),
            TextColor = appearance.TextColor,
            IconSize = appearance.IconSize,
            IconLayoutPolicy = ParseIconLayoutPolicy(appearance.IconLayoutPolicy, ref preserved),
            ViewMode = ReadEnum(content.ViewMode, PanelViewMode.List, ViewModeKey, ref preserved),
            ContentFilter = ReadEnum(content.ContentFilter, PanelContentFilter.ShowAll, ContentFilterKey, ref preserved),
            SortMode = ReadEnum(content.SortMode, PanelSortMode.Manual, SortModeKey, ref preserved),
            SourceFolderPath = content.SourceFolderPath,
            FilePaths = content.FilePaths is null ? [] : [.. content.FilePaths],
            PersistedExtras = PanelPersistedExtras.TryCreate(dto.Extras, geometry.Extras, appearance.Extras, content.Extras, preserved),
        };
    }

    private static PanelSizingMode ParseSizingMode(string? raw, ref Dictionary<string, PreservedEnumValue>? preserved)
    {
        var parsed = PanelSizingModeParser.ParseOrManual(raw);
        Preserve(raw, parsed, PanelSizingMode.Manual, SizingModeKey, ref preserved);
        return parsed;
    }

    private static IconLayoutPolicy ParseIconLayoutPolicy(string? raw, ref Dictionary<string, PreservedEnumValue>? preserved)
    {
        var parsed = IconLayoutPolicyParser.ParseOrLegacyStretch(raw);
        Preserve(raw, parsed, IconLayoutPolicy.LegacyStretch, IconLayoutPolicyKey, ref preserved);
        return parsed;
    }

    /// <summary>Tolerant read of a string-typed enum field: unknown input yields <paramref name="fallback"/> and is remembered.</summary>
    private static TEnum ReadEnum<TEnum>(string? raw, TEnum fallback, string key, ref Dictionary<string, PreservedEnumValue>? preserved)
        where TEnum : struct, Enum
    {
        var parsed = TolerantEnumParser.Parse(raw, fallback, out var recognized);
        if (!recognized)
        {
            (preserved ??= [])[key] = new PreservedEnumValue(raw!, fallback.ToString());
        }

        return parsed;
    }

    /// <summary>Remembers <paramref name="raw"/> when the pre-existing parser that produced <paramref name="parsed"/> had to fall back.</summary>
    private static void Preserve<TEnum>(string? raw, TEnum parsed, TEnum fallback, string key, ref Dictionary<string, PreservedEnumValue>? preserved)
        where TEnum : struct, Enum
    {
        if (!TolerantEnumParser.IsRecognized(raw, parsed, fallback))
        {
            (preserved ??= [])[key] = new PreservedEnumValue(raw!, fallback.ToString());
        }
    }

    /// <summary>
    /// The on-disk string for an enum field: the model's value name — except that an unrecognised string read
    /// from disk is written back verbatim as long as the model still holds the fallback it was degraded to (the
    /// user did not touch the field). Choosing the fallback member explicitly is indistinguishable from not touching
    /// it, so the raw string wins in that one case (documented limitation).
    /// </summary>
    private static string WriteEnum<TEnum>(TEnum value, PanelPersistedExtras? extras, string key)
        where TEnum : struct, Enum
    {
        var name = value.ToString();
        return extras?.Enums is { } enums
               && enums.TryGetValue(key, out var preserved)
               && string.Equals(name, preserved.Fallback, StringComparison.Ordinal)
            ? preserved.Raw
            : name;
    }

    private static Dictionary<string, JsonElement>? CopyOrNull(IReadOnlyDictionary<string, JsonElement>? source)
        => source is { Count: > 0 } ? new Dictionary<string, JsonElement>(source) : null;
}
