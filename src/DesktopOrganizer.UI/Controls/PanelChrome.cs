using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Theming;

namespace DesktopOrganizer.UI.Controls;

/// <summary>
/// Lookless, reusable panel chrome — the shared Header/AccentIndicator/ContentPresenter/InteractionOverlay
/// composition (ADR-025, docs/PLANS/phase-5-ambient-accent-ui.md §1 D-8, §5.3 of the phase spec).
/// </summary>
/// <remarks>
/// <para>
/// A lookless <see cref="ContentControl"/>, not a <c>UserControl</c> and not "a <see cref="Style"/> on a
/// <see cref="Border"/>" — both alternatives were rejected in D-8: a <c>UserControl</c> cannot be
/// re-templated and would force <c>ChildWindow</c>'s drag/rename/drop event wiring to move into this
/// control's own code-behind, and a plain <c>Style</c> still needs real <see cref="DependencyProperty"/>s
/// for its triggers, so a lookless <see cref="ContentControl"/> with named template parts is the smallest
/// step that actually buys anything (a reviewable/testable PART_* contract).
/// </para>
/// <para>
/// The default style lives in <c>Resources/Styles/Panels.xaml</c> (set via
/// <see cref="FrameworkElement.DefaultStyleKeyProperty"/> below) — <c>Themes/Generic.xaml</c> is
/// deliberately not introduced; <c>Panels.xaml</c> is merged explicitly into <c>App.xaml</c>'s
/// <c>MergedDictionaries</c> alongside its siblings (D-1), and WPF's default-style lookup walks up to
/// <see cref="Application.Resources"/> regardless of which merged dictionary a key came from.
/// </para>
/// <para>
/// <b>Deliberately owns no interaction code.</b> The panel title bar's drag-to-move gesture and its
/// right-click context menu (<see cref="Behaviors.PanelDragMoveBehavior"/>, kept byte-for-byte unchanged
/// per docs/PLANS/phase-5-ambient-accent-ui.md §0) reference <c>ChildWindow.xaml.cs</c> code-behind
/// methods by name — something a <see cref="ResourceDictionary"/>-hosted <see cref="ControlTemplate"/>
/// cannot do (a <see cref="ResourceDictionary"/> has no code-behind class). <c>ChildWindow.xaml</c>
/// therefore attaches those handlers directly to the element it supplies via <see cref="TitleEditor"/>
/// (which it declares itself, in its own file) rather than to anything inside this control's template —
/// <see cref="PartHeader"/> only lays the header out, it never handles a gesture itself.
/// </para>
/// </remarks>
public class PanelChrome : ContentControl
{
    /// <summary>The header row: icon/title/item-count/actions layout (§5.4).</summary>
    public const string PartHeader = "PART_Header";

    /// <summary>
    /// The accent identity marker (§5.3). Pre-ADR-030: a rectangular <see cref="Border"/> strip filling a
    /// dedicated layout column. ADR-030 (docs/PLANS/reference-panel-chrome.md §1 D-4) replaced its visual
    /// with a curved <see cref="System.Windows.Shapes.Path"/> tracing the panel's own top-left rounded
    /// corner, hosted in a fixed-size overlay (<see cref="PartAccentIndicatorHost"/>, no longer a layout
    /// column) inside <see cref="PartSurfaceLayer"/> — the template part NAME is unchanged (ADR-030
    /// explicitly permits renaming template parts, but this one's semantics — accent identity +
    /// emphasis-by-state — are preserved, so the name stays), and tests that locate it by name
    /// (<c>chrome.Template.FindName(PartAccentIndicator, chrome)</c>) keep working unmodified.
    /// </summary>
    public const string PartAccentIndicator = "PART_AccentIndicator";

    /// <summary>
    /// The fixed-size overlay hosting <see cref="PartAccentIndicator"/> (plan D-4/D-7) — a direct child of
    /// <see cref="PartSurfaceLayer"/>, sized from <c>PanelMetrics.AccentMarkerHostWidth</c>/
    /// <c>AccentMarkerHostHeight</c> so a per-state <see cref="System.Windows.Shapes.Shape.StrokeThickness"/>
    /// change on the marker never perturbs this control's own <see cref="System.Windows.UIElement.DesiredSize"/>
    /// (D-7's "no layout jitter").
    /// </summary>
    public const string PartAccentIndicatorHost = "PART_AccentIndicatorHost";

