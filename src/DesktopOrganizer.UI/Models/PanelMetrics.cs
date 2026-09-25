using System.Windows;
using DesktopOrganizer.UI.Theming;

namespace DesktopOrganizer.UI.Models;

/// <summary>
/// Single source of truth for the panel chrome's fixed layout constants (docs/PLANS/phase-5-ambient-accent-ui.md
/// §1 D-11). Previously, the header/title-bar height was a literal repeated across
/// <c>ChildWindow.xaml.cs</c>'s <c>TitleBarHeight</c>, <c>RollUpBehavior</c>'s collapsed target height, and
/// <c>ChildWindow.xaml</c>'s row/window metrics — this type replaces those independent literals with one
/// constant, consumed via <c>x:Static</c> from XAML and directly from C#.
/// <para>
/// ADR-030 (docs/PLANS/reference-panel-chrome.md, "Reference-Style Layered Panel Chrome") added the
/// tokens from <see cref="ChromeOuterInset"/> onward below: a budgeted 2-DIP transparent inset inside the
/// panel's own (persisted, unchanged) window rect, hosting a glow/neutral-stroke/accent-stroke/curved-marker
/// layered chrome. All new tokens are pure design constants — no persisted state, no new
/// <see cref="System.Windows.DependencyProperty"/> — per that plan's §0/§10 persistence freeze.
/// </para>
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
    /// Width of the horizontal chrome reserved between a panel's <see cref="System.Windows.Window.Width"/>
    /// and the icon-grid content area (<c>PART_SurfaceLayer</c>'s inner <c>[pad|*|pad]</c> columns,
    /// <c>Resources/Styles/Panels.xaml</c>) — the single source of truth for the boundary between
    /// ADR-028's workspace panel grid (a panel's whole <see cref="System.Windows.Window.Width"/>) and
    /// ADR-015/018/029's internal icon layout (DIP relative to CONTENT origin, i.e.
    /// <c>Window.Width - ContentLeftInset</c>) — see adr/ADR-029-icon-layout-policy.md, Решение 2 ("F-3"
    /// fix, docs/PLANS/workspace-grid-00-baseline.md finding F-3). Before this constant existed,
    /// <see cref="GridMetrics.FileListHPadding"/> was an independent <c>0d</c> literal that every
    /// analytical snap-time computation (<c>PanelResizeController</c>/<c>NativePanelResizeController</c>'s
    /// <c>SnapWidthToGrid</c>, <see cref="GridMetrics.MinPanelWidth(double)"/>,
    /// <c>ResizeConstraintsFactory.BuildConstraints</c>'s <c>HorizontalChrome</c>) used to convert
    /// between a window-width value and a content-width value — silently disagreeing with this real
    /// chrome by exactly one column's worth of width at every snap point.
    /// <para>
    /// <b>ADR-030 amendment (docs/PLANS/reference-panel-chrome.md D-3):</b> this value's SHAPE changed,
    /// its NUMBER did not. Before ADR-030, this 8 DIP was entirely the fixed <c>PART_AccentIndicator</c>
    /// column that used to sit to the LEFT of the content column. ADR-030 retired that column (D-3, D-4 —
    /// the accent identity is now a curved marker overlaid on the chrome, not a layout column) and
    /// re-derived the SAME 8 DIP as <c>2·ChromeOuterInset + 2·ContentSidePadding</c> — two symmetric
    /// 2-DIP side paddings inside the panel's chrome inset (see <see cref="ChromeOuterInset"/>/
    /// <see cref="ContentSidePadding"/>), split roughly evenly left/right instead of concentrated in one
    /// left-hand column. <see cref="GridMetrics.FileListHPadding"/>'s value (still exactly this
    /// constant), every analytical snap-time formula that reads it, and every persisted panel <c>Size</c>
    /// are therefore numerically UNCHANGED by ADR-030 — T-M2 (<c>PanelMetricsTests</c>) pins the new
    /// decomposition equals this same total.
    /// </para>
    /// </summary>
    public const double ContentLeftInset = 8d;

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

    // ─────────────────────────────────────────────────────────────────────────────────────────────────
    // ADR-030 "Reference-Style Layered Panel Chrome" (docs/PLANS/reference-panel-chrome.md §2.3) — chrome
    // inset, glow, stroke, and curved-marker design tokens. Every value below is a pure design constant:
    // no persisted state, sourced/consumed entirely from Resources/Styles/Panels.xaml and
    // Theming/LayeredChromeVisuals.cs/ChromeEffectsPolicy.cs. Starting values only — "подбираются только
    // внутри неравенств, закреплённых тестами" (plan §2.3); see PanelMetricsTests/LayeredChromeVisualsTests
    // for the pinned invariants.
    // ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The width, in DIP, of the transparent inset budgeted INSIDE a panel's existing (persisted,
    /// unchanged) window rect for the layered chrome's glow/stroke layers (plan D-1). Bounded above by
    /// four independent constraints (plan §1 D-1's table, pinned by <c>PanelMetricsTests</c> T-M1/T-M3/
    /// T-M4): a rolled-up header row must still fit <see cref="HeaderButtonSize"/> after two insets
    /// (<c>HeaderHeight - 2·ChromeOuterInset ≥ HeaderButtonSize</c>); the horizontal chrome budget
    /// (<see cref="ContentLeftInset"/>) must exactly absorb <c>2·ChromeOuterInset + 2·ContentSidePadding</c>;
    /// every inset pixel must lie inside the resize hit-test strip
    /// (<c>ChromeOuterInset &lt; ResizeEdgeThickness</c>, closed by <c>PART_HitCatcher</c>, plan D-5); and
    /// the bottom inset must not exceed <see cref="GridMetrics.FileListVPadding"/>'s already-reserved
    /// vertical chrome. <c>Window.Left/Top/Width/Height</c>, snap-time math, and
    /// <c>NativeHitTestMapper</c>'s window-rect classification are all UNCHANGED — the inset lives
    /// entirely inside <c>PanelChrome</c>'s own template (<c>PART_SurfaceLayer</c>'s
    /// <see cref="ChromeOuterInsetThickness"/> margin, <c>Resources/Styles/Panels.xaml</c>), never in
    /// window geometry.
    /// </summary>
    public const double ChromeOuterInset = 2d;

    /// <summary>
    /// Symmetric left/right padding, in DIP, between <c>PART_Chrome</c>'s own edge (already inset by
    /// <see cref="ChromeOuterInset"/>) and the content columns (<c>PART_Header</c>/<c>PART_ContentHost</c>)
    /// — <c>(ContentLeftInset - 2·ChromeOuterInset) / 2</c>. Replaces the old, asymmetric single 8-DIP
    /// LEFT-only <c>PART_AccentIndicator</c> column (ADR-030 D-3): the accent identity moved to an
    /// overlaid curved marker (<see cref="AccentMarkerStrokeThickness"/> et al.) that no longer needs its
    /// own layout column, so the leftover horizontal chrome splits evenly on both sides instead.
    /// </summary>
    public const double ContentSidePadding = (ContentLeftInset - (2 * ChromeOuterInset)) / 2d;

    /// <summary>
    /// The header row's own content height inside <c>PART_Chrome</c>, after budgeting
    /// <see cref="ChromeOuterInset"/> off the top of <see cref="HeaderHeight"/> —
    /// <c>HeaderHeight - 2·ChromeOuterInset</c>. Still comfortably covers <see cref="HeaderButtonSize"/>
    /// (T-M1) — the constraint that bounds <see cref="ChromeOuterInset"/> from above in the first place.
    /// </summary>
    public const double HeaderInnerHeight = HeaderHeight - (2 * ChromeOuterInset);

    /// <summary>
    /// The spacer row, in DIP, between <see cref="HeaderInnerHeight"/> and the content row inside
    /// <c>PART_Chrome</c>'s inner grid — deliberately equal to <see cref="ChromeOuterInset"/> so
    /// <c>ChromeOuterInset + HeaderInnerHeight + HeaderContentGap == HeaderHeight</c> (T-M1): the
    /// header/content boundary inside the inset chrome lands at exactly the same
    /// <see cref="HeaderHeight"/> (32 DIP) screen offset the pre-ADR-030 template used, so
    /// <c>ChildWindow.xaml</c>'s own <c>RollUpBehavior</c> target height and content-row math need no
    /// changes at all (plan D-2/D-3 — <c>ChildWindow.xaml</c> stays byte-for-byte unmodified).
    /// </summary>
    public const double HeaderContentGap = ChromeOuterInset;

    /// <summary>Blur radius, in DIP, of <c>PART_Glow</c>'s single <see cref="System.Windows.Media.Effects.DropShadowEffect"/> (plan D-9/D-10).</summary>
    public const double GlowBlurRadius = 3d;

    /// <summary>
    /// The <see cref="System.Windows.Media.Effects.DropShadowEffect"/>'s own fixed
    /// <see cref="System.Windows.Media.Effects.DropShadowEffect.Opacity"/> (plan D-9) — state-driven
    /// expressiveness is layered on top via <c>PART_Glow</c>'s own <see cref="System.Windows.UIElement.Opacity"/>
    /// (<see cref="GlowOpacityIdle"/>/<see cref="GlowOpacityMax"/>, <c>Theming/LayeredChromeVisuals.GlowOpacityFor</c>),
    /// never by re-creating the effect.
    /// </summary>
    public const double GlowEffectOpacity = 0.6;

    /// <summary><c>PART_Glow</c>'s own <see cref="System.Windows.UIElement.Opacity"/> at <see cref="Theming.PanelVisualState.Idle"/> (plan §2.3, D-6/D-10).</summary>
    public const double GlowOpacityIdle = 0.35;

    /// <summary>
    /// <c>PART_Glow</c>'s own <see cref="System.Windows.UIElement.Opacity"/> at
    /// <see cref="Theming.PanelVisualState.Resizing"/> (plan §2.3, D-6/D-10) — reached regardless of
    /// <c>isLayoutEditing</c>, NOT only while Edit Layout is active: <c>Theming.LayeredChromeVisuals</c>'s
    /// interpolation (D-6) is driven by <c>LayoutEditVisuals.BorderOpacityFor</c>, whose own value at
    /// <see cref="Theming.PanelVisualState.Resizing"/> is already <c>1.0</c> (the channel's own ceiling)
    /// in BOTH modes — <c>Math.Max(1.0, EditBorderFloor)</c> stays <c>1.0</c> either way — so this ceiling
    /// is reached the moment a resize gesture starts, edit mode or not.
    /// </summary>
    public const double GlowOpacityMax = 0.8;

    /// <summary>
    /// How far INSIDE <see cref="ChromeOuterInset"/>, in DIP, <c>PART_GlowFallback</c>'s ring sits (plan
    /// D-9) — its <see cref="System.Windows.FrameworkElement.Margin"/> is
    /// <c>ChromeOuterInset - GlowFallbackRingOffset</c> (<see cref="GlowFallbackMarginThickness"/>), i.e.
    /// 1 DIP outside <c>PART_Chrome</c>'s own surface, when the shader-based <c>PART_Glow</c> is disabled
    /// (<see cref="Theming.ChromeEffectsPolicy.IsGlowEffectEnabled"/> is <see langword="false"/>).
    /// </summary>
    public const double GlowFallbackRingOffset = 1d;

    /// <summary>Multiplier applied to the <c>"glow"</c> opacity channel for <c>PART_GlowFallback</c>'s own opacity (plan D-9) — a dimmer, static substitute for the shader-based glow's own <see cref="GlowEffectOpacity"/> factor.</summary>
    public const double GlowFallbackOpacityFactor = 0.5;

    /// <summary>
    /// Upper bound on the glow's residual alpha at the panel window's own edge (plan D-10) — the model
    /// <c>residual = GlowEffectOpacity · GlowOpacityMax · exp(-ChromeOuterInset² / (2·(GlowBlurRadius/3)²))</c>
    /// must stay at or below this so the glow reads as a soft fade rather than a visible "cut line" at the
    /// window boundary. Pinned by <c>PanelMetricsTests</c> T-M5.
    /// </summary>
    public const double GlowEdgeResidualMax = 0.08;

    /// <summary><c>PART_AccentStroke</c>'s own opacity at <see cref="Theming.PanelVisualState.Idle"/> (plan §2.3/§3, D-6) — deliberately weaker than <see cref="AccentMarkerStrokeThickness"/>'s own visible weight (T-P4).</summary>
    public const double AccentStrokeOpacityIdle = 0.25;

    /// <summary>
    /// <c>PART_AccentStroke</c>'s own opacity at <see cref="Theming.PanelVisualState.Resizing"/> (plan
    /// §2.3/§3, D-6) — reached regardless of <c>isLayoutEditing</c>; see <see cref="GlowOpacityMax"/>'s own
    /// remarks for why every <c>*Max</c> token in this section shares that same "Resizing alone is
    /// enough" property.
    /// </summary>
    public const double AccentStrokeOpacityMax = 0.55;

    /// <summary><c>PART_NeutralStroke</c>'s own opacity at <see cref="Theming.PanelVisualState.Idle"/> (plan §2.3, D-6).</summary>
    public const double NeutralStrokeOpacityIdle = 0.55;

    /// <summary>
    /// <c>PART_NeutralStroke</c>'s own opacity at <see cref="Theming.PanelVisualState.Resizing"/> (plan
    /// §2.3, D-6) — reached regardless of <c>isLayoutEditing</c>; see <see cref="GlowOpacityMax"/>'s own
    /// remarks.
    /// </summary>
    public const double NeutralStrokeOpacityMax = 0.9;

    /// <summary>
    /// The curved accent marker's (<c>PART_AccentIndicator</c>, now a <see cref="System.Windows.Shapes.Path"/>
    /// — plan D-4) base <see cref="System.Windows.Shapes.Shape.StrokeThickness"/>, before the per-state
    /// <c>LayoutEditVisuals.AccentEmphasisFor</c> multiplier. Because the marker's <see cref="System.Windows.Shapes.Path"/>
    /// is clipped to the SAME rounded rectangle <c>PART_Chrome</c>'s own surface paints (plan D-4 — "видна
    /// только внутренняя половина штриха"), only half of this thickness ever renders — the token is
    /// deliberately "2× the desired visible width" (visible Idle width = <c>4 * 1.0 / 2</c> = 2 DIP).
    /// </summary>
    public const double AccentMarkerStrokeThickness = 4d;

    /// <summary>
    /// Length, in DIP, of the curved marker's straight vertical stem below the rounded corner's own
    /// radius (plan D-4) — the marker's path runs from <c>(0, RadiusPanel + AccentMarkerStemLength)</c>
    /// up to <c>(0, RadiusPanel)</c> before curving. Chosen (14, not the plan's illustrative starting
    /// value of 16 — see docs/PLANS/reference-panel-chrome.md §2.3's own "стартовые значения" framing)
    /// so the marker's own maximally-emphasized round end-cap
    /// (<c>AccentMarkerStrokeThickness * InteractionVisualState.AccentEmphasisFor(Resizing) *
    /// LayoutEditVisuals.AccentEmphasis / 2</c> ≈ 4.59 DIP) never pushes the marker's own bottom edge past
    /// <see cref="HeaderInnerHeight"/> (plan D-4 collision requirement (б); see
    /// <see cref="AccentMarkerHostHeight"/>, pinned by <c>PanelMetricsTests</c>).
    /// </summary>
    public const double AccentMarkerStemLength = 14d;

    /// <summary>Length, in DIP, of the curved marker's short horizontal tail past the rounded corner's own radius (plan D-4).</summary>
    public const double AccentMarkerTailLength = 2d;

    /// <summary>Offset (0..1, top-to-bottom) where <c>PART_SurfaceHighlight</c>'s <see cref="System.Windows.Media.LinearGradientBrush"/> fades to fully transparent (plan D-11).</summary>
    public const double SurfaceHighlightEndOffset = 0.5;

    /// <summary>
    /// Duplicated (by hand, deliberately) mirror of <c>Resources/Styles/Spacing.xaml</c>'s
    /// <c>Radius.Panel</c> (8 DIP), used only to size the curved marker's fixed host (see
    /// <see cref="AccentMarkerHostWidth"/>'s remarks) — never rendered directly. A hand-written literal,
    /// not a computed expression, because XAML's <c>CornerRadius</c> resource has no C# counterpart this
    /// class could read at compile time (the same reason <c>Spacing.xaml</c>'s <c>Radius.Panel.Inner</c>
    /// is itself a hand-written literal, not an expression) — the equality with the REAL
    /// <c>Radius.Panel</c> resource is pinned by <c>PanelMetricsTests.AccentMarkerCornerRadiusBudget_EqualsRadiusPanel</c>,
    /// not merely asserted in this comment (the exact class of drift this plan's own §9 calls out as a
    /// past review-cycle lesson). <see langword="public"/> (not <see langword="internal"/>), matching this
    /// static class's own established convention: every token here, including ones with no direct XAML
    /// consumer, is public — a shared C#/XAML token library, not an encapsulated implementation type.
    /// </summary>
    public const double AccentMarkerCornerRadiusBudget = 8d;

    /// <summary>
    /// The curved marker's own maximum combined <see cref="System.Windows.Shapes.Shape.StrokeThickness"/>
    /// — <see cref="AccentMarkerStrokeThickness"/> scaled by the SAME two REAL multipliers (not
    /// hand-copied mirrors of them) the pre-existing rectangular indicator's own maximum combined width
    /// used (docs/PLANS/phase-5-ambient-accent-ui.md MAJOR-1/MAJOR-B):
    /// <see cref="InteractionVisualState.AccentEmphasisFor"/> at <see cref="PanelVisualState.Resizing"/>
    /// (1.7) and <see cref="LayoutEditVisuals.AccentEmphasis"/> (1.35) on top, reached at
    /// <see cref="PanelVisualState.Resizing"/> while Edit Layout is active (the marker's stroke round-cap
    /// radius at that combination is half this value, ≈4.59 DIP — plan D-4's own collision-analysis
    /// number). <see langword="static readonly"/>, not <see langword="const"/>, because
    /// <see cref="InteractionVisualState.AccentEmphasisFor"/> is a method call, not a compile-time
    /// constant — evaluated once, the first time this type is touched, same as every other
    /// <see langword="static readonly"/> token below.
    /// </summary>
    public static readonly double AccentMarkerMaxStrokeThickness =
        AccentMarkerStrokeThickness
        * InteractionVisualState.AccentEmphasisFor(PanelVisualState.Resizing)
        * LayoutEditVisuals.AccentEmphasis;

    /// <summary>
    /// A <see cref="GridLength"/> wrapping <see cref="ResizeEdgeThickness"/>, for direct <c>x:Static</c>
    /// use in <c>ChildWindow.xaml</c>'s <c>ResizeOverlay</c> <c>Grid.ColumnDefinitions</c>/
    /// <c>RowDefinitions</c> of the straight-edge <c>Thumb</c>s' own MIDDLE column/row — the same "wrap
    /// the double in a <see cref="GridLength"/>" pattern already used by <see cref="HeaderRowHeight"/>/
    /// <see cref="ContentLeftInsetColumnWidth"/> below. See <see cref="ResizeCornerThicknessLength"/> for
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
    /// A <see cref="GridLength"/> wrapping <see cref="ContentLeftInset"/>. Historically
    /// <c>PART_AccentIndicator</c>'s own <c>ColumnDefinition.Width</c> (ADR-025 MAJOR-1); ADR-030 retired
    /// that column (see <see cref="ContentLeftInset"/>'s own remarks) — this wrapper's only remaining
    /// consumer is the sync test pinning <see cref="GridMetrics.FileListHPadding"/> against the same
    /// underlying constant. Kept (not deleted) because it is <see langword="public"/> API with an existing
    /// regression test, and a working, harmless wrapper is not "unused code" the 0-warnings rule requires
    /// removing (docs/PLANS/reference-panel-chrome.md §2.2's own explicit "либо оставляется" allowance).
    /// </summary>
    public static readonly GridLength ContentLeftInsetColumnWidth = new(ContentLeftInset);

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

    /// <summary>
    /// A <see cref="Thickness"/> wrapping <see cref="ChromeOuterInset"/> uniformly on all four sides, for
    /// direct <c>x:Static</c> use as <c>PART_Glow</c>/<c>PART_SurfaceLayer</c>'s own <c>Margin</c>
    /// (<c>Resources/Styles/Panels.xaml</c>) — the same "wrap the double for the one consumer that needs
    /// the wrapped type" pattern as <see cref="HeaderRowHeight"/>/<see cref="ContentLeftInsetColumnWidth"/>.
    /// </summary>
    public static readonly Thickness ChromeOuterInsetThickness = new(ChromeOuterInset);

    /// <summary>A <see cref="GridLength"/> wrapping <see cref="ContentSidePadding"/>, for <c>PART_Chrome</c>'s inner <c>[pad|*|pad]</c> <c>ColumnDefinition</c>s.</summary>
    public static readonly GridLength ContentSidePaddingLength = new(ContentSidePadding);

    /// <summary>A <see cref="GridLength"/> wrapping <see cref="HeaderInnerHeight"/>, for <c>PART_Chrome</c>'s inner header <c>RowDefinition</c>.</summary>
    public static readonly GridLength HeaderInnerRowHeight = new(HeaderInnerHeight);

    /// <summary>A <see cref="GridLength"/> wrapping <see cref="HeaderContentGap"/>, for <c>PART_Chrome</c>'s inner spacer <c>RowDefinition</c>.</summary>
    public static readonly GridLength HeaderContentGapLength = new(HeaderContentGap);

    /// <summary>
    /// A <see cref="Thickness"/> for <c>PART_GlowFallback</c>'s own <c>Margin</c> —
    /// <c>ChromeOuterInset - GlowFallbackRingOffset</c> uniformly on all sides (plan D-9).
    /// </summary>
    public static readonly Thickness GlowFallbackMarginThickness = new(ChromeOuterInset - GlowFallbackRingOffset);

    /// <summary>
    /// The curved accent marker's fixed host <see cref="System.Windows.FrameworkElement.Width"/>
    /// (plan D-4/D-7 item 2) — computed, not a literal, from the marker's own path bounds
    /// (<c>AccentMarkerCornerRadiusBudget + AccentMarkerTailLength</c>, horizontal extent) inflated by
    /// half its own maximum combined <see cref="System.Windows.Shapes.Shape.StrokeThickness"/> (round
    /// end-caps extend a stroke's visual bounds by <c>StrokeThickness / 2</c> beyond its geometric path).
    /// Never <see langword="Auto"/> — a fixed host size is what keeps a <see cref="System.Windows.Shapes.Shape.StrokeThickness"/>
    /// change (a per-state, not per-layout, fact) from ever perturbing this control's own
    /// <see cref="System.Windows.UIElement.DesiredSize"/> (plan D-7 "no layout jitter").
    /// </summary>
    public static readonly double AccentMarkerHostWidth =
        AccentMarkerCornerRadiusBudget + AccentMarkerTailLength + (AccentMarkerMaxStrokeThickness / 2d);

    /// <summary>
    /// The curved accent marker's fixed host <see cref="System.Windows.FrameworkElement.Height"/> — see
    /// <see cref="AccentMarkerHostWidth"/>'s remarks; the vertical counterpart uses
    /// <see cref="AccentMarkerStemLength"/> instead of <see cref="AccentMarkerTailLength"/>. Chosen (via
    /// <see cref="AccentMarkerStemLength"/>'s own value) to stay at/under <see cref="HeaderInnerHeight"/>
    /// — plan D-4 collision requirement (б); pinned by <c>PanelMetricsTests</c>.
    /// </summary>
    public static readonly double AccentMarkerHostHeight =
        AccentMarkerCornerRadiusBudget + AccentMarkerStemLength + (AccentMarkerMaxStrokeThickness / 2d);
}
