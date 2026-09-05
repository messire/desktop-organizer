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
    private string _backgroundColor = "#1E1E1E";
    private double _backgroundOpacity = 0.8;
    private string _textColor = "#DDDDDD";
    private double _iconSize = 48;
    private PanelViewMode _viewMode = PanelViewMode.List;
    private PanelContentFilter _contentFilter = PanelContentFilter.ShowAll;
    private PanelSortMode _sortMode = PanelSortMode.Manual;
    private bool _isRolledUp;

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
    /// Background colour of the panel as a hex string (e.g. <c>#1E1E1E</c>).
    /// </summary>
    public string BackgroundColor
    {
        get => _backgroundColor;
        set => SetField(ref _backgroundColor, value);
    }

    /// <summary>
    /// Background opacity (0.0–1.0). Applied to the background colour only, not the icons.
    /// </summary>
    public double BackgroundOpacity
    {
        get => _backgroundOpacity;
        set => SetField(ref _backgroundOpacity, value);
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
