using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;
using DesktopOrganizer.UI.Theming;

namespace DesktopOrganizer.UI.Behaviors;

/// <summary>
/// Owns the panel roll-up/roll-down gesture (collapsing a <see cref="Views.ChildWindow"/> to its
/// title bar and restoring it), extracted from <c>ChildWindow.xaml.cs</c>'s <c>ToggleRollUp</c>/
/// <c>ApplyRollUp</c> (REFACTORING_PLAN.md §5.3). Mutates the owning window's <see cref="FrameworkElement.Height"/>
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
    private readonly IResizeAffordance _resizeAffordance;
    private readonly double _titleBarHeight;
    private readonly Func<ChildWindowModel?> _getModel;

    // P04 (docs/PLANS/workspace-grid-03-04-integration.md §3.6): the ADR-028 workspace-grid seam — see
    // IPanelSizingGridHost. Null (a plain Window, or a test that does not supply one) means
    // LegacyFreeform, i.e. the pre-P04 title-bar/remembered-height behaviour, unchanged.
    private readonly IPanelSizingGridHost? _gridHost;

    // Seeded from the same constant ChildWindow.xaml's own Window.Height binds to, rather than a bare
    // 300 literal that silently duplicated it (P02b verify finding). Only ever read before the first
    // ToggleRollUp of a panel whose ExpandedHeight was never assigned by ChildWindow.Initialize.
    private double _expandedHeight = PanelMetrics.DefaultPanelHeight;

    // finalization MAJOR M-6: bumped every time ApplyRollUp starts a new animation, and captured by
    // value into that animation's own Completed handler below — lets a stale handler recognize it has
    // been superseded by a LATER roll-up toggle and no-op instead of clobbering the newer animation's
    // in-flight/just-committed Height. Without this, two toggles fired within the 150ms animation
    // duration (a fast double-click) race: the FIRST (now-obsolete) animation's Completed handler can
    // still fire after the SECOND one starts, detach the SECOND (live) animation from
    // Window.HeightProperty, and stamp Height back to the FIRST animation's (stale) target — silently
    // desyncing the on-screen panel from the model's IsRolledUp, which then gets persisted as-is.
    private int _rollUpGeneration;

    // P06 fix-cycle MINOR-m-2 (adr/ADR-029-icon-layout-policy.md "Fix-цикл P06"): true only WHILE the
    // 150ms DoubleAnimation started by ApplyRollUp is actually in flight — never set for the
    // MotionState.AnimationsEnabled == false branch, which never animates at all. Consulted by
    // Views/ChildWindow.ContentSizing.cs's PanelSizingMode.AutoContent recompute so it never races a
    // roll-up/roll-down transition (a plain Height assignment made mid-animation is silently ignored by
    // WPF — see ApplyRollUp's own H3 remarks — while the caller would otherwise still go on to persist
    // ChildWindowModel.Size/spans as if it had taken effect, drifting the model from what actually ends
    // up on screen).
    private bool _isAnimating;

    /// <summary>
    /// Initializes a new instance of the <see cref="RollUpBehavior"/> class.
    /// </summary>
    /// <param name="window">The owning panel window (its <see cref="FrameworkElement.Height"/> is animated).</param>
    /// <param name="windowManager">Used to persist layout state after a roll-up toggle.</param>
    /// <param name="contentRow">The panel's content <see cref="RowDefinition"/>, collapsed to zero height while rolled up.</param>
    /// <param name="fileListHost">The file-list host element, hidden while rolled up.</param>
    /// <param name="resizeAffordance">
    /// The resize affordance to disable while rolled up (docs/PLANS/phase-2-native-panel-sizing.md
    /// §1.6, R-9) — a <see cref="ThumbOverlayAffordance"/> wrapping the resize-thumb overlay on the
    /// legacy path, or a <see cref="NativeSizingAffordance"/> gating <see cref="NativePanelResizeController"/>'s
    /// hit-testing on the native path. This is the single point that toggles resize availability; the
    /// underlying <c>Thumb</c> overlay/native hit-test gate is never touched anywhere else.
    /// </param>
    /// <param name="titleBarHeight">The panel's fixed title-bar height — the collapsed target height.</param>
    /// <param name="getModel">Accessor for the current <see cref="ChildWindowModel"/> (may return <see langword="null"/> before <c>Initialize</c>).</param>
    /// <param name="gridHost">
    /// P04 (§3.6): the ADR-028 workspace-grid seam. Defaults to <see langword="null"/>, in which case it
    /// falls back to <paramref name="window"/> itself when that window implements
    /// <see cref="IPanelSizingGridHost"/> (the production path — <c>Views.ChildWindow</c> does) — so
    /// <c>ChildWindow</c>'s constructor needs no new argument. Tests pass a fake directly.
    /// </param>
    public RollUpBehavior(
        Window window,
        IWindowManager windowManager,
        RowDefinition contentRow,
        UIElement fileListHost,
        IResizeAffordance resizeAffordance,
        double titleBarHeight,
        Func<ChildWindowModel?> getModel,
        IPanelSizingGridHost? gridHost = null)
    {
        _window = window;
        _windowManager = windowManager;
        _contentRow = contentRow;
        _fileListHost = fileListHost;
        _resizeAffordance = resizeAffordance;
        _titleBarHeight = titleBarHeight;
        _getModel = getModel;
        _gridHost = gridHost ?? window as IPanelSizingGridHost;
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
    /// <see langword="true"/> only while a roll-up/roll-down <see cref="DoubleAnimation"/> started by
    /// <see cref="ApplyRollUp"/> is actually running (P06 fix-cycle MINOR-m-2 — see the backing field's
    /// remarks). Always <see langword="false"/> when <see cref="MotionState.AnimationsEnabled"/> is off.
    /// </summary>
    public bool IsAnimating => _isAnimating;

    /// <summary>
    /// Collapses the panel to its title bar, or restores it to the previous height.
    /// </summary>
    public void ToggleRollUp()
    {
        var model = _getModel();
        if (model is null) return;

        if (!model.IsRolledUp)
        {
            _expandedHeight = _window.Height; // remember before collapsing

            // P04-R1 (§3.6): for a GridAligned panel, also pin the EXPANDED height as a span before it
            // disappears behind the collapsed geometry — roll-down then restores a span, not a raw DIP
            // height that a DPI/work-area change could have invalidated in the meantime. Done here,
            // while the panel is still expanded, precisely so the span describes the expanded size.
            CaptureExpandedSpan();
        }

        model.IsRolledUp = !model.IsRolledUp;
        ApplyRollUp(model.IsRolledUp);
        _windowManager.SaveWindowsState();
    }

    /// <summary>
    /// Applies the collapsed/expanded visual state, animating <see cref="FrameworkElement.Height"/> between the
    /// title-bar height and <see cref="ExpandedHeight"/>.
    /// </summary>
    /// <param name="collapsed"><see langword="true"/> to collapse the panel to its title bar; <see langword="false"/> to restore it.</param>
    public void ApplyRollUp(bool collapsed)
    {
        _contentRow.Height = collapsed ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        _fileListHost.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;

        // R-9: routed through IResizeAffordance instead of touching ResizeOverlay directly — on the
        // native path there is no overlay to hide (it was already disabled once, up front, when that
        // path was selected — see ChildWindow's constructor), only the hit-test gate to flip.
        _resizeAffordance.SetEnabled(!collapsed);

        var (collapsedHeight, expandedHeight) = ResolveRollUpHeights();

        double fromHeight = collapsed ? expandedHeight : collapsedHeight;
        double toHeight = collapsed ? collapsedHeight : expandedHeight;

        // M-6: claim a new generation for THIS animation before it starts (or before the reduced-motion
        // branch below applies the target height directly) — either way, any PREVIOUS animation's
        // Completed handler must recognize itself as stale and no-op (see field doc comment).
        var generation = ++_rollUpGeneration;

        // docs/PLANS/workspace-grid-07-visual.md "reduced motion": when Windows' own animation setting
        // is off (MotionState.AnimationsEnabled, read once at startup — see its own remarks), snap
        // straight to the target height instead of animating. BeginAnimation(..., null) first detaches
        // any earlier in-flight roll-up animation still holding HeightProperty (H3, see this method's
        // own Completed-handler comment below for why an animation can otherwise pin the property) —
        // without it, this plain assignment could be silently ignored if a previous toggle's animation
        // clock were still attached.
        if (!MotionState.AnimationsEnabled)
        {
            _window.BeginAnimation(Window.HeightProperty, null);
            _window.Height = toHeight;
            return;
        }

        _isAnimating = true;

        var animation = new DoubleAnimation
        {
            From = fromHeight,
            To = toHeight,
            Duration = TimeSpan.FromMilliseconds(150),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };

        // Release the animation's hold on Window.HeightProperty once it finishes (H3,
        // docs/PLANS/phase-1-resize-stabilization.md §3.3): DoubleAnimation defaults to
        // FillBehavior.HoldEnd, which keeps the animation clock — and therefore its animated value —
        // attached to HeightProperty forever after Completed. Any LATER plain assignment
        // (`_window.Height = x`, including every vertical resize delta in
        // PanelResizeController.OnResizeDragDelta) becomes a local value with lower priority than that
        // still-active animated value, so WPF silently ignores it: vertical resize visually stops
        // working the first time a panel is rolled up/down. Detaching the animation
        // (BeginAnimation(..., null)) and re-asserting toHeight as a plain local value, right after the
        // animation's own visual effect finishes, restores normal Height semantics. FillBehavior.Stop
        // was considered and rejected — it snaps back to the pre-animation value for one frame before
        // this handler runs, producing a visible flicker.
        animation.Completed += (_, _) =>
        {
            // M-6: a later toggle may have already claimed a new generation and started its own
            // animation by the time this (now-stale) Completed handler runs — if so, bail out instead of
            // detaching/overwriting the live animation with this obsolete one's target Height.
            if (generation != _rollUpGeneration) return;

            _isAnimating = false;
            _window.BeginAnimation(Window.HeightProperty, null);
            _window.Height = toHeight;
        };

        // Set Height to the start value synchronously so the first frame is correct.
        _window.Height = fromHeight;
        _window.BeginAnimation(Window.HeightProperty, animation);
    }

    /// <summary>
    /// P04-R2 (§3.6): the collapsed/expanded target heights for <see cref="ApplyRollUp"/>.
    /// <list type="bullet">
    /// <item><see cref="PanelGeometryMode.LegacyFreeform"/> (and any panel without a grid host): exactly
    /// the pre-P04 pair — <c>_titleBarHeight</c> and the remembered <see cref="ExpandedHeight"/>.</item>
    /// <item><see cref="PanelGeometryMode.GridAligned"/>: the collapsed height is
    /// <c>SpansToBounds(CollapsedHeaderSpan)</c> — the same 32 DIP as <c>_titleBarHeight</c>, now sourced
    /// from the grid so the two can never drift (DoD D4-4) — and the expanded height is
    /// <c>SpansToBounds(model.HeightSpanUnits)</c>, i.e. the panel's own persisted SPAN rather than a raw
    /// DIP height.</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// The grid state is resolved against the panel's EXPANDED bounds
    /// (<see cref="ExpandedBoundsEstimate"/>), never its live (possibly mid-animation, possibly
    /// collapsed) height: <c>PanelGridStateResolver</c> lazily migrates a missing span FROM the bounds it
    /// is handed, and handing it a collapsed height would persist the collapsed height as the panel's
    /// span — poisoning exactly the value roll-down is about to restore.
    /// </remarks>
    private (double Collapsed, double Expanded) ResolveRollUpHeights()
    {
        var state = _gridHost?.ResolveGridState(ExpandedBoundsEstimate()) ?? PanelGridState.Legacy;
        if (!state.IsGridAligned || !double.IsFinite(state.UnitSize) || state.UnitSize <= 0d)
            return (_titleBarHeight, _expandedHeight);

        var collapsed = WorkspaceLayoutPolicy
            .SpansToBounds(new GridSpan(1, WorkspaceGridMetrics.CollapsedHeaderSpan), state.UnitSize, state.Gap)
            .Height;

        var expanded = _getModel()?.HeightSpanUnits is { } heightUnits && heightUnits > 0
            ? WorkspaceLayoutPolicy.SpansToBounds(new GridSpan(1, heightUnits), state.UnitSize, state.Gap).Height
            : _expandedHeight;

        return (collapsed, expanded);
    }

    /// <summary>
    /// P04-R1: pins the panel's current (expanded) size as a span through
    /// <see cref="PanelGeometryWriter"/>, so <see cref="ApplyRollUp"/>'s roll-down branch has a span to
    /// restore. A no-op for a legacy panel or non-finite geometry.
    /// </summary>
    private void CaptureExpandedSpan()
    {
        if (_gridHost is null) return;

        var bounds = new Rect(_window.Left, _window.Top, _window.Width, _window.Height);
        PanelSizingGridSupport.PersistSpans(_gridHost, bounds);
    }

    /// <summary>
    /// The panel's bounds with its EXPANDED height substituted for whatever the window's live height
    /// currently is (collapsed, or mid-animation) — see <see cref="ResolveRollUpHeights"/>'s remarks.
    /// </summary>
    private Rect ExpandedBoundsEstimate()
    {
        var height = double.IsFinite(_expandedHeight) && _expandedHeight > 0d ? _expandedHeight : _window.Height;

        return new Rect(_window.Left, _window.Top, _window.Width, height);
    }
}
