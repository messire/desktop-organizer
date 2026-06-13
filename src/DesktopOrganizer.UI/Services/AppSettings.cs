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
    public string SortingRules
    {
        get => Properties.Settings.Default.SortingRules;
        set => Properties.Settings.Default.SortingRules = value;
    }

    /// <inheritdoc />
    public string LayoutSnapshots
    {
        get => Properties.Settings.Default.LayoutSnapshots;
        set => Properties.Settings.Default.LayoutSnapshots = value;
    }

    /// <inheritdoc />
    public void Save() => Properties.Settings.Default.Save();
}
