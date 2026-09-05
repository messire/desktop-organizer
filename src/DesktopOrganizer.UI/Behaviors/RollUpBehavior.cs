using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;

namespace DesktopOrganizer.UI.Behaviors;

/// <summary>
/// Owns the panel roll-up/roll-down gesture (collapsing a <see cref="Views.ChildWindow"/> to its
/// title bar and restoring it), extracted from <c>ChildWindow.xaml.cs</c>'s <c>ToggleRollUp</c>/
/// <c>ApplyRollUp</c> (REFACTORING_PLAN.md §5.3). Mutates the owning window's <see cref="Window.Height"/>
/// via a short <see cref="DoubleAnimation"/> and toggles the visibility of the content row/resize
/// overlay — all UI elements are supplied by the owning window at construction, so this class carries
/// no dependency on <c>ChildWindow</c> itself.
/// </summary>
public sealed class RollUpBehavior
{
    private readonly Window _window;
    private readonly IWindowManager _windowManager;
    private readonly RowDefinition _contentRow;
    private readonly UIElement _fileListHost;
    private readonly UIElement _resizeOverlay;
    private readonly double _titleBarHeight;
    private readonly Func<ChildWindowModel?> _getModel;

    private double _expandedHeight = 300;

    /// <summary>
    /// Initializes a new instance of the <see cref="RollUpBehavior"/> class.
    /// </summary>
    /// <param name="window">The owning panel window (its <see cref="Window.Height"/> is animated).</param>
    /// <param name="windowManager">Used to persist layout state after a roll-up toggle.</param>
    /// <param name="contentRow">The panel's content <see cref="RowDefinition"/>, collapsed to zero height while rolled up.</param>
    /// <param name="fileListHost">The file-list host element, hidden while rolled up.</param>
    /// <param name="resizeOverlay">The resize-thumb overlay, hidden while rolled up.</param>
    /// <param name="titleBarHeight">The panel's fixed title-bar height — the collapsed target height.</param>
    /// <param name="getModel">Accessor for the current <see cref="ChildWindowModel"/> (may return <see langword="null"/> before <c>Initialize</c>).</param>
    public RollUpBehavior(
        Window window,
        IWindowManager windowManager,
        RowDefinition contentRow,
        UIElement fileListHost,
        UIElement resizeOverlay,
        double titleBarHeight,
        Func<ChildWindowModel?> getModel)
    {
        _window = window;
        _windowManager = windowManager;
        _contentRow = contentRow;
        _fileListHost = fileListHost;
        _resizeOverlay = resizeOverlay;
        _titleBarHeight = titleBarHeight;
        _getModel = getModel;
    }

    /// <summary>
    /// The panel height remembered immediately before collapsing, used to restore it on roll-down.
    /// Also read/written by resize-snap code (<c>PanelResizeController.SnapWidthToGrid</c> does not need
    /// it, but <see cref="Views.ChildWindow.Initialize"/> seeds it from the loaded model's height).
    /// </summary>
    public double ExpandedHeight
    {
        get => _expandedHeight;
        set => _expandedHeight = value;
    }

    /// <summary>
    /// Collapses the panel to its title bar, or restores it to the previous height.
    /// </summary>
    public void ToggleRollUp()
    {
        var model = _getModel();
        if (model is null) return;

        if (!model.IsRolledUp) _expandedHeight = _window.Height; // remember before collapsing

        model.IsRolledUp = !model.IsRolledUp;
        ApplyRollUp(model.IsRolledUp);
        _windowManager.SaveWindowsState();
    }

    /// <summary>
    /// Applies the collapsed/expanded visual state, animating <see cref="Window.Height"/> between the
    /// title-bar height and <see cref="ExpandedHeight"/>.
    /// </summary>
    public void ApplyRollUp(bool collapsed)
    {
        _contentRow.Height = collapsed ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        _fileListHost.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        _resizeOverlay.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;

        double fromHeight = collapsed ? _expandedHeight : _titleBarHeight;
        double toHeight   = collapsed ? _titleBarHeight  : _expandedHeight;

        var animation = new DoubleAnimation
        {
            From           = fromHeight,
            To             = toHeight,
            Duration       = TimeSpan.FromMilliseconds(150),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };

        // Set Height to the start value synchronously so the first frame is correct.
        _window.Height = fromHeight;
        _window.BeginAnimation(Window.HeightProperty, animation);
    }
}
