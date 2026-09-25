using System.Windows;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.Tests.Unit.Models;

/// <summary>
/// Unit tests for <see cref="DesktopCoordinateSpace"/> — the single isolation point for ADR-028's
/// Вопрос 2 (undocumented <c>Window.Left</c>/<c>Top</c> space after <c>SetParent(WorkerW)</c>). Each
/// test resets the type's process-wide static state in a <c>finally</c> block
/// (<see cref="DesktopCoordinateSpace.ResetForTests"/>) — safe because the test assembly disables
/// parallelization (<c>AssemblyInfo.cs</c>).
/// </summary>
[Trait(UnitTests, "UI")]
public class DesktopCoordinateSpaceTests
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DesktopCoordinateSpaceTests"/> class, resetting
    /// <see cref="DesktopCoordinateSpace"/>'s process-wide static state before each test.
    /// </summary>
    public DesktopCoordinateSpaceTests() => DesktopCoordinateSpace.ResetForTests();

    /// <summary>
    /// Verifies that <see cref="DesktopCoordinateSpace.Offset"/> is the zero vector before any
    /// calibration has occurred.
    /// </summary>
    [Fact]
    public void Offset_Default_IsZero()
    {
        Assert.Equal(new Vector(0d, 0d), DesktopCoordinateSpace.Offset);
    }

    /// <summary>
    /// Verifies that <see cref="DesktopCoordinateSpace.IsIdentity"/> is <see langword="true"/> before
    /// any calibration has occurred.
    /// </summary>
    [Fact]
    public void IsIdentity_Default_IsTrue()
    {
        Assert.True(DesktopCoordinateSpace.IsIdentity);
    }

    /// <summary>
    /// Verifies that <see cref="DesktopCoordinateSpace.ToWindowSpace(Point)"/> followed by
    /// <see cref="DesktopCoordinateSpace.ToScreenSpace(Point)"/> round-trips to the same point when no
    /// calibration offset has been applied.
    /// </summary>
    /// <param name="x">The point's x-coordinate.</param>
    /// <param name="y">The point's y-coordinate.</param>
    [Theory]
    [InlineData(0d, 0d)]
    [InlineData(100d, 200d)]
    [InlineData(-1366d, -768d)]
    [InlineData(3.5d, -0.25d)]
    public void ToWindowSpace_ThenToScreenSpace_Point_IsIdentity_WhenOffsetIsZero(double x, double y)
    {
        var point = new Point(x, y);

        var windowSpace = DesktopCoordinateSpace.ToWindowSpace(point);
        var roundTripped = DesktopCoordinateSpace.ToScreenSpace(windowSpace);

        Assert.Equal(point, windowSpace);
        Assert.Equal(point, roundTripped);
    }

    /// <summary>
    /// Verifies that <see cref="DesktopCoordinateSpace.ToWindowSpace(Rect)"/> returns the input rect
    /// unchanged when no calibration offset has been applied.
    /// </summary>
    [Fact]
    public void ToWindowSpace_Rect_IsIdentity_WhenOffsetIsZero()
    {
        var rect = new Rect(-1366d, -40d, 1920d, 1080d);

        var windowSpace = DesktopCoordinateSpace.ToWindowSpace(rect);

        Assert.Equal(rect, windowSpace);
    }

    /// <summary>
    /// Verifies that <see cref="DesktopCoordinateSpace.ToWindowSpace(Rect)"/> maps
    /// <see cref="Rect.Empty"/> to an empty rect rather than an arbitrary offset rect.
    /// </summary>
    [Fact]
    public void ToWindowSpace_EmptyRect_StaysEmpty()
    {
        var windowSpace = DesktopCoordinateSpace.ToWindowSpace(Rect.Empty);

        Assert.True(windowSpace.IsEmpty);
    }

    /// <summary>
    /// Verifies that <see cref="DesktopCoordinateSpace.ToScreenSpace(Rect)"/> maps
    /// <see cref="Rect.Empty"/> to an empty rect rather than an arbitrary offset rect.
    /// </summary>
    [Fact]
    public void ToScreenSpace_EmptyRect_StaysEmpty()
    {
        var screenSpace = DesktopCoordinateSpace.ToScreenSpace(Rect.Empty);

        Assert.True(screenSpace.IsEmpty);
    }

    /// <summary>
    /// Verifies that <see cref="DesktopCoordinateSpace.CalibrateOnce(Point, Point)"/> sets
    /// <see cref="DesktopCoordinateSpace.Offset"/> to the vector difference between the given screen and
    /// window points.
    /// </summary>
    /// <param name="dx">The x-component of the expected calibrated offset.</param>
    /// <param name="dy">The y-component of the expected calibrated offset.</param>
    [Theory]
    [InlineData(0d, 0d)]
    [InlineData(50d, -25d)]
    [InlineData(-1920d, -1080d)]
    public void CalibrateOnce_SetsOffset_ToDifferenceBetweenScreenAndWindowPoints(double dx, double dy)
    {
        var windowPoint = new Point(10d, 20d);
        var screenPoint = new Point(10d + dx, 20d + dy);

        DesktopCoordinateSpace.CalibrateOnce(screenPoint, windowPoint);

        Assert.Equal(new Vector(dx, dy), DesktopCoordinateSpace.Offset);
    }

    /// <summary>
    /// Verifies that a second call to <see cref="DesktopCoordinateSpace.CalibrateOnce(Point, Point)"/>
    /// is a no-op — the offset stays pinned to the first call's result.
    /// </summary>
    [Fact]
    public void CalibrateOnce_CalledTwice_SecondCallIsNoOp()
    {
        DesktopCoordinateSpace.CalibrateOnce(new Point(10d, 10d), new Point(0d, 0d));
        DesktopCoordinateSpace.CalibrateOnce(new Point(999d, 999d), new Point(0d, 0d));

        Assert.Equal(new Vector(10d, 10d), DesktopCoordinateSpace.Offset);
    }

    /// <summary>
    /// Verifies that after <see cref="DesktopCoordinateSpace.CalibrateOnce(Point, Point)"/> has shifted
    /// the mapping, a screen point still round-trips through window space and back to itself.
    /// </summary>
    [Fact]
    public void CalibrateOnce_AfterCalibration_RoundTripStillIdentity()
    {
        DesktopCoordinateSpace.CalibrateOnce(new Point(1366d, 40d), new Point(0d, 0d));
        var screenDip = new Point(-500d, -300d);

        var windowSpace = DesktopCoordinateSpace.ToWindowSpace(screenDip);
        var roundTripped = DesktopCoordinateSpace.ToScreenSpace(windowSpace);

        Assert.Equal(screenDip, roundTripped);
        Assert.NotEqual(screenDip, windowSpace); // sanity: calibration actually shifted the mapping.
    }

    /// <summary>
    /// Verifies that <see cref="DesktopCoordinateSpace.ResetForTests"/> restores
    /// <see cref="DesktopCoordinateSpace.IsIdentity"/> and <see cref="DesktopCoordinateSpace.Offset"/>
    /// to their defaults after calibration.
    /// </summary>
    [Fact]
    public void ResetForTests_AfterCalibration_RestoresIdentity()
    {
        DesktopCoordinateSpace.CalibrateOnce(new Point(100d, 100d), new Point(0d, 0d));

        DesktopCoordinateSpace.ResetForTests();

        Assert.True(DesktopCoordinateSpace.IsIdentity);
        Assert.Equal(new Vector(0d, 0d), DesktopCoordinateSpace.Offset);
    }

    /// <summary>
    /// Verifies that after <see cref="DesktopCoordinateSpace.ResetForTests"/>,
    /// <see cref="DesktopCoordinateSpace.CalibrateOnce(Point, Point)"/> can run again and set a new
    /// offset (its "exactly once per process" guard has been cleared).
    /// </summary>
    [Fact]
    public void ResetForTests_AllowsCalibrateOnceToRunAgain()
    {
        DesktopCoordinateSpace.CalibrateOnce(new Point(100d, 100d), new Point(0d, 0d));
        DesktopCoordinateSpace.ResetForTests();

        DesktopCoordinateSpace.CalibrateOnce(new Point(5d, 5d), new Point(0d, 0d));

        Assert.Equal(new Vector(5d, 5d), DesktopCoordinateSpace.Offset);
    }
}
