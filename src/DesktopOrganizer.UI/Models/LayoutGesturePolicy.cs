namespace DesktopOrganizer.UI.Models;

/// <summary>
/// Which panel-geometry gestures are currently allowed. The single machine-checkable source of truth
/// both the move/resize gates (P6-2) and the edit-mode visual language (P6-4) read from
/// (docs/PLANS/phase-6-edit-layout-ux.md §1 D-2).
/// </summary>
public readonly record struct LayoutGestureAllowance(bool CanMove, bool CanResize);

/// <summary>
/// Pure decision table (no WPF, no side effects) resolving <see cref="LayoutGestureAllowance"/> from
/// the three independent inputs — <c>IsLayoutLocked</c> (D-3), <see cref="LayoutInteractionMode"/>
/// (D-1), and the Normal-mode preference <see cref="NormalModeLayoutGestures"/> (D-2). See the plan's
/// §1 D-2 truth table (2×2×3 = 12 cases): <c>IsLayoutLocked</c> always wins; otherwise
/// <see cref="LayoutInteractionMode.EditLayout"/> always allows both; otherwise the outcome is decided
/// per-gesture by <paramref name="normalPolicy"/>&#8203;'s value.
/// </summary>
public static class LayoutGesturePolicy
{
    /// <summary>Resolves the current <see cref="LayoutGestureAllowance"/> from the three policy inputs. Pure, deterministic, never throws for any combination of valid enum values.</summary>
    public static LayoutGestureAllowance Resolve(
        LayoutInteractionMode mode, bool isLayoutLocked, NormalModeLayoutGestures normalPolicy)
    {
        if (isLayoutLocked)
        {
            return new LayoutGestureAllowance(CanMove: false, CanResize: false);
        }

        if (mode == LayoutInteractionMode.EditLayout)
        {
            return new LayoutGestureAllowance(CanMove: true, CanResize: true);
        }

        return normalPolicy switch
        {
            NormalModeLayoutGestures.MoveAndResize => new LayoutGestureAllowance(CanMove: true, CanResize: true),
            NormalModeLayoutGestures.MoveOnly => new LayoutGestureAllowance(CanMove: true, CanResize: false),
            _ => new LayoutGestureAllowance(CanMove: false, CanResize: false),
        };
    }
}
