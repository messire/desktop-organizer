using System.ComponentModel;
using System.Windows.Input;
using DesktopOrganizer.UI.Commands;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;

namespace DesktopOrganizer.UI.ViewModels;

/// <summary>
/// Global default settings for new panels and grid/snap behavior, extracted from
/// <see cref="MainWindowViewModel"/> (REFACTORING_PLAN.md §5.3). Owned and constructed internally by
/// <see cref="MainWindowViewModel"/>, which forwards its own identically-named properties to this
/// instance and re-raises its <see cref="PropertyChanged"/> notifications verbatim — so
/// <c>MainWindow.xaml</c>'s existing <c>{Binding GridSize}</c>-style bindings (against the root
/// <see cref="MainWindowViewModel"/>) keep working unchanged.
/// </summary>
public class GlobalDefaultsViewModel : INotifyPropertyChanged
{
    private readonly IAppSettings _appSettings;
    private readonly IWindowManager _windowManager;
    private readonly IColorPickerService _colorPicker;

    // Settings-window redesign (docs/prompts/settings-window-redesign.md, "Layout & snapping"): the last
    // non-1 GridSize the user had before toggling snapping off, so re-enabling the "Snap panel position
    // to grid" toggle restores that step instead of always resetting to the same default. Deliberately
    // in-memory only (not a new IAppSettings key — CLAUDE.md persistence freeze): losing this on restart
    // just means the toggle-on default falls back to 16, not that anything the user saved is lost.
    private int _lastNonOneGridSize = 16;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Initializes a new instance of the <see cref="GlobalDefaultsViewModel"/> class.
    /// </summary>
    /// <param name="appSettings">Backing store for every global default/grid setting.</param>
    /// <param name="windowManager">
    /// ADR-011 Amendment (2026-09-24): re-aligns every open panel onto the new
    /// <see cref="IAppSettings.GridSize"/> lattice right after it changes — see the
    /// <see cref="GridSize"/> setter.
    /// </param>
    /// <param name="colorPicker">Backs <see cref="DefaultColors"/>' "Custom…" commands (docs/PLANS/color-presets-and-size-step.md §3.7).</param>
    public GlobalDefaultsViewModel(IAppSettings appSettings, IWindowManager windowManager, IColorPickerService colorPicker)
    {
        _appSettings = appSettings;
        _windowManager = windowManager;
        _colorPicker = colorPicker;

        DefaultColors = new PanelColorEditorViewModel(
            _colorPicker,
            () => DefaultBackgroundColor,
            v => DefaultBackgroundColor = v,
            () => DefaultTextColor,
            v => DefaultTextColor = v,
            SetDefaultColors,
            resetDescriptionKey: "Settings_NewPanelDefaults_ResetColors_Description");

        SetDefaultSurfaceStyleCommand = new RelayCommand<PanelSurfaceStyle>(style => DefaultSurfaceStyle = style);
    }

    /// <summary>
    /// The "New panel defaults" section's accent/text colour editor (docs/PLANS/color-presets-and-size-step.md
    /// §3.7) — presets, "Custom…", and the advanced hex boxes, all proxying <see cref="DefaultBackgroundColor"/>/
    /// <see cref="DefaultTextColor"/>.
    /// </summary>
    public PanelColorEditorViewModel DefaultColors { get; }

    /// <summary>
    /// Snap-to-grid cell size in pixels. 1 = no snapping, 2–128 = snap.
    /// </summary>
    /// <remarks>
    /// ADR-011 Amendment (2026-09-24): changing this setting re-aligns every currently open panel onto the
    /// NEW grid via <see cref="IWindowManager.NormalizePanelsToGrid"/> — otherwise a panel positioned under
    /// the previous <see cref="GridSize"/> would keep "living on a different grid" than panels created (or
    /// dragged) after the change, since a subsequent drag only ever nudges an EXISTING off-grid position by
    /// whole NEW-grid steps rather than re-quantising it.
    /// </remarks>
    public int GridSize
    {
        get => _appSettings.GridSize;
        set
        {
            if (_appSettings.GridSize == value) return;
            _appSettings.GridSize = value;
            if (value > 1) _lastNonOneGridSize = value;
            _appSettings.Save();
            _windowManager.NormalizePanelsToGrid();
            OnPropertyChanged(nameof(GridSize));
            OnPropertyChanged(nameof(SnapToGridEnabled));
            OnPropertyChanged(nameof(IsSnapResizeToIconGridAvailable));
            OnPropertyChanged(nameof(GridStep));
        }
    }

