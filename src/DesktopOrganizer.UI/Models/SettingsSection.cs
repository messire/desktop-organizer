namespace DesktopOrganizer.UI.Models;

/// <summary>
/// One top-level section of the settings-window sidebar navigation
/// (docs/prompts/settings-window-redesign.md, D-2/D-10). The sidebar shows all eight members;
/// <see cref="About"/> is rendered as a separate, visually pinned-to-the-bottom entry by
/// <c>MainWindow.xaml</c> rather than appearing in the scrolling list of the other seven.
/// </summary>
public enum SettingsSection
{
    /// <summary>Autostart and the "Advanced" (native resize, one-time hints) sub-section.</summary>
    General,

    /// <summary>Grid snapping, drag magnet, lock layout, and normal-mode move/resize gestures.</summary>
    LayoutSnapping,

    /// <summary>Per-page panel list: add/remove/customize/move-to-page/align-all.</summary>
    Panels,

    /// <summary>Default appearance/geometry seeded onto newly created panels.</summary>
    NewPanelDefaults,

    /// <summary>Page list: add/remove/switch.</summary>
    Pages,

    /// <summary>The desktop System Icons Panel feature.</summary>
    SystemIcons,

    /// <summary>Layout snapshots: save/restore/delete.</summary>
    Backup,

    /// <summary>App version, storage folder, diagnostics hint.</summary>
    About,
}

/// <summary>
/// Sidebar display metadata for a <see cref="SettingsSection"/>: the label and a Segoe MDL2 Assets
/// glyph. A plain record (rather than a converter) so <c>MainWindow.xaml</c> can bind a
/// <see cref="System.Windows.Controls.ListBox"/> directly to an ordered list of these instead of
/// re-deriving the label/glyph from the enum value at render time.
/// </summary>
/// <param name="Section">The section this entry navigates to.</param>
/// <param name="TitleKey">
/// The RESX key (<c>Settings_Nav_*</c>, docs/PLANS/localization.md §2.6/§4.5) for the sidebar label —
/// resolved hot-switch-aware via <c>{loc:LocBinding TitleKey, Converter={StaticResource LocKey}}</c>
/// in <c>Resources/Styles/Settings.xaml</c>'s <c>Settings.SidebarItemTemplate</c>. Supersedes ADR-031
/// D-8 ("English only") — localization is now in scope (ADR-032).
/// </param>
/// <param name="Glyph">A single Segoe MDL2 Assets glyph shown left of the resolved <paramref name="TitleKey"/>.</param>
public sealed record SettingsSectionInfo(SettingsSection Section, string TitleKey, string Glyph);

/// <summary>
/// The fixed sidebar catalog (docs/prompts/settings-window-redesign.md inventory table), split into
/// <see cref="Primary"/> (the seven scrolling entries) and <see cref="About"/> (pinned separately) so
/// <c>MainWindow.xaml</c> never needs to filter <see cref="SettingsSection.About"/> out at binding time.
/// </summary>
public static class SettingsSectionCatalog
{
    /// <summary>The seven sections shown in the sidebar's main scrolling list, in display order.</summary>
    public static IReadOnlyList<SettingsSectionInfo> Primary { get; } =
    [
        new(SettingsSection.General, "Settings_Nav_General", ""),
        new(SettingsSection.LayoutSnapping, "Settings_Nav_LayoutSnapping", ""),
        new(SettingsSection.Panels, "Settings_Nav_Panels", ""),
        new(SettingsSection.NewPanelDefaults, "Settings_Nav_NewPanelDefaults", ""),
        new(SettingsSection.Pages, "Settings_Nav_Pages", ""),
        new(SettingsSection.SystemIcons, "Settings_Nav_SystemIcons", ""),
        new(SettingsSection.Backup, "Settings_Nav_Backup", ""),
    ];

    /// <summary>The single entry pinned to the bottom of the sidebar.</summary>
    public static SettingsSectionInfo About { get; } = new(SettingsSection.About, "Settings_Nav_About", "");
}