    /// <summary><see cref="ContentPresenter"/> hosting <see cref="ContentControl.Content"/> (the file list host).</summary>
    public const string PartContentHost = "PART_ContentHost";

    /// <summary>
    /// Purely-visual border feedback for <see cref="VisualState"/> (D-6). <b><see cref="UIElement.IsHitTestVisible"/>
    /// must stay <see langword="false"/> in the default style</b> — R-5: this element sits on top of the
    /// whole chrome, and if it were hit-testable it would silently swallow the title-bar drag, the resize
    /// thumbs (a sibling of this control in <c>ChildWindow.xaml</c>, not a descendant, but still under the
    /// same window), and file drag/drop.
    /// </summary>
    public const string PartInteractionOverlay = "PART_InteractionOverlay";

    // ─────────────────────────────────────────────────────────────────────────────────────────────────
    // ADR-030 "Reference-Style Layered Panel Chrome" (docs/PLANS/reference-panel-chrome.md §3) — the
    // layered template's new decorative parts. Every one of these MUST render with
    // <see cref="UIElement.IsHitTestVisible"/> = <see langword="false"/> in the default style
    // (<c>Resources/Styles/Panels.xaml</c>) — same R-5 rationale as <see cref="PartInteractionOverlay"/>
    // above: none of them may ever intercept a mouse event the title-bar drag, the native
    // resize hit-test zones (<see cref="Interaction.Native.NativeHitTestMapper"/>), or file drag/drop need to receive. The one
    // deliberate EXCEPTION to "purely decorative" is <see cref="PartHitCatcher"/> itself, whose entire
    // purpose is to keep the OS routing clicks to this window (plan D-5) — it is still
    // <c>IsHitTestVisible="False"</c> for WPF's OWN hit-test tree (so it never steals a click from a real
    // WPF element sitting above it), while remaining opaque enough (alpha ≥ 1) for the OS-level layered-
    // window compositor to route the click here at all.
    // ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Bottom-most layer, spanning the whole window rect — keeps the OS routing clicks to this window across the transparent chrome inset (plan D-5).</summary>
    public const string PartHitCatcher = "PART_HitCatcher";

    /// <summary>The accent-colored shader-based glow ring, drawn inside the chrome inset (plan D-2/D-9).</summary>
    public const string PartGlow = "PART_Glow";

    /// <summary>The shader-free substitute for <see cref="PartGlow"/>, shown instead of it when <see cref="Theming.ChromeEffectsPolicy.IsGlowEffectEnabled"/> is <see langword="false"/> (plan D-9).</summary>
    public const string PartGlowFallback = "PART_GlowFallback";

    /// <summary>The inset, clipped layer hosting <see cref="PartHeader"/>/<see cref="PartContentHost"/>/the strokes/the marker — the single rounded-rect <see cref="UIElement.Clip"/> owner (plan D-2/D-12).</summary>
    public const string PartSurfaceLayer = "PART_SurfaceLayer";

    /// <summary>The very weak non-semantic top-to-bottom gradient over the panel surface (plan D-11).</summary>
    public const string PartSurfaceHighlight = "PART_SurfaceHighlight";

    /// <summary>The 1-DIP neutral (non-accent) rounded-rect stroke around the panel surface (plan D-2/D-6).</summary>
    public const string PartNeutralStroke = "PART_NeutralStroke";

    /// <summary>The thin, deliberately weaker-than-the-marker accent-colored inner stroke (plan D-2/D-6).</summary>
    public const string PartAccentStroke = "PART_AccentStroke";

