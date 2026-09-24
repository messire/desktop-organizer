using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json.Serialization;
using DesktopOrganizer.Core;
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;
using WindowState = System.Windows.WindowState;

namespace DesktopOrganizer.UI.Models;

/// <summary>
/// Represents a child window with its properties.
/// Appearance properties raise <see cref="PropertyChanged"/> so the live panel and the
/// settings window (which share the same instance) stay in sync.
/// </summary>
public class ChildWindowModel : INotifyPropertyChanged
{
    private string _title = string.Empty;
    private string _accentColor = "#6B7280";
    private double _surfaceOpacity = 0.8;
    private PanelSurfaceStyle _surfaceStyle = PanelSurfaceStyle.Ambient;
    private string _textColor = "#DDDDDD";
    private double _iconSize = 48;
    private PanelViewMode _viewMode = PanelViewMode.List;
    private PanelContentFilter _contentFilter = PanelContentFilter.ShowAll;
    private PanelSortMode _sortMode = PanelSortMode.Manual;
    private bool _isRolledUp;
    private PanelGeometryMode _geometryMode = PanelGeometryMode.LegacyFreeform;
    private int? _widthSpanUnits;
    private int? _heightSpanUnits;
    private PanelSizingMode _sizingMode = PanelSizingMode.Manual;
    private IconLayoutPolicy _iconLayoutPolicy = IconLayoutPolicy.LegacyStretch;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Gets or sets the unique identifier for the child window.
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Gets or sets the title of the child window.
    /// </summary>
    public string Title
    {
        get => _title;
        set => SetField(ref _title, value);
    }

    /// <summary>
    /// Semantic accent colour of the panel as a hex string (e.g. <c>#6B7280</c>). Unlike the old
    /// <c>BackgroundColor</c> it replaces, this never fills the whole panel surface — it only tints
    /// the indicator/selection/icon-hover chrome, and (for <see cref="PanelSurfaceStyle.Ambient"/>) is
    /// subtly mixed into the neutral surface at <see cref="Theming.SurfaceComposer.AmbientAccentRatio"/>
    /// (ADR-025, docs/PLANS/phase-5-ambient-accent-ui.md §1 D-3).
    /// </summary>
    public string AccentColor
    {
        get => _accentColor;
        set => SetField(ref _accentColor, value);
    }

    /// <summary>
    /// Opacity (0.0–1.0) of the panel's neutral surface fill. Applied to the surface only, not the
    /// icons or text (renamed from <c>BackgroundOpacity</c> — same semantics, new name to match
    /// <see cref="AccentColor"/> no longer being a background fill colour).
    /// </summary>
    public double SurfaceOpacity
    {
        get => _surfaceOpacity;
        set => SetField(ref _surfaceOpacity, value);
    }

    /// <summary>
    /// How the panel's neutral surface blends with <see cref="AccentColor"/> — see
    /// <see cref="Theming.SurfaceComposer.ComposeSurface"/> and <see cref="PanelSurfaceStyle"/>.
    /// </summary>
    public PanelSurfaceStyle SurfaceStyle
    {
        get => _surfaceStyle;
        set => SetField(ref _surfaceStyle, value);
    }

    /// <summary>
    /// Text colour of the panel as a hex string (e.g. <c>#DDDDDD</c>).
    /// </summary>
    public string TextColor
    {
        get => _textColor;
        set => SetField(ref _textColor, value);
    }

    /// <summary>
    /// Icon size in pixels for the wrapping (grid) view.
    /// </summary>
    public double IconSize
    {
        get => _iconSize;
        set => SetField(ref _iconSize, value);
    }

    /// <summary>
    /// How icons are distributed inside the panel's content area (ADR-029). Persisted
    /// (<c>appearance.iconLayoutPolicy</c>, schema v5 — docs/PLANS/workspace-grid-migration.md); a
    /// panel that existed before this feature migrates to <c>IconLayoutPolicy.LegacyStretch</c>
    /// explicitly (never a silently-defaulted value — ADR-029 Decision 6).
    /// </summary>
    public IconLayoutPolicy IconLayoutPolicy
    {
        get => _iconLayoutPolicy;
        set => SetField(ref _iconLayoutPolicy, value);
    }

    /// <summary>
    /// How the panel lays out its items (list or grid).
    /// </summary>
    public PanelViewMode ViewMode
    {
        get => _viewMode;
        set => SetField(ref _viewMode, value);
    }

    /// <summary>
    /// Which item types the panel shows. Serialised to JSON; missing field deserialises to
    /// <see cref="PanelContentFilter.ShowAll"/> (backwards-compatible default).
    /// </summary>
    public PanelContentFilter ContentFilter
    {
        get => _contentFilter;
        set => SetField(ref _contentFilter, value);
    }

