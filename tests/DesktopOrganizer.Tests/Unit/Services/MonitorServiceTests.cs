using System.Windows;
using DesktopOrganizer.Services.Abstractions;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="IMonitorService"/> contract logic.
/// Because <see cref="MonitorService"/> relies entirely on P/Invoke, these tests
/// exercise the contract via a <see cref="Mock{T}"/> with a pre-populated monitor list.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class MonitorServiceTests
{
    // ── FindMonitorAt ────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="IMonitorService.FindMonitorAt"/> returns the specific monitor whose
    /// bounds contain the given point, out of several candidates.
    /// </summary>
    [Fact]
    public void FindMonitorAt_PointInsideBounds_ReturnsCorrectMonitor()
    {
        // Arrange
        var primary = MakeMonitor(@"\\.\DISPLAY1", 0, 0, 1920, 1080, primary: true);
        var secondary = MakeMonitor(@"\\.\DISPLAY2", 1920, 0, 1920, 1080);
        var svc = BuildMock([primary, secondary]).Object;

        // Act
        var result = svc.FindMonitorAt(new Point(1950, 100)); // inside secondary

        // Assert
        Assert.NotNull(result);
        Assert.Equal(@"\\.\DISPLAY2", result.DeviceName);
    }

    /// <summary>
    /// Verifies that a point inside the primary monitor's bounds resolves to a monitor whose
    /// <see cref="MonitorInfo.IsPrimary"/> flag is set.
    /// </summary>
    [Fact]
    public void FindMonitorAt_PointOnPrimaryMonitor_ReturnsPrimary()
    {
        // Arrange
        var primary = MakeMonitor(@"\\.\DISPLAY1", 0, 0, 1920, 1080, primary: true);
        var svc = BuildMock([primary]).Object;

        // Act
        var result = svc.FindMonitorAt(new Point(100, 100));

        // Assert
        Assert.NotNull(result);
        Assert.True(result.IsPrimary);
    }

    /// <summary>
    /// Verifies that <see cref="IMonitorService.FindMonitorAt"/> returns <see langword="null"/> for a
    /// point that lies outside every known monitor's bounds.
    /// </summary>
    [Fact]
    public void FindMonitorAt_PointOutsideAllMonitors_ReturnsNull()
    {
        // Arrange
        var primary = MakeMonitor(@"\\.\DISPLAY1", 0, 0, 1920, 1080, primary: true);
        var svc = BuildMock([primary]).Object;

        // Act
        var result = svc.FindMonitorAt(new Point(9999, 9999));

        // Assert
        Assert.Null(result);
    }

    // ── FindByDeviceName ─────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="IMonitorService.FindByDeviceName"/> returns the matching monitor for an
    /// existing device name.
    /// </summary>
    [Fact]
    public void FindByDeviceName_ExistingDevice_ReturnsMonitor()
    {
        // Arrange
        var primary = MakeMonitor(@"\\.\DISPLAY1", 0, 0, 1920, 1080, primary: true);
        var svc = BuildMock([primary]).Object;

        // Act
        var result = svc.FindByDeviceName(@"\\.\DISPLAY1");

        // Assert
        Assert.NotNull(result);
        Assert.Equal(@"\\.\DISPLAY1", result.DeviceName);
    }

    /// <summary>
    /// Verifies that <see cref="IMonitorService.FindByDeviceName"/> matches the device name
    /// case-insensitively.
    /// </summary>
    [Fact]
    public void FindByDeviceName_CaseInsensitive_ReturnsMonitor()
    {
        // Arrange
        var primary = MakeMonitor(@"\\.\DISPLAY1", 0, 0, 1920, 1080, primary: true);
        var svc = BuildMock([primary]).Object;

        // Act
        var result = svc.FindByDeviceName(@"\\.\display1");

        // Assert
        Assert.NotNull(result);
    }

    /// <summary>
    /// Verifies that <see cref="IMonitorService.FindByDeviceName"/> returns <see langword="null"/> for a
    /// device name that does not match any known monitor.
    /// </summary>
    [Fact]
    public void FindByDeviceName_UnknownDevice_ReturnsNull()
    {
        // Arrange
        var primary = MakeMonitor(@"\\.\DISPLAY1", 0, 0, 1920, 1080, primary: true);
        var svc = BuildMock([primary]).Object;

        // Act
        var result = svc.FindByDeviceName(@"\\.\DISPLAY99");

        // Assert
        Assert.Null(result);
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    private static MonitorInfo MakeMonitor(string device, double x, double y, double w, double h, bool primary = false)
        => new(device, new Rect(x, y, w, h), new Rect(x, y, w, h), primary);

    private static Mock<IMonitorService> BuildMock(IReadOnlyList<MonitorInfo> monitors)
    {
        var mock = new Mock<IMonitorService>();
        mock.Setup(s => s.GetMonitors()).Returns(monitors);
        mock.Setup(s => s.FindMonitorAt(It.IsAny<Point>()))
            .Returns<Point>(p => monitors.FirstOrDefault(m => m.Bounds.Contains(p)));
        mock.Setup(s => s.FindByDeviceName(It.IsAny<string>()))
            .Returns<string>(name => monitors.FirstOrDefault(m =>
                string.Equals(m.DeviceName, name, StringComparison.OrdinalIgnoreCase)));
        return mock;
    }
}
