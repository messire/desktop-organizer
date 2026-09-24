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
    public DesktopCoordinateSpaceTests() => DesktopCoordinateSpace.ResetForTests();

    [Fact]
    public void Offset_Default_IsZero()
    {
        Assert.Equal(new Vector(0d, 0d), DesktopCoordinateSpace.Offset);
    }

    [Fact]
    public void IsIdentity_Default_IsTrue()
    {
        Assert.True(DesktopCoordinateSpace.IsIdentity);
    }

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

    [Fact]
    public void ToWindowSpace_Rect_IsIdentity_WhenOffsetIsZero()
    {
        var rect = new Rect(-1366d, -40d, 1920d, 1080d);

        var windowSpace = DesktopCoordinateSpace.ToWindowSpace(rect);

        Assert.Equal(rect, windowSpace);
    }

    [Fact]
    public void ToWindowSpace_EmptyRect_StaysEmpty()
    {
        var windowSpace = DesktopCoordinateSpace.ToWindowSpace(Rect.Empty);

        Assert.True(windowSpace.IsEmpty);
    }

    [Fact]
    public void ToScreenSpace_EmptyRect_StaysEmpty()
    {
        var screenSpace = DesktopCoordinateSpace.ToScreenSpace(Rect.Empty);

        Assert.True(screenSpace.IsEmpty);
    }

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

    [Fact]
    public void CalibrateOnce_CalledTwice_SecondCallIsNoOp()
    {
        DesktopCoordinateSpace.CalibrateOnce(new Point(10d, 10d), new Point(0d, 0d));
        DesktopCoordinateSpace.CalibrateOnce(new Point(999d, 999d), new Point(0d, 0d));

        Assert.Equal(new Vector(10d, 10d), DesktopCoordinateSpace.Offset);
    }

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

    [Fact]
    public void ResetForTests_AfterCalibration_RestoresIdentity()
    {
        DesktopCoordinateSpace.CalibrateOnce(new Point(100d, 100d), new Point(0d, 0d));

        DesktopCoordinateSpace.ResetForTests();

        Assert.True(DesktopCoordinateSpace.IsIdentity);
        Assert.Equal(new Vector(0d, 0d), DesktopCoordinateSpace.Offset);
    }

    [Fact]
    public void ResetForTests_AllowsCalibrateOnceToRunAgain()
    {
        DesktopCoordinateSpace.CalibrateOnce(new Point(100d, 100d), new Point(0d, 0d));
        DesktopCoordinateSpace.ResetForTests();

        DesktopCoordinateSpace.CalibrateOnce(new Point(5d, 5d), new Point(0d, 0d));

        Assert.Equal(new Vector(5d, 5d), DesktopCoordinateSpace.Offset);
    }
}