    /// <summary>
    /// How the panel orders its items — manual (drag) order or alphabetical by name. Serialised to
    /// JSON; a JSON document written before this feature existed has no <c>SortMode</c> property at
    /// all, and <c>System.Text.Json</c> deserializes a missing enum property to
    /// <see langword="default"/>, i.e. <see cref="PanelSortMode.Manual"/> — so panels saved before
    /// this feature keep showing their items in exactly the order they always did, with no migration
    /// (see <see cref="PanelSortMode"/> for the full rationale).
    /// </summary>
    public PanelSortMode SortMode
    {
        get => _sortMode;
        set => SetField(ref _sortMode, value);
    }

    /// <summary>
    /// Stable device name of the monitor the panel belongs to (e.g. <c>\\.\DISPLAY1</c>).
    /// Used to restore the panel onto the same physical monitor across sessions. If the monitor
    /// no longer exists, positioning falls back to the saved <see cref="Position"/> coordinates.
    /// </summary>
    public string? MonitorDeviceId { get; set; }

    /// <summary>
    /// Gets or sets the position of the child window on the desktop.
    /// </summary>
    public Point Position { get; set; }

    /// <summary>
    /// Gets or sets the size of the child window.
    /// </summary>
    public Size Size { get; set; }

    /// <summary>
    /// How <see cref="Position"/>/<see cref="Size"/> relate to the workspace panel grid (ADR-028).
    /// Persisted (<c>geometry.geometryMode</c>, schema v5 — docs/PLANS/workspace-grid-migration.md); a
    /// panel that existed before this feature keeps <see cref="PanelGeometryMode.LegacyFreeform"/>
    /// (the enum's own default — no jump on first launch after upgrade).
    /// </summary>
    public PanelGeometryMode GeometryMode
    {
        get => _geometryMode;
        set => SetField(ref _geometryMode, value);
    }

    /// <summary>
    /// Width as an integer span of workspace grid units, meaningful only when
    /// <see cref="GeometryMode"/> is <see cref="PanelGeometryMode.GridAligned"/> — <see langword="null"/>
    /// otherwise. Not the source of truth for on-screen width by itself: <see cref="Size"/> is always
    /// kept as the consistent DIP projection at the moment of the last save (ADR-028;
    /// docs/PLANS/workspace-grid-migration.md §2).
    /// </summary>
    public int? WidthSpanUnits
    {
        get => _widthSpanUnits;
        set => SetField(ref _widthSpanUnits, value);
    }

    /// <summary>Symmetric to <see cref="WidthSpanUnits"/> for height.</summary>
    public int? HeightSpanUnits
    {
        get => _heightSpanUnits;
        set => SetField(ref _heightSpanUnits, value);
    }

    /// <summary>
    /// Gets or sets whether the window is visible.
    /// </summary>
    public bool IsVisible { get; set; } = true;

    /// <summary>
    /// Gets or sets the window state (normal, minimized, maximized).
    /// </summary>
    public WindowState WindowState { get; set; } = WindowState.Normal;

    /// <summary>
    /// Gets or sets whether the panel is rolled up (collapsed to its title bar).
    /// While rolled up, <see cref="Size"/> keeps the expanded height for restore.
    /// </summary>
    public bool IsRolledUp
    {
        get => _isRolledUp;
        set => SetField(ref _isRolledUp, value);
    }

    /// <summary>
    /// Whether the panel's height follows its content (ADR-029 Decision 7). Persisted
    /// (<c>geometry.sizingMode</c>, schema v5 — docs/PLANS/workspace-grid-migration.md); a panel that
    /// existed before this feature migrates to <c>PanelSizingMode.Manual</c> explicitly (its persisted
    /// height was never passively re-derived from content before this feature existed — a symbolically
    /// neutral migration, ADR-029).
    /// </summary>
    public PanelSizingMode SizingMode
    {
        get => _sizingMode;
        set => SetField(ref _sizingMode, value);
    }

    /// <summary>
    /// Gets or sets the page this panel belongs to. Default is <c>"default"</c>.
    /// </summary>
    public string PageId { get; set; } = "default";

    /// <summary>
    /// If set, the panel mirrors the contents of this folder (a "portal", e.g. the Desktop)
    /// instead of a manually curated list. When set, <see cref="FilePaths"/> is not persisted.
    /// </summary>
    public string? SourceFolderPath { get; set; }

    /// <summary>
    /// File paths persisted to settings. Used for serialization.
    /// </summary>
    public List<string> FilePaths { get; set; } = [];

    /// <summary>
    /// Runtime collection of file items. Not persisted — populated from <see cref="FilePaths"/> on load.
    /// </summary>
    [JsonIgnore]
    public ObservableCollection<FileItem> FileItems { get; set; } = [];

    private void SetField<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
