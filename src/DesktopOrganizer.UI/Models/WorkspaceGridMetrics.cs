using System.Collections.Frozen;

namespace DesktopOrganizer.UI.Models;

/// <summary>
/// Default constants and validation for the workspace panel grid (ADR-028, System B — WHERE a panel is
/// and HOW BIG it is, not the icon layout inside it). Deliberately separate from, and not derived from,
/// <see cref="GridMetrics"/> (icon cell geometry, System C) or <see cref="AdaptiveGridLayout"/> (icon
/// row/column packing, System C) — ADR-028 requires the two grids to stay incommensurable so a panel's
/// <c>Width</c>/<c>Height</c> never accidentally becomes a function of <c>IconSize</c> again (the exact
/// bug this ADR closes, baseline finding F-2). See <see cref="WorkspaceLayoutPolicy"/> for the pure
/// snap/quantisation functions that consume these constants.
/// </summary>
public static class WorkspaceGridMetrics
{
    /// <summary>
    /// The workspace grid module <c>U</c>, in DIP. ADR-028's starting hypothesis (structurally, not
    /// visually, verified — see the ADR's "Модуль U" section): <c>4*U</c> equals the existing
    /// <see cref="PanelMetrics.HeaderHeight"/> (32 DIP) and <c>15*U</c> equals the existing WPF
    /// <c>MinPanelSize</c> floor (120 DIP), both without remainder. Manual verify (Win10/Win11,
    /// visual "airiness" at 100/125/150% DPI) remains an open ADR-028 gate before this value is
    /// considered final; the single point of change if it needs to move is this one constant.
    /// </summary>
    public const double UnitSize = 8d;

    /// <summary>
    /// The default visible gap <c>G</c> between two adjacent <see cref="PanelGeometryMode.GridAligned"/>
    /// panels, in DIP. Must always be a whole multiple of <see cref="UnitSize"/> (validated by
    /// <see cref="ValidateGap"/>) — ADR-028 default is <c>G = U</c>. Do not confuse with
    /// <see cref="AdaptiveGridLayout.DefaultMinGap"/>, which is the unrelated System-C (icon) gap.
    /// </summary>
    public const double DefaultGap = 8d;

    /// <summary>
    /// Minimum expanded width, in <see cref="UnitSize"/>-units, for a <see cref="PanelGeometryMode.GridAligned"/>
    /// panel (ADR-028 table, "Минимумы"). <c>16</c> units = <c>16*8 - 8 = 120</c> DIP.
    /// </summary>
    /// <remarks>
    /// <b>Resolved by P04 (docs/PLANS/workspace-grid-03-04-integration.md §3, item 6; closes ADR-028
    /// Open Question 1).</b> The P02 placeholder was <c>12</c> (= 88 DIP), gauged only against the
    /// structural constants, and it was too small on two independent counts:
    /// <list type="number">
    /// <item><c>ChildWindow.xaml</c> coerces <c>Window.MinWidth</c> to <b>120</b> DIP. A model minimum
    /// BELOW that coercion is not a minimum at all — WPF would silently clamp the window to 120 while
    /// the span said 88, and every subsequent <c>BoundsToSpans</c> would disagree with the stored span
    /// (the P02b verify finding "модель 88 DIP &lt; окно 120").</item>
    /// <item>The header button row alone is 4 x <see cref="PanelMetrics.HeaderButtonSize"/> = <b>112</b>
    /// DIP before the accent indicator, the content inset or a single character of title — the
    /// measurement ADR-028 Open Question 1 deferred to this stage.</item>
    /// </list>
    /// 120 DIP satisfies both and is simultaneously <c>GridMetrics.MinPanelSize</c>, so all three
    /// minimums (WPF coercion, legacy floor, workspace-grid floor) are now the same number. Kept as a
    /// literal rather than computed from <c>GridMetrics.MinPanelSize</c> on purpose: this type must not
    /// take a dependency on System C (see the type's own remarks) — the agreement is enforced by
    /// <c>WorkspaceGridMetricsTests</c>/<c>ChildWindowSizingGridTests</c> instead.
    /// </remarks>
    public const int MinWidthSpanExpanded = 16;

