using System.Windows;

namespace DesktopOrganizer.UI.Models;

/// <summary>
/// Single source of truth for the panel chrome's fixed layout constants (docs/PLANS/phase-5-ambient-accent-ui.md
/// §1 D-11). Previously, the header/title-bar height was a literal repeated across
/// <c>ChildWindow.xaml.cs</c>'s <c>TitleBarHeight</c>, <c>RollUpBehavior</c>'s collapsed target height, and
/// <c>ChildWindow.xaml</c>'s row/window metrics — this type replaces those independent literals with one
/// constant, consumed via <c>x:Static</c> from XAML and directly from C#.
/// </summary>
public static class PanelMetrics
{
    /// <summary>The panel title bar / <c>PanelChrome</c> header height, in device-independent pixels.</summary>
    public const double HeaderHeight = 32d;

    /// <summary>Thickness of the accent indicator strip on a panel's edge.</summary>
    public const double AccentIndicatorThickness = 3d;

    /// <summary>Width/height of a square header action button (Sort/View/RollUp/Overflow).</summary>
    public const double HeaderButtonSize = 28d;

    /// <summary>
    /// CORNER resize-grip width/height, in DIP — single source of truth shared by
    /// <c>ChildWindow.xaml</c>'s <c>ResizeOverlay</c> corner <c>Thumb</c>s (WPF path) and
    /// <c>NativePanelResizeController</c>'s <c>CornerDip</c> constant (native <c>WM_NCHITTEST</c> path,
    /// via <see cref="Interaction.Native.NativeHitTestMapper.Map"/>'s <c>cornerDip</c> parameter).
    /// Widened <c>6 -&gt; 8</c> (docs/PLANS/workspace-grid-07-visual.md fix-cycle 1, MAJOR-1) to match
    /// <c>Radius.Panel</c>'s own widening (<c>Resources/Styles/Spacing.xaml</c>): the panel's own rounded
    /// corner (painted by <c>PART_Chrome</c>'s <see cref="System.Windows.Controls.Border.CornerRadius"/>,
    /// clipped identically via <c>Converters/RoundedRectClipConverter.cs</c>) never actually paints the
    /// small square lens outside its own quarter-circle arc — on a window with
    /// <c>AllowsTransparency="True"</c>, a mouse-down landing on that permanently-alpha-0 lens is routed
    /// by the OS straight to whatever is beneath this window (the desktop), never reaching this window's
    /// WPF message loop at all, REGARDLESS of the WPF <c>Thumb</c> resize handle's own hit-test geometry
    /// sitting at the same screen location (see <see cref="Interaction.Native.NativeHitTestMapper"/> and
    /// <c>Converters/RoundedRectClipConverter.cs</c>'s own remarks for the full explanation — this is an
    /// OS-level per-pixel-alpha click-through effect on a layered window, not a WPF <c>Clip</c>-scoping
    /// bug). That lens' own area only depends on the corner radius (<c>r² · (1 - π/4)</c>, ≈13.7 DIP² at
    /// <c>r=8</c>) — widening the grip to match the radius does not shrink the lens itself, but DOES
    /// shrink the fraction of the grip's own footprint it occupies (from ~38% at the pre-fix 6×6 grip to
    /// ~21% at this 8×8 grip — see <c>PanelMetricsTests</c>), leaving most of a diagonal-resize gesture's
    /// initial mouse-down over a still-painted, non-zero-alpha pixel.
    /// <para>
    /// docs/PLANS/workspace-grid-07-visual.md fix-cycle 2 (MAJOR-A): this used to be the SAME constant
    /// (<c>ResizeGripThickness</c>) the straight-edge strips also used, which widened every straight edge
    /// to 8 DIP too — the click-through-lens argument above only ever applies to the four CORNERS (the
    /// only places a rounded arc actually removes a triangular slice of opaque pixels); a straight edge
    /// midpoint is never near the arc at all. Sharing one constant between both roles was itself the root
    /// cause of a real regression: an 8-DIP-tall TOP edge strip swallowed the top ~21% of every 28-DIP
    /// header button (<see cref="HeaderButtonSize"/> centred in <see cref="HeaderHeight"/>, a 2-DIP top
    /// margin) and an 8-DIP-wide RIGHT edge strip covered roughly half of
    /// <see cref="System.Windows.SystemParameters.VerticalScrollBarWidth"/>'s default width. This
    /// constant now covers ONLY the four corner squares; see <see cref="ResizeEdgeThickness"/> for the
    /// (narrower, restored-to-pre-fix-cycle-1) straight-edge value.
    /// </para>
    /// <para>
    /// P09 release-gate fix (docs/PLANS/workspace-grid-07-visual.md, defect list): fix-cycle 2's own
    /// approach — give each corner <c>Thumb</c> an explicit <c>Width</c>/<c>Height</c> of this (larger)
    /// constant while leaving it inside a cell sized by <see cref="ResizeEdgeThickness"/> (6), relying on
    /// corner-anchoring <c>Horizontal</c>/<c>VerticalAlignment</c> to "overflow" the extra 2 DIP — never
    /// actually rendered/hit-tested at 8×8 on this WPF path. WPF applies an automatic per-element layout
    /// clip (<see cref="System.Windows.FrameworkElement"/>'s internal <c>NeedsClipBounds</c> mechanism —
    /// entirely separate from <see cref="System.Windows.Controls.Panel.ClipToBounds"/>, and NOT bypassed
    /// by alignment) to ANY child whose resolved <see cref="System.Windows.FrameworkElement.RenderSize"/>
    /// exceeds the arrange slot the <c>Grid</c> actually gave it — silently clipping fix-cycle 2's
    /// "overflow" straight back down to 6×6; alignment only ever controlled WHICH 6×6 corner of the 8×8
    /// desired size survived the clip, never whether the clip applied. Confirmed with a real, on-Windows
    /// STA regression test (<c>Unit.Views.ResizeOverlayGeometryTests</c>) before this fix. Fix: dedicate
    /// <c>ResizeOverlay</c>'s outer <c>Grid</c> cells to THIS (wider) constant via
    /// <see cref="ResizeCornerThicknessLength"/> instead, and make the straight-edge <c>Thumb</c>s the
    /// SMALLER ones (<see cref="ResizeEdgeThickness"/>, anchored to their own outer edge) — a child
    /// SMALLER than its own cell is never auto-clipped regardless of alignment, so the corner
    /// <c>Thumb</c>s now simply fill their own dedicated cell (default <c>Stretch</c> alignment, no
    /// explicit <c>Width</c>/<c>Height</c> needed at all) with no reliance on clip semantics either way.
    /// </para>
    /// </summary>
    public const double ResizeCornerThickness = 8d;

