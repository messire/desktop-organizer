namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// Describes the rectangle drawn by a right-button lasso on the desktop, in screen coordinates.
/// </summary>
public sealed class DesktopLassoEventArgs(int x, int y, int width, int height) : EventArgs
{
    /// <summary>Left edge in screen pixels.</summary>
    public int X { get; } = x;

    /// <summary>Top edge in screen pixels.</summary>
    public int Y { get; } = y;

    /// <summary>Width in pixels.</summary>
    public int Width { get; } = width;

    /// <summary>Height in pixels.</summary>
    public int Height { get; } = height;
}
