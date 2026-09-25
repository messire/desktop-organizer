using System.Reflection;
using System.Windows;
using DesktopOrganizer.UI.Behaviors;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.Tests.Unit.Behaviors;

/// <summary>
/// Unit tests for <see cref="PanelGeometryWriter"/> — the single atomic Window+model geometry write
/// (docs/PLANS/workspace-grid-03-04-integration.md §1.5, baseline risk 17). Runs on a dedicated STA
/// thread (<see cref="StaTestRunner"/>) because it touches a real <see cref="Window"/>'s
/// <see cref="Window.Left"/>/<see cref="Window.Top"/>/<see cref="FrameworkElement.Width"/>/<see cref="FrameworkElement.Height"/>
/// — the same convention <see cref="RollUpBehaviorMotionTests"/> uses for a plain, never-shown
/// <see cref="Window"/> instance.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class PanelGeometryWriterTests
{
    private static readonly FieldInfo ApplyingField =
        typeof(PanelGeometryWriter).GetField("_applying", BindingFlags.NonPublic | BindingFlags.Instance)!;

    /// <summary>Verifies that <see cref="PanelGeometryWriter.Apply"/> with no model attached does not throw and leaves the window's position untouched.</summary>
    [Fact]
    public void Apply_NoModelAttached_DoesNotThrow_AndLeavesWindowUntouched()
    {
        StaTestRunner.Run(() =>
        {
            var window = new Window();
            var writer = new PanelGeometryWriter(window, () => null);

            writer.Apply(new Rect(10, 20, 30, 40), span: null, monitorDeviceId: null);

            // Window.Left/Top default to NaN ("unset" -- the OS positions the window); untouched here
            // is the point, not any specific numeric value.
            Assert.True(double.IsNaN(window.Left));
            Assert.True(double.IsNaN(window.Top));
        });
    }

    /// <summary>Verifies that <see cref="PanelGeometryWriter.Apply"/> writes both the window's bounds and the model's position and size.</summary>
    [Fact]
    public void Apply_WritesWindowBoundsAndModelPositionAndSize()
    {
        StaTestRunner.Run(() =>
        {
            var window = new Window();
            var model = new ChildWindowModel();
            var writer = new PanelGeometryWriter(window, () => model);

            writer.Apply(new Rect(10, 20, 300, 400), span: null, monitorDeviceId: null);

            Assert.Equal(10d, window.Left);
            Assert.Equal(20d, window.Top);
            Assert.Equal(300d, window.Width);
            Assert.Equal(400d, window.Height);
            Assert.Equal(new System.Drawing.Point(10, 20), model.Position);
            Assert.Equal(new System.Drawing.Size(300, 400), model.Size);
        });
    }

    // ── m-1 fix-cycle 1: Math.Round, not a truncating (int) cast (docs/PLANS/workspace-grid-03-04-
    //    integration.md §2, code-review) ─────────────────────────────────────────────────────────────

    /// <summary>Verifies that <see cref="PanelGeometryWriter.Apply"/> rounds a negative fractional coordinate to the nearest integer instead of truncating toward zero.</summary>
    [Fact]
    public void Apply_NegativeFractionalCoordinate_RoundsToNearest_NotTowardZero()
    {
        StaTestRunner.Run(() =>
        {
            // A monitor left of/above the primary (ADR-028 "Origin для системы B") can legitimately place
            // a panel at a negative DIP coordinate. A truncating (int) cast rounds toward ZERO -- (-0.6)
            // becomes 0, not -1 -- which disagreed with LassoPanelGeometry's own Math.Round-based
            // conversion (F-7) for the exact same class of coordinate.
            var window = new Window();
            var model = new ChildWindowModel();
            var writer = new PanelGeometryWriter(window, () => model);

            writer.Apply(new Rect(-0.6, -1366.6, 300, 400), span: null, monitorDeviceId: null);

            Assert.Equal(new System.Drawing.Point(-1, -1367), model.Position);
        });
    }

    /// <summary>Verifies that <see cref="PanelGeometryWriter.ApplyPosition"/> rounds a negative fractional coordinate using banker's rounding, not truncation toward zero.</summary>
    [Fact]
    public void ApplyPosition_NegativeFractionalCoordinate_RoundsToNearest_NotTowardZero()
    {
        StaTestRunner.Run(() =>
        {
            var window = new Window();
            var model = new ChildWindowModel();
            var writer = new PanelGeometryWriter(window, () => model);

            writer.ApplyPosition(new Point(-0.6, -101.5), span: null, monitorDeviceId: null);

            // -101.5 is a Math.Round midpoint: banker's rounding (ToEven, the default) picks -102 (even)
            // over -101 (odd) -- either way, NOT the (int) cast's -101 (truncation toward zero).
            Assert.Equal(new System.Drawing.Point(-1, -102), model.Position);
        });
    }

    /// <summary>Verifies that <see cref="PanelGeometryWriter.Apply"/> writes a supplied <see cref="GridSpan"/> into the model's span units.</summary>
    [Fact]
    public void Apply_WithSpan_WritesModelSpanUnits()
    {
        StaTestRunner.Run(() =>
        {
            var window = new Window();
            var model = new ChildWindowModel();
            var writer = new PanelGeometryWriter(window, () => model);

            writer.Apply(new Rect(0, 0, 152, 152), span: new GridSpan(20, 20), monitorDeviceId: null);

            Assert.Equal(20, model.WidthSpanUnits);
            Assert.Equal(20, model.HeightSpanUnits);
        });
    }

    /// <summary>Verifies that <see cref="PanelGeometryWriter.Apply"/> with a <see langword="null"/> span leaves the model's existing span units untouched.</summary>
    [Fact]
    public void Apply_NullSpan_LeavesExistingModelSpanUnitsUntouched()
    {
        StaTestRunner.Run(() =>
        {
            var window = new Window();
            var model = new ChildWindowModel { WidthSpanUnits = 12, HeightSpanUnits = 12 };
            var writer = new PanelGeometryWriter(window, () => model);

            writer.Apply(new Rect(0, 0, 100, 100), span: null, monitorDeviceId: null);

            Assert.Equal(12, model.WidthSpanUnits);
            Assert.Equal(12, model.HeightSpanUnits);
        });
    }

    /// <summary>Verifies that <see cref="PanelGeometryWriter.Apply"/> writes a supplied monitor device ID into the model.</summary>
    [Fact]
    public void Apply_WithMonitorDeviceId_WritesModelMonitorDeviceId()
    {
        StaTestRunner.Run(() =>
        {
            var window = new Window();
            var model = new ChildWindowModel();
            var writer = new PanelGeometryWriter(window, () => model);

            writer.Apply(new Rect(0, 0, 100, 100), span: null, monitorDeviceId: @"\\.\DISPLAY2");

            Assert.Equal(@"\\.\DISPLAY2", model.MonitorDeviceId);
        });
    }

    /// <summary>Verifies that <see cref="PanelGeometryWriter.Apply"/> with a <see langword="null"/> monitor device ID leaves the model's existing monitor device ID untouched.</summary>
    [Fact]
    public void Apply_NullMonitorDeviceId_LeavesExistingModelMonitorDeviceIdUntouched()
    {
        StaTestRunner.Run(() =>
        {
            var window = new Window();
            var model = new ChildWindowModel { MonitorDeviceId = @"\\.\DISPLAY1" };
            var writer = new PanelGeometryWriter(window, () => model);

            writer.Apply(new Rect(0, 0, 100, 100), span: null, monitorDeviceId: null);

            Assert.Equal(@"\\.\DISPLAY1", model.MonitorDeviceId);
        });
    }

    /// <summary>Verifies that <see cref="PanelGeometryWriter.IsApplying"/> is false both before and after a call to <see cref="PanelGeometryWriter.Apply"/> completes.</summary>
    [Fact]
    public void IsApplying_IsFalse_BeforeAndAfterApply()
    {
        StaTestRunner.Run(() =>
        {
            var window = new Window();
            var model = new ChildWindowModel();
            var writer = new PanelGeometryWriter(window, () => model);

            Assert.False(writer.IsApplying);

            writer.Apply(new Rect(0, 0, 100, 100), span: null, monitorDeviceId: null);

            Assert.False(writer.IsApplying);
        });
    }

    /// <summary>
    /// Direct test of the re-entrancy guard (baseline risk 17): if <see cref="PanelGeometryWriter.Apply"/>
    /// were somehow re-entered synchronously while already applying — the exact shape of the
    /// <c>OnRenderSizeChanged -&gt; model.Size -&gt; ... -&gt; Window.Width -&gt; OnRenderSizeChanged</c>
    /// cycle this type exists to prevent — the nested call must be a complete no-op rather than
    /// overwriting the in-flight write with different values. Sets the private guard field directly via
    /// reflection to simulate "already applying" deterministically, without depending on whether a
    /// never-shown <see cref="Window"/> actually raises <see cref="Window.LocationChanged"/>/
    /// <c>SizeChanged</c> synchronously (it does not, absent a real HWND/layout pass).
    /// </summary>
    [Fact]
    public void Apply_WhenAlreadyApplying_IsANoOp()
    {
        StaTestRunner.Run(() =>
        {
            var window = new Window { Left = 1, Top = 2, Width = 3, Height = 4 };
            var model = new ChildWindowModel
            {
                Position = new System.Drawing.Point(1, 2),
                Size = new System.Drawing.Size(3, 4),
            };
            var writer = new PanelGeometryWriter(window, () => model);
            ApplyingField.SetValue(writer, true);

            writer.Apply(new Rect(999, 999, 999, 999), span: new GridSpan(50, 50), monitorDeviceId: @"\\.\DISPLAY9");

            Assert.Equal(1d, window.Left);
            Assert.Equal(2d, window.Top);
            Assert.Equal(3d, window.Width);
            Assert.Equal(4d, window.Height);
            Assert.Equal(new System.Drawing.Point(1, 2), model.Position);
            Assert.Null(model.WidthSpanUnits);
            Assert.Null(model.MonitorDeviceId);
        });
    }

    // ── ApplyPosition (P03): move-only writes ─────────────────────────────────────────────────────

    /// <summary>Verifies that <see cref="PanelGeometryWriter.ApplyPosition"/> writes the window's Left/Top and the model's position while leaving both sizes untouched.</summary>
    [Fact]
    public void ApplyPosition_WritesLeftTopAndModelPosition_LeavingSizeUntouched()
    {
        StaTestRunner.Run(() =>
        {
            var window = new Window { Width = 300, Height = 400 };
            var model = new ChildWindowModel { Size = new System.Drawing.Size(300, 400) };
            var writer = new PanelGeometryWriter(window, () => model);

            writer.ApplyPosition(new Point(17, 23), span: null, monitorDeviceId: null);

            Assert.Equal(17d, window.Left);
            Assert.Equal(23d, window.Top);
            Assert.Equal(new System.Drawing.Point(17, 23), model.Position);

            // The whole point of this overload: neither the window nor the model is resized.
            Assert.Equal(300d, window.Width);
            Assert.Equal(400d, window.Height);
            Assert.Equal(new System.Drawing.Size(300, 400), model.Size);
        });
    }

    /// <summary>Verifies that <see cref="PanelGeometryWriter.ApplyPosition"/> on a rolled-up panel keeps the model's persisted expanded size, without overwriting it with the collapsed window height.</summary>
    [Fact]
    public void ApplyPosition_RolledUpPanel_KeepsThePersistedExpandedSize()
    {
        StaTestRunner.Run(() =>
        {
            // While rolled up the window's live Height is the collapsed header height, but the model
            // deliberately still holds the expanded height for restore -- a move must not overwrite it.
            var window = new Window { Width = 300, Height = 32 };
            var model = new ChildWindowModel { IsRolledUp = true, Size = new System.Drawing.Size(300, 400) };
            var writer = new PanelGeometryWriter(window, () => model);

            writer.ApplyPosition(new Point(50, 60), span: null, monitorDeviceId: null);

            Assert.Equal(new System.Drawing.Size(300, 400), model.Size);
            Assert.Equal(32d, window.Height);
        });
    }

    /// <summary>Verifies that <see cref="PanelGeometryWriter.ApplyPosition"/> writes both the span and the monitor device ID into the model when supplied.</summary>
    [Fact]
    public void ApplyPosition_WritesSpanAndMonitorDeviceIdWhenSupplied()
    {
        StaTestRunner.Run(() =>
        {
            var window = new Window();
            var model = new ChildWindowModel();
            var writer = new PanelGeometryWriter(window, () => model);

            writer.ApplyPosition(new Point(0, 0), new GridSpan(31, 32), @"\.\DISPLAY2");

            Assert.Equal(31, model.WidthSpanUnits);
            Assert.Equal(32, model.HeightSpanUnits);
            Assert.Equal(@"\.\DISPLAY2", model.MonitorDeviceId);
        });
    }

    /// <summary>Verifies that <see cref="PanelGeometryWriter.ApplyPosition"/> with no model attached is a complete no-op, leaving the window's position untouched.</summary>
    [Fact]
    public void ApplyPosition_NoModelAttached_IsANoOp()
    {
        StaTestRunner.Run(() =>
        {
            var window = new Window();
            var writer = new PanelGeometryWriter(window, () => null);

            writer.ApplyPosition(new Point(10, 20), span: null, monitorDeviceId: null);

            Assert.True(double.IsNaN(window.Left));
            Assert.True(double.IsNaN(window.Top));
        });
    }

    /// <summary>Verifies that <see cref="PanelGeometryWriter.IsApplying"/> is true for the whole duration of an <see cref="PanelGeometryWriter.ApplyPosition"/> write, observed from inside <see cref="Window.LocationChanged"/>, and false again once it returns.</summary>
    [Fact]
    public void ApplyPosition_SetsIsApplyingForTheDurationOfTheWrite()
    {
        StaTestRunner.Run(() =>
        {
            var window = new Window();
            var model = new ChildWindowModel();
            var writer = new PanelGeometryWriter(window, () => model);
            var observed = new List<bool>();
            window.LocationChanged += (_, _) => observed.Add(writer.IsApplying);

            writer.ApplyPosition(new Point(10, 20), span: null, monitorDeviceId: null);

            Assert.All(observed, applying => Assert.True(applying));
            Assert.False(writer.IsApplying);
        });
    }
}