    /// <summary>
    /// STRAIGHT-EDGE resize-grip strip thickness, in DIP — the four non-corner <c>ResizeOverlay</c>
    /// <c>Thumb</c>s (Top/Bottom/Left/Right) and the matching straight-edge classification band in
    /// <see cref="Interaction.Native.NativeHitTestMapper.Map"/> (native path). Restored to its
    /// pre-fix-cycle-1 value of <c>6</c> (docs/PLANS/workspace-grid-07-visual.md fix-cycle 2, MAJOR-A) —
    /// see <see cref="ResizeCornerThickness"/>'s remarks for why sharing one constant between the corner
    /// and edge roles was wrong in the first place. A straight edge never sits near
    /// <c>Radius.Panel</c>'s rounded arc (that only ever touches the four corners), so it has no
    /// click-through-lens reason to be any wider than it always was.
    /// </summary>
    public const double ResizeEdgeThickness = 6d;

    /// <summary>
    /// A <see cref="GridLength"/> wrapping <see cref="ResizeEdgeThickness"/>, for direct <c>x:Static</c>
    /// use in <c>ChildWindow.xaml</c>'s <c>ResizeOverlay</c> <c>Grid.ColumnDefinitions</c>/
    /// <c>RowDefinitions</c> of the straight-edge <c>Thumb</c>s' own MIDDLE column/row — the same "wrap
    /// the double in a <see cref="GridLength"/>" pattern already used by <see cref="HeaderRowHeight"/>/
    /// <see cref="ContentLeftInsetColumnWidth"/> above. See <see cref="ResizeCornerThicknessLength"/> for
    /// the OUTER (corner) cells' own wrapper — P09 (docs/PLANS/workspace-grid-07-visual.md, defect list)
    /// gave <c>ResizeOverlay</c>'s outer cells dedicated <see cref="ResizeCornerThickness"/> sizing
    /// instead of sharing this (narrower) one, which is what this field's own doc used to describe as
    /// the shared shape — see <see cref="ResizeCornerThickness"/>'s remarks for why.
    /// </summary>
    public static readonly GridLength ResizeEdgeThicknessLength = new(ResizeEdgeThickness);

    /// <summary>
    /// A <see cref="GridLength"/> wrapping <see cref="ResizeCornerThickness"/>, for direct <c>x:Static</c>
    /// use in <c>ChildWindow.xaml</c>'s <c>ResizeOverlay</c> <c>Grid.ColumnDefinitions</c>/
    /// <c>RowDefinitions</c> OUTER (corner) cells — added by the P09 fix
    /// (docs/PLANS/workspace-grid-07-visual.md, defect list; see <see cref="ResizeCornerThickness"/>'s own
    /// remarks for the bug this replaces). The corner <c>Thumb</c>s themselves still consume
    /// <see cref="ResizeCornerThickness"/> only implicitly (they fill their own dedicated cell via default
    /// <c>Stretch</c> alignment, no explicit <c>Width</c>/<c>Height</c> needed) — this wrapper exists
    /// purely for the <c>Grid</c>'s own column/row definitions, the same "one raw constant, plus a wrapped
    /// type for the one consumer that needs it" split already used for <see cref="ResizeEdgeThickness"/>/
    /// <see cref="ResizeEdgeThicknessLength"/> above.
    /// </summary>
    public static readonly GridLength ResizeCornerThicknessLength = new(ResizeCornerThickness);

    /// <summary>A <see cref="GridLength"/> wrapping <see cref="HeaderHeight"/>, for direct use in a <c>RowDefinition</c>.</summary>
    public static readonly GridLength HeaderRowHeight = new(HeaderHeight);

