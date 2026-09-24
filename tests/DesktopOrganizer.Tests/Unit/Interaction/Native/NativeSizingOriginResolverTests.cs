using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Interaction.Native;

namespace DesktopOrganizer.Tests.Unit.Interaction.Native;

/// <summary>
/// Phase 2 (commit C2, docs/PLANS/phase-2-native-panel-sizing.md §1.4/§5, T-ORIG-01..03) tests for
/// <see cref="NativeSizingOriginResolver"/> — the WM_SIZING coordinate-space self-calibration that
/// mitigates R-3 (unknown coordinate space for a reparented <c>WS_POPUP</c> in <c>WorkerW</c>).
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class NativeSizingOriginResolverTests
{
    [Fact]
    public void Resolve_TopLevelWindow_SameCoordinateSpace_ReturnsZeroOffset()
    {
        var windowRectScreen = new NativeRectPx(100, 200, 500, 500);
        var firstSizingRect = new NativeRectPx(100, 200, 500, 500);

        var offset = NativeSizingOriginResolver.Resolve(windowRectScreen, firstSizingRect);

        Assert.Equal(new NativeVectorPx(0, 0), offset);
    }

    [Fact]
    public void Resolve_ChildWorkerWWindow_ParentRelativeCoordinates_ReturnsNonZeroOffset()
    {
        // Panel is SetParent-ed into WorkerW whose own screen origin is (50, 50); a parent-relative
        // WM_SIZING rect for the same physical location reports 50px less on each axis than
        // GetWindowRect (which is always screen-space).
        var windowRectScreen = new NativeRectPx(100, 200, 500, 500);
        var firstSizingRect = new NativeRectPx(50, 150, 450, 450);

        var offset = NativeSizingOriginResolver.Resolve(windowRectScreen, firstSizingRect);

        Assert.Equal(new NativeVectorPx(50, 50), offset);
    }

    [Fact]
    public void Resolve_CalledAgainWithDifferentInputs_RecalculatesIndependently()
    {
        // Simulates Explorer restart / re-attach between two gestures: the resolver is a pure function
        // of its two arguments, so a second call with a different WorkerW origin must not be influenced
        // by any previous call.
        var first = NativeSizingOriginResolver.Resolve(
            new NativeRectPx(100, 200, 500, 500),
            new NativeRectPx(50, 150, 450, 450));
        Assert.Equal(new NativeVectorPx(50, 50), first);

        var second = NativeSizingOriginResolver.Resolve(
            new NativeRectPx(100, 200, 500, 500),
            new NativeRectPx(0, 0, 400, 300));
        Assert.Equal(new NativeVectorPx(100, 200), second);
    }
}
