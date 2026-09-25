namespace DesktopOrganizer.UI.Models;

/// <summary>
/// The configurable, validatable parameter set for the workspace panel grid (ADR-028): unit size
/// <c>U</c>, panel gap <c>G</c>, the minimum spans, and the preset → <see cref="GridSpan"/> table. A
/// plain immutable data carrier — <see cref="WorkspaceLayoutPolicy"/>'s methods stay pure static
/// functions that take <c>unitSize</c>/<c>gap</c> as plain <see cref="double"/> parameters (ADR-028's
/// invariant table), so this type is a convenience for callers that want to bundle/persist/validate the
/// whole parameter set at once (e.g. a future <c>IAppSettings</c>-backed options object) rather than a
/// required input to the policy itself.
/// </summary>
/// <param name="UnitSize">The workspace grid unit <c>U</c>, in DIP. Must be finite and &gt; 0.</param>
/// <param name="Gap">The panel gap <c>G</c>, in DIP. Must be finite, &gt;= 0, and a whole multiple of <paramref name="UnitSize"/>.</param>
/// <param name="MinWidthSpanExpanded">Minimum expanded width, in <paramref name="UnitSize"/>-units.</param>
/// <param name="MinHeightSpanExpanded">Minimum expanded height, in <paramref name="UnitSize"/>-units.</param>
/// <param name="CollapsedHeaderSpan">Collapsed (rolled-up) header height, in <paramref name="UnitSize"/>-units.</param>
/// <param name="PresetSpans">The preset → <see cref="GridSpan"/> table used by <see cref="WorkspaceLayoutPolicy.PreferredSize(PanelSizePreset, WorkspaceGridOptions)"/> callers.</param>
public sealed record WorkspaceGridOptions(
    double UnitSize,
    double Gap,
    int MinWidthSpanExpanded,
    int MinHeightSpanExpanded,
    int CollapsedHeaderSpan,
    IReadOnlyDictionary<PanelSizePreset, GridSpan> PresetSpans)
{
    /// <summary>
    /// The ADR-028 default parameter set, built entirely from <see cref="WorkspaceGridMetrics"/>'s
    /// constants — the two are kept in sync by construction, not by hand.
    /// </summary>
    public static WorkspaceGridOptions Default { get; } = new(
        WorkspaceGridMetrics.UnitSize,
        WorkspaceGridMetrics.DefaultGap,
        WorkspaceGridMetrics.MinWidthSpanExpanded,
        WorkspaceGridMetrics.MinHeightSpanExpanded,
        WorkspaceGridMetrics.CollapsedHeaderSpan,
        WorkspaceGridMetrics.DefaultPresetSpans);

    /// <summary>The minimum <see cref="GridSpan"/> for an expanded panel under this option set.</summary>
    public GridSpan MinSpanExpanded => new(MinWidthSpanExpanded, MinHeightSpanExpanded);

    /// <summary>
    /// The minimum <see cref="GridSpan"/> for a rolled-up panel under this option set. Width is always
    /// <see cref="MinWidthSpanExpanded"/> — roll-up never changes width (ADR-028 table,
    /// "MinWidthSpanCollapsed = MinWidthSpanExpanded").
    /// </summary>
    public GridSpan MinSpanCollapsed => new(MinWidthSpanExpanded, CollapsedHeaderSpan);

    /// <summary>
    /// Validates every field: <see cref="UnitSize"/>/<see cref="Gap"/> via
    /// <see cref="WorkspaceGridMetrics.ValidateGap"/>, the three span-shaped integer fields for
    /// positivity, and every entry of <see cref="PresetSpans"/> against <see cref="MinSpanExpanded"/>
    /// (a preset is always meant to describe an expanded panel).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="UnitSize"/> is not finite/positive.</exception>
    /// <exception cref="ArgumentNullException"><see cref="PresetSpans"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <see cref="Gap"/> is invalid (including a gap that would make
    /// <see cref="WorkspaceLayoutPolicy.SpansToBounds"/> return a non-positive size for
    /// <see cref="MinSpanExpanded"/>/<see cref="MinSpanCollapsed"/> — fix-cycle 1 MINOR: the "cell + gap"
    /// model's extent formula <c>n*UnitSize - Gap</c> is only guaranteed positive for every valid span
    /// when <see cref="Gap"/> is strictly less than <see cref="UnitSize"/> times the SMALLEST allowed
    /// span dimension, not "for any span" as the un-amended ADR-028 table informally assumed — see the
    /// ADR-028 "Amendment 2026-09-22 — P02 review" section), a span-shaped field is not positive,
    /// <see cref="CollapsedHeaderSpan"/> is not smaller than <see cref="MinHeightSpanExpanded"/>,
    /// <see cref="PresetSpans"/> is empty or missing an entry for a <see cref="PanelSizePreset"/> member,
    /// or any preset span is below <see cref="MinSpanExpanded"/>.
    /// </exception>
    public void Validate()
    {
        WorkspaceGridMetrics.ValidateGap(UnitSize, Gap);

        if (MinWidthSpanExpanded <= 0)
            throw new ArgumentException($"{nameof(MinWidthSpanExpanded)} ({MinWidthSpanExpanded}) must be > 0.", nameof(MinWidthSpanExpanded));

        if (MinHeightSpanExpanded <= 0)
            throw new ArgumentException($"{nameof(MinHeightSpanExpanded)} ({MinHeightSpanExpanded}) must be > 0.", nameof(MinHeightSpanExpanded));

        if (CollapsedHeaderSpan <= 0)
            throw new ArgumentException($"{nameof(CollapsedHeaderSpan)} ({CollapsedHeaderSpan}) must be > 0.", nameof(CollapsedHeaderSpan));

        if (CollapsedHeaderSpan > MinHeightSpanExpanded)
        {
            throw new ArgumentException(
                $"{nameof(CollapsedHeaderSpan)} ({CollapsedHeaderSpan}) must not exceed {nameof(MinHeightSpanExpanded)} ({MinHeightSpanExpanded}) — a rolled-up panel must never be taller than the expanded minimum.",
                nameof(CollapsedHeaderSpan));
        }

        // Fix-cycle 1 MINOR: without this check, a pathological Gap (e.g. Gap=48, UnitSize=8,
        // CollapsedHeaderSpan=5 -> 5*8-48 = -8) silently produces a ZERO-size panel via
        // WorkspaceLayoutPolicy.SpansToBounds's own defensive Math.Max(0d, ...) floor instead of failing
        // loudly at configuration time, where the mistake is actually diagnosable.
        var smallestSpanDimension = Math.Min(Math.Min(MinWidthSpanExpanded, MinHeightSpanExpanded), CollapsedHeaderSpan);
        if (Gap >= UnitSize * smallestSpanDimension)
        {
            throw new ArgumentException(
                $"{nameof(Gap)} ({Gap}) must be strictly less than {nameof(UnitSize)} * the smallest allowed span dimension ({UnitSize} * {smallestSpanDimension} = {UnitSize * smallestSpanDimension}), or the smallest panel's DIP extent would be zero or negative.",
                nameof(Gap));
        }

        // Fix-cycle 1 MINOR: a null or incomplete PresetSpans table (missing an entry for some
        // PanelSizePreset member) previously surfaced much later, and only as the framework's generic,
        // caller-unfriendly KeyNotFoundException, the first time something looked that preset up (e.g.
        // WorkspaceLayoutPolicy.PreferredSize(preset, options)) — fail here instead, with a clear message,
        // at the point the options object itself is validated.
        if (PresetSpans is null)
            throw new ArgumentNullException(nameof(PresetSpans));

        if (PresetSpans.Count == 0)
            throw new ArgumentException("PresetSpans must not be empty.", nameof(PresetSpans));

        foreach (var preset in Enum.GetValues<PanelSizePreset>())
        {
            if (!PresetSpans.ContainsKey(preset))
                throw new ArgumentException($"PresetSpans is missing an entry for preset '{preset}'.", nameof(PresetSpans));
        }

        var minExpanded = MinSpanExpanded;
        foreach (var (preset, span) in PresetSpans)
        {
            try
            {
                WorkspaceGridMetrics.ValidateSpan(span, minExpanded);
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException($"Preset {preset}: {ex.Message}", nameof(PresetSpans), ex);
            }
        }
    }
}
