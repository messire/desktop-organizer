namespace DesktopOrganizer.UI.Models;

/// <summary>
/// Fixed step and range policy for the three size sliders that use <c>SteppedValueEditor</c>: icon size
/// (Customize, New panel defaults) and grid step (Layout &amp; snapping). Pure constants — the step is
/// not user-configurable (docs/PLANS/color-presets-and-size-step.md §2.1 D-S1).
/// </summary>
public static class SizeStepPolicy
{
    /// <summary>
    /// The single step every stepped slider uses (§2.1 D-S1). 8 px matches the native icon-size buckets
    /// (16/24/32/40/48/64/72/96/128, ADR-039) at 100% DPI and the Fluent/Material 8-px spacing grid.
    /// </summary>
    public const int Step = 8;

    /// <summary>Lower bound of the icon-size range (Customize, New panel defaults) — unchanged from before.</summary>
    public const int IconSizeMinimum = 16;

    /// <summary>Upper bound of the icon-size range — raised from 96 to 128 (§2.2).</summary>
    public const int IconSizeMaximum = 128;

    /// <summary>
    /// Lower bound of the grid-step range (Layout &amp; snapping). Kept at 2, not raised to a multiple of
    /// <see cref="Step"/>, so legacy <c>GridSize</c> values of 2 or 3 remain valid without a migration
    /// (§2.2, M-6).
    /// </summary>
    public const int GridStepMinimum = 2;

    /// <summary>Upper bound of the grid-step range — unchanged at 128 (§2.2).</summary>
    public const int GridStepMaximum = 128;
}
