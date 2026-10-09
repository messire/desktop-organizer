using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Diagnostics;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Diagnostics;

/// <summary>
/// Unit tests for <see cref="AppLogEventThrottle"/> — the per-event, per-session cap on repeated app-log entries
/// (ADR-audit review cycle 1, G2/G4).
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class AppLogEventThrottleTests
{
    private const string Source = "TestSource";

    private readonly Mock<IAppLog> _log = new ();

    /// <summary>The first <c>limit</c> occurrences are written, the next one is replaced by one suppression notice, the rest are silent.</summary>
    [Fact]
    public void Error_WritesFirstK_ThenOneSuppressionNotice_ThenNothing()
    {
        var throttle = new AppLogEventThrottle(limitPerEvent: 5);
        var ex = new InvalidOperationException("boom");

        for (var i = 0; i < 20; i++) throttle.Error(_log.Object, Source, "Evt", ex);

        _log.Verify(l => l.Error(Source, "Evt", ex, It.IsAny<KeyValuePair<string, object?>[]>()), Times.Exactly(5));
        _log.Verify(
            l => l.Warn(
                Source,
                AppLogEventThrottle.SuppressedEvent,
                It.Is<KeyValuePair<string, object?>[]>(d =>
                    d.Any(kv => kv.Key == "event" && Equals(kv.Value, "Evt")) &&
                    d.Any(kv => kv.Key == "limit" && Equals(kv.Value, 5)))),
            Times.Once);
        Assert.Equal(15, throttle.SuppressedCount("Evt"));
    }

    /// <summary>Every event name has its own budget.</summary>
    [Fact]
    public void Warn_BudgetIsPerEventName()
    {
        var throttle = new AppLogEventThrottle(limitPerEvent: 1, announceSuppression: false);

        throttle.Warn(_log.Object, Source, "A");
        throttle.Warn(_log.Object, Source, "A");
        throttle.Warn(_log.Object, Source, "B");

        _log.Verify(l => l.Warn(Source, "A", It.IsAny<KeyValuePair<string, object?>[]>()), Times.Once);
        _log.Verify(l => l.Warn(Source, "B", It.IsAny<KeyValuePair<string, object?>[]>()), Times.Once);
        _log.Verify(l => l.Warn(Source, AppLogEventThrottle.SuppressedEvent, It.IsAny<KeyValuePair<string, object?>[]>()), Times.Never);
    }

    /// <summary>A missing log consumes no budget, so a later real log still gets the first entries.</summary>
    [Fact]
    public void NullLog_DoesNotConsumeBudget()
    {
        var throttle = new AppLogEventThrottle(limitPerEvent: 1, announceSuppression: false);

        throttle.Warn(null, Source, "A");
        throttle.Warn(_log.Object, Source, "A");

        _log.Verify(l => l.Warn(Source, "A", It.IsAny<KeyValuePair<string, object?>[]>()), Times.Once);
    }

    /// <summary>A throwing log never escapes the throttle.</summary>
    [Fact]
    public void ThrowingLog_IsSwallowed()
    {
        var throttle = new AppLogEventThrottle(limitPerEvent: 2);
        _log.Setup(l => l.Error(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Exception?>(), It.IsAny<KeyValuePair<string, object?>[]>()))
            .Throws(new InvalidOperationException("sink"));

        var exception = Record.Exception(() => throttle.Error(_log.Object, Source, "Evt", null));

        Assert.Null(exception);
    }

    /// <summary>The payload is passed through unchanged.</summary>
    [Fact]
    public void Warn_PassesPayloadThrough()
    {
        var throttle = new AppLogEventThrottle(limitPerEvent: 1);
        var field = new KeyValuePair<string, object?>("lane", "Local");

        throttle.Warn(_log.Object, Source, "Evt", field);

        _log.Verify(l => l.Warn(Source, "Evt", new[] { field }), Times.Once);
    }

    /// <summary>A non-positive limit is rejected.</summary>
    [Fact]
    public void Constructor_RejectsNonPositiveLimit()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new AppLogEventThrottle(limitPerEvent: 0));
}