    /// <summary>
    /// The "Snap panel position to grid" toggle (Layout &amp; snapping section): <see langword="true"/>
    /// iff <see cref="GridSize"/> is currently greater than 1. Turning it off sets <see cref="GridSize"/>
    /// to <c>1</c> (no snapping); turning it back on restores whatever non-1 step was last in effect
    /// (<c>16</c> the first time, since <see cref="GridSize"/> defaults to <c>1</c>).
    /// </summary>
    public bool SnapToGridEnabled
    {
        get => GridSize > 1;
        set
        {
            if (value == SnapToGridEnabled) return;

            // Capture the step in effect right now — not just whatever the GridSize setter has tracked
            // across its own prior calls — since GridSize may have started out >1 without ever passing
            // through that setter (e.g. loaded from settings at startup).
            if (!value && GridSize > 1) _lastNonOneGridSize = GridSize;

            GridSize = value ? (_lastNonOneGridSize > 1 ? _lastNonOneGridSize : 16) : 1;
        }
    }

    /// <summary>
    /// The grid step shown by the "Snap panel position to grid" card's slider: <see cref="GridSize"/>
    /// while snapping is on, or the remembered last step while it is off (D-9: a disabled control still
    /// shows a meaningful value, never a misleading "1"). Setting it is a no-op while
    /// <see cref="SnapToGridEnabled"/> is <see langword="false"/> — the slider is disabled in that state.
    /// </summary>
    public int GridStep
    {
        get => GridSize > 1 ? GridSize : _lastNonOneGridSize;
        set
        {
            if (SnapToGridEnabled) GridSize = value;
        }
    }

    /// <summary>
    /// <see langword="true"/> while the "Snap panel size to icon grid" toggle can have an effect: the
    /// icon-grid snap yields to the positional grid (<c>ResizeConstraintsFactory.IsIconGridSnapActive</c>
    /// is false for <c>GridSize &gt; 1</c>), so the toggle is disabled while <see cref="SnapToGridEnabled"/>
    /// is on.
    /// </summary>
    public bool IsSnapResizeToIconGridAvailable => !SnapToGridEnabled;

    /// <summary>
    /// When <see langword="true"/>, resizing snaps panel dimensions to the icon-grid cell size.
    /// </summary>
    public bool SnapResizeToIconGrid
    {
        get => _appSettings.SnapResizeToIconGrid;
        set
        {
            if (_appSettings.SnapResizeToIconGrid == value) return;
            _appSettings.SnapResizeToIconGrid = value;
            _appSettings.Save();
            OnPropertyChanged(nameof(SnapResizeToIconGrid));
        }
    }

    /// <summary>
    /// Magnetic pull distance (px) applied while dragging a panel. 0 disables the live drag-time
    /// magnet (movement stays free until drop, where <see cref="GridSize"/> quantisation still applies).
    /// </summary>
    public int GridSnapDistance
    {
        get => _appSettings.GridSnapDistance;
        set
        {
            if (_appSettings.GridSnapDistance == value) return;
            _appSettings.GridSnapDistance = value;
            _appSettings.Save();
            OnPropertyChanged(nameof(GridSnapDistance));
        }
    }

    /// <summary>
    /// Default background colour (hex) for new panels. Setting it directly (bypassing
    /// <see cref="DefaultColors"/>'s own accent proxy — e.g. via <c>MainWindowViewModel</c>'s legacy
    /// forwarding property) still refreshes <see cref="DefaultColors"/>' selected-preset highlight.
    /// </summary>
    public string DefaultBackgroundColor
    {
        get => _appSettings.DefaultBackgroundColor;
        set
        {
            if (SetGlobal(v => _appSettings.DefaultBackgroundColor = v, value, _appSettings.DefaultBackgroundColor, nameof(DefaultBackgroundColor)))
                DefaultColors.Refresh();
        }
    }

