using DesktopOrganizer.Services.Abstractions;

namespace DesktopOrganizer.UI.Interaction.Native;

/// <summary>
/// Maps between <see cref="NativeSizingEdge"/> (Abstractions, <c>WM_NCHITTEST</c>-shaped) and
/// <see cref="ResizeEdge"/> (UI, the Phase-1 <c>ResizeGeometryCalculator</c>/<c>ResizeGestureSession</c>
/// vocabulary) — the two are deliberately separate enums (Abstractions must not depend on UI), so every
/// call into the reused Phase-1 pipeline goes through here. Also maps the raw <c>WM_SIZING</c> <c>wParam</c>
/// values (<c>WMSZ_*</c>, WinUser.h) straight to <see cref="NativeSizingEdge"/>.
/// </summary>
public static class NativeSizingEdgeMap
{
    /// <summary>WinUser.h <c>WMSZ_LEFT</c>.</summary>
    public const int WmszLeft = 1;

    /// <summary>WinUser.h <c>WMSZ_RIGHT</c>.</summary>
    public const int WmszRight = 2;

    /// <summary>WinUser.h <c>WMSZ_TOP</c>.</summary>
    public const int WmszTop = 3;

    /// <summary>WinUser.h <c>WMSZ_TOPLEFT</c>.</summary>
    public const int WmszTopLeft = 4;

    /// <summary>WinUser.h <c>WMSZ_TOPRIGHT</c>.</summary>
    public const int WmszTopRight = 5;

    /// <summary>WinUser.h <c>WMSZ_BOTTOM</c>.</summary>
    public const int WmszBottom = 6;

    /// <summary>WinUser.h <c>WMSZ_BOTTOMLEFT</c>.</summary>
    public const int WmszBottomLeft = 7;

    /// <summary>WinUser.h <c>WMSZ_BOTTOMRIGHT</c>.</summary>
    public const int WmszBottomRight = 8;

    /// <summary>
    /// Maps a <see cref="NativeSizingEdge"/> to the equivalent <see cref="ResizeEdge"/>, or
    /// <see langword="null"/> for <see cref="NativeSizingEdge.None"/> (no <see cref="ResizeEdge"/>
    /// equivalent exists — <see cref="ResizeEdge"/> has no "none" member).
    /// </summary>
    public static ResizeEdge? ToResizeEdge(NativeSizingEdge edge) => edge switch
    {
        NativeSizingEdge.Left => ResizeEdge.Left,
        NativeSizingEdge.Top => ResizeEdge.Top,
        NativeSizingEdge.Right => ResizeEdge.Right,
        NativeSizingEdge.Bottom => ResizeEdge.Bottom,
        NativeSizingEdge.TopLeft => ResizeEdge.TopLeft,
        NativeSizingEdge.TopRight => ResizeEdge.TopRight,
        NativeSizingEdge.BottomLeft => ResizeEdge.BottomLeft,
        NativeSizingEdge.BottomRight => ResizeEdge.BottomRight,
        _ => null,
    };

    /// <summary>Maps a <see cref="ResizeEdge"/> to the equivalent <see cref="NativeSizingEdge"/>.</summary>
    public static NativeSizingEdge FromResizeEdge(ResizeEdge edge) => edge switch
    {
        ResizeEdge.Left => NativeSizingEdge.Left,
        ResizeEdge.Top => NativeSizingEdge.Top,
        ResizeEdge.Right => NativeSizingEdge.Right,
        ResizeEdge.Bottom => NativeSizingEdge.Bottom,
        ResizeEdge.TopLeft => NativeSizingEdge.TopLeft,
        ResizeEdge.TopRight => NativeSizingEdge.TopRight,
        ResizeEdge.BottomLeft => NativeSizingEdge.BottomLeft,
        ResizeEdge.BottomRight => NativeSizingEdge.BottomRight,
        _ => throw new ArgumentOutOfRangeException(nameof(edge), edge, "Unknown ResizeEdge."),
    };

    /// <summary>
    /// Maps a raw <c>WM_SIZING</c> <c>wParam</c> value (<c>WMSZ_*</c>) to <see cref="NativeSizingEdge"/>.
    /// Any value outside the 8 documented <c>WMSZ_*</c> constants (including 0, which Win32 never sends
    /// for <c>WM_SIZING</c>) returns <see cref="NativeSizingEdge.None"/> rather than throwing — this is a
    /// cross-check against the edge remembered from <c>WM_ENTERSIZEMOVE</c>, not a hard contract.
    /// </summary>
    public static NativeSizingEdge FromWmsz(int wParam) => wParam switch
    {
        WmszLeft => NativeSizingEdge.Left,
        WmszRight => NativeSizingEdge.Right,
        WmszTop => NativeSizingEdge.Top,
        WmszTopLeft => NativeSizingEdge.TopLeft,
        WmszTopRight => NativeSizingEdge.TopRight,
        WmszBottom => NativeSizingEdge.Bottom,
        WmszBottomLeft => NativeSizingEdge.BottomLeft,
        WmszBottomRight => NativeSizingEdge.BottomRight,
        _ => NativeSizingEdge.None,
    };
}
