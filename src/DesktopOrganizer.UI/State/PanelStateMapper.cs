using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.State.Dto;
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;

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
/// </remarks>
internal static class PanelStateMapper
{
    /// <summary>Maps a live <see cref="ChildWindowModel"/> to its schema-v3 <see cref="PanelStateDto"/>.</summary>
    /// <param name="model">The panel model to map.</param>
    public static PanelStateDto ToDto(ChildWindowModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        return new PanelStateDto
        {
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
                WindowState = model.WindowState,
                IsRolledUp = model.IsRolledUp,
                IsVisible = model.IsVisible,
                GeometryMode = model.GeometryMode.ToString(),

                // MINOR fix (fix-cycle 1, docs/PLANS/workspace-grid-migration.md §10.5): spans are only
                // meaningful for GridAligned (§2 of the plan) — a LegacyFreeform panel that happens to
                // still carry a stale span pair (e.g. the user switched it back from GridAligned) must
                // not persist them, or a future reader could mistake "has spans" for "is GridAligned".
                WidthSpanUnits = model.GeometryMode == PanelGeometryMode.GridAligned ? model.WidthSpanUnits : null,
                HeightSpanUnits = model.GeometryMode == PanelGeometryMode.GridAligned ? model.HeightSpanUnits : null,
                SizingMode = model.SizingMode.ToString(),
            },
            Appearance = new PanelAppearanceDto
            {
                AccentColor = model.AccentColor,
                SurfaceOpacity = model.SurfaceOpacity,
                SurfaceStyle = model.SurfaceStyle,
                TextColor = model.TextColor,
                IconSize = model.IconSize,
                IconLayoutPolicy = model.IconLayoutPolicy.ToString(),
            },
            Content = new PanelContentDto
            {
                ViewMode = model.ViewMode,
                ContentFilter = model.ContentFilter,
                SortMode = model.SortMode,
                SourceFolderPath = model.SourceFolderPath,
                FilePaths = [.. model.FilePaths],
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

        var geometryMode = PanelGeometryModeParser.ParseOrLegacyFreeform(geometry.GeometryMode);
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
            WindowState = geometry.WindowState,
            IsRolledUp = geometry.IsRolledUp,
            IsVisible = geometry.IsVisible,
            GeometryMode = geometryMode,
            WidthSpanUnits = widthSpanUnits,
            HeightSpanUnits = heightSpanUnits,
            SizingMode = PanelSizingModeParser.ParseOrManual(geometry.SizingMode),
            AccentColor = appearance.AccentColor,
            SurfaceOpacity = appearance.SurfaceOpacity,
            SurfaceStyle = appearance.SurfaceStyle,
            TextColor = appearance.TextColor,
            IconSize = appearance.IconSize,
            IconLayoutPolicy = IconLayoutPolicyParser.ParseOrLegacyStretch(appearance.IconLayoutPolicy),
            ViewMode = content.ViewMode,
            ContentFilter = content.ContentFilter,
            SortMode = content.SortMode,
            SourceFolderPath = content.SourceFolderPath,
            FilePaths = content.FilePaths is null ? [] : [.. content.FilePaths],
        };
    }
}
