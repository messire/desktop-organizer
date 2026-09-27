namespace DesktopOrganizer.UI.Models;

/// <summary>Which way a stepped value should move — one step up (towards <c>Maximum</c>) or down.</summary>
public enum StepDirection
{
    /// <summary>Move towards the nearest multiple of the step strictly below the current value.</summary>
    Down = -1,

    /// <summary>Move towards the nearest multiple of the step strictly above the current value.</summary>
    Up = 1,
}

/// <summary>
/// Pure, WPF-free stepping arithmetic shared by <c>SteppedSlider</c> and <c>SteppedValueEditor</c>
/// (docs/PLANS/color-presets-and-size-step.md §5.1). No control ever computes a stepped/snapped value
/// itself — everything routes through here so keyboard, mouse-drag, mouse-wheel, and manual-entry paths
/// agree on the exact same target value.
/// </summary>
public static class SteppedValue
{
    /// <summary>
    /// Tolerance used when deciding whether a value already sits exactly on a step multiple, or on the
    /// caller's minimum/maximum, so tiny floating-point noise (e.g. a persisted <c>31.9999999</c>) is
    /// treated the same as the exact multiple it represents.
    /// </summary>
    public const double Tolerance = 1e-6;

    /// <summary>
    /// Returns the nearest multiple of <paramref name="step"/> strictly above (<see cref="StepDirection.Up"/>)
    /// or strictly below (<see cref="StepDirection.Down"/>) <paramref name="current"/>, clamped to
    /// <c>[<paramref name="minimum"/>, <paramref name="maximum"/>]</c>. A value within <see cref="Tolerance"/>
    /// of a multiple is treated as being exactly that multiple. <paramref name="current"/> of
    /// <c>NaN</c>/±<c>Infinity</c> is treated as <paramref name="minimum"/> rather than throwing (§3.2).
    /// </summary>
    /// <param name="current">The value to step from. Clamped to range first; non-finite values become <paramref name="minimum"/>.</param>
    /// <param name="step">The step size. Must be at least 1.</param>
    /// <param name="direction">Which way to step.</param>
    /// <param name="minimum">Inclusive lower bound of the result.</param>
    /// <param name="maximum">Inclusive upper bound of the result.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="step"/> is less than 1, or <paramref name="minimum"/> exceeds <paramref name="maximum"/>.</exception>
    public static int Next(double current, int step, StepDirection direction, int minimum, int maximum)
    {
        ValidateStep(step);
        ValidateRange(minimum, maximum);

        var c = ClampFinite(current, minimum, maximum);

        return direction == StepDirection.Up
            ? NextUp(c, step, maximum)
            : NextDown(c, step, minimum);
    }

    /// <summary>
    /// Computes the value a drag gesture should snap to, given the raw (unsnapped) pointer-derived value
    /// and the value the drag started from (§5.1). The result never moves "against" the drag direction
    /// relative to <paramref name="dragStart"/>: it always lands on at least one full step away from the
    /// start once the pointer has moved <see cref="Tolerance"/> past it, so a drag that has moved slightly
    /// in one direction can never reverse into the opposite step by a rounding accident. When
    /// <paramref name="raw"/> is still within <see cref="Tolerance"/> of <paramref name="dragStart"/> (no
    /// meaningful movement yet), the result is simply <paramref name="dragStart"/> rounded and clamped —
    /// this is what lets the caller detect "no movement" and skip committing a write (§5.3).
    /// </summary>
    /// <param name="raw">The raw, unsnapped value implied by the current pointer position. Clamped to range first; non-finite becomes <paramref name="minimum"/>.</param>
    /// <param name="dragStart">The value the drag gesture started from. Non-finite is treated as <paramref name="minimum"/>.</param>
    /// <param name="step">The step size. Must be at least 1.</param>
    /// <param name="minimum">Inclusive lower bound of the result.</param>
    /// <param name="maximum">Inclusive upper bound of the result.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="step"/> is less than 1, or <paramref name="minimum"/> exceeds <paramref name="maximum"/>.</exception>
    public static int SnapForDrag(double raw, double dragStart, int step, int minimum, int maximum)
    {
        ValidateStep(step);
        ValidateRange(minimum, maximum);

        var r = ClampFinite(raw, minimum, maximum);
        var start = IsNonFinite(dragStart) ? minimum : dragStart;

        var nearestRaw = Math.Round(r / step, MidpointRounding.AwayFromZero) * step;
        var nearest = ClampToInt(nearestRaw, minimum, maximum);

        if (r > start + Tolerance)
        {
            var upFromStart = NextUp(ClampFinite(start, minimum, maximum), step, maximum);
            return Math.Min(Math.Max(nearest, upFromStart), maximum);
        }

        if (r < start - Tolerance)
        {
            var downFromStart = NextDown(ClampFinite(start, minimum, maximum), step, minimum);
            return Math.Max(Math.Min(nearest, downFromStart), minimum);
        }

        return RoundAndClamp(start, minimum, maximum);
    }

    /// <summary>
    /// Rounds <paramref name="value"/> to the nearest integer (ties away from zero) and clamps it to
    /// <c>[<paramref name="minimum"/>, <paramref name="maximum"/>]</c>. <c>NaN</c>/±<c>Infinity</c> map to
    /// <paramref name="minimum"/>. Used to normalize external/UIA writes (e.g. <c>RangeValuePattern.SetValue</c>)
    /// to an integer, and to resolve "no movement" drag/commit outcomes (§5.1).
    /// </summary>
    /// <param name="value">The value to round and clamp.</param>
    /// <param name="minimum">Inclusive lower bound of the result.</param>
    /// <param name="maximum">Inclusive upper bound of the result.</param>
    public static int RoundAndClamp(double value, int minimum, int maximum)
    {
        ValidateRange(minimum, maximum);

        if (IsNonFinite(value))
            return minimum;

        var rounded = Math.Round(value, MidpointRounding.AwayFromZero);
        return ClampToInt(rounded, minimum, maximum);
    }

    private static int NextUp(double current, int step, int maximum)
    {
        if (current >= maximum - Tolerance)
            return maximum;

        var k = Math.Floor((current + Tolerance) / step);
        var candidate = (k + 1) * step;
        return (int)Math.Min(Math.Round(candidate, MidpointRounding.AwayFromZero), maximum);
    }

    private static int NextDown(double current, int step, int minimum)
    {
        if (current <= minimum + Tolerance)
            return minimum;

        var k = Math.Ceiling((current - Tolerance) / step);
        var candidate = (k - 1) * step;
        return (int)Math.Max(Math.Round(candidate, MidpointRounding.AwayFromZero), minimum);
    }

    private static double ClampFinite(double value, int minimum, int maximum)
    {
        if (IsNonFinite(value))
            return minimum;

        if (value < minimum) return minimum;
        if (value > maximum) return maximum;
        return value;
    }

    private static int ClampToInt(double value, int minimum, int maximum)
    {
        var rounded = Math.Round(value, MidpointRounding.AwayFromZero);
        if (rounded < minimum) return minimum;
        if (rounded > maximum) return maximum;
        return (int)rounded;
    }

    private static bool IsNonFinite(double value) => double.IsNaN(value) || double.IsInfinity(value);

    private static void ValidateStep(int step)
    {
        if (step < 1)
            throw new ArgumentOutOfRangeException(nameof(step), step, "Step must be at least 1.");
    }

    private static void ValidateRange(int minimum, int maximum)
    {
        if (minimum > maximum)
            throw new ArgumentOutOfRangeException(nameof(minimum), minimum, "Minimum must not exceed maximum.");
    }
}
