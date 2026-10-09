using System.Windows.Threading;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Views;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Views;

/// <summary>
/// Unit tests for <see cref="DesktopZOrderDiagnostics"/> — the once-per-session app-log entries of the fence
/// panels' Z-order keeper (ADR-audit review cycle 1, G2): nothing is written from inside the
/// <c>WM_WINDOWPOSCHANGING</c> hook, and each failure kind appears once per session.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.UI))]
public class DesktopZOrderDiagnosticsTests
{
    private readonly Mock<IAppLog> _log = new ();

    /// <summary>A failure kind is written once, however often it repeats; other kinds keep their own budget.</summary>
    [Fact]
    public void WarnOnce_WritesEachEventOncePerSession()
    {
        var sut = new DesktopZOrderDiagnostics(() => _log.Object);

        sut.WarnOnce("ZOrderReassertFailed", new InvalidOperationException("a"));
        sut.WarnOnce("ZOrderReassertFailed", new InvalidOperationException("b"));
        sut.WarnOnce("ZOrderHookRemoveFailed", new ObjectDisposedException("c"));

        _log.Verify(
            l => l.Warn(
                DesktopZOrderDiagnostics.Source,
                "ZOrderReassertFailed",
                It.Is<KeyValuePair<string, object?>[]>(d => d.Any(kv => kv.Key == "errorType" && Equals(kv.Value, nameof(InvalidOperationException))))),
            Times.Once);
        _log.Verify(l => l.Warn(DesktopZOrderDiagnostics.Source, "ZOrderHookRemoveFailed", It.IsAny<KeyValuePair<string, object?>[]>()), Times.Once);
    }

    /// <summary>
    /// A failure inside the window procedure writes nothing synchronously; the entry appears only after the
    /// dispatcher has run its queue, and only once however many messages failed.
    /// </summary>
    [Fact]
    public void DeferWindowProcFailure_WritesNothingSynchronously_ThenOnceAfterTheDispatcherRuns()
    {
        StaTestRunner.Run(() =>
        {
            var sut = new DesktopZOrderDiagnostics(() => _log.Object);
            var dispatcher = Dispatcher.CurrentDispatcher;

            for (var i = 0; i < 10; i++) sut.DeferWindowProcFailure(dispatcher, new InvalidOperationException("boom"));

            _log.Verify(l => l.Warn(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<KeyValuePair<string, object?>[]>()), Times.Never);

            FlushDispatcherQueue(dispatcher);
            sut.DeferWindowProcFailure(dispatcher, new InvalidOperationException("later"));
            FlushDispatcherQueue(dispatcher);

            _log.Verify(
                l => l.Warn(DesktopZOrderDiagnostics.Source, DesktopZOrderDiagnostics.WindowProcFailedEvent, It.IsAny<KeyValuePair<string, object?>[]>()),
                Times.Once);
        });
    }

    /// <summary>A log that cannot even be resolved never surfaces.</summary>
    [Fact]
    public void WarnOnce_LogResolutionThrows_IsSwallowed()
    {
        var sut = new DesktopZOrderDiagnostics(() => throw new InvalidOperationException("no log"));

        var exception = Record.Exception(() => sut.WarnOnce("ZOrderReassertFailed", null));

        Assert.Null(exception);
    }

    private static void FlushDispatcherQueue(Dispatcher dispatcher)
    {
        var frame = new DispatcherFrame();
        dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