    /// <summary>Default text colour (hex) for new panels. Same direct-set refresh note as <see cref="DefaultBackgroundColor"/>.</summary>
    public string DefaultTextColor
    {
        get => _appSettings.DefaultTextColor;
        set
        {
            if (SetGlobal(v => _appSettings.DefaultTextColor = v, value, _appSettings.DefaultTextColor, nameof(DefaultTextColor)))
                DefaultColors.Refresh();
        }
    }

    /// <summary>Default opacity (0–1) for new panels.</summary>
    public double DefaultOpacity
    {
        get => _appSettings.DefaultOpacity;
        set => SetGlobal(v => _appSettings.DefaultOpacity = v, value, _appSettings.DefaultOpacity, nameof(DefaultOpacity));
    }

    /// <summary>Default icon size (px) for new panels.</summary>
    public double DefaultIconSize
    {
        get => _appSettings.DefaultIconSize;
        set => SetGlobal(v => _appSettings.DefaultIconSize = v, value, _appSettings.DefaultIconSize, nameof(DefaultIconSize));
    }

    /// <summary>
    /// Default <see cref="PanelSurfaceStyle"/> for new panels, persisted as the enum member name via
    /// <see cref="IAppSettings.DefaultSurfaceStyle"/> — parsed with
    /// <see cref="PanelSurfaceStyleParser.ParseOrAmbient"/>, which never throws. Typed as
    /// <see cref="PanelSurfaceStyle"/> (rather than the raw persisted string, as before the
    /// settings-window redesign gave it its first control — docs/prompts/settings-window-redesign.md
    /// item 6) for the same reason as <see cref="DefaultPanelGeometryMode"/>/<see cref="DefaultIconLayoutPolicy"/>
    /// below: a ComboBox binds <c>SelectedItem</c> against the enum, not the persisted string.
    /// </summary>
    public PanelSurfaceStyle DefaultSurfaceStyle
    {
        get => PanelSurfaceStyleParser.ParseOrAmbient(_appSettings.DefaultSurfaceStyle);
        set => SetGlobal(
            v => _appSettings.DefaultSurfaceStyle = v.ToString(),
            value,
            DefaultSurfaceStyle,
            nameof(DefaultSurfaceStyle));
    }

    /// <summary>
    /// Sets <see cref="DefaultSurfaceStyle"/> from a <see cref="PanelSurfaceStyle"/> command parameter —
    /// backs the "New panel defaults" section's Surface style segmented control (release-polish-1.0 visual
    /// review item 5, replacing its previous enum-options-bound ComboBox — that options list is now dead
    /// code, removed per m8), mirroring
    /// <see cref="SelectedPanelSettingsViewModel.SetSurfaceStyleCommand"/>'s exact Command+CommandParameter
    /// shape and the SAME <see cref="DefaultSurfaceStyle"/> read/write path the ComboBox it replaces used.
    /// </summary>
    public ICommand SetDefaultSurfaceStyleCommand { get; }

    /// <summary>
    /// Which panel move/resize gestures remain available while <see cref="ILayoutEditModeService.Mode"/>
    /// is <see cref="LayoutInteractionMode.Normal"/> (docs/PLANS/phase-6-edit-layout-ux.md §1 D-2).
    /// Persisted as the enum member name via <see cref="IAppSettings.NormalModeLayoutGestures"/> — parsed
    /// with <see cref="NormalModeLayoutGesturesParser.ParseOrMoveOnly"/>, which never throws (same shape
    /// as <see cref="DefaultSurfaceStyle"/> above).
    /// </summary>
    public NormalModeLayoutGestures NormalModeLayoutGestures
    {
        get => NormalModeLayoutGesturesParser.ParseOrMoveOnly(_appSettings.NormalModeLayoutGestures);
        set => SetGlobal(
            v => _appSettings.NormalModeLayoutGestures = v.ToString(),
            value,
            NormalModeLayoutGestures,
            nameof(NormalModeLayoutGestures));
    }

