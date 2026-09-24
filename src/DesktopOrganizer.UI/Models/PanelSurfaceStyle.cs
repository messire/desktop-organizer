namespace DesktopOrganizer.UI.Models;

/// <summary>
/// How a panel's <see cref="Theming.SurfaceComposer.ComposeSurface"/> result blends the neutral
/// panel surface with its accent colour (ADR-025, docs/PLANS/phase-5-ambient-accent-ui.md §1 D-3).
/// </summary>
public enum PanelSurfaceStyle
{
    /// <summary>Neutral surface at <c>SurfaceOpacity</c>; the accent appears only in the indicator/selection, never in the fill.</summary>
    Solid = 0,

    /// <summary>Default. Neutral base with the accent subtly mixed in at <see cref="Theming.SurfaceComposer.AmbientAccentRatio"/> (~8%).</summary>
    Ambient = 1,

    /// <summary>
    /// Lower opacity + a stronger border. This is <b>not</b> a real backdrop-blur/acrylic effect —
    /// Phase 5 has no compositor integration for that (R-8). A genuine DWM/acrylic blur is a Phase 8
    /// (native) candidate; until then, "Glass" only means "more see-through, more outlined".
    /// </summary>
    Glass = 2,
}

/// <summary>
/// Parsing helper for <see cref="PanelSurfaceStyle"/> — shared by every call site that seeds a new
/// panel's <see cref="ChildWindowModel.SurfaceStyle"/> from the persisted, free-form
/// <c>IAppSettings.DefaultSurfaceStyle</c> string (<c>App.xaml.cs</c>'s <c>NewPanelWithDefaults</c> and
/// <c>MainWindowViewModel.AddPanel</c>), so both stay in lock-step instead of duplicating the same
/// try-parse-or-fallback logic (ADR-025 D-10).
/// </summary>
public static class PanelSurfaceStyleParser
{
    /// <summary>Parses <paramref name="value"/> case-insensitively; unrecognised/empty input falls back to <see cref="PanelSurfaceStyle.Ambient"/> — never throws.</summary>
    public static PanelSurfaceStyle ParseOrAmbient(string? value) =>
        Enum.TryParse<PanelSurfaceStyle>(value, ignoreCase: true, out var parsed) ? parsed : PanelSurfaceStyle.Ambient;
}
