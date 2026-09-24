using System.Windows;

namespace DesktopOrganizer.UI.Interaction;

public enum ResizeEdge
{
    Left, Top, Right, Bottom,
    TopLeft, TopRight, BottomLeft, BottomRight,
}

public readonly record struct ResizeConstraints(
    double MinWidth,
    double MinHeight,
    double SnapCellWidth,
    double SnapCellHeight);

/// <summary>
/// Pure geometry seam. Production implementation should reuse current GridMetrics /
/// AdaptiveGridLayout rules so migration to native sizing does not change semantics.
/// </summary>
public static class ResizeGeometryCalculator
{
    public static Rect Apply(
        Rect start,
        ResizeEdge edge,
        Vector delta,
        ResizeConstraints constraints)
    {
        var left = start.Left;
        var top = start.Top;
        var right = start.Right;
        var bottom = start.Bottom;

        if (edge is ResizeEdge.Left or ResizeEdge.TopLeft or ResizeEdge.BottomLeft)
            left += delta.X;
        if (edge is ResizeEdge.Right or ResizeEdge.TopRight or ResizeEdge.BottomRight)
            right += delta.X;
        if (edge is ResizeEdge.Top or ResizeEdge.TopLeft or ResizeEdge.TopRight)
            top += delta.Y;
        if (edge is ResizeEdge.Bottom or ResizeEdge.BottomLeft or ResizeEdge.BottomRight)
            bottom += delta.Y;

        if (right - left < constraints.MinWidth)
        {
            if (edge is ResizeEdge.Left or ResizeEdge.TopLeft or ResizeEdge.BottomLeft)
                left = right - constraints.MinWidth;
            else
                right = left + constraints.MinWidth;
        }

        if (bottom - top < constraints.MinHeight)
        {
            if (edge is ResizeEdge.Top or ResizeEdge.TopLeft or ResizeEdge.TopRight)
                top = bottom - constraints.MinHeight;
            else
                bottom = top + constraints.MinHeight;
        }

        return new Rect(new Point(left, top), new Point(right, bottom));
    }
}
