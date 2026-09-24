using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.SingleInstance;
using Moq;
using static DesktopOrganizer.Services.SingleInstance.ActivationMessageCodec;

namespace DesktopOrganizer.Tests.Unit.SingleInstance;

/// <summary>
/// Covers the five single-instance lifecycle events of docs/PLANS/phase-3-single-instance-ipc.md §4 —
/// what <see cref="SingleInstanceCoordinator"/> writes, when it writes it, and (most importantly) the
/// privacy invariant every payload has to satisfy.
/// </summary>
/// <remarks>
/// These events are written straight to <see cref="IDiagnosticsLog"/> rather than through
/// <c>IGestureDiagnostics</c>, following the precedent already set by
/// <c>SingleInstanceShutdownSequence</c> (commit C3) and by <c>JsonLinesDiagnosticsLog</c>'s own
/// <c>DiagnosticsDropped</c> marker: <c>IGestureDiagnostics</c> lives in <c>DesktopOrganizer.UI</c>
/// and models a per-panel gesture, neither of which the coordinator — a
/// <c>DesktopOrganizer.Services</c> type with no UI and no panel — has any relationship to.
/// </remarks>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class SingleInstanceDiagnosticsTests
{
    /// <summary>
    /// Every value any of the five events is allowed to put in a string payload field. Deliberately an
    /// explicit allow-list rather than a "does not look like a path" heuristic: these fields are
    /// specified as enum names (phase-3 plan §4), so anything outside the closed set is a defect
    /// regardless of whether it happens to look like user data.
    /// </summary>
    private static readonly HashSet<string> AllowedStringPayloadValues =
    [
        .. Enum.GetNames<ActivationCommand>(),
        .. Enum.GetNames<ActivationSendResult>(),
        .. Enum.GetNames<ActivationServerUnavailableReason>(),
        "None", // SingleInstanceCoordinator's "a secondary that never sent anything" sendResult.
    ];

    private static readonly SingleInstanceHandshakePolicy FastPolicy = new(
        maxAttempts: 3,
        connectTimeout: TimeSpan.FromMilliseconds(10),
        budget: TimeSpan.FromSeconds(5),
        backoff: [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero]);

    private readonly List<RecordedWrite> _writes = [];
    private readonly Mock<IDiagnosticsLog> _logMock = new();

    public SingleInstanceDiagnosticsTests()
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

    // ── SingleInstancePrimaryAcquired ───────────────────────────────────────────────────────────
    [Fact]
    public void Start_AsPrimary_WritesPrimaryAcquired_WithHandshakeShape()
    {
        var coordinator = CreateCoordinator(new FakeMutexGate(true), new FakeActivationServer(startResult: true));

        coordinator.Start();

        var write = Assert.Single(_writes, w => w.Name == "SingleInstancePrimaryAcquired");
        Assert.Equal("session", write.Category);
        Assert.Null(write.Scope);
        Assert.Equal(
            new[] { "attempts", "elapsedMs", "wasAbandoned", "serverStarted" },
            write.Payload.Select(p => p.Key).ToArray());
        Assert.Equal(1, write.Value("attempts"));
        Assert.Equal(true, write.Value("serverStarted"));
        Assert.Equal(false, write.Value("wasAbandoned"));
    }

    /// <summary>T-CO-05's diagnostics half: a primary that inherited an abandoned mutex says so.</summary>
    [Fact]
    public void Start_AsPrimary_AfterAbandonedMutex_ReportsWasAbandoned()
    {
        var guard = new FakeMutexGate(true) { WasAbandoned = true };
        var coordinator = CreateCoordinator(guard, new FakeActivationServer(startResult: true));

        coordinator.Start();

        var write = Assert.Single(_writes, w => w.Name == "SingleInstancePrimaryAcquired");
        Assert.Equal(true, write.Value("wasAbandoned"));
    }

    // ── R-12: an ordinary secondary writes nothing at all ───────────────────────────────────────

    /// <summary>
    /// The single most important assertion in this file. In a Debug build diagnostics are on by
    /// default, so if a plain secondary resolved <see cref="IDiagnosticsLog"/> it would create a JSONL
    /// session file per launch — and the 20-launch stress run (M-1) would evict the real primary's own
    /// log through file-count retention (ADR-023 R-12, manual matrix M-12).
    /// </summary>
    [Fact]
    public void Start_AsPlainSecondary_NeverResolvesDiagnostics()
    {
        var resolveCount = 0;
        var coordinator = new SingleInstanceCoordinator(
            new FakeMutexGate(), // always busy — this process never owns the guard
            new FakeMutexGate(),
            new FakeActivationServer(startResult: true),
            new FakeActivationClient(ActivationSendResult.Delivered),
            FastPolicy,
            nowMs: () => 0,
            sessionId: 1,
            diagnostics: () =>
            {
                resolveCount++;
                return _logMock.Object;
            });

        var decision = coordinator.Start();

        Assert.Equal(SingleInstanceStartupAction.ExitAfterActivation, decision.Action);
        Assert.Equal(0, resolveCount);
        Assert.Empty(_writes);
    }

    // ── ActivationServerUnavailable + SingleInstanceSecondaryDetected ───────────────────────────

    /// <summary>
    /// The degraded D-4 path (T-CO-06): the guard was taken, so this process was momentarily the
    /// primary and has already materialized diagnostics — which is exactly when the secondary-detected
    /// event is worth writing, because it completes the story the unavailable-server event started.
    /// </summary>
    /// <remarks>
    /// Three facts rather than a <c>[Theory]</c>: <c>ActivationServerUnavailableReason</c> is internal
    /// to <c>DesktopOrganizer.Services</c>, so it cannot appear in the signature of the public method
    /// xUnit requires for a data-driven test.
    /// </remarks>
    [Fact]
    public void Start_ServerUnavailable_PipeBusy_WritesUnavailableReason_ThenSecondaryDetected() =>
        AssertServerUnavailableSequence_DegradesToSecondary(ActivationServerUnavailableReason.PipeBusy);

    /// <summary>
    /// M-2/D-4: AclFailed is not a competing primary — the process must stay Primary in a degraded
    /// mode (no working activation channel), not fall back to Secondary.
    /// </summary>
    [Fact]
    public void Start_ServerUnavailable_AclFailed_WritesUnavailableReason_ThenStaysDegradedPrimary() =>
        AssertServerUnavailableSequence_StaysDegradedPrimary(ActivationServerUnavailableReason.AclFailed);

    /// <summary>M-2/D-4: same as AclFailed above — Faulted is not a competing primary either.</summary>
    [Fact]
    public void Start_ServerUnavailable_Faulted_WritesUnavailableReason_ThenStaysDegradedPrimary() =>
        AssertServerUnavailableSequence_StaysDegradedPrimary(ActivationServerUnavailableReason.Faulted);

    // ── ActivationRequestReceived ───────────────────────────────────────────────────────────────
    [Fact]
    public void ActivationRequest_ArrivingBeforeUiIsReady_IsReportedAsBufferedAtArrival()
    {
        var server = new FakeActivationServer(startResult: true);
        var coordinator = CreateCoordinator(new FakeMutexGate(true), server);
        Assert.True(coordinator.TryBeginServing());

        server.CapturedOnRequest!(new ActivationRequest(1, ActivationCommand.ActivateSettings, SessionId: 1));

        var write = Assert.Single(_writes, w => w.Name == "ActivationRequestReceived");
        Assert.Equal("session", write.Category);
        Assert.Null(write.Scope);
        Assert.Equal(
            new[] { "command", "protocolVersion", "sameSession", "bufferedAtArrival" },
            write.Payload.Select(p => p.Key).ToArray());
        Assert.Equal(nameof(ActivationCommand.ActivateSettings), write.Value("command"));
        Assert.Equal(1, write.Value("protocolVersion"));
        Assert.Equal(true, write.Value("sameSession"));
        Assert.Equal(true, write.Value("bufferedAtArrival"));
    }

    [Fact]
    public void ActivationRequest_ArrivingAfterSubscription_IsReportedAsNotBuffered()
    {
        var server = new FakeActivationServer(startResult: true);
        var coordinator = CreateCoordinator(new FakeMutexGate(true), server);
        Assert.True(coordinator.TryBeginServing());
        coordinator.ActivationRequested += (_, _) => { };

        server.CapturedOnRequest!(new ActivationRequest(1, ActivationCommand.ActivateSettings, SessionId: 1));

        var write = Assert.Single(_writes, w => w.Name == "ActivationRequestReceived");
        Assert.Equal(false, write.Value("bufferedAtArrival"));
    }

    [Fact]
    public void ActivationRequest_FromAnotherWindowsSession_IsReportedAsNotSameSession()
    {
        var server = new FakeActivationServer(startResult: true);
        var coordinator = CreateCoordinator(new FakeMutexGate(true), server);
        Assert.True(coordinator.TryBeginServing());

        server.CapturedOnRequest!(new ActivationRequest(1, ActivationCommand.ActivateSettings, SessionId: 999));

        var write = Assert.Single(_writes, w => w.Name == "ActivationRequestReceived");
        Assert.Equal(false, write.Value("sameSession"));
    }

    // ── SingleInstanceShutdownSequence ──────────────────────────────────────────────────────────

    /// <summary>
    /// C-2: the event is written by <see cref="ISingleInstanceCoordinator.CompleteShutdown"/>, called
    /// explicitly before disposal (see <c>App.OnExit</c>) — never by <c>Dispose()</c> itself, which
    /// would be unsafe when invoked automatically as part of the DI container's own disposal chain.
    /// </summary>
    [Fact]
    public void CompleteShutdown_AfterBecomingPrimary_WritesShutdownSequence()
    {
        var coordinator = CreateCoordinator(new FakeMutexGate(true), new FakeActivationServer(startResult: true));
        coordinator.Start();

        coordinator.CompleteShutdown();

        var write = Assert.Single(_writes, w => w.Name == "SingleInstanceShutdownSequence");
        Assert.Equal("session", write.Category);
        Assert.Equal(
            new[] { "stopServerMs", "serverStopTimedOut", "mutexReleased" },
            write.Payload.Select(p => p.Key).ToArray());
        Assert.Equal(false, write.Value("serverStopTimedOut"));
        Assert.Equal(true, write.Value("mutexReleased"));
    }

    /// <summary>C-2 regression: <c>Dispose()</c> alone (no prior <c>CompleteShutdown()</c>) must never
    /// write <c>SingleInstanceShutdownSequence</c> — it must not resolve <see cref="IDiagnosticsLog"/>
    /// at all.</summary>
    [Fact]
    public void Dispose_AfterBecomingPrimary_WithoutCompleteShutdown_DoesNotWriteShutdownSequence()
    {
        var coordinator = CreateCoordinator(new FakeMutexGate(true), new FakeActivationServer(startResult: true));
        coordinator.Start();

        coordinator.Dispose();

        Assert.DoesNotContain(_writes, w => w.Name == "SingleInstanceShutdownSequence");
    }

    // ── Privacy invariant across all five events ────────────────────────────────────────────────

    /// <summary>
    /// Privacy sweep over a full primary lifecycle (phase-3 plan §4, mirroring
    /// <c>GestureDiagnosticsTests.Payload_AcrossFullLifecycle_…</c>). Two things are being pinned:
    /// no payload string may fall outside the closed enum sets these events are specified with, and —
    /// the one project-specific rule — the <c>{sidHash}</c> that names the guard mutex and the
    /// activation pipe must never reach the log, directly or as a substring of a name.
    /// </summary>
    [Fact]
    public void Payload_AcrossFullPrimaryLifecycle_ContainsOnlyClosedEnumNamesNumbersAndBooleans()
    {
        const int SessionId = 7;
        var userKeyHash = SingleInstanceNaming.UserKeyHash("S-1-5-21-1111111111-2222222222-3333333333-1001");

        var server = new FakeActivationServer(startResult: true);
        var coordinator = CreateCoordinator(new FakeMutexGate(true), server, sessionId: SessionId);

        coordinator.Start();
        server.CapturedOnRequest!(new ActivationRequest(1, ActivationCommand.ActivateSettings, SessionId));
        coordinator.ActivationRequested += (_, _) => { };
        server.CapturedOnRequest!(new ActivationRequest(1, ActivationCommand.ActivateSettings, SessionId));
        coordinator.CompleteShutdown();
        coordinator.Dispose();

        Assert.NotEmpty(_writes);
        foreach (var write in _writes)
        {
            Assert.Equal("session", write.Category);
            Assert.Null(write.Scope);

            foreach (var (key, value) in write.Payload)
            {
                Assert.DoesNotContain("path", key, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("name", key, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("user", key, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("sid", key, StringComparison.OrdinalIgnoreCase);

                Assert.True(
                    value is int or long or bool or string,
                    $"Payload '{write.Name}.{key}' is a {value?.GetType().Name ?? "null"}; only numbers, booleans and closed-set enum names may be logged.");

                if (value is not string text) continue;

                Assert.Contains(text, AllowedStringPayloadValues);
                Assert.DoesNotContain(userKeyHash, text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain('\\', text);
                Assert.DoesNotContain(':', text);
            }
        }
    }

    private void AssertServerUnavailableSequence_DegradesToSecondary(ActivationServerUnavailableReason reason)
    {
        var coordinator = CreateCoordinator(
            new FakeMutexGate(true),
            new FakeActivationServer(startResult: false, failureReason: reason),
            new FakeActivationClient(ActivationSendResult.OtherSession));

        coordinator.Start();

        var unavailable = Assert.Single(_writes, w => w.Name == "ActivationServerUnavailable");
        Assert.Equal("session", unavailable.Category);
        Assert.Equal(new[] { "reason" }, unavailable.Payload.Select(p => p.Key).ToArray());
        Assert.Equal(reason.ToString(), unavailable.Value("reason"));

        var secondary = Assert.Single(_writes, w => w.Name == "SingleInstanceSecondaryDetected");
        Assert.Equal(
            new[] { "attempts", "elapsedMs", "sendResult" },
            secondary.Payload.Select(p => p.Key).ToArray());
        Assert.Equal(nameof(ActivationSendResult.OtherSession), secondary.Value("sendResult"));

        // Ordering matters for readability of the log: "the server could not start" must precede
        // "…and so this process is a secondary after all".
        Assert.True(
            _writes.FindIndex(w => w.Name == "ActivationServerUnavailable")
            < _writes.FindIndex(w => w.Name == "SingleInstanceSecondaryDetected"));
    }

    /// <summary>
    /// M-2/D-4 counterpart of <see cref="AssertServerUnavailableSequence_DegradesToSecondary"/>: for
    /// AclFailed/Faulted the process keeps the guard and is resolved as Primary — the
    /// <c>ActivationServerUnavailable</c> event is written (same as the PipeBusy case, so the failure is
    /// still visible in the log), but no send is ever attempted and no
    /// <c>SingleInstanceSecondaryDetected</c> event follows; instead the ordinary
    /// <c>SingleInstancePrimaryAcquired</c> event reports <c>serverStarted: false</c>.
    /// </summary>
    private void AssertServerUnavailableSequence_StaysDegradedPrimary(ActivationServerUnavailableReason reason)
    {
        var client = new FakeActivationClient(ActivationSendResult.OtherSession); // must never be consulted
        var coordinator = CreateCoordinator(
            new FakeMutexGate(true),
            new FakeActivationServer(startResult: false, failureReason: reason),
            client);

        var decision = coordinator.Start();

        Assert.Equal(SingleInstanceStartupAction.RunAsPrimary, decision.Action);
        Assert.Equal(SingleInstanceRole.Primary, decision.Role);

        var unavailable = Assert.Single(_writes, w => w.Name == "ActivationServerUnavailable");
        Assert.Equal(reason.ToString(), unavailable.Value("reason"));

        var acquired = Assert.Single(_writes, w => w.Name == "SingleInstancePrimaryAcquired");
        Assert.Equal(false, acquired.Value("serverStarted"));

        Assert.DoesNotContain(_writes, w => w.Name == "SingleInstanceSecondaryDetected");
    }

    private SingleInstanceCoordinator CreateCoordinator(
        FakeMutexGate guard,
        FakeActivationServer server,
        FakeActivationClient? client = null,
        int sessionId = 1) =>
        new(
            guard,
            new FakeMutexGate(),
            server,
            client ?? new FakeActivationClient(),
            FastPolicy,
            nowMs: () => 0,
            sessionId: sessionId,
            diagnostics: () => _logMock.Object);

    private sealed record RecordedWrite(string Category, string Name, string? Scope, KeyValuePair<string, object?>[] Payload)
    {
        public object? Value(string key) => Payload.Single(p => p.Key == key).Value;
    }

    private sealed class FakeMutexGate(params bool[] results) : IMutexGate
    {
        private readonly Queue<bool> _results = new(results);
        private bool _lastResult;

        public bool IsOwned { get; private set; }

        public bool WasAbandoned { get; set; }

        public bool TryAcquire()
        {
            _lastResult = _results.Count > 0 ? _results.Dequeue() : _lastResult;
            IsOwned = _lastResult;
            return IsOwned;
        }

        public void Release() => IsOwned = false;

        public void Dispose()
        {
        }
    }

    private sealed class FakeActivationServer(
        bool startResult = true,
        ActivationServerUnavailableReason failureReason = ActivationServerUnavailableReason.PipeBusy)
        : IActivationServer
    {
        public ActivationServerUnavailableReason? LastStartFailure { get; private set; }

        public Action<ActivationRequest>? CapturedOnRequest { get; private set; }

        public bool TryStart(Action<ActivationRequest> onRequest, Func<ActivationResponseCode> statusProvider)
        {
            CapturedOnRequest = onRequest;
            LastStartFailure = startResult ? null : failureReason;
            return startResult;
        }

        public void Stop()
        {
        }
    }

    private sealed class FakeActivationClient(params ActivationSendResult[] results) : IActivationClient
    {
        private readonly Queue<ActivationSendResult> _results = new(results);

        public ActivationSendResult TrySend(ActivationRequest request, TimeSpan connectTimeout) =>
            _results.Count > 0 ? _results.Dequeue() : ActivationSendResult.NoListener;
    }
}
