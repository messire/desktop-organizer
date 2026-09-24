using DesktopOrganizer.Services.Abstractions;
using static DesktopOrganizer.Services.SingleInstance.ActivationMessageCodec;

namespace DesktopOrganizer.Services.SingleInstance;

/// <summary>
/// Orchestrates the single-instance handshake (ADR-023): drives
/// <see cref="SingleInstanceHandshakePolicy"/>'s acquire/send state machine against the injected
/// mutex/pipe gates, then resolves the outcome via <see cref="SingleInstanceStartupDecisionTable"/>.
/// </summary>
/// <remarks>
/// Registered in DI (see <c>DesktopOrganizer.Services.Configuration.DiConfig</c>, ADR-023 phase-3 plan
/// commit C3) and driven from <c>DesktopOrganizer.UI.Program.Main</c> (commit C4), the process's own
/// entry point — never from the WPF <c>Application</c>'s own startup hooks, since a process that turns
/// out to be Secondary must never construct <c>Application</c> at all (ADR-023 D-2/R-1).
/// </remarks>
internal sealed class SingleInstanceCoordinator : ISingleInstanceCoordinator
{
    /// <summary>Protocol version this build speaks (ADR-023 D-5, <c>DO/1</c>).</summary>
    private const int ProtocolVersion = 1;

    /// <summary>
    /// The bounded join budget <c>NamedPipeActivationServer.Stop()</c> promises (ADR-023 §2.3 item 6).
    /// Mirrored here only to decide the <c>serverStopTimedOut</c> flag of
    /// <c>SingleInstanceShutdownSequence</c>: reaching it means the accept thread had to be abandoned
    /// rather than joined.
    /// </summary>
    private const long ServerStopBudgetMs = 2000;

    /// <summary>
    /// <c>sendResult</c> value for a Secondary that never got as far as sending anything. A literal
    /// name rather than <see langword="null"/> so the field's closed-set contract holds for every row
    /// in the log.
    /// </summary>
    private const string NoSendResult = "None";

    private readonly IMutexGate _guard;
    private readonly IMutexGate _detection;
    private readonly IActivationServer _server;
    private readonly IActivationClient _client;
    private readonly SingleInstanceHandshakePolicy _policy;
    private readonly Func<long> _nowMs;
    private readonly int _sessionId;
    private readonly Func<IDiagnosticsLog> _diagnostics;

    // ADR-023 §2.2: the pipe server is started (TryBeginServing, below) before anyone has subscribed
    // to ActivationRequested — Program.Main starts it during Start(), well before App/StartApp exist
    // to attach a handler. The relay buffers any requests that arrive in that gap and replays them,
    // in order, the moment the first subscriber attaches, so a double-click on the shortcut during our
    // own startup is never silently dropped.
    private readonly ActivationRequestRelay _relay = new();
    private EventHandler<ActivationRequest>? _activationRequested;

    private volatile bool _shuttingDown;
    private bool _disposed;
    private bool _shutdownCompleted;

    // Whether this process has already forced DI to materialize IDiagnosticsLog. Only ever set by
    // WriteSessionEvent below, and only reached on paths a process that (at least momentarily) owned
    // the guard can take — see WriteHandshakeDiagnostics for why this flag, not a plain "is enabled"
    // check, is what keeps ADR-023 R-12 true.
    private bool _diagnosticsMaterialized;