    /// <summary>Available options for the Normal-mode gesture-policy selector (settings window).</summary>
    public IReadOnlyList<NormalModeLayoutGestures> NormalModeLayoutGesturesOptions { get; } = Enum.GetValues<NormalModeLayoutGestures>();

    /// <summary>
    /// Default <see cref="PanelGeometryMode"/> for new panels (ADR-028; docs/PLANS/workspace-grid-migration.md),
    /// persisted as the enum member name via <see cref="IAppSettings.DefaultPanelGeometryMode"/> — parsed
    /// with <see cref="PanelGeometryModeParser.ParseOrLegacyFreeform"/>, which never throws (same shape as
    /// <see cref="NormalModeLayoutGestures"/> above). Consumed by panel-creation call sites
    /// (<see cref="MainWindowViewModel.AddPanel"/>, <c>App.xaml.cs</c>'s <c>NewPanelWithDefaults</c>) to seed
    /// <see cref="Models.ChildWindowModel.GeometryMode"/> — never affects panels that already exist.
    /// </summary>
    public PanelGeometryMode DefaultPanelGeometryMode
    {
        get => PanelGeometryModeParser.ParseOrLegacyFreeform(_appSettings.DefaultPanelGeometryMode);
        set => SetGlobal(
            v => _appSettings.DefaultPanelGeometryMode = v.ToString(),
            value,
            DefaultPanelGeometryMode,
            nameof(DefaultPanelGeometryMode));
    }

    /// <summary>Available options for the default-panel-geometry-mode selector (settings window).</summary>
    public IReadOnlyList<PanelGeometryMode> PanelGeometryModeOptions { get; } = Enum.GetValues<PanelGeometryMode>();

    /// <summary>
    /// Default <see cref="IconLayoutPolicy"/> for new panels (ADR-029 Decision 6), persisted as the enum
    /// member name via <see cref="IAppSettings.DefaultIconLayoutPolicy"/> — parsed with
    /// <see cref="IconLayoutPolicyParser.ParseOrLegacyStretch"/>, which never throws. Consumed by the same
    /// panel-creation call sites as <see cref="DefaultPanelGeometryMode"/> to seed
    /// <see cref="Models.ChildWindowModel.IconLayoutPolicy"/> — never affects panels that already exist.
    /// </summary>
    public IconLayoutPolicy DefaultIconLayoutPolicy
    {
        get => IconLayoutPolicyParser.ParseOrLegacyStretch(_appSettings.DefaultIconLayoutPolicy);
        set => SetGlobal(
            v => _appSettings.DefaultIconLayoutPolicy = v.ToString(),
            value,
            DefaultIconLayoutPolicy,
            nameof(DefaultIconLayoutPolicy));
    }

    /// <summary>Available options for the default-icon-layout-policy selector (settings window).</summary>
    public IReadOnlyList<IconLayoutPolicy> IconLayoutPolicyOptions { get; } = Enum.GetValues<IconLayoutPolicy>();

