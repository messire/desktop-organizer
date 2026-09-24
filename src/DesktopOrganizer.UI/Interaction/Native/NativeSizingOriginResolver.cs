using DesktopOrganizer.Services.Abstractions;

namespace DesktopOrganizer.UI.Interaction.Native;

/// <summary>
/// An offset, in physical pixels, between two coordinate spaces — see
/// <see cref="NativeSizingOriginResolver.Resolve"/>.
/// </summary>
/// <param name="X">Horizontal offset, in physical pixels.</param>
/// <param name="Y">Vertical offset, in physical pixels.</param>
public readonly record struct NativeVectorPx(int X, int Y);

/// <summary>
/// Self-calibrates the coordinate space of a <c>WM_SIZING</c> gesture (R-3,
/// docs/PLANS/phase-2-native-panel-sizing.md §1.4/§4). The panel is <c>SetParent</c>-ed into
/// <c>WorkerW</c>; whether a reparented <c>WS_POPUP</c>'s <c>WM_SIZING</c> rect arrives in screen or
/// parent-client coordinates is undocumented, so this does not guess — it derives a constant offset from
/// the one value that is ALWAYS screen-space (<c>GetWindowRect</c>) and the first <c>WM_SIZING</c> rect
/// of the gesture, then that offset is applied to every subsequent rect of the same gesture. Pure and
/// stateless: callers recompute it once per gesture (on <c>WM_ENTERSIZEMOVE</c> + the first
/// <c>WM_SIZING</c>), which also transparently survives an Explorer restart / re-parent between gestures.
/// </summary>
public static class NativeSizingOriginResolver
{
    /// <summary>
    /// Computes the offset to add to every <c>WM_SIZING</c> rect of the current gesture to bring it into
    /// screen-space, i.e. <c>windowRectScreen - firstSizingRect</c> (top-left difference).
    /// </summary>
    /// <param name="windowRectScreen">The panel's <c>GetWindowRect</c> result at gesture start — always
    /// screen-space, regardless of parentage.</param>
    /// <param name="firstSizingRect">The first <c>WM_SIZING</c> rect received for this gesture, in
    /// whatever coordinate space the system actually used.</param>
    public static NativeVectorPx Resolve(NativeRectPx windowRectScreen, NativeRectPx firstSizingRect) =>
        new(windowRectScreen.Left - firstSizingRect.Left, windowRectScreen.Top - firstSizingRect.Top);
}
