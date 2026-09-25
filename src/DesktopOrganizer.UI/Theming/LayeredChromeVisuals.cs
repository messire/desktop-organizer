using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.UI.Theming;

/// <summary>
/// Derived opacity channels for ADR-030's layered panel chrome (docs/PLANS/reference-panel-chrome.md §1
/// D-6) — <c>PART_Glow</c>, <c>PART_AccentStroke</c>, and <c>PART_NeutralStroke</c>
/// (<c>Resources/Styles/Panels.xaml</c>). Deliberately NOT a second state machine: every function here is
/// an affine (linear) remap of the ALREADY-tested <see cref="LayoutEditVisuals.BorderOpacityFor"/> channel
/// — <c>t = (LayoutEditVisuals.BorderOpacityFor(state, isLayoutEditing) - B_idle) / (B_max - B_idle)</c>,
/// where <c>B_idle</c>/<c>B_max</c> are <see cref="InteractionVisualState.BorderOpacityFor"/>'s own Idle/
/// Resizing values (the baseline channel's own extremes, read once, never a second copy of the switch
/// expression). Monotonicity, five-state distinguishability, and "edit mode is never weaker" are therefore
/// inherited from <see cref="LayoutEditVisuals.BorderOpacityFor"/>'s own regression coverage by
/// construction, not re-derived here.
/// </summary>
/// <remarks>
/// <see langword="internal"/> — consumed only by <see cref="Converters.EditAwareVisualStateToDoubleConverter"/>
/// (same assembly); <c>Resources/Styles/Panels.xaml</c> only ever reaches these channels THROUGH that
/// converter's <c>"glow"</c>/<c>"accentStroke"</c>/<c>"neutralStroke"</c> parameters, never by binding
/// directly to a member here, so nothing needs this type — or any of its members — to be
/// <see langword="public"/>. Tests reach it via
/// <c>[assembly: InternalsVisibleTo("DesktopOrganizer.Tests")]</c> (<c>AssemblyInfo.cs</c>).
/// </remarks>
internal static class LayeredChromeVisuals
{
    private static readonly double IdleBaseline = InteractionVisualState.BorderOpacityFor(PanelVisualState.Idle);
    private static readonly double MaxBaseline = InteractionVisualState.BorderOpacityFor(PanelVisualState.Resizing);

    /// <summary><c>PART_Glow</c>'s own <see cref="System.Windows.UIElement.Opacity"/> for <paramref name="state"/>/<paramref name="isLayoutEditing"/> — <see cref="PanelMetrics.GlowOpacityIdle"/> .. <see cref="PanelMetrics.GlowOpacityMax"/>.</summary>
    /// <param name="state">The resolved visual state.</param>
    /// <param name="isLayoutEditing">Whether Edit Layout mode is currently active.</param>
    public static double GlowOpacityFor(PanelVisualState state, bool isLayoutEditing)
        => Interpolate(state, isLayoutEditing, PanelMetrics.GlowOpacityIdle, PanelMetrics.GlowOpacityMax);

    /// <summary><c>PART_AccentStroke</c>'s own <see cref="System.Windows.UIElement.Opacity"/> for <paramref name="state"/>/<paramref name="isLayoutEditing"/> — <see cref="PanelMetrics.AccentStrokeOpacityIdle"/> .. <see cref="PanelMetrics.AccentStrokeOpacityMax"/>.</summary>
    /// <param name="state">The resolved visual state.</param>
    /// <param name="isLayoutEditing">Whether Edit Layout mode is currently active.</param>
    public static double AccentStrokeOpacityFor(PanelVisualState state, bool isLayoutEditing)
        => Interpolate(state, isLayoutEditing, PanelMetrics.AccentStrokeOpacityIdle, PanelMetrics.AccentStrokeOpacityMax);

    /// <summary><c>PART_NeutralStroke</c>'s own <see cref="System.Windows.UIElement.Opacity"/> for <paramref name="state"/>/<paramref name="isLayoutEditing"/> — <see cref="PanelMetrics.NeutralStrokeOpacityIdle"/> .. <see cref="PanelMetrics.NeutralStrokeOpacityMax"/>.</summary>
    /// <param name="state">The resolved visual state.</param>
    /// <param name="isLayoutEditing">Whether Edit Layout mode is currently active.</param>
    public static double NeutralStrokeOpacityFor(PanelVisualState state, bool isLayoutEditing)
        => Interpolate(state, isLayoutEditing, PanelMetrics.NeutralStrokeOpacityIdle, PanelMetrics.NeutralStrokeOpacityMax);

    /// <summary>
    /// The EFFECTIVE combined opacity of <c>PART_NeutralStroke</c> and <c>PART_InteractionOverlay</c> as
    /// actually seen on screen (docs/PLANS/reference-panel-chrome.md §9 lesson P09) — both draw the
    /// identical <c>Brush.Border.Default</c> hairline ring, at the identical geometry, on the SAME pixels
    /// for the Solid/Ambient <c>SurfaceStyle</c>s (<c>Resources/Styles/Panels.xaml</c>,
    /// <c>PART_NeutralStroke</c>'s own remarks explain why that duplication is deliberate, not a bug; those
    /// same remarks also cover the Glass <c>SurfaceStyle</c>, where the Glass trigger widens
    /// <c>PART_InteractionOverlay</c>'s own <c>BorderThickness</c> to 1.5 DIP and only the outer 1 DIP of
    /// its ring still coincides with <c>PART_NeutralStroke</c>'s own 1-DIP stroke — this function's formula
    /// below is therefore an approximation, not an exact pixel match, under Glass). Standard "two
    /// independent translucent layers of the same colour, stacked over the same background" alpha
    /// composition: <c>1 - (1 - overlayOpacity) * (1 - neutralOpacity)</c> — each factor is the fraction of
    /// the background that layer lets through, so the product is the fraction BOTH layers let through, and
    /// one minus that is the combined coverage. Test-only/documentation: nothing in the real template reads
    /// this — each ring keeps computing its own <c>Border.Opacity</c> independently;
    /// <c>LayeredChromeVisualsTests</c> uses this to pin that the combination stays monotonic and pairwise
    /// distinguishable, not merely that each INPUT channel is.
    /// </summary>
    /// <param name="state">The resolved visual state.</param>
    /// <param name="isLayoutEditing">Whether Edit Layout mode is currently active.</param>
    internal static double CombinedNeutralBorderOpacityFor(PanelVisualState state, bool isLayoutEditing)
    {
        var overlayOpacity = LayoutEditVisuals.BorderOpacityFor(state, isLayoutEditing);
        var neutralOpacity = NeutralStrokeOpacityFor(state, isLayoutEditing);
        return 1d - ((1d - overlayOpacity) * (1d - neutralOpacity));
    }

    private static double Interpolate(PanelVisualState state, bool isLayoutEditing, double idleValue, double maxValue)
    {
        var baseline = LayoutEditVisuals.BorderOpacityFor(state, isLayoutEditing);
        var t = (baseline - IdleBaseline) / (MaxBaseline - IdleBaseline);
        return idleValue + ((maxValue - idleValue) * t);
    }
}
