using System.Windows;
using System.Windows.Media;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.UI.Theming;

/// <summary>
/// Attached, inherited theme properties for panel chrome (ADR-025, docs/PLANS/phase-5-ambient-accent-ui.md
/// §1 D-7). <c>PanelItemContainerStyle</c> is shared by <c>ChildWindow</c> (whose <c>DataContext</c> is
/// <c>ChildWindowViewModel</c>, which owns an accent) and <c>SystemIconsPanelWindow</c> (whose
/// <c>DataContext</c> has no such concept) — a <c>DataContext</c>-relative binding would either not
/// compile for both windows or produce a binding error at runtime for one of them. Delivering the
/// accent/surface style/visual state via inherited attached properties instead means every consumer
/// just reads <c>(theming:PanelTheme.Accent)</c> off itself; a window that never sets it gets the
/// neutral/Ambient/Idle default for free.
/// </summary>
/// <remarks>
/// <b>Known WPF trap (R-6):</b> an inherited attached <see cref="DependencyProperty"/> value does
/// <i>not</i> flow into a <see cref="System.Windows.Controls.ContextMenu"/> or <see cref="System.Windows.Controls.Primitives.Popup"/> —
/// both open a separate visual tree that is not a descendant of the element that set the property.
/// Any accent-dependent chrome inside a panel's context menu or popups must be themed with a
/// <c>StaticResource</c> token, never by reading <see cref="AccentProperty"/>/<see cref="SurfaceStyleProperty"/>/
/// <see cref="VisualStateProperty"/> from inside that menu/popup — it will silently fall back to the
/// default value there instead of erroring, which makes the bug easy to miss in review.
/// </remarks>
public static class PanelTheme
{
    /// <summary>The panel's accent colour brush. Inherited; defaults to a frozen brush for <see cref="AccentPalette.Neutral"/>.</summary>
    public static readonly DependencyProperty AccentProperty = DependencyProperty.RegisterAttached(
        "Accent",
        typeof(Brush),
        typeof(PanelTheme),
        new FrameworkPropertyMetadata(CreateDefaultAccentBrush(), FrameworkPropertyMetadataOptions.Inherits));