    /// <summary>
    /// Minimum expanded height, in <see cref="UnitSize"/>-units (ADR-028 table). Deliberately NOT derived
    /// from <c>IconSize</c> (unlike the legacy <c>MinPanelWidth(iconSize)</c>) — doing so would violate
    /// the "IconSize never changes panel bounds" rule at the level of the minimum itself. Composed of
    /// <see cref="CollapsedHeaderSpan"/> (the header) plus a ~7-unit minimum content row, taking the
    /// <c>IconSize=16</c> slider floor (<c>GridMetrics.RowHeight(16)=60</c> DIP) as a loose one-row
    /// reference, rounded up to a whole <see cref="UnitSize"/> multiple. A larger <c>IconSize</c> may not
    /// show a full row without scrolling at this minimum — expected/allowed (ADR-028, "Недостаточная
    /// площадь → scroll"), not a bug.
    /// </summary>
    public const int MinHeightSpanExpanded = 12;

    /// <summary>
    /// Minimum expanded width while rolled up (collapsed) — always equal to
    /// <see cref="MinWidthSpanExpanded"/>: roll-up changes only height (<c>RollUpBehavior</c>, ADR-005),
    /// never width, in either geometry mode.
    /// </summary>
    public const int MinWidthSpanCollapsed = MinWidthSpanExpanded;

    /// <summary>
    /// Collapsed (rolled-up) header height, in <see cref="UnitSize"/>-units. Not a new constant — this
    /// is <see cref="PanelMetrics.HeaderHeight"/> (32 DIP) expressed in grid units, re-derived here (not
    /// hardcoded) so the two never drift apart.
    /// </summary>
    /// <remarks>
    /// <b>P04 fix (docs/PLANS/workspace-grid-03-04-integration.md §3.6, OQ-4 = A).</b> Adding
    /// <see cref="DefaultGap"/> before dividing (i.e. <c>+1</c> unit, since <c>G == U</c> by default) is
    /// simply the exact inverse of the "cell + gap" model rather than a fudge factor: an extent is
    /// <c>Extent(n) = n*UnitSize - DefaultGap</c>, never <c>n*UnitSize</c>, so the P02 form
    /// (<c>HeaderHeight / UnitSize == 4</c>) actually described a 24 DIP extent — 8 DIP SHORTER than the
    /// real collapsed height every other part of the app uses (<c>ChildWindow.TitleBarHeight</c>,
    /// <c>ChildWindow.xaml</c>'s <c>MinHeight</c>, <c>RollUpBehavior</c>'s animation target,
    /// <c>WindowManager.CollectOtherPanelBounds</c>' obstacle height). With <c>5</c>:
    /// <c>5*8 - 8 = 32 == PanelMetrics.HeaderHeight</c>, so a rolled-up panel occupies exactly five whole
    /// units and its bottom edge lands ON the workspace grid — ADR-028 requires the collapsed height to
    /// be expressible on the grid without an exception, and DoD D4-4 ("collapsed header never becomes a
    /// full cell") is then true by construction. The two rejected alternatives (special-casing the gap
    /// inside <see cref="WorkspaceLayoutPolicy.SpansToBounds"/>, or accepting an off-grid collapsed
    /// height) are recorded in the plan's §3.6.
    /// </remarks>
    public const int CollapsedHeaderSpan = (int)((PanelMetrics.HeaderHeight + DefaultGap) / UnitSize);

    /// <summary>The minimum <see cref="GridSpan"/> for an expanded panel: <see cref="MinWidthSpanExpanded"/> x <see cref="MinHeightSpanExpanded"/>.</summary>
    public static readonly GridSpan MinSpanExpanded = new(MinWidthSpanExpanded, MinHeightSpanExpanded);

    /// <summary>The minimum <see cref="GridSpan"/> for a rolled-up panel: <see cref="MinWidthSpanCollapsed"/> x <see cref="CollapsedHeaderSpan"/>.</summary>
    public static readonly GridSpan MinSpanCollapsed = new(MinWidthSpanCollapsed, CollapsedHeaderSpan);

    /// <summary>
    /// Default preset → <see cref="GridSpan"/> map (ADR-028 table, "Presets"). Exposed as a plain
    /// dictionary (not hardcoded into <see cref="WorkspaceLayoutPolicy.PreferredSize"/>) so a future
    /// <c>WorkspaceGridOptions</c> consumer (settings UI, ADR-028 has no such UI requirement yet) could
    /// substitute an overridden table without touching this type. Backed by a
    /// <see cref="FrozenDictionary{TKey,TValue}"/> (fix-cycle 1, NIT) rather than a plain mutable
    /// <see cref="Dictionary{TKey,TValue}"/> exposed through the <see cref="IReadOnlyDictionary{TKey,TValue}"/>
    /// interface — the previous shape compiled fine but relied entirely on callers never down-casting
    /// back to <see cref="Dictionary{TKey,TValue}"/> to preserve immutability; a frozen collection makes
    /// that guarantee true at the type level instead of by convention.
    /// </summary>
    public static readonly FrozenDictionary<PanelSizePreset, GridSpan> DefaultPresetSpans =
        new Dictionary<PanelSizePreset, GridSpan>
        {
            [PanelSizePreset.Small] = new GridSpan(20, 20),
            [PanelSizePreset.Medium] = new GridSpan(32, 30),
            [PanelSizePreset.Large] = new GridSpan(44, 40),
            [PanelSizePreset.ExtraLarge] = new GridSpan(60, 50),
        }.ToFrozenDictionary();