    /// <summary>
    /// Initializes a new instance of the <see cref="SingleInstanceCoordinator"/> class.
    /// </summary>
    /// <param name="guard">Per-session ownership gate — "owning it ⟺ I am primary" (ADR-023 D-1).</param>
    /// <param name="detection">
    /// Installer-contract mutex — its sole job is to exist for the process lifetime; this type never
    /// calls <see cref="IMutexGate.TryAcquire"/> on it, only disposes it on <see cref="Dispose"/>
    /// (ADR-023 D-1).
    /// </param>
    /// <param name="server">Machine-global activation pipe server (ADR-023 D-4).</param>
    /// <param name="client">Activation pipe client used while this process is still probing for a primary.</param>
    /// <param name="policy">Acquire/send schedule (ADR-023 D-6).</param>
    /// <param name="nowMs">Monotonic clock in milliseconds — injectable so tests never actually wait.</param>
    /// <param name="sessionId"><c>Process.GetCurrentProcess().SessionId</c> of this process.</param>
    /// <param name="diagnostics">
    /// Lazily-resolved diagnostics sink. A <see cref="Func{TResult}"/> rather than
    /// <see cref="IDiagnosticsLog"/> directly so that a process that never becomes primary never forces
    /// DI to materialize the JSONL log writer (ADR-023 R-12 — a burst of secondary launches must not
    /// evict the primary's own session log from its file-count retention).
    /// </param>
    internal SingleInstanceCoordinator(
        IMutexGate guard,
        IMutexGate detection,
        IActivationServer server,
        IActivationClient client,
        SingleInstanceHandshakePolicy policy,
        Func<long> nowMs,
        int sessionId,
        Func<IDiagnosticsLog> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(detection);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(nowMs);
        ArgumentNullException.ThrowIfNull(diagnostics);

        _guard = guard;
        _detection = detection;
        _server = server;
        _client = client;
        _policy = policy;
        _nowMs = nowMs;
        _sessionId = sessionId;
        _diagnostics = diagnostics;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Custom add/remove (instead of a plain field-like event) so that subscribing is exactly the
    /// "attach sink" moment <see cref="ActivationRequestRelay"/> needs (ADR-023 §2.2): the first (and,
    /// in practice, only — the UI layer's activation handler) subscriber immediately receives anything
    /// buffered since <see cref="TryBeginServing"/> ran.
    /// </remarks>
    public event EventHandler<ActivationRequest>? ActivationRequested
    {
        add
        {
            _activationRequested += value;
            _relay.AttachSink(request => _activationRequested?.Invoke(this, request));
        }

        remove => _activationRequested -= value;
    }

    /// <inheritdoc />
    public SingleInstanceStartupDecision Start()
    {
        var startTimestamp = _nowMs();
        var attempt = 0;
        ActivationSendResult? last = null;
        var acquired = false;
        var serverStarted = false;

        // Driving loop per SingleInstanceHandshakePolicy's documented contract (ADR-023 D-6), with one
        // addition: a successful guard acquisition is not immediately final. The machine-global pipe
        // server is the authoritative tie-breaker (ADR-023 D-4/R-7) — if starting it fails (another
        // session's primary already owns the single pipe instance, or a stuck client is occupying it,
        // R-10), we release the guard we just took and fall through to this same attempt's paired send,
        // exactly as if the acquire itself had failed (T-CO-06).
        while (true)
        {
            var elapsedMs = _nowMs() - startTimestamp;
            var step = _policy.NextStep(attempt, elapsedMs, last, out var delay);

            if (step == SingleInstanceHandshakePolicy.HandshakeStep.GiveUp)
            {
                break;
            }

            if (delay > TimeSpan.Zero)
            {
                Thread.Sleep(delay);
            }

            if (step == SingleInstanceHandshakePolicy.HandshakeStep.TryAcquire)
            {
                attempt++;

                if (_guard.TryAcquire())
                {
                    if (TryBeginServing())
                    {
                        acquired = true;
                        serverStarted = true;
                        break;
                    }

                    // ADR-023 D-4: only PipeBusy means an actual competing primary (typically Fast User
                    // Switching, R-8) — that is the one failure this process cannot resolve by staying
                    // up, so it releases the guard it just took and falls back to this same attempt's
                    // paired send, same as an outright failed acquire (T-CO-06). AclFailed/Faulted mean
                    // the pipe server itself could not come up for reasons that have nothing to do with
                    // another process holding the pipe; per SingleInstanceStartupDecisionTable's own
                    // degraded-primary contract ("acquired ⇒ RunAsPrimary regardless of serverStarted"),
                    // this process must stay primary — just without a working activation channel —
                    // rather than release a guard it legitimately holds and report a misleading
                    // "already running" failure to the user (M-2).
                    if ((_server.LastStartFailure ?? ActivationServerUnavailableReason.Faulted)
                        != ActivationServerUnavailableReason.PipeBusy)
                    {
                        acquired = true;
                        serverStarted = false;
                        break;
                    }

                    _guard.Release();
                    last = null;
                    continue;
                }

                last = null;
                continue;
            }

            // step == HandshakeStep.TrySend
            last = _client.TrySend(BuildActivationRequest(), _policy.ConnectTimeout);
        }

        var finalElapsedMs = _nowMs() - startTimestamp;
        var decision = SingleInstanceStartupDecisionTable.Resolve(acquired, serverStarted, last, attempt, finalElapsedMs);

        WriteHandshakeDiagnostics(decision, serverStarted);

        return decision;
    }

    /// <inheritdoc />
    public bool TryBeginServing()
    {
        // OnActivationRequestReceived is all the pipe thread ever does on the caller's behalf: hand off
        // to the thread-safe relay and enqueue one diagnostics line (same "minimum possible work in the
        // callback" discipline as the WH_MOUSE_LL hook, ADR-023 §2.3 item 4). It never raises
        // ActivationRequested directly — that would silently drop any request arriving before a
        // subscriber attaches.
        if (_server.TryStart(
                onRequest: OnActivationRequestReceived,
                statusProvider: () => _shuttingDown ? ActivationResponseCode.ShuttingDown : ActivationResponseCode.Ok))
        {
            return true;
        }

        // Only reachable from Start()'s loop, immediately after this process took the guard (or from
        // Program.Main once the server is already running, where TryStart short-circuits to true) — so
        // resolving diagnostics here is always a process that was, at that instant, the primary.
        WriteSessionEvent(
            "ActivationServerUnavailable",
            new KeyValuePair<string, object?>(
                "reason",
                (_server.LastStartFailure ?? ActivationServerUnavailableReason.Faulted).ToString()));

        return false;
    }

    /// <inheritdoc />
    public void EnterShutdown() => _shuttingDown = true;

    /// <inheritdoc />
    public void CompleteShutdown()
    {
        if (_shutdownCompleted)
        {
            return;
        }

        _shutdownCompleted = true;

        var wasOwned = _guard.IsOwned;
        var stopStarted = _nowMs();

        // Order matters (ADR-023 D-9): stop answering/accepting activations first, then release
        // ownership, so a process racing to acquire the guard right now never starts writing state
        // (SaveWindowsStateNow / SystemIconsPanel.Save) while we might still be mid-write ourselves.
        _server.Stop();

        var stopServerMs = _nowMs() - stopStarted;

        _guard.Release();

        // ADR-023 R-12: only a process that actually held the guard (i.e. was Primary) ever resolves
        // IDiagnosticsLog here. A Secondary process — which never acquired the guard — must be able to
        // probe, fail the handshake, and complete its coordinator's shutdown without forcing DI to
        // materialize the JSONL log writer; a burst of Secondary launches must not evict the real
        // Primary's own session log from its file-count retention.
        //
        // This resolve is only safe because the caller (App.OnExit) invokes CompleteShutdown() before
        // disposing the DI container that owns IDiagnosticsLog — never from inside Dispose() itself
        // (C-2): Dispose() can run automatically as part of that same container's own disposal chain,
        // by which point the container is already marked disposed and GetService throws
        // ObjectDisposedException unconditionally, with no short-circuit for already-created singletons.
        if (wasOwned)
        {
            WriteShutdownDiagnostics(stopServerMs, wasOwned);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // The normal path: App.OnExit already called CompleteShutdown() (stopped the server, released
        // the guard, written SingleInstanceShutdownSequence for a Primary) before disposing the DI
        // container whose disposal chain reaches this method. Nothing left to do here but release the
        // two OS handles.
        //
        // The fallback path (CompleteShutdown() never ran — e.g. this process never became Primary and
        // the host skipped calling it, or a caller disposes the coordinator directly without going
        // through the host's shutdown sequence) still performs the equivalent teardown, but MUST NOT
        // resolve IDiagnosticsLog: this method itself is what the DI container invokes automatically
        // while disposing its registered singletons (Program.Main's `using var provider`), so by the
        // time it runs the container may already be marked disposed — resolving anything here can throw
        // ObjectDisposedException (C-2). Stopping an already-stopped server and releasing an
        // already-released/never-owned guard are both no-ops by contract (IActivationServer.Stop,
        // MutexGate.Release), so this is safe to run unconditionally.
        if (!_shutdownCompleted)
        {
            _server.Stop();
            _guard.Release();
        }

        _guard.Dispose();
        _detection.Dispose();
    }

    private void WriteShutdownDiagnostics(long stopServerMs, bool mutexReleased) =>
        WriteSessionEvent(
            "SingleInstanceShutdownSequence",
            new KeyValuePair<string, object?>("stopServerMs", stopServerMs),
            new KeyValuePair<string, object?>("serverStopTimedOut", stopServerMs >= ServerStopBudgetMs),
            new KeyValuePair<string, object?>("mutexReleased", mutexReleased));

    /// <summary>
    /// Records how the handshake ended (phase-3 plan §4).
    /// </summary>
    /// <remarks>
    /// The asymmetry between the two events is deliberate and is what keeps ADR-023 R-12 true. A
    /// Primary always writes. A Secondary writes only if this process had <i>already</i> materialized
    /// the diagnostics sink earlier in the same handshake — which happens on exactly one path, the
    /// degraded "took the guard, then the machine-global pipe turned out to belong to another session"
    /// fallback (D-4/T-CO-06), whose <c>ActivationServerUnavailable</c> event is precisely the context
    /// this one completes. The ordinary Secondary — the one that never got the guard, i.e. all 20
    /// processes of the stress run (M-1/M-12) — must not touch <c>IDiagnosticsLog</c> at all: in a
    /// Debug build diagnostics are on by default, so resolving it would create a JSONL session file per
    /// launch and evict the real Primary's own log through file-count retention.
    /// </remarks>
    private void WriteHandshakeDiagnostics(SingleInstanceStartupDecision decision, bool serverStarted)
    {
        if (decision.Role == SingleInstanceRole.Primary)
        {
            WriteSessionEvent(
                "SingleInstancePrimaryAcquired",
                new KeyValuePair<string, object?>("attempts", decision.Attempts),
                new KeyValuePair<string, object?>("elapsedMs", decision.ElapsedMs),
                new KeyValuePair<string, object?>("wasAbandoned", _guard.WasAbandoned),
                new KeyValuePair<string, object?>("serverStarted", serverStarted));

            return;
        }

        if (!_diagnosticsMaterialized)
        {
            return;
        }

        WriteSessionEvent(
            "SingleInstanceSecondaryDetected",
            new KeyValuePair<string, object?>("attempts", decision.Attempts),
            new KeyValuePair<string, object?>("elapsedMs", decision.ElapsedMs),
            new KeyValuePair<string, object?>("sendResult", decision.SendResult?.ToString() ?? NoSendResult));
    }

    /// <summary>
    /// Records an activation arriving from a secondary process, then hands it to the relay.
    /// </summary>
    /// <remarks>
    /// Runs on the pipe server's accept thread. Only a Primary ever runs a server, so resolving
    /// diagnostics here never violates ADR-023 R-12, and <c>IDiagnosticsLog.Write</c> is a
    /// non-blocking, thread-safe enqueue (see <c>JsonLinesDiagnosticsLog</c>) — it does not turn the
    /// callback into real work. Note the ordering: <see cref="ActivationRequestRelay.Publish"/> runs
    /// first because it is what decides <c>bufferedAtArrival</c>, atomically, by actually delivering
    /// or buffering the request.
    /// <para>
    /// This is also where the secondary side shows up in the primary's log at all: the secondary
    /// process itself deliberately writes nothing (R-12).
    /// </para>
    /// </remarks>
    private void OnActivationRequestReceived(ActivationRequest request)
    {
        var bufferedAtArrival = _relay.Publish(request);

        WriteSessionEvent(
            "ActivationRequestReceived",
            new KeyValuePair<string, object?>("command", request.Command.ToString()),
            new KeyValuePair<string, object?>("protocolVersion", request.ProtocolVersion),
            new KeyValuePair<string, object?>("sameSession", request.SessionId == _sessionId),
            new KeyValuePair<string, object?>("bufferedAtArrival", bufferedAtArrival));
    }

    /// <summary>
    /// Single funnel for every event this type writes: category <c>session</c>, <c>scope</c> always
    /// <see langword="null"/> (these are process-lifetime events, not panel-scoped ones), payloads
    /// restricted to numbers, bools, and names of closed enums. No value reaching this method is ever
    /// derived from user data — in particular the <c>{sidHash}</c> that names the guard mutex and the
    /// activation pipe is never written anywhere (phase-3 plan §4, privacy invariant).
    /// </summary>
    private void WriteSessionEvent(string name, params KeyValuePair<string, object?>[] payload)
    {
        var log = _diagnostics();
        _diagnosticsMaterialized = true;

        if (!log.IsEnabled)
        {
            return;
        }

        log.Write("session", name, null, payload);
    }

    private ActivationRequest BuildActivationRequest() =>
        new(ProtocolVersion, ActivationCommand.ActivateSettings, _sessionId);
}
