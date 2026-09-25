using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Diagnostics;
using DesktopOrganizer.Services.SingleInstance;
using static DesktopOrganizer.Services.SingleInstance.ActivationMessageCodec;

namespace DesktopOrganizer.Tests.Unit.SingleInstance;

/// <summary>
/// Orchestration tests for <see cref="SingleInstanceCoordinator"/> (ADR-023) on fakes — no real mutex
/// or pipe involved, so every scenario is instant and deterministic. Covers the base handshake
/// outcomes (T-CO-01..04, T-CO-06, T-CO-07 from the phase-3 plan §5.2); the full T-CO-05/T-CO-08
/// matrix is expected to be extended in the DI-registration commit (C3) alongside real wiring.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class SingleInstanceCoordinatorTests
{
    // Zero backoff and a tiny attempt count keep every test instant; nowMs is pinned at 0 so only the
    // attempt-count limit (never the time budget) can end a loop, which keeps assertions deterministic.
    private static readonly SingleInstanceHandshakePolicy FastPolicy = new(
        maxAttempts: 3,
        connectTimeout: TimeSpan.FromMilliseconds(10),
        budget: TimeSpan.FromSeconds(5),
        backoff: [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero]);

    /// <summary>
    /// Verifies that when the guard mutex is free, the coordinator runs as primary, starts the server,
    /// and sends nothing (T-CO-01).
    /// </summary>
    [Fact]
    public void Start_MutexFree_RunsAsPrimary_StartsServer_SendsNothing()
    {
        var guard = new FakeMutexGate(true);
        var server = new FakeActivationServer(startResult: true);
        var client = new FakeActivationClient();
        var coordinator = CreateCoordinator(guard, server: server, client: client);

        var decision = coordinator.Start();

        Assert.Equal(SingleInstanceStartupAction.RunAsPrimary, decision.Action);
        Assert.Equal(SingleInstanceRole.Primary, decision.Role);
        Assert.Null(decision.SendResult);
        Assert.Equal(1, decision.Attempts);
        Assert.Equal(1, server.StartCallCount);
        Assert.Empty(client.SentRequests);
    }

    /// <summary>
    /// Verifies that when the guard mutex is busy and the primary answers <c>Ok</c>, the coordinator
    /// exits after activation as a secondary without starting its own server (T-CO-02).
    /// </summary>
    [Fact]
    public void Start_MutexBusy_PrimaryAnswersOk_ExitsAfterActivation()
    {
        var guard = new FakeMutexGate(); // always busy
        var server = new FakeActivationServer(startResult: true);
        var client = new FakeActivationClient(ActivationSendResult.Delivered);
        var coordinator = CreateCoordinator(guard, server: server, client: client);

        var decision = coordinator.Start();

        Assert.Equal(SingleInstanceStartupAction.ExitAfterActivation, decision.Action);
        Assert.Equal(SingleInstanceRole.Secondary, decision.Role);
        Assert.Equal(ActivationSendResult.Delivered, decision.SendResult);
        var sent = Assert.Single(client.SentRequests);
        Assert.Equal(ActivationCommand.ActivateSettings, sent.Command);
        Assert.Equal(0, server.StartCallCount);
    }

    /// <summary>
    /// Verifies that when the guard mutex frees up on the third attempt after two
    /// <c>PrimaryShuttingDown</c> responses, the coordinator runs as primary (T-CO-03).
    /// </summary>
    [Fact]
    public void Start_MutexFreesUpOnThirdAttempt_AfterTwoShuttingDownResponses_RunsAsPrimary()
    {
        var guard = new FakeMutexGate(false, false, true);
        var server = new FakeActivationServer(startResult: true);
        var client = new FakeActivationClient(ActivationSendResult.PrimaryShuttingDown, ActivationSendResult.PrimaryShuttingDown);
        var coordinator = CreateCoordinator(guard, server: server, client: client);

        var decision = coordinator.Start();

        Assert.Equal(SingleInstanceStartupAction.RunAsPrimary, decision.Action);
        Assert.Equal(3, decision.Attempts);
        Assert.Equal(2, client.SentRequests.Count);
        Assert.Equal(1, server.StartCallCount);
    }

    /// <summary>
    /// Verifies that when the primary never responds and the attempt budget is exhausted, the
    /// coordinator exits with <see cref="SingleInstanceStartupAction.ExitActivationFailed"/> (T-CO-04).
    /// </summary>
    [Fact]
    public void Start_PrimaryNeverResponds_BudgetExhausted_ExitsActivationFailed()
    {
        var guard = new FakeMutexGate(); // never frees up
        var server = new FakeActivationServer(startResult: true);
        var client = new FakeActivationClient(ActivationSendResult.NoListener, ActivationSendResult.NoListener, ActivationSendResult.NoListener);
        var coordinator = CreateCoordinator(guard, server: server, client: client);

        var decision = coordinator.Start();

        Assert.Equal(SingleInstanceStartupAction.ExitActivationFailed, decision.Action);
        Assert.Equal(SingleInstanceRole.Secondary, decision.Role);
        Assert.Equal(3, decision.Attempts); // FastPolicy.maxAttempts
        Assert.Equal(0, server.StartCallCount);
    }

    /// <summary>
    /// Verifies that when the guard mutex is acquired but the pipe is busy (<c>ERROR_PIPE_BUSY</c>,
    /// ADR-023 D-4), the coordinator releases the guard and falls back to running as secondary
    /// (T-CO-06).
    /// </summary>
    [Fact]
    public void Start_GuardAcquired_ButPipeBusy_ReleasesGuard_FallsBackToSecondary()
    {
        var guard = new FakeMutexGate(true);
        var server = new FakeActivationServer(startResult: false); // ERROR_PIPE_BUSY (D-4)
        var client = new FakeActivationClient(ActivationSendResult.OtherSession);
        var coordinator = CreateCoordinator(guard, server: server, client: client);

        var decision = coordinator.Start();

        Assert.Equal(SingleInstanceStartupAction.ExitOtherSession, decision.Action);
        Assert.Equal(SingleInstanceRole.Secondary, decision.Role);
        Assert.Equal(1, decision.Attempts);
        Assert.False(guard.IsOwned);
        Assert.True(guard.ReleaseCallCount >= 1);
        var sent = Assert.Single(client.SentRequests);
        Assert.Equal(ActivationCommand.ActivateSettings, sent.Command);
    }

    // M-2: only PipeBusy is an actual competing primary (ADR-023 D-4). AclFailed/Faulted mean the pipe
    // server itself could not come up for reasons unrelated to another process's server — the process
    // legitimately holding the guard must stay Primary rather than release it and report a misleading
    // "already running" failure.

    /// <summary>
    /// Verifies that when the pipe server fails to start with <see cref="ActivationServerUnavailableReason.AclFailed"/>,
    /// the coordinator stays primary and keeps the guard rather than falling back to secondary (M-2).
    /// </summary>
    [Fact]
    public void Start_GuardAcquired_ServerAclFailed_StaysPrimary_KeepsGuard() =>
        AssertDegradedPrimary(ActivationServerUnavailableReason.AclFailed);

    /// <summary>
    /// Verifies that when the pipe server fails to start with <see cref="ActivationServerUnavailableReason.Faulted"/>,
    /// the coordinator stays primary and keeps the guard rather than falling back to secondary (M-2).
    /// </summary>
    [Fact]
    public void Start_GuardAcquired_ServerFaulted_StaysPrimary_KeepsGuard() =>
        AssertDegradedPrimary(ActivationServerUnavailableReason.Faulted);

    /// <summary>
    /// Verifies that calling <see cref="ISingleInstanceCoordinator.EnterShutdown"/> and the inherited
    /// <c>Dispose</c> repeatedly, without ever calling <c>Start</c>, is idempotent and does not throw
    /// (T-CO-07).
    /// </summary>
    [Fact]
    public void Dispose_WithoutStart_IsIdempotent_AndDoesNotThrow()
    {
        var guard = new FakeMutexGate();
        var detection = new FakeMutexGate();
        var server = new FakeActivationServer();
        var coordinator = CreateCoordinator(guard, detection, server);

        coordinator.EnterShutdown();
        coordinator.EnterShutdown();

        coordinator.Dispose();
        coordinator.Dispose();

        Assert.Equal(1, server.StopCallCount);
        Assert.Equal(1, guard.ReleaseCallCount);
        Assert.Equal(1, guard.DisposeCallCount);
        Assert.Equal(1, detection.DisposeCallCount);
        Assert.False(guard.IsOwned);
    }

    /// <summary>
    /// Verifies that after acquiring the primary role, disposing the coordinator stops the server and
    /// releases the guard.
    /// </summary>
    [Fact]
    public void Dispose_AfterAcquiringPrimary_ReleasesGuardAndStopsServer_InOrder()
    {
        var guard = new FakeMutexGate(true);
        var detection = new FakeMutexGate();
        var server = new FakeActivationServer(startResult: true);
        var coordinator = CreateCoordinator(guard, detection, server);

        coordinator.Start();
        coordinator.Dispose();

        Assert.Equal(1, server.StopCallCount);
        Assert.Equal(1, guard.ReleaseCallCount);
        Assert.False(guard.IsOwned);
        Assert.Equal(1, detection.DisposeCallCount);
    }

    /// <summary>
    /// Verifies that invoking the server's captured <c>onRequest</c> callback after
    /// <see cref="ISingleInstanceCoordinator.TryBeginServing"/> raises
    /// <see cref="ISingleInstanceCoordinator.ActivationRequested"/> with the request unchanged.
    /// </summary>
    [Fact]
    public void TryBeginServing_ServerCallback_RaisesActivationRequested()
    {
        var server = new FakeActivationServer(startResult: true);
        var coordinator = CreateCoordinator(new FakeMutexGate(), server: server);

        ActivationRequest? received = null;
        coordinator.ActivationRequested += (_, request) => received = request;

        Assert.True(coordinator.TryBeginServing());

        var sample = new ActivationRequest(1, ActivationCommand.ActivateSettings, 42);
        server.CapturedOnRequest!(sample);

        Assert.Equal(sample, received);
    }

    /// <summary>
    /// Verifies that the server's captured status provider reflects <see cref="ActivationResponseCode.Ok"/>
    /// before <see cref="ISingleInstanceCoordinator.EnterShutdown"/> and <see cref="ActivationResponseCode.ShuttingDown"/>
    /// after it.
    /// </summary>
    [Fact]
    public void TryBeginServing_StatusProvider_ReflectsEnterShutdown()
    {
        var server = new FakeActivationServer(startResult: true);
        var coordinator = CreateCoordinator(new FakeMutexGate(), server: server);

        Assert.True(coordinator.TryBeginServing());
        Assert.Equal(ActivationResponseCode.Ok, server.CapturedStatusProvider!());

        coordinator.EnterShutdown();

        Assert.Equal(ActivationResponseCode.ShuttingDown, server.CapturedStatusProvider!());
    }

    /// <summary>
    /// Pins the buffering behavior <see cref="ActivationRequestRelay"/> exists for (ADR-023 §2.2):
    /// the pipe server starts (and can receive requests) well before <c>App.StartApp</c> subscribes to
    /// <see cref="ISingleInstanceCoordinator.ActivationRequested"/> in production — a request that
    /// arrives in that gap must be delivered, not silently dropped, the moment someone finally
    /// subscribes.
    /// </summary>
    [Fact]
    public void ActivationRequested_RequestArrivesBeforeSubscription_IsDeliveredOnSubscribe()
    {
        var server = new FakeActivationServer(startResult: true);
        var coordinator = CreateCoordinator(new FakeMutexGate(), server: server);

        Assert.True(coordinator.TryBeginServing());

        var sample = new ActivationRequest(1, ActivationCommand.ActivateSettings, 7);
        server.CapturedOnRequest!(sample); // simulates the pipe thread — arrives before any subscriber

        ActivationRequest? received = null;
        coordinator.ActivationRequested += (_, request) => received = request;

        Assert.Equal(sample, received);
    }

    /// <summary>
    /// ADR-023 R-12: a process that never acquired the guard (i.e. never became Primary) must not force
    /// DI to resolve <see cref="IDiagnosticsLog"/> when its coordinator is disposed — otherwise a burst
    /// of Secondary launches would evict the real Primary's own session log from its file-count
    /// retention (see <c>JsonLinesDiagnosticsLog</c>).
    /// </summary>
    [Fact]
    public void Dispose_GuardNeverAcquired_DoesNotResolveDiagnostics()
    {
        var diagnosticsResolveCount = 0;
        var coordinator = new SingleInstanceCoordinator(
            new FakeMutexGate(), // always busy — never acquired
            new FakeMutexGate(),
            new FakeActivationServer(),
            new FakeActivationClient(),
            FastPolicy,
            nowMs: () => 0,
            sessionId: 1,
            diagnostics: () =>
            {
                diagnosticsResolveCount++;
                return NullDiagnosticsLog.Instance;
            });

        coordinator.Dispose();

        Assert.Equal(0, diagnosticsResolveCount);
    }

    /// <summary>
    /// Counterpart of <see cref="Dispose_GuardNeverAcquired_DoesNotResolveDiagnostics"/>: a process
    /// that did become Primary still gets its lifecycle diagnostics — one resolve for
    /// <c>SingleInstancePrimaryAcquired</c> at the end of the handshake, one for
    /// <c>SingleInstanceShutdownSequence</c> from <see cref="ISingleInstanceCoordinator.CompleteShutdown"/>
    /// (phase-3 plan §4). Calls <c>CompleteShutdown()</c> before <c>Dispose()</c>, mirroring the real
    /// <c>App.OnExit</c> sequence (C-2) — <c>Dispose()</c> alone never resolves diagnostics.
    /// </summary>
    [Fact]
    public void StartCompleteShutdownAndDispose_GuardWasAcquired_ResolvesDiagnosticsOnlyOnPrimaryPaths()
    {
        var diagnosticsResolveCount = 0;
        var coordinator = new SingleInstanceCoordinator(
            new FakeMutexGate(true),
            new FakeMutexGate(),
            new FakeActivationServer(startResult: true),
            new FakeActivationClient(),
            FastPolicy,
            nowMs: () => 0,
            sessionId: 1,
            diagnostics: () =>
            {
                diagnosticsResolveCount++;
                return NullDiagnosticsLog.Instance;
            });

        coordinator.Start();
        coordinator.CompleteShutdown();
        coordinator.Dispose();

        Assert.Equal(2, diagnosticsResolveCount);
    }

    /// <summary>
    /// C-2 regression: <see cref="ISingleInstanceCoordinator"/>'s inherited <c>Dispose</c> must never
    /// resolve <see cref="IDiagnosticsLog"/> itself — only <see cref="ISingleInstanceCoordinator.CompleteShutdown"/>
    /// does, and the host is required to call that before disposing the DI container that owns the log
    /// (see <c>App.OnExit</c>). A coordinator disposed without ever calling <c>CompleteShutdown()</c> —
    /// exactly what happens when a real <c>ServiceProvider</c> disposes its registered singletons — must
    /// tear down cleanly and resolve nothing.
    /// </summary>
    [Fact]
    public void Dispose_WithoutCompleteShutdown_AfterBecomingPrimary_NeverResolvesDiagnostics()
    {
        var diagnosticsResolveCount = 0;
        var guard = new FakeMutexGate(true);
        var server = new FakeActivationServer(startResult: true);
        var coordinator = new SingleInstanceCoordinator(
            guard,
            new FakeMutexGate(),
            server,
            new FakeActivationClient(),
            FastPolicy,
            nowMs: () => 0,
            sessionId: 1,
            diagnostics: () =>
            {
                diagnosticsResolveCount++;
                return NullDiagnosticsLog.Instance;
            });

        coordinator.Start(); // one resolve, for SingleInstancePrimaryAcquired
        var resolvesBeforeDispose = diagnosticsResolveCount;

        coordinator.Dispose(); // must not resolve again — CompleteShutdown() was never called

        Assert.Equal(resolvesBeforeDispose, diagnosticsResolveCount);
        Assert.Equal(1, server.StopCallCount);
        Assert.False(guard.IsOwned);
    }

    /// <summary>C-2: a second <c>CompleteShutdown()</c> call — or a subsequent <c>Dispose()</c> — must not
    /// stop the server or release the guard again, and must not resolve diagnostics a second time.</summary>
    [Fact]
    public void CompleteShutdown_IsIdempotent_SubsequentDisposeDoesNotRepeatTeardownOrDiagnostics()
    {
        var diagnosticsResolveCount = 0;
        var guard = new FakeMutexGate(true);
        var server = new FakeActivationServer(startResult: true);
        var coordinator = new SingleInstanceCoordinator(
            guard,
            new FakeMutexGate(),
            server,
            new FakeActivationClient(),
            FastPolicy,
            nowMs: () => 0,
            sessionId: 1,
            diagnostics: () =>
            {
                diagnosticsResolveCount++;
                return NullDiagnosticsLog.Instance;
            });

        coordinator.Start();
        coordinator.CompleteShutdown();
        var resolvesAfterFirstShutdown = diagnosticsResolveCount;
        var releasesAfterFirstShutdown = guard.ReleaseCallCount;
        var stopsAfterFirstShutdown = server.StopCallCount;

        coordinator.CompleteShutdown(); // idempotent — no-op
        coordinator.Dispose(); // fallback branch must not run either

        Assert.Equal(resolvesAfterFirstShutdown, diagnosticsResolveCount);
        Assert.Equal(releasesAfterFirstShutdown, guard.ReleaseCallCount);
        Assert.Equal(stopsAfterFirstShutdown, server.StopCallCount);
    }

    /// <summary>
    /// Verifies that the <see cref="SingleInstanceCoordinator"/> constructor throws
    /// <see cref="ArgumentNullException"/> for a <see langword="null"/> guard.
    /// </summary>
    [Fact]
    public void Constructor_NullGuard_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new SingleInstanceCoordinator(
            null!,
            new FakeMutexGate(),
            new FakeActivationServer(),
            new FakeActivationClient(),
            FastPolicy,
            () => 0,
            sessionId: 1,
            () => NullDiagnosticsLog.Instance));
    }

    private static void AssertDegradedPrimary(ActivationServerUnavailableReason reason)
    {
        var guard = new FakeMutexGate(true);
        var server = new FakeActivationServer(startResult: false, failureReason: reason);
        var client = new FakeActivationClient(); // must never be consulted — this process never falls back
        var coordinator = CreateCoordinator(guard, server: server, client: client);

        var decision = coordinator.Start();

        Assert.Equal(SingleInstanceStartupAction.RunAsPrimary, decision.Action);
        Assert.Equal(SingleInstanceRole.Primary, decision.Role);
        Assert.Equal(1, decision.Attempts);
        Assert.True(guard.IsOwned);
        Assert.Equal(0, guard.ReleaseCallCount);
        Assert.Equal(1, server.StartCallCount);
        Assert.Empty(client.SentRequests);
    }

    private static SingleInstanceCoordinator CreateCoordinator(
        FakeMutexGate guard,
        FakeMutexGate? detection = null,
        FakeActivationServer? server = null,
        FakeActivationClient? client = null) =>
        new(
            guard,
            detection ?? new FakeMutexGate(),
            server ?? new FakeActivationServer(),
            client ?? new FakeActivationClient(),
            FastPolicy,
            nowMs: () => 0,
            sessionId: 1,
            diagnostics: () => NullDiagnosticsLog.Instance);

    /// <summary>
    /// Sequenced <see cref="IMutexGate"/> fake. Each <see cref="TryAcquire"/> call dequeues the next
    /// scripted result; once the queue is empty the last dequeued value repeats (so a constructor with
    /// no arguments means "always busy", and a single <see langword="true"/> means "free forever
    /// after the first acquire").
    /// </summary>
    private sealed class FakeMutexGate(params bool[] results) : IMutexGate
    {
        private readonly Queue<bool> _results = new(results);
        private bool _lastResult;

        public bool IsOwned { get; private set; }

        /// <summary>
        /// Settable by the test rather than scripted through the constructor: the real
        /// <see cref="MutexGate"/> only ever sets this as a side effect of catching
        /// <c>AbandonedMutexException</c>, which a fake has no way to reproduce faithfully.
        /// </summary>
        public bool WasAbandoned { get; set; }

        public int TryAcquireCallCount { get; private set; }

        public int ReleaseCallCount { get; private set; }

        public int DisposeCallCount { get; private set; }

        public bool TryAcquire()
        {
            TryAcquireCallCount++;
            _lastResult = _results.Count > 0 ? _results.Dequeue() : _lastResult;
            IsOwned = _lastResult;
            return IsOwned;
        }

        public void Release()
        {
            ReleaseCallCount++;
            IsOwned = false;
        }

        public void Dispose() => DisposeCallCount++;
    }

    private sealed class FakeActivationServer(bool startResult = true, ActivationServerUnavailableReason failureReason = ActivationServerUnavailableReason.PipeBusy)
        : IActivationServer
    {
        public int StartCallCount { get; private set; }

        public int StopCallCount { get; private set; }

        public ActivationServerUnavailableReason? LastStartFailure { get; private set; }

        public Action<ActivationRequest>? CapturedOnRequest { get; private set; }

        public Func<ActivationResponseCode>? CapturedStatusProvider { get; private set; }

        public bool TryStart(Action<ActivationRequest> onRequest, Func<ActivationResponseCode> statusProvider)
        {
            StartCallCount++;
            CapturedOnRequest = onRequest;
            CapturedStatusProvider = statusProvider;
            LastStartFailure = startResult ? null : failureReason;
            return startResult;
        }

        public void Stop() => StopCallCount++;
    }

    private sealed class FakeActivationClient(params ActivationSendResult[] results) : IActivationClient
    {
        private readonly Queue<ActivationSendResult> _results = new(results);

        public List<ActivationRequest> SentRequests { get; } = [];

        public ActivationSendResult TrySend(ActivationRequest request, TimeSpan connectTimeout)
        {
            SentRequests.Add(request);
            return _results.Count > 0 ? _results.Dequeue() : ActivationSendResult.NoListener;
        }
    }
}
