namespace DesktopOrganizer.UI.Services;

/// <inheritdoc />
internal class AppSettings : IAppSettings
{
    /// <inheritdoc />
    public int GridSize
    {
        get => Properties.Settings.Default.GridSize;
        set => Properties.Settings.Default.GridSize = value;
    }

    /// <inheritdoc />
    public bool SnapResizeToIconGrid
    {
        get => Properties.Settings.Default.SnapResizeToIconGrid;
        set => Properties.Settings.Default.SnapResizeToIconGrid = value;
    }

    /// <inheritdoc />
    public int GridSnapDistance
    {
        get => Properties.Settings.Default.GridSnapDistance;
        set => Properties.Settings.Default.GridSnapDistance = value;
    }

    /// <inheritdoc />
    public bool HasShownMoveHint
    {
        get => Properties.Settings.Default.HasShownMoveHint;
        set => Properties.Settings.Default.HasShownMoveHint = value;
    }

    /// <inheritdoc />
    public string DefaultBackgroundColor
    {
        get => Properties.Settings.Default.DefaultBackgroundColor;
        set => Properties.Settings.Default.DefaultBackgroundColor = value;
    }

    /// <inheritdoc />
    public string DefaultTextColor
    {
        get => Properties.Settings.Default.DefaultTextColor;
        set => Properties.Settings.Default.DefaultTextColor = value;
    }

    /// <inheritdoc />
    public double DefaultOpacity
    {
        get => Properties.Settings.Default.DefaultOpacity;
        set => Properties.Settings.Default.DefaultOpacity = value;
    }

    /// <inheritdoc />
    public double DefaultIconSize
    {
        get => Properties.Settings.Default.DefaultIconSize;
        set => Properties.Settings.Default.DefaultIconSize = value;
    }

    /// <inheritdoc />
    public string DefaultSurfaceStyle
    {
        get => Properties.Settings.Default.DefaultSurfaceStyle;
        set => Properties.Settings.Default.DefaultSurfaceStyle = value;
    }

    /// <inheritdoc />
    public string DefaultPanelGeometryMode
    {
        get => Properties.Settings.Default.DefaultPanelGeometryMode;
        set => Properties.Settings.Default.DefaultPanelGeometryMode = value;
    }

    /// <inheritdoc />
    public string DefaultIconLayoutPolicy
    {
        get => Properties.Settings.Default.DefaultIconLayoutPolicy;
        set => Properties.Settings.Default.DefaultIconLayoutPolicy = value;
    }

    /// <inheritdoc />
    public string Language
    {
        get => Properties.Settings.Default.Language;
        set => Properties.Settings.Default.Language = value;
    }

    /// <inheritdoc />
    public bool IsLayoutLocked
    {
        get => Properties.Settings.Default.IsLayoutLocked;
        set => Properties.Settings.Default.IsLayoutLocked = value;
    }

    /// <inheritdoc />
    public string NormalModeLayoutGestures
    {
        get => Properties.Settings.Default.NormalModeLayoutGestures;
        set => Properties.Settings.Default.NormalModeLayoutGestures = value;
    }

    /// <inheritdoc />
    public string ChildWindowsState
    {
        get => Properties.Settings.Default.ChildWindowsState;
        set => Properties.Settings.Default.ChildWindowsState = value;
    }

    /// <inheritdoc />
    public string Pages
    {
        get => Properties.Settings.Default.Pages;
        set => Properties.Settings.Default.Pages = value;
    }

    /// <inheritdoc />
    public string CurrentPage
    {
        get => Properties.Settings.Default.CurrentPage;
        set => Properties.Settings.Default.CurrentPage = value;
    }

    /// <inheritdoc />
    public string LayoutSnapshots
    {
        get => Properties.Settings.Default.LayoutSnapshots;
        set => Properties.Settings.Default.LayoutSnapshots = value;
    }

    /// <inheritdoc />
    public string SystemIconsPanel
    {
        get => Properties.Settings.Default.SystemIconsPanel;
        set => Properties.Settings.Default.SystemIconsPanel = value;
    }

    /// <inheritdoc />
    public void Save() => Properties.Settings.Default.Save();
}