    /// <summary>
    /// The panel's text colour brush (<c>Model.TextColor</c>) — MAJOR-2 fix (review round, orchestrator
    /// decision "variant A", docs/PLANS/color-presets-and-size-step.md Q-1 variant B). <see cref="SurfaceComposer.ResolveNeutralBase"/>
    /// now needs the (accent, text) PAIR, not the accent alone, to decide a panel's light/dark neutral
    /// base — this property is the single source of truth every consumer of that decision
    /// (<see cref="Converters.PanelSurfaceBrushConverter"/>, <see cref="Converters.HoverOverlayBrushConverter"/>,
    /// <see cref="Converters.PanelChromeForegroundBrushConverter"/>) reads, exactly the same "inherited
    /// attached property fed from <c>ChildWindow.xaml</c>'s <c>PanelRoot</c>" shape <see cref="AccentProperty"/>
    /// already uses (same R-6 ContextMenu/Popup caveat applies). Inherited; defaults to a frozen brush for
    /// <c>Color.Text.Primary</c> (<c>#DDDDDD</c>) — the app's own pre-existing default text colour, so a
    /// window that never sets this (e.g. <c>SystemIconsPanelWindow</c>, which never sets
    /// <see cref="AccentProperty"/> either) gets the same neutral pairing <see cref="AccentProperty"/>'s own
    /// default already implies.
    /// </summary>
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text",
        typeof(Brush),
        typeof(PanelTheme),
        new FrameworkPropertyMetadata(CreateDefaultTextBrush(), FrameworkPropertyMetadataOptions.Inherits));

    /// <summary>The panel's surface composition style. Inherited; defaults to <see cref="PanelSurfaceStyle.Ambient"/>.</summary>
    public static readonly DependencyProperty SurfaceStyleProperty = DependencyProperty.RegisterAttached(
        "SurfaceStyle",
        typeof(PanelSurfaceStyle),
        typeof(PanelTheme),
        new FrameworkPropertyMetadata(PanelSurfaceStyle.Ambient, FrameworkPropertyMetadataOptions.Inherits));

    /// <summary>The panel's current visual interaction state. Inherited; defaults to <see cref="PanelVisualState.Idle"/>.</summary>
    public static readonly DependencyProperty VisualStateProperty = DependencyProperty.RegisterAttached(
        "VisualState",
        typeof(PanelVisualState),
        typeof(PanelTheme),
        new FrameworkPropertyMetadata(PanelVisualState.Idle, FrameworkPropertyMetadataOptions.Inherits));

    /// <summary>
    /// Whether the application is currently in Edit Layout mode (docs/PLANS/phase-6-edit-layout-ux.md §1
    /// D-6) — an orthogonal fact to <see cref="VisualStateProperty"/> (a panel can be
    /// <see cref="PanelVisualState.Dragging"/> inside either mode). Inherited; defaults to
    /// <see langword="false"/> (Normal mode). Written by
    /// <see cref="DesktopOrganizer.UI.Behaviors.LayoutEditModeBehavior"/> — the direct sibling of
    /// <see cref="DesktopOrganizer.UI.Behaviors.PanelVisualStateBehavior"/>, which writes
    /// <see cref="VisualStateProperty"/> the same way — onto the same <c>PanelRoot</c> element.
    /// </summary>
    public static readonly DependencyProperty IsLayoutEditingProperty = DependencyProperty.RegisterAttached(
        "IsLayoutEditing",
        typeof(bool),
        typeof(PanelTheme),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    /// <summary>Gets the value of the <see cref="AccentProperty"/> attached property.</summary>
    /// <param name="d">The element whose attached value is read.</param>
    public static Brush GetAccent(DependencyObject d) => (Brush)d.GetValue(AccentProperty);

    /// <summary>Sets the value of the <see cref="AccentProperty"/> attached property.</summary>
    /// <param name="d">The element whose attached value is written.</param>
    /// <param name="value">The new value to store on <paramref name="d"/>.</param>
    public static void SetAccent(DependencyObject d, Brush value) => d.SetValue(AccentProperty, value);

    /// <summary>Gets the value of the <see cref="TextProperty"/> attached property.</summary>
    /// <param name="d">The element whose attached value is read.</param>
    public static Brush GetText(DependencyObject d) => (Brush)d.GetValue(TextProperty);

    /// <summary>Sets the value of the <see cref="TextProperty"/> attached property.</summary>
    /// <param name="d">The element whose attached value is written.</param>
    /// <param name="value">The new value to store on <paramref name="d"/>.</param>
    public static void SetText(DependencyObject d, Brush value) => d.SetValue(TextProperty, value);

    /// <summary>Gets the value of the <see cref="SurfaceStyleProperty"/> attached property.</summary>
    /// <param name="d">The element whose attached value is read.</param>
    public static PanelSurfaceStyle GetSurfaceStyle(DependencyObject d) => (PanelSurfaceStyle)d.GetValue(SurfaceStyleProperty);

    /// <summary>Sets the value of the <see cref="SurfaceStyleProperty"/> attached property.</summary>
    /// <param name="d">The element whose attached value is written.</param>
    /// <param name="value">The new value to store on <paramref name="d"/>.</param>
    public static void SetSurfaceStyle(DependencyObject d, PanelSurfaceStyle value) => d.SetValue(SurfaceStyleProperty, value);

    /// <summary>Gets the value of the <see cref="VisualStateProperty"/> attached property.</summary>
    /// <param name="d">The element whose attached value is read.</param>
    public static PanelVisualState GetVisualState(DependencyObject d) => (PanelVisualState)d.GetValue(VisualStateProperty);

    /// <summary>Sets the value of the <see cref="VisualStateProperty"/> attached property.</summary>
    /// <param name="d">The element whose attached value is written.</param>
    /// <param name="value">The new value to store on <paramref name="d"/>.</param>
    public static void SetVisualState(DependencyObject d, PanelVisualState value) => d.SetValue(VisualStateProperty, value);

    /// <summary>Gets the value of the <see cref="IsLayoutEditingProperty"/> attached property.</summary>
    /// <param name="d">The element whose attached value is read.</param>
    public static bool GetIsLayoutEditing(DependencyObject d) => (bool)d.GetValue(IsLayoutEditingProperty);

    /// <summary>Sets the value of the <see cref="IsLayoutEditingProperty"/> attached property.</summary>
    /// <param name="d">The element whose attached value is written.</param>
    /// <param name="value">The new value to store on <paramref name="d"/>.</param>
    public static void SetIsLayoutEditing(DependencyObject d, bool value) => d.SetValue(IsLayoutEditingProperty, value);

    private static Brush CreateDefaultAccentBrush()
    {
        var color = (Color)ColorConverter.ConvertFromString(AccentPalette.Neutral)!;
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Brush CreateDefaultTextBrush()
    {
        // Byte-identical to Colors.xaml's Color.Text.Primary (#DDDDDD) — this project's own
        // pre-existing default text colour, kept in sync by comment the same way PanelSurfaceBrushConverter's
        // NeutralSurfaceHex constant is kept in sync with Color.Surface.Base.
        var brush = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD));
        brush.Freeze();
        return brush;
    }
}