    /// <summary>
    /// Width of the fixed accent-indicator chrome column reserved to the LEFT of a panel's content area
    /// (<c>PART_AccentIndicator</c>'s <c>ColumnDefinition</c>, <c>Resources/Styles/Panels.xaml</c>,
    /// ADR-025 MAJOR-1 — kept as a fixed DIP literal there, not <c>Auto</c>, to avoid the ~2.1px content
    /// jitter <c>Auto</c> sizing caused on every hover/drag/resize <c>VisualState</c> transition).
    /// <para>
    /// Single source of truth for the boundary between ADR-028's workspace panel grid (a panel's whole
    /// <see cref="System.Windows.Window.Width"/>) and ADR-015/018/029's internal icon layout (DIP
    /// relative to CONTENT origin, i.e. <c>Window.Width - ContentLeftInset</c>) — see
    /// adr/ADR-029-icon-layout-policy.md, Решение 2 ("F-3" fix,
    /// docs/PLANS/workspace-grid-00-baseline.md finding F-3). Before this constant existed,
    /// <see cref="GridMetrics.FileListHPadding"/> was an independent <c>0d</c> literal that every
    /// analytical snap-time computation (<c>PanelResizeController</c>/<c>NativePanelResizeController</c>'s
    /// <c>SnapWidthToGrid</c>, <see cref="GridMetrics.MinPanelWidth"/>,
    /// <c>ResizeConstraintsFactory.BuildConstraints</c>'s <c>HorizontalChrome</c>) used to convert
    /// between a window-width value and a content-width value — silently disagreeing with this real
    /// chrome column by exactly one column's worth of width at every snap point. Applies identically to
    /// BOTH <see cref="IconLayoutPolicy"/> values (ADR-029 Open Question Q2 = A): this is a chrome-
    /// accounting fix, orthogonal to the stretch-vs-fixed-gap contract itself.
    /// </para>
    /// </summary>
    public const double ContentLeftInset = 8d;

    /// <summary>
    /// A <see cref="GridLength"/> wrapping <see cref="ContentLeftInset"/>, for direct <c>x:Static</c> use
    /// in <c>Resources/Styles/Panels.xaml</c>'s <c>PART_AccentIndicator</c> <c>ColumnDefinition.Width</c>
    /// — the same "wrap the double in a <see cref="GridLength"/>" pattern already used for
    /// <see cref="HeaderRowHeight"/> above. Unlike a plain <see langword="double"/>,
    /// <see cref="GridLength"/> has no double-&gt;<see cref="GridLength"/>
    /// <see cref="System.ComponentModel.TypeConverter"/> pathway reachable from a bare <c>x:Static</c> of
    /// <see cref="ContentLeftInset"/> — this wrapper is what lets the XAML column and
    /// <see cref="GridMetrics.FileListHPadding"/> in C# read the same single source of truth instead of
    /// two independently-maintained literal "8"s.
    /// </summary>
    public static readonly GridLength ContentLeftInsetColumnWidth = new(ContentLeftInset);

    /// <summary>
    /// The default panel width, in DIP, for a newly created panel whose caller never assigned an
    /// explicit <c>Size</c> (the plain "Add Panel" action — <c>MainWindowViewModel.AddPanel</c> relies
    /// entirely on <c>ChildWindow.xaml</c>'s own <c>Window.Width</c> default instead of setting
    /// <c>ChildWindowModel.Size</c> itself). Single source of truth (MINOR N-6, fix-cycle 2,
    /// docs/PLANS/workspace-grid-migration.md §10.8) for a value that used to be an independent XAML
    /// literal (<c>ChildWindow.xaml</c>'s <c>Window.Width="260"</c>) with no C# counterpart — see
    /// <see cref="DefaultPanelSize"/> for why a second constant exists alongside it.
    /// </summary>
    public const double DefaultPanelWidth = 260d;

    /// <summary>The default panel height, in DIP. See <see cref="DefaultPanelWidth"/>'s remarks.</summary>
    public const double DefaultPanelHeight = 300d;

    /// <summary>
    /// <see cref="DefaultPanelWidth"/>/<see cref="DefaultPanelHeight"/> wrapped as a single
    /// <see cref="Size"/>, for C# call sites (e.g. <c>PanelDefaults.ReconcileGridAlignedSpans</c>) that
    /// need both dimensions together. <c>ChildWindow.xaml</c>'s <c>Window.Width</c>/<c>Window.Height</c>
    /// bind to the two separate <see langword="double"/> constants above instead of this property,
    /// because <c>x:Static</c> cannot address a nested member (<c>DefaultPanelSize.Width</c>) — the same
    /// "one raw constant, plus a wrapped type for the one consumer that needs it" split already used for
    /// <see cref="HeaderHeight"/>/<see cref="HeaderRowHeight"/> and <see cref="ContentLeftInset"/>/
    /// <see cref="ContentLeftInsetColumnWidth"/> above.
    /// </summary>
    public static readonly Size DefaultPanelSize = new(DefaultPanelWidth, DefaultPanelHeight);
}