    /// <summary>
    /// Backs the "New panel defaults" section's "Take from selected panel" button
    /// (docs/prompts/settings-window-redesign.md item 6): copies <paramref name="panel"/>'s
    /// appearance/geometry into every default this class owns, so the next "Add panel"/lasso panel
    /// starts out looking like it. Never touches <paramref name="panel"/> itself.
    /// </summary>
    /// <remarks>
    /// Fix-cycle finding 13: writes every changed field first and calls <see cref="IAppSettings.Save"/>
    /// at most once for the whole call, rather than once per one of the (up to seven) fields that
    /// actually differ — <see cref="SetGlobal{T}"/> would otherwise persist the whole settings file up to
    /// seven times for a single button click.
    /// </remarks>
    /// <param name="panel">The panel to copy defaults from.</param>
    public void ApplyFromPanel(Models.ChildWindowModel panel)
    {
        ArgumentNullException.ThrowIfNull(panel);

        var changed = false;
        changed |= SetGlobalNoSave(v => _appSettings.DefaultBackgroundColor = v, panel.AccentColor, DefaultBackgroundColor, nameof(DefaultBackgroundColor));
        changed |= SetGlobalNoSave(v => _appSettings.DefaultTextColor = v, panel.TextColor, DefaultTextColor, nameof(DefaultTextColor));
        changed |= SetGlobalNoSave(v => _appSettings.DefaultOpacity = v, panel.SurfaceOpacity, DefaultOpacity, nameof(DefaultOpacity));
        changed |= SetGlobalNoSave(v => _appSettings.DefaultSurfaceStyle = v.ToString(), panel.SurfaceStyle, DefaultSurfaceStyle, nameof(DefaultSurfaceStyle));
        changed |= SetGlobalNoSave(v => _appSettings.DefaultIconSize = v, panel.IconSize, DefaultIconSize, nameof(DefaultIconSize));
        changed |= SetGlobalNoSave(v => _appSettings.DefaultPanelGeometryMode = v.ToString(), panel.GeometryMode, DefaultPanelGeometryMode, nameof(DefaultPanelGeometryMode));
        changed |= SetGlobalNoSave(v => _appSettings.DefaultIconLayoutPolicy = v.ToString(), panel.IconLayoutPolicy, DefaultIconLayoutPolicy, nameof(DefaultIconLayoutPolicy));

        if (changed) _appSettings.Save();
        if (changed) DefaultColors.Refresh();
    }

    /// <summary>
    /// Applies a colour-preset pair to the new-panel defaults atomically (docs/PLANS/color-presets-and-size-step.md
    /// §3.7) — the <see cref="DefaultColors"/> equivalent of <see cref="ApplyFromPanel"/>'s own batching:
    /// both <see cref="DefaultBackgroundColor"/> and <see cref="DefaultTextColor"/> are written via
    /// <see cref="SetGlobalNoSave{T}"/> and persisted with exactly one <see cref="IAppSettings.Save"/>.
    /// </summary>
    /// <param name="accent">The preset's accent colour, canonical uppercase <c>#RRGGBB</c>.</param>
    /// <param name="text">The preset's text colour, canonical uppercase <c>#RRGGBB</c>.</param>
    private void SetDefaultColors(string accent, string text)
    {
        var changed = false;
        changed |= SetGlobalNoSave(v => _appSettings.DefaultBackgroundColor = v, accent, DefaultBackgroundColor, nameof(DefaultBackgroundColor));
        changed |= SetGlobalNoSave(v => _appSettings.DefaultTextColor = v, text, DefaultTextColor, nameof(DefaultTextColor));

        if (changed) _appSettings.Save();
    }

    /// <summary>
    /// Applies <paramref name="apply"/> and raises <paramref name="propertyName"/>'s
    /// <see cref="PropertyChanged"/> when <paramref name="newValue"/> differs from <paramref name="current"/>
    /// — same equality guard as <see cref="SetGlobal{T}"/>, but leaves persisting the change to the
    /// caller (<see cref="ApplyFromPanel"/>), which batches every field's write into a single
    /// <see cref="IAppSettings.Save"/> call.
    /// </summary>
    /// <returns><see langword="true"/> iff the value actually changed (and was therefore applied).</returns>
    private bool SetGlobalNoSave<T>(Action<T> apply, T newValue, T current, string propertyName)
    {
        if (EqualityComparer<T>.Default.Equals(current, newValue)) return false;
        apply(newValue);
        OnPropertyChanged(propertyName);
        return true;
    }

    /// <returns><see langword="true"/> iff the value actually changed (and was therefore applied and saved).</returns>
    private bool SetGlobal<T>(Action<T> apply, T newValue, T current, string propertyName)
    {
        if (!SetGlobalNoSave(apply, newValue, current, propertyName)) return false;

        _appSettings.Save();
        return true;
    }

    private void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
