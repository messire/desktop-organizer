using DesktopOrganizer.Services.Abstractions;
using Moq;

namespace DesktopOrganizer.Tests.Helpers;

/// <summary>
/// Builds a stub <see cref="IDesktopScaleProvider"/> for the many pre-F3 tests that construct a
/// <c>ChildWindow</c>/behavior/<c>WindowManager</c> directly and never exercised DPI scaling before
/// release-hardening F (ADR-037) threaded the provider through their constructors. Every one of them
/// returns a UNIFORM, scale-<c>1.0</c>, <see cref="DesktopLayerMode.Embedded"/> snapshot — under which
/// every <see cref="DesktopScaleModel"/> conversion the new adapter code performs
/// (<c>PanelWindowCoordinates</c>/<c>DragPositionCalculator</c>/<c>TopLevelPlacement</c>) is byte-for-byte
/// identity, so these pre-existing tests keep asserting the exact same numbers they did before F3.
/// </summary>
public static class FakeDesktopScale
{
    /// <summary>
    /// Builds a mocked <see cref="IDesktopScaleProvider"/> whose <see cref="IDesktopScaleProvider.Current"/>
    /// is a uniform, <see cref="DesktopLayerMode.Embedded"/> snapshot at <paramref name="scale"/> (default
    /// <c>1.0</c> — 100%, no scaling).
    /// </summary>
    /// <param name="scale">The single px/DIP scale every conversion uses (default <c>1.0</c>).</param>
    /// <returns>The mock; its <c>.Object</c> is what production constructors expect.</returns>
    public static Mock<IDesktopScaleProvider> Uniform(double scale = 1d)
    {
        var model = DesktopScaleModel.Create(DesktopLayerMode.Embedded, scale, scale, []);
        var mock = new Mock<IDesktopScaleProvider>();
        mock.SetupGet(p => p.Current).Returns(model);
        mock.SetupGet(p => p.Mode).Returns(model.Mode);
        return mock;
    }
}