    /// <summary>
    /// Looks up <paramref name="preset"/> in <see cref="DefaultPresetSpans"/>.
    /// </summary>
    /// <param name="preset">The named preset.</param>
    /// <returns>The preset's <see cref="GridSpan"/>, in <see cref="UnitSize"/>-units.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="preset"/> has no entry in <see cref="DefaultPresetSpans"/> (fix-cycle 1, MINOR:
    /// previously threw the framework's generic, caller-unfriendly <see cref="KeyNotFoundException"/> via
    /// the dictionary indexer — reachable only if a future <see cref="PanelSizePreset"/> member is added
    /// without a matching table entry, but worth a clear message when it happens).
    /// </exception>
    public static GridSpan SpanForPreset(PanelSizePreset preset)
    {
        if (!DefaultPresetSpans.TryGetValue(preset, out var span))
        {
            throw new ArgumentOutOfRangeException(nameof(preset), preset, $"No default {nameof(GridSpan)} is registered for preset '{preset}'.");
        }

        return span;
    }

    /// <summary>
    /// Validates that <paramref name="gap"/> is a non-negative whole multiple of
    /// <paramref name="unitSize"/> and that <paramref name="unitSize"/> itself is strictly positive
    /// (ADR-028 table: "gap % unitSize == 0"). Uses a relative floating-point tolerance rather than exact
    /// <c>%</c> equality so values round-tripped through settings persistence (e.g. a <c>double</c> read
    /// back from JSON) are not rejected by accumulated binary-fraction noise.
    /// </summary>
    /// <param name="unitSize">The candidate workspace grid unit <c>U</c>, in DIP.</param>
    /// <param name="gap">The candidate panel gap <c>G</c>, in DIP.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="unitSize"/> is not finite and positive.</exception>
    /// <exception cref="ArgumentException"><paramref name="gap"/> is negative, not finite, or not a whole multiple of <paramref name="unitSize"/>.</exception>
    public static void ValidateGap(double unitSize, double gap)
    {
        if (!double.IsFinite(unitSize) || unitSize <= 0d)
            throw new ArgumentOutOfRangeException(nameof(unitSize), unitSize, "Workspace grid unit size (U) must be finite and > 0.");

        if (!double.IsFinite(gap) || gap < 0d)
            throw new ArgumentException("Workspace panel gap (G) must be finite and >= 0.", nameof(gap));

        var units = gap / unitSize;
        var nearestWhole = Math.Round(units);
        if (Math.Abs(units - nearestWhole) > 1e-6)
            throw new ArgumentException($"Workspace panel gap (G={gap}) must be a whole multiple of the unit size (U={unitSize}).", nameof(gap));
    }

    /// <summary>
    /// Validates a candidate <see cref="GridSpan"/> against a minimum (component-wise
    /// <c>&gt;=</c>) — used to check preset/persisted spans against
    /// <see cref="MinSpanExpanded"/>/<see cref="MinSpanCollapsed"/>.
    /// </summary>
    /// <param name="span">The candidate span.</param>
    /// <param name="minimum">The minimum span both dimensions must meet or exceed.</param>
    /// <exception cref="ArgumentException"><paramref name="span"/> has a non-positive dimension, or either dimension is below <paramref name="minimum"/>.</exception>
    public static void ValidateSpan(GridSpan span, GridSpan minimum)
    {
        if (span.WidthUnits <= 0 || span.HeightUnits <= 0)
            throw new ArgumentException($"Grid span ({span.WidthUnits}x{span.HeightUnits}) must have positive dimensions.", nameof(span));

        if (span.WidthUnits < minimum.WidthUnits || span.HeightUnits < minimum.HeightUnits)
        {
            throw new ArgumentException(
                $"Grid span ({span.WidthUnits}x{span.HeightUnits}) is below the minimum ({minimum.WidthUnits}x{minimum.HeightUnits}).",
                nameof(span));
        }
    }
}