    /// <summary>Identifies the <see cref="Title"/> dependency property — the panel's display name, shown (or edited via <see cref="TitleEditor"/>) in the header.</summary>
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title),
        typeof(string),
        typeof(PanelChrome),
        new PropertyMetadata(string.Empty));

    /// <summary>
    /// The rename-capable title widget (the existing <c>TitleText</c>/<c>TitleEditBox</c> pair) —
    /// <c>ChildWindow.xaml</c> supplies its own elements here rather than PanelChrome rendering
    /// <see cref="Title"/> itself, so the inline-rename behavior (<see cref="Behaviors.PanelTitleRenameBehavior"/>)
    /// is never rewritten (D-8, §0).
    /// </summary>
    public static readonly DependencyProperty TitleEditorProperty = DependencyProperty.Register(
        nameof(TitleEditor),
        typeof(object),
        typeof(PanelChrome),
        new PropertyMetadata(null));

    /// <summary>The Sort/View/RollUp (and, from a later commit, Overflow) button group.</summary>
    public static readonly DependencyProperty HeaderActionsProperty = DependencyProperty.Register(
        nameof(HeaderActions),
        typeof(object),
        typeof(PanelChrome),
        new PropertyMetadata(null));

    /// <summary>Optional Segoe MDL2 glyph shown before the title (§5.4). No current consumer sets this — reserved for a future panel-kind indicator.</summary>
    public static readonly DependencyProperty SemanticIconProperty = DependencyProperty.Register(
        nameof(SemanticIcon),
        typeof(string),
        typeof(PanelChrome),
        new PropertyMetadata(string.Empty));

    /// <summary>Identifies the <see cref="AccentBrush"/> dependency property — the panel's accent color, painted onto <see cref="PartAccentIndicator"/>/<see cref="PartAccentStroke"/>.</summary>
    public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(
        nameof(AccentBrush),
        typeof(Brush),
        typeof(PanelChrome),
        new PropertyMetadata(Brushes.Transparent));

    /// <summary>The composed panel surface fill — the result of <c>PanelSurfaceBrushConverter</c> (<see cref="Theming.SurfaceComposer"/>).</summary>
    public static readonly DependencyProperty SurfaceBrushProperty = DependencyProperty.Register(
        nameof(SurfaceBrush),
        typeof(Brush),
        typeof(PanelChrome),
        new PropertyMetadata(Brushes.Transparent));

    /// <summary>Identifies the <see cref="SurfaceStyle"/> dependency property — which of the ADR-025/ADR-030 surface treatments (<see cref="PanelSurfaceStyle"/>) this panel renders.</summary>
    public static readonly DependencyProperty SurfaceStyleProperty = DependencyProperty.Register(
        nameof(SurfaceStyle),
        typeof(PanelSurfaceStyle),
        typeof(PanelChrome),
        new PropertyMetadata(PanelSurfaceStyle.Ambient));

    /// <summary>
    /// Drives the default style's opacity/border values (D-6) — bound via
    /// <see cref="System.Windows.Data.MultiBinding"/> into <c>EditAwareVisualStateToDoubleConverter</c>,
    /// not via <see cref="Trigger"/>s — see
    /// <see cref="Behaviors.PanelVisualStateBehavior"/>, the first real writer of
    /// <see cref="Theming.PanelTheme.VisualStateProperty"/>.
    /// </summary>
    public static readonly DependencyProperty VisualStateProperty = DependencyProperty.Register(
        nameof(VisualState),
        typeof(PanelVisualState),
        typeof(PanelChrome),
        new PropertyMetadata(PanelVisualState.Idle));

    /// <summary>
    /// Mirrors <see cref="Theming.PanelTheme.IsLayoutEditingProperty"/> (docs/PLANS/phase-6-edit-layout-ux.md
    /// §1 D-6) the same way <see cref="VisualStateProperty"/> mirrors
    /// <see cref="Theming.PanelTheme.VisualStateProperty"/>: <c>ChildWindow.xaml</c> binds this to the
    /// inherited attached property explicitly, keeping this lookless, reusable control free of any direct
    /// dependency on <see cref="Theming.PanelTheme"/> itself. Drives the default style's edit-mode
    /// opacity/width values, via the same <see cref="System.Windows.Data.MultiBinding"/> mechanism,
    /// alongside <see cref="VisualState"/>.
    /// </summary>
    public static readonly DependencyProperty IsLayoutEditingProperty = DependencyProperty.Register(
        nameof(IsLayoutEditing),
        typeof(bool),
        typeof(PanelChrome),
        new PropertyMetadata(false));

    /// <summary>
    /// Whether the panel is currently collapsed to its header (§5.6). Purely a rendering hint for this
    /// control's own template (e.g. showing <see cref="ItemCount"/>) — it does <b>not</b> drive the actual
    /// collapse/expand animation, which stays entirely inside <see cref="Behaviors.RollUpBehavior"/> (§0):
    /// that behavior collapses <c>ChildWindow.xaml</c>'s own <c>ContentRow</c> row definition, which this
    /// control spans via <c>Grid.RowSpan</c>, so the header/content proportions here just fall out of
    /// whatever total height <see cref="Behaviors.RollUpBehavior"/> leaves available.
    /// </summary>
    public static readonly DependencyProperty IsRolledUpProperty = DependencyProperty.Register(
        nameof(IsRolledUp),
        typeof(bool),
        typeof(PanelChrome),
        new PropertyMetadata(false));

    /// <summary>Item count shown next to the title only while <see cref="IsRolledUp"/> (§5.6) — sourced from the already-existing <c>ChildWindowViewModel.FileItems.Count</c>, no new ViewModel property (R-12).</summary>
    public static readonly DependencyProperty ItemCountProperty = DependencyProperty.Register(
        nameof(ItemCount),
        typeof(int),
        typeof(PanelChrome),
        new PropertyMetadata(0));

    /// <summary>Header row height, in device-independent pixels. Defaults to <see cref="PanelMetrics.HeaderHeight"/> — the single source of truth (D-11) — but stays overridable per-instance for a future non-32px consumer.</summary>
    public static readonly DependencyProperty HeaderHeightProperty = DependencyProperty.Register(
        nameof(HeaderHeight),
        typeof(double),
        typeof(PanelChrome),
        new PropertyMetadata(PanelMetrics.HeaderHeight));

    static PanelChrome()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(PanelChrome), new FrameworkPropertyMetadata(typeof(PanelChrome)));
    }

    /// <summary>Gets or sets the panel's display name, shown (or edited via <see cref="TitleEditor"/>) in the header.</summary>
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>Gets or sets the rename-capable title widget supplied by <c>ChildWindow.xaml</c> (see <see cref="TitleEditorProperty"/>).</summary>
    public object TitleEditor
    {
        get => GetValue(TitleEditorProperty);
        set => SetValue(TitleEditorProperty, value);
    }

    /// <summary>Gets or sets the Sort/View/RollUp (and Overflow) button group shown in the header.</summary>
    public object HeaderActions
    {
        get => GetValue(HeaderActionsProperty);
        set => SetValue(HeaderActionsProperty, value);
    }

    /// <summary>Gets or sets the optional Segoe MDL2 glyph shown before the title. No current consumer sets this — reserved for a future panel-kind indicator.</summary>
    public string SemanticIcon
    {
        get => (string)GetValue(SemanticIconProperty);
        set => SetValue(SemanticIconProperty, value);
    }

    /// <summary>Gets or sets the panel's accent color, painted onto <see cref="PartAccentIndicator"/>/<see cref="PartAccentStroke"/>.</summary>
    public Brush AccentBrush
    {
        get => (Brush)GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    /// <summary>Gets or sets the composed panel surface fill — the result of <c>PanelSurfaceBrushConverter</c> (<see cref="Theming.SurfaceComposer"/>).</summary>
    public Brush SurfaceBrush
    {
        get => (Brush)GetValue(SurfaceBrushProperty);
        set => SetValue(SurfaceBrushProperty, value);
    }

    /// <summary>Gets or sets which of the ADR-025/ADR-030 surface treatments this panel renders.</summary>
    public PanelSurfaceStyle SurfaceStyle
    {
        get => (PanelSurfaceStyle)GetValue(SurfaceStyleProperty);
        set => SetValue(SurfaceStyleProperty, value);
    }

    /// <summary>Gets or sets the resolved interaction visual state, driving the default style's opacity/border values via <see cref="System.Windows.Data.MultiBinding"/> (D-6).</summary>
    public PanelVisualState VisualState
    {
        get => (PanelVisualState)GetValue(VisualStateProperty);
        set => SetValue(VisualStateProperty, value);
    }

    /// <summary>Gets or sets whether Edit Layout mode is active for this panel, driving the default style's edit-mode opacity/width values via <see cref="System.Windows.Data.MultiBinding"/>.</summary>
    public bool IsLayoutEditing
    {
        get => (bool)GetValue(IsLayoutEditingProperty);
        set => SetValue(IsLayoutEditingProperty, value);
    }

    /// <summary>Gets or sets whether the panel is currently collapsed to its header — a rendering hint only; the actual animation is owned by <see cref="Behaviors.RollUpBehavior"/>.</summary>
    public bool IsRolledUp
    {
        get => (bool)GetValue(IsRolledUpProperty);
        set => SetValue(IsRolledUpProperty, value);
    }

    /// <summary>Gets or sets the item count shown next to the title while <see cref="IsRolledUp"/> is <see langword="true"/>.</summary>
    public int ItemCount
    {
        get => (int)GetValue(ItemCountProperty);
        set => SetValue(ItemCountProperty, value);
    }

    /// <summary>Gets or sets the header row height, in device-independent pixels.</summary>
    public double HeaderHeight
    {
        get => (double)GetValue(HeaderHeightProperty);
        set => SetValue(HeaderHeightProperty, value);
    }
}
