using System.Windows;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Diagnostics;
using DesktopOrganizer.UI.Models;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Diagnostics;

/// <summary>
/// Unit tests for <see cref="GestureDiagnostics"/> — the UI-facing facade over
/// <see cref="IDiagnosticsLog"/>. <see cref="IDiagnosticsLog"/> is mocked throughout, and the
/// monotonic clock is injected via the constructor's <c>nowTicks</c> parameter so throttling can be
/// driven deterministically without any real timing.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class GestureDiagnosticsTests
{
    private const int IntervalMs = 50;

    private readonly Mock<IDiagnosticsLog> _logMock = new();
    private readonly List<RecordedWrite> _writes = [];
    private long _clock;

    /// <summary>
    /// Initializes a new instance of the <see cref="GestureDiagnosticsTests"/> class, wiring the mocked
    /// <see cref="IDiagnosticsLog"/> to record every <c>Write</c> call into <see cref="_writes"/> for
    /// later assertion.
    /// </summary>
    public GestureDiagnosticsTests()
    {
        _logMock.SetupGet(l => l.IsEnabled).Returns(true);
        _logMock
            .Setup(l => l.Write(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<KeyValuePair<string, object?>[]>()))
            .Callback<string, string, string?, KeyValuePair<string, object?>[]>(
                (category, name, scope, payload) => _writes.Add(new RecordedWrite(category, name, scope, payload)));
    }

    // ── Delta throttling: first delta always emitted ────────────────────────

    /// <summary>
    /// Verifies that the very first <c>ResizeDelta</c> of a gesture is always emitted, even when it
    /// arrives well inside the throttle window.
    /// </summary>
    [Fact]
    public void ResizeDelta_FirstDeltaOfGesture_IsAlwaysEmitted()
    {
        var sut = BuildEnabled();
        var panelId = Guid.NewGuid();
        sut.ResizeStarted(panelId, "Right", MakeRect(0, 0, 100, 100), PanelViewMode.Grid, 32, true, 8, 8, "Thumb");

        _clock = 5; // well inside the throttle window, but it's the very first delta
        sut.ResizeDelta(panelId, "Right", 5, 0, 105, 100, MakeRect(0, 0, 105, 100));

        Assert.Single(_writes, w => w.Name == "ResizeDelta");
    }

    /// <summary>
    /// Verifies that the very first <c>DragDelta</c> of a gesture is always emitted, even when it
    /// arrives well inside the throttle window.
    /// </summary>
    [Fact]
    public void DragDelta_FirstDeltaOfGesture_IsAlwaysEmitted()
    {
        var sut = BuildEnabled();
        var panelId = Guid.NewGuid();
        sut.DragStarted(panelId, new Point(0, 0), MakeRect(0, 0, 100, 100));

        _clock = 5;
        sut.DragDelta(panelId, MakeRect(5, 0, 100, 100));

        Assert.Single(_writes, w => w.Name == "DragDelta");
    }

    // ── Delta throttling: suppressed within the window ──────────────────────

    /// <summary>
    /// Verifies that a second <c>ResizeDelta</c> arriving within the throttle window after the first is
    /// suppressed (not emitted).
    /// </summary>
    [Fact]
    public void ResizeDelta_WithinThrottleWindow_IsSuppressed()
    {
        var sut = BuildEnabled();
        var panelId = Guid.NewGuid();
        sut.ResizeStarted(panelId, "Right", MakeRect(0, 0, 100, 100), PanelViewMode.Grid, 32, true, 8, 8, "Thumb");

        _clock = 0;
        sut.ResizeDelta(panelId, "Right", 1, 0, 101, 100, MakeRect(0, 0, 101, 100)); // emitted (first)
        _clock = IntervalMs - 1;
        sut.ResizeDelta(panelId, "Right", 2, 0, 102, 100, MakeRect(0, 0, 102, 100)); // suppressed

        Assert.Equal(1, _writes.Count(w => w.Name == "ResizeDelta"));
    }

    /// <summary>
    /// Verifies that a second <c>DragDelta</c> arriving within the throttle window after the first is
    /// suppressed (not emitted).
    /// </summary>
    [Fact]
    public void DragDelta_WithinThrottleWindow_IsSuppressed()
    {
        var sut = BuildEnabled();
        var panelId = Guid.NewGuid();
        sut.DragStarted(panelId, new Point(0, 0), MakeRect(0, 0, 100, 100));

        _clock = 0;
        sut.DragDelta(panelId, MakeRect(1, 0, 100, 100)); // emitted (first)
        _clock = IntervalMs - 1;
        sut.DragDelta(panelId, MakeRect(2, 0, 100, 100)); // suppressed

        Assert.Equal(1, _writes.Count(w => w.Name == "DragDelta"));
    }

    // ── Delta throttling: emission resumes once the interval elapses ────────

    /// <summary>
    /// Verifies that <c>ResizeDelta</c> emission resumes once the throttle interval has fully elapsed
    /// since the last emitted delta.
    /// </summary>
    [Fact]
    public void ResizeDelta_AfterIntervalElapses_EmitsAgain()
    {
        var sut = BuildEnabled();
        var panelId = Guid.NewGuid();
        sut.ResizeStarted(panelId, "Right", MakeRect(0, 0, 100, 100), PanelViewMode.Grid, 32, true, 8, 8, "Thumb");

        _clock = 0;
        sut.ResizeDelta(panelId, "Right", 1, 0, 101, 100, MakeRect(0, 0, 101, 100)); // emitted (first)
        _clock = 10;
        sut.ResizeDelta(panelId, "Right", 2, 0, 102, 100, MakeRect(0, 0, 102, 100)); // suppressed
        _clock = IntervalMs; // exactly the interval since the last emitted delta (ticks 0)
        sut.ResizeDelta(panelId, "Right", 3, 0, 103, 100, MakeRect(0, 0, 103, 100)); // emitted again

        Assert.Equal(2, _writes.Count(w => w.Name == "ResizeDelta"));
    }

    /// <summary>
    /// Verifies that <c>DragDelta</c> emission resumes once the throttle interval has fully elapsed
    /// since the last emitted delta.
    /// </summary>
    [Fact]
    public void DragDelta_AfterIntervalElapses_EmitsAgain()
    {
        var sut = BuildEnabled();
        var panelId = Guid.NewGuid();
        sut.DragStarted(panelId, new Point(0, 0), MakeRect(0, 0, 100, 100));

        _clock = 0;
        sut.DragDelta(panelId, MakeRect(1, 0, 100, 100)); // emitted (first)
        _clock = 10;
        sut.DragDelta(panelId, MakeRect(2, 0, 100, 100)); // suppressed
        _clock = IntervalMs;
        sut.DragDelta(panelId, MakeRect(3, 0, 100, 100)); // emitted again

        Assert.Equal(2, _writes.Count(w => w.Name == "DragDelta"));
    }

    // ── Completed payload carries the last (possibly throttled) delta ───────

    /// <summary>
    /// Verifies that the <c>ResizeCompleted</c> payload's <c>lastRawWidth</c>/<c>lastRawHeight</c>
    /// reflect the most recent delta even when that delta itself was throttled and never emitted.
    /// </summary>
    [Fact]
    public void ResizeCompleted_Payload_CarriesLastRawSizeEvenWhenThrottled()
    {
        var sut = BuildEnabled();
        var panelId = Guid.NewGuid();
        sut.ResizeStarted(panelId, "Right", MakeRect(0, 0, 100, 100), PanelViewMode.Grid, 32, true, 8, 8, "Thumb");

        _clock = 0;
        sut.ResizeDelta(panelId, "Right", 1, 0, 101, 100, MakeRect(0, 0, 101, 100)); // emitted
        _clock = 10;
        sut.ResizeDelta(panelId, "Right", 4, 0, 999, 100, MakeRect(0, 0, 999, 100)); // suppressed, but state updated

        _clock = 20;
        sut.ResizeCompleted(panelId, "Right", canceled: false, MakeRect(0, 0, 999, 100), endReason: "Completed", exceptionType: null);

        var completed = Assert.Single(_writes, w => w.Name == "ResizeCompleted");
        Assert.Equal(999.0, GetPayloadValue<double>(completed, "lastRawWidth"));
        Assert.Equal(100.0, GetPayloadValue<double>(completed, "lastRawHeight"));
    }

    /// <summary>
    /// Verifies that the <c>DragCompleted</c> payload's <c>lastAppliedX</c> reflects the most recent
    /// delta even when that delta itself was throttled and never emitted.
    /// </summary>
    [Fact]
    public void DragCompleted_Payload_CarriesLastAppliedRectEvenWhenThrottled()
    {
        var sut = BuildEnabled();
        var panelId = Guid.NewGuid();
        sut.DragStarted(panelId, new Point(0, 0), MakeRect(0, 0, 100, 100));

        _clock = 0;
        sut.DragDelta(panelId, MakeRect(1, 0, 100, 100)); // emitted
        _clock = 10;
        sut.DragDelta(panelId, MakeRect(777, 0, 100, 100)); // suppressed, but state updated

        _clock = 20;
        sut.DragCompleted(panelId, MakeRect(777, 0, 100, 100));

        var completed = Assert.Single(_writes, w => w.Name == "DragCompleted");
        Assert.Equal(777.0, GetPayloadValue<double>(completed, "lastAppliedX"));
    }

    // ── deltaCount / emittedCount / durationMs ───────────────────────────────

    /// <summary>
    /// Verifies that the <c>ResizeCompleted</c> payload's <c>deltaCount</c>, <c>emittedCount</c>, and
    /// <c>durationMs</c> correctly reflect a gesture with a mix of emitted and throttled deltas.
    /// </summary>
    [Fact]
    public void ResizeCompleted_Payload_HasCorrectDeltaCountEmittedCountAndDurationMs()
    {
        var sut = BuildEnabled();
        var panelId = Guid.NewGuid();

        _clock = 1000;
        sut.ResizeStarted(panelId, "Right", MakeRect(0, 0, 100, 100), PanelViewMode.Grid, 32, true, 8, 8, "Thumb");

        _clock = 1000;
        sut.ResizeDelta(panelId, "Right", 1, 0, 101, 100, MakeRect(0, 0, 101, 100)); // emitted #1 (first)
        _clock = 1010;
        sut.ResizeDelta(panelId, "Right", 1, 0, 102, 100, MakeRect(0, 0, 102, 100)); // suppressed (10ms < 50ms)
        _clock = 1080;
        sut.ResizeDelta(panelId, "Right", 1, 0, 103, 100, MakeRect(0, 0, 103, 100)); // emitted #2 (80ms since last emit)

        _clock = 1500;
        sut.ResizeCompleted(panelId, "Right", canceled: false, MakeRect(0, 0, 103, 100), endReason: "Completed", exceptionType: null);

        var completed = Assert.Single(_writes, w => w.Name == "ResizeCompleted");
        Assert.Equal(3, GetPayloadValue<int>(completed, "deltaCount"));
        Assert.Equal(2, GetPayloadValue<int>(completed, "emittedCount"));
        Assert.Equal(500L, GetPayloadValue<long>(completed, "durationMs"));
    }

    /// <summary>
    /// Verifies that the <c>DragCompleted</c> payload's <c>deltaCount</c>, <c>emittedCount</c>, and
    /// <c>durationMs</c> correctly reflect a gesture with a mix of emitted and throttled deltas.
    /// </summary>
    [Fact]
    public void DragCompleted_Payload_HasCorrectDeltaCountEmittedCountAndDurationMs()
    {
        var sut = BuildEnabled();
        var panelId = Guid.NewGuid();

        _clock = 2000;
        sut.DragStarted(panelId, new Point(0, 0), MakeRect(0, 0, 100, 100));

        _clock = 2000;
        sut.DragDelta(panelId, MakeRect(1, 0, 100, 100)); // emitted #1
        _clock = 2010;
        sut.DragDelta(panelId, MakeRect(2, 0, 100, 100)); // suppressed
        _clock = 2010 + IntervalMs;
        sut.DragDelta(panelId, MakeRect(3, 0, 100, 100)); // emitted #2

        _clock = 2300;
        sut.DragCompleted(panelId, MakeRect(3, 0, 100, 100));

        var completed = Assert.Single(_writes, w => w.Name == "DragCompleted");
        Assert.Equal(3, GetPayloadValue<int>(completed, "deltaCount"));
        Assert.Equal(2, GetPayloadValue<int>(completed, "emittedCount"));
        Assert.Equal(300L, GetPayloadValue<long>(completed, "durationMs"));
    }

    /// <summary>
    /// Verifies that starting a second resize gesture on the same <see cref="GestureDiagnostics"/>
    /// instance resets the delta/emitted counters rather than carrying over the first gesture's counts.
    /// </summary>
    [Fact]
    public void ResizeStarted_ResetsCountersForANewGesture()
    {
        // A second gesture on the same GestureDiagnostics instance (same ChildWindow — transient
        // per-panel lifetime, see the type remarks) must not carry over counters from the first.
        var sut = BuildEnabled();
        var panelId = Guid.NewGuid();

        _clock = 0;
        sut.ResizeStarted(panelId, "Right", MakeRect(0, 0, 100, 100), PanelViewMode.Grid, 32, true, 8, 8, "Thumb");
        sut.ResizeDelta(panelId, "Right", 1, 0, 101, 100, MakeRect(0, 0, 101, 100));
        sut.ResizeCompleted(panelId, "Right", canceled: false, MakeRect(0, 0, 101, 100), endReason: "Completed", exceptionType: null);

        _clock = 100;
        sut.ResizeStarted(panelId, "Right", MakeRect(0, 0, 200, 200), PanelViewMode.Grid, 32, true, 8, 8, "Thumb");
        _clock = 150;
        sut.ResizeCompleted(panelId, "Right", canceled: false, MakeRect(0, 0, 200, 200), endReason: "Completed", exceptionType: null);

        var secondCompleted = _writes.Where(w => w.Name == "ResizeCompleted").ElementAt(1);
        Assert.Equal(0, GetPayloadValue<int>(secondCompleted, "deltaCount"));
        Assert.Equal(0, GetPayloadValue<int>(secondCompleted, "emittedCount"));
    }

    // ── durationMs sentinel when Completed is observed without a matching Started (MINOR-6) ────

    /// <summary>
    /// Verifies that <c>ResizeCompleted</c> reports the <c>-1</c> sentinel <c>durationMs</c> when no
    /// matching <c>ResizeStarted</c> was observed first, rather than leaking a large uptime-like value
    /// (MINOR-6).
    /// </summary>
    [Fact]
    public void ResizeCompleted_WithoutPrecedingResizeStarted_ReportsSentinelDurationMs()
    {
        var sut = BuildEnabled();
        var panelId = Guid.NewGuid();

        _clock = 999_999; // large uptime-like value the old (non-sentinel) code would have leaked
        sut.ResizeCompleted(panelId, "Right", canceled: false, MakeRect(0, 0, 100, 100), endReason: "Completed", exceptionType: null);

        var completed = Assert.Single(_writes, w => w.Name == "ResizeCompleted");
        Assert.Equal(-1L, GetPayloadValue<long>(completed, "durationMs"));
    }

    /// <summary>
    /// Verifies that <c>DragCompleted</c> reports the <c>-1</c> sentinel <c>durationMs</c> when no
    /// matching <c>DragStarted</c> was observed first, rather than leaking a large uptime-like value
    /// (MINOR-6).
    /// </summary>
    [Fact]
    public void DragCompleted_WithoutPrecedingDragStarted_ReportsSentinelDurationMs()
    {
        var sut = BuildEnabled();
        var panelId = Guid.NewGuid();

        _clock = 999_999;
        sut.DragCompleted(panelId, MakeRect(0, 0, 100, 100));

        var completed = Assert.Single(_writes, w => w.Name == "DragCompleted");
        Assert.Equal(-1L, GetPayloadValue<long>(completed, "durationMs"));
    }

    // ── Constructor validation (MINOR-5) ─────────────────────────────────────

    /// <summary>
    /// Verifies that the <see cref="GestureDiagnostics"/> constructor throws
    /// <see cref="ArgumentNullException"/> for a <see langword="null"/> log (MINOR-5).
    /// </summary>
    [Fact]
    public void Constructor_NullLog_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new GestureDiagnostics(null!));
    }

    /// <summary>
    /// Verifies that the <see cref="GestureDiagnostics"/> constructor throws
    /// <see cref="ArgumentOutOfRangeException"/> for a negative delta sample interval (MINOR-5).
    /// </summary>
    [Fact]
    public void Constructor_NegativeDeltaSampleIntervalMs_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GestureDiagnostics(_logMock.Object, -1));
    }

    // ── Zero cost when disabled ──────────────────────────────────────────────

    /// <summary>
    /// Verifies that with logging disabled, every <see cref="GestureDiagnostics"/> method only ever
    /// checks <see cref="IDiagnosticsLog.IsEnabled"/> and never calls <c>Write</c>, <c>Flush</c>, or
    /// <c>Dispose</c> on the sink.
    /// </summary>
    [Fact]
    public void AllMethods_WhenDisabled_NeverCallWriteOrFlushOrDispose()
    {
        _logMock.SetupGet(l => l.IsEnabled).Returns(false);
        var sut = new GestureDiagnostics(_logMock.Object, IntervalMs, () => _clock);
        var panelId = Guid.NewGuid();

        sut.SessionStarted("1.0.0", "Windows 11", 1.5, 2, 3);
        sut.ResizeStarted(panelId, "Right", MakeRect(0, 0, 100, 100), PanelViewMode.Grid, 32, true, 8, 8, "Thumb");
        sut.ResizeDelta(panelId, "Right", 1, 0, 101, 100, MakeRect(0, 0, 101, 100));
        sut.ResizeCompleted(panelId, "Right", canceled: false, MakeRect(0, 0, 101, 100), endReason: "Completed", exceptionType: null);
        sut.DragStarted(panelId, new Point(0, 0), MakeRect(0, 0, 100, 100));
        sut.DragDelta(panelId, MakeRect(1, 0, 100, 100));
        sut.DragCompleted(panelId, MakeRect(1, 0, 100, 100));
        sut.CaptureGot(panelId, "Thumb", "Thumb");
        sut.CaptureLost(panelId, "Thumb", null, thumbIsDragging: false);
        sut.PanelClosing(panelId, thumbIsDragging: false);
        sut.SizeMoveEntered(panelId, 1, "Right");
        sut.SizeMoveExited(panelId, 1, MakeRect(0, 0, 1, 1));
        sut.SessionExiting();

        Assert.Empty(_writes);

        // The only member ever touched on the mock is the IsEnabled getter (checked once per method
        // above, before any state is read/allocated) — proves the zero-cost seam actually holds and
        // nothing else (Write/Flush/Dispose) was ever invoked on the sink.
        _logMock.VerifyGet(l => l.IsEnabled, Times.AtLeast(13));
        _logMock.VerifyNoOtherCalls();
    }

    // ── Payload shape / privacy invariants ──────────────────────────────────

    /// <summary>
    /// Verifies that the <c>ResizeStarted</c> payload contains exactly the expected set of keys, no
    /// more and no fewer.
    /// </summary>
    [Fact]
    public void ResizeStarted_Payload_HasExactlyTheExpectedKeys()
    {
        var sut = BuildEnabled();
        var panelId = Guid.NewGuid();

        sut.ResizeStarted(panelId, "Right", MakeRect(1, 2, 3, 4), PanelViewMode.Grid, 32, true, 8, 8, "Thumb");

        var write = Assert.Single(_writes, w => w.Name == "ResizeStarted");
        Assert.Equal(
            new HashSet<string>
            {
                "direction", "boundsX", "boundsY", "boundsWidth", "boundsHeight", "viewMode",
                "iconSize", "snapEnabled", "snapCellWidth", "snapCellHeight", "captureOwner",
            },
            write.Payload.Select(p => p.Key).ToHashSet());
    }

    /// <summary>
    /// Verifies that the <c>ResizeCompleted</c> payload contains exactly the expected set of keys, no
    /// more and no fewer.
    /// </summary>
    [Fact]
    public void ResizeCompleted_Payload_HasExactlyTheExpectedKeys()
    {
        var sut = BuildEnabled();
        var panelId = Guid.NewGuid();
        sut.ResizeStarted(panelId, "Right", MakeRect(0, 0, 100, 100), PanelViewMode.Grid, 32, true, 8, 8, "Thumb");

        sut.ResizeCompleted(panelId, "Right", canceled: false, MakeRect(0, 0, 100, 100), endReason: "Completed", exceptionType: null);

        var write = Assert.Single(_writes, w => w.Name == "ResizeCompleted");
        Assert.Equal(
            new HashSet<string>
            {
                "direction", "canceled", "boundsX", "boundsY", "boundsWidth", "boundsHeight",
                "lastRawWidth", "lastRawHeight", "lastAppliedX", "lastAppliedY", "lastAppliedWidth",
                "lastAppliedHeight", "deltaCount", "emittedCount", "durationMs", "endReason", "exceptionType",
            },
            write.Payload.Select(p => p.Key).ToHashSet());
    }

    /// <summary>
    /// Verifies that an ordinary (non-failed) <c>ResizeCompleted</c> payload carries the <c>endReason</c>
    /// verbatim and a <see langword="null"/> <c>exceptionType</c>.
    /// </summary>
    [Fact]
    public void ResizeCompleted_Payload_CarriesEndReasonAndNullExceptionTypeForOrdinaryCompletion()
    {
        var sut = BuildEnabled();
        var panelId = Guid.NewGuid();
        sut.ResizeStarted(panelId, "Right", MakeRect(0, 0, 100, 100), PanelViewMode.Grid, 32, true, 8, 8, "Thumb");

        sut.ResizeCompleted(panelId, "Right", canceled: false, MakeRect(0, 0, 100, 100), endReason: "Completed", exceptionType: null);

        var completed = Assert.Single(_writes, w => w.Name == "ResizeCompleted");
        Assert.Equal("Completed", GetPayloadValue<string>(completed, "endReason"));
        Assert.Null(completed.Payload.Single(p => p.Key == "exceptionType").Value);
    }

    /// <summary>
    /// C8 privacy invariant: when a gesture ends with <c>Failed</c>, <c>exceptionType</c> carries only
    /// the exception's type name — never anything resembling its <c>Message</c> (which may contain a
    /// file path). This mirrors <see cref="DesktopOrganizer.UI.Behaviors.PanelResizeController"/>'s own
    /// contract (it only ever passes <c>ex.GetType().Name</c>), verified here at the diagnostics boundary.
    /// </summary>
    [Fact]
    public void ResizeCompleted_Payload_Failed_CarriesOnlyExceptionTypeName_NeverMessageLikeText()
    {
        var sut = BuildEnabled();
        var panelId = Guid.NewGuid();
        sut.ResizeStarted(panelId, "Right", MakeRect(0, 0, 100, 100), PanelViewMode.Grid, 32, true, 8, 8, "Thumb");

        var exceptionType = typeof(InvalidOperationException).Name;
        sut.ResizeCompleted(panelId, "Right", canceled: false, MakeRect(0, 0, 100, 100), endReason: "Failed", exceptionType: exceptionType);

        var completed = Assert.Single(_writes, w => w.Name == "ResizeCompleted");
        Assert.Equal("Failed", GetPayloadValue<string>(completed, "endReason"));
        Assert.Equal("InvalidOperationException", GetPayloadValue<string>(completed, "exceptionType"));
        Assert.DoesNotContain('\\', exceptionType);
        Assert.DoesNotContain(':', exceptionType);
    }

    /// <summary>
    /// P03 fix-cycle 1 final verification (MINOR): "Align to workspace grid" refusals used to go to
    /// <c>Console.Error</c>, which nobody reads in a WinExe process — same defect class as
    /// <see cref="GestureDiagnostics.ExistenceRefreshFailed"/>. Panel-scoped, payload carries only the
    /// reason code.
    /// </summary>
    [Fact]
    public void AlignToWorkspaceGridRefused_WritesPanelScopedEventWithReasonOnly()
    {
        var sut = BuildEnabled();
        var panelId = Guid.NewGuid();

        sut.AlignToWorkspaceGridRefused(panelId, "Unmeasured");

        var write = Assert.Single(_writes, w => w.Name == "AlignToWorkspaceGridRefused");
        Assert.Equal("panel", write.Category);
        Assert.Equal(8, write.Scope!.Length);
        Assert.Equal("Unmeasured", GetPayloadValue<string>(write, "reason"));
        Assert.Single(write.Payload);
    }

    /// <summary>
    /// Verifies that a panel-scoped event's <c>Scope</c> is always an opaque eight-character prefix,
    /// never the panel id's full GUID.
    /// </summary>
    [Fact]
    public void Scope_IsAlwaysAnEightCharacterPrefix_NeverTheFullGuid()
    {
        var sut = BuildEnabled();
        var panelId = Guid.NewGuid();

        sut.PanelClosing(panelId, thumbIsDragging: false);

        var write = Assert.Single(_writes, w => w.Name == "PanelClosing");
        Assert.NotNull(write.Scope);
        Assert.Equal(8, write.Scope!.Length);
        Assert.DoesNotContain('-', write.Scope);
        Assert.NotEqual(panelId.ToString(), write.Scope);
    }

    /// <summary>
    /// Verifies that session-level events (<c>SessionStarted</c>/<c>SessionExiting</c>) always have a
    /// <see langword="null"/> <c>Scope</c>, since they are not associated with a specific panel.
    /// </summary>
    [Fact]
    public void SessionLevelEvents_HaveNullScope()
    {
        var sut = BuildEnabled();

        sut.SessionStarted("1.0.0", "Windows 11", 1.0, 1, 0);
        sut.SessionExiting();

        Assert.All(_writes, w => Assert.Null(w.Scope));
    }

    /// <summary>
    /// Privacy sweep across a full gesture lifecycle: no payload key or string value may look like a
    /// panel title or file-system path (docs/Refactoring/ §3.3 privacy invariant, mirrored from
    /// <see cref="DiagnosticEvent"/>'s remarks). This exercises real behavior across every method on
    /// the interface rather than special-casing one event.
    /// </summary>
    [Fact]
    public void Payload_AcrossFullLifecycle_NeverContainsPathOrTitleLikeFieldsOrValues()
    {
        var sut = BuildEnabled();
        var panelId = Guid.NewGuid();
        var bounds = MakeRect(0, 0, 100, 100);

        sut.SessionStarted("1.0.0", "Windows 11", 1.5, 2, 3);
        sut.ResizeStarted(panelId, "BottomRight", bounds, PanelViewMode.Grid, 32, true, 8, 8, "Thumb");
        sut.ResizeDelta(panelId, "BottomRight", 1, 1, 101, 101, bounds);
        sut.ResizeCompleted(panelId, "BottomRight", canceled: false, bounds, endReason: "Completed", exceptionType: null);
        sut.DragStarted(panelId, new Point(10, 10), bounds);
        sut.DragDelta(panelId, bounds);
        sut.DragCompleted(panelId, bounds);
        sut.CaptureGot(panelId, "Thumb", "Thumb");
        sut.CaptureLost(panelId, "Thumb", null, thumbIsDragging: false);
        sut.PanelClosing(panelId, thumbIsDragging: false);
        sut.SizeMoveEntered(panelId, 0x1234, "BottomRight");
        sut.SizeMoveExited(panelId, 0x1234, bounds);
        sut.SessionExiting();

        Assert.NotEmpty(_writes);
        foreach (var write in _writes)
        {
            if (write.Scope is not null)
                Assert.True(write.Scope.Length <= 8, $"Scope '{write.Scope}' for {write.Name} must be an opaque short prefix, never a full identifier.");

            foreach (var (key, value) in write.Payload)
            {
                Assert.DoesNotContain("path", key, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("title", key, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("filename", key, StringComparison.OrdinalIgnoreCase);

                if (value is string s)
                {
                    Assert.DoesNotContain('\\', s);
                    Assert.DoesNotContain(':', s);
                }
            }
        }
    }

    // ── SizeMoveEntered / SizeMoveExited (Phase 2 commit C4, native WM_SIZING loop) ──────────────────
    // The raw wire-level WM_ENTERSIZEMOVE/WM_EXITSIZEMOVE signals — distinct from, and independent of,
    // ResizeStarted/ResizeCompleted above (docs/PLANS/phase-2-native-panel-sizing.md §1.7).

    /// <summary>
    /// Verifies that the <c>SizeMoveEntered</c> payload contains exactly the expected set of keys, no
    /// more and no fewer.
    /// </summary>
    [Fact]
    public void SizeMoveEntered_Payload_HasExactlyTheExpectedKeys()
    {
        var sut = BuildEnabled();
        var panelId = Guid.NewGuid();

        sut.SizeMoveEntered(panelId, 0x1234, "Right");

        var write = Assert.Single(_writes, w => w.Name == "SizeMoveEntered");
        Assert.Equal(
            new HashSet<string> { "hwnd", "edge" },
            write.Payload.Select(p => p.Key).ToHashSet());
    }

    /// <summary>
    /// Verifies that the <c>SizeMoveEntered</c> payload carries the raw <c>hwnd</c> and <c>edge</c>
    /// values verbatim.
    /// </summary>
    [Fact]
    public void SizeMoveEntered_Payload_CarriesHwndAndEdgeVerbatim()
    {
        var sut = BuildEnabled();
        var panelId = Guid.NewGuid();

        sut.SizeMoveEntered(panelId, 0x1234, "BottomRight");

        var write = Assert.Single(_writes, w => w.Name == "SizeMoveEntered");
        Assert.Equal(0x1234L, GetPayloadValue<long>(write, "hwnd"));
        Assert.Equal("BottomRight", GetPayloadValue<string>(write, "edge"));
    }

    /// <summary>
    /// Verifies that the <c>SizeMoveExited</c> payload contains exactly the expected set of keys, no
    /// more and no fewer.
    /// </summary>
    [Fact]
    public void SizeMoveExited_Payload_HasExactlyTheExpectedKeys()
    {
        var sut = BuildEnabled();
        var panelId = Guid.NewGuid();

        sut.SizeMoveExited(panelId, 0x1234, MakeRect(1, 2, 3, 4));

        var write = Assert.Single(_writes, w => w.Name == "SizeMoveExited");
        Assert.Equal(
            new HashSet<string> { "hwnd", "finalX", "finalY", "finalWidth", "finalHeight" },
            write.Payload.Select(p => p.Key).ToHashSet());
    }

    /// <summary>
    /// Verifies that the <c>SizeMoveExited</c> payload carries the final bounds verbatim.
    /// </summary>
    [Fact]
    public void SizeMoveExited_Payload_CarriesFinalBoundsVerbatim()
    {
        var sut = BuildEnabled();
        var panelId = Guid.NewGuid();

        sut.SizeMoveExited(panelId, 0x1234, MakeRect(10, 20, 300, 400));

        var write = Assert.Single(_writes, w => w.Name == "SizeMoveExited");
        Assert.Equal(10.0, GetPayloadValue<double>(write, "finalX"));
        Assert.Equal(20.0, GetPayloadValue<double>(write, "finalY"));
        Assert.Equal(300.0, GetPayloadValue<double>(write, "finalWidth"));
        Assert.Equal(400.0, GetPayloadValue<double>(write, "finalHeight"));
    }

    /// <summary>
    /// Verifies that <c>SizeMoveEntered</c>/<c>SizeMoveExited</c> events' <c>Scope</c> is always an
    /// opaque eight-character prefix, never the panel id's full GUID.
    /// </summary>
    [Fact]
    public void SizeMoveEnteredAndExited_Scope_IsAlwaysAnEightCharacterPrefix_NeverTheFullGuid()
    {
        var sut = BuildEnabled();
        var panelId = Guid.NewGuid();

        sut.SizeMoveEntered(panelId, 1, "Left");
        sut.SizeMoveExited(panelId, 1, MakeRect(0, 0, 1, 1));

        foreach (var write in _writes)
        {
            Assert.NotNull(write.Scope);
            Assert.Equal(8, write.Scope!.Length);
            Assert.DoesNotContain('-', write.Scope);
            Assert.NotEqual(panelId.ToString(), write.Scope);
        }
    }

    /// <summary>
    /// Verifies that with logging disabled, <c>SizeMoveEntered</c>/<c>SizeMoveExited</c> never call
    /// <c>Write</c> on the sink.
    /// </summary>
    [Fact]
    public void SizeMoveEnteredAndExited_WhenDisabled_NeverCallWrite()
    {
        _logMock.SetupGet(l => l.IsEnabled).Returns(false);
        var sut = new GestureDiagnostics(_logMock.Object, IntervalMs, () => _clock);
        var panelId = Guid.NewGuid();

        sut.SizeMoveEntered(panelId, 1, "Left");
        sut.SizeMoveExited(panelId, 1, MakeRect(0, 0, 1, 1));

        Assert.Empty(_writes);
    }

    /// <summary>
    /// Privacy invariant (diagnostics-log-events.md, plan §1.7): <c>edge</c> is always one of the closed
    /// set of <see cref="NativeSizingEdge"/> names — never free text — and <c>hwnd</c> is a plain
    /// integer, never a formatted pointer string that could look path-like.
    /// </summary>
    /// <param name="edge">The <see cref="NativeSizingEdge"/> value round-tripped through the payload.</param>
    [Theory]
    [InlineData(NativeSizingEdge.Left)]
    [InlineData(NativeSizingEdge.Top)]
    [InlineData(NativeSizingEdge.Right)]
    [InlineData(NativeSizingEdge.Bottom)]
    [InlineData(NativeSizingEdge.TopLeft)]
    [InlineData(NativeSizingEdge.TopRight)]
    [InlineData(NativeSizingEdge.BottomLeft)]
    [InlineData(NativeSizingEdge.BottomRight)]
    public void SizeMoveEntered_Edge_IsAlwaysAClosedSetEnumName_NeverFreeText(NativeSizingEdge edge)
    {
        var sut = BuildEnabled();
        var panelId = Guid.NewGuid();

        sut.SizeMoveEntered(panelId, 1, edge.ToString());

        var write = Assert.Single(_writes, w => w.Name == "SizeMoveEntered");
        var value = GetPayloadValue<string>(write, "edge");
        Assert.True(Enum.TryParse<NativeSizingEdge>(value, out _));
        Assert.DoesNotContain('\\', value);
        Assert.DoesNotContain(':', value);
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    private static Rect MakeRect(double x, double y, double width, double height) => new(x, y, width, height);

    private static T GetPayloadValue<T>(RecordedWrite write, string key)
    {
        var match = write.Payload.Single(p => p.Key == key);
        return Assert.IsType<T>(match.Value);
    }

    private GestureDiagnostics BuildEnabled() =>
        new(_logMock.Object, IntervalMs, () => _clock);

    private sealed record RecordedWrite(string Category, string Name, string? Scope, KeyValuePair<string, object?>[] Payload);
}
