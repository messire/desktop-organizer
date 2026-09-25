using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using DesktopOrganizer.Services.Abstractions;
using static DesktopOrganizer.Services.SingleInstance.ActivationMessageCodec;

namespace DesktopOrganizer.Services.SingleInstance;

/// <summary>
/// <see cref="NamedPipeServerStream"/>-backed <see cref="IActivationServer"/> (ADR-023 D-4/D-5).
/// </summary>
/// <remarks>
/// Accept-loop discipline (ADR-023 §2.3, all mandatory):
/// <list type="number">
/// <item>the accept loop runs on its own background thread (<see cref="ThreadPriority"/> default,
/// <c>IsBackground = true</c>), never on a thread-pool worker that could be starved;</item>
/// <item>every accepted connection gets a hard 500ms budget for read+respond, then an unconditional
/// <see cref="NamedPipeServerStream.Disconnect"/>/<see cref="IDisposable.Dispose"/>;</item>
/// <item>reads are bounded by <see cref="ActivationMessageCodec.MaxMessageBytes"/> — anything larger
/// gets <see cref="ActivationResponseCode.Unsupported"/> and the connection is torn down;</item>
/// <item>the only work done on the pipe thread on behalf of the caller is invoking the supplied
/// <c>onRequest</c> callback (in practice, <c>ActivationRequestRelay.Publish</c>) — the same "minimum
/// possible work in the callback" discipline as the <c>WH_MOUSE_LL</c> hook
/// (<see cref="DesktopOrganizer.Services.Tools.DesktopGestureService"/>) — and it is invoked
/// <i>after</i> the response has been written, flushed and drained, so a slow handler can never hold
/// the client's connection open (see <see cref="HandleConnection"/>);</item>
/// <item>the body of every loop iteration is wrapped in <c>try/catch (Exception)</c> — one bad client
/// must never kill the accept loop;</item>
/// <item>every iteration owns exactly one <see cref="NamedPipeServerStream"/> in a local and disposes
/// precisely that instance before the next iteration begins; because the pipe is created with
/// <c>maxNumberOfServerInstances: 1</c>, leaking a single instance would permanently occupy the name
/// and turn the loop into a create-fail/retry busy-spin. Recreation failures are additionally
/// backed off and hard-capped (<see cref="MaxConsecutiveCreateFailures"/>) so no future regression can
/// reintroduce a 100%-CPU spin;</item>
/// <item><see cref="Stop"/> cancels, disposes the in-flight <see cref="NamedPipeServerStream"/> (the
/// only reliable way to abort a pending <c>WaitForConnectionAsync</c>), then bounded-joins the accept
/// thread for at most 2 seconds — the same pattern as
/// <see cref="DesktopOrganizer.Services.Tools.SystemShellService.Dispose"/>.</item>
/// </list>
/// ACL (ADR-023 R-14): the pipe is created with a <see cref="PipeSecurity"/> granting access to the
/// current Windows user only. If the ACL cannot be created, <see cref="TryStart"/> returns
/// <see langword="false"/> rather than silently falling back to an unsecured pipe.
/// </remarks>
internal sealed class NamedPipeActivationServer : IActivationServer
{
    private const int ConnectionBudgetMs = 500;
    private const int StopJoinBudgetSeconds = 2;

    /// <summary>
    /// How many consecutive <see cref="CreateServerStream"/> failures the accept loop tolerates before
    /// abandoning itself. A failure here means the machine-global name is unavailable to this process;
    /// if that state persists there is nothing the loop can usefully do except stop, and stopping is
    /// strictly better than spinning: the user still gets "activation unavailable" (secondary launches
    /// fall back to <see cref="ActivationSendResult.NoListener"/>) but no CPU core is burned.
    /// </summary>
    private const int MaxConsecutiveCreateFailures = 10;

    /// <summary>Linear backoff step between recreation attempts (capped by <see cref="RecreateBackoffMaxMs"/>).</summary>
    private const int RecreateBackoffStepMs = 50;

    /// <summary>Upper bound of the recreation backoff, so <see cref="Stop"/> stays responsive.</summary>
    private const int RecreateBackoffMaxMs = 500;

    /// <summary><c>ERROR_PIPE_BUSY</c> — all instances of the named pipe are in use.</summary>
    private const int ErrorPipeBusy = 231;

    /// <summary>
    /// What the pipe's DACL grants the current user — and nobody else (ADR-023 R-14).
    /// </summary>
    /// <remarks>
    /// <see cref="PipeAccessRights.ReadWrite"/> is what the conversation itself needs. The other two
    /// exist so that a <i>competing</i> <c>CreateNamedPipe</c> on the same name — the machine-global
    /// tie-breaker firing, i.e. the same user's other Windows session (ADR-023 D-4/R-8, and note that
    /// <see cref="SingleInstanceNaming.ActivationPipeName"/> is per-user, so the competitor's SID is by
    /// construction the SID this rule names) — gets far enough to be told the truth:
    /// <list type="bullet">
    /// <item><see cref="PipeAccessRights.CreateNewInstance"/> (<c>FILE_CREATE_PIPE_INSTANCE</c>) is
    /// checked against the <i>existing</i> pipe's DACL for every create on an existing name;</item>
    /// <item><see cref="PipeAccessRights.TakeOwnership"/> (<c>WRITE_OWNER</c>) is required because that
    /// competing create also supplies a security descriptor of its own (this very method) — measured
    /// on Windows 11 26200, granting <c>ReadWrite | CreateNewInstance</c> alone still yields
    /// <c>ERROR_ACCESS_DENIED</c>.</item>
    /// </list>
    /// Without both, the competing create fails the access check <i>before</i> the instance-count check
    /// and reports <c>ERROR_ACCESS_DENIED</c>, which <see cref="Classify"/> can only read as
    /// <see cref="ActivationServerUnavailableReason.AclFailed"/> — making
    /// <see cref="ActivationServerUnavailableReason.PipeBusy"/> unreachable and mis-diagnosing the one
    /// scenario the machine-global pipe exists for. Neither right widens exposure: the rule still names
    /// a single SID, the instance count still caps at 1, and that SID is the object's owner anyway
    /// (an owner can rewrite its own DACL regardless). Pinned by <c>T-IPC-02</c>.
    /// </remarks>
    private const PipeAccessRights CompetingCreateRights =
        PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance | PipeAccessRights.TakeOwnership;

    private readonly string _pipeName;
    private readonly int _currentSessionId;
    private readonly object _streamLock = new();

    private CancellationTokenSource? _cts;
    private Thread? _acceptThread;
    private NamedPipeServerStream? _currentStream;
    private bool _started;

    /// <summary>
    /// Initializes a new instance of the <see cref="NamedPipeActivationServer"/> class.
    /// </summary>
    /// <param name="pipeName">Machine-global pipe name (see <see cref="SingleInstanceNaming"/>).</param>
    /// <param name="currentSessionId">
    /// <c>Process.GetCurrentProcess().SessionId</c> of this (primary) process. Compared against each
    /// incoming request's <see cref="ActivationRequest.SessionId"/> to answer
    /// <see cref="ActivationResponseCode.OtherSession"/> instead of activating a window that lives in
    /// a different Windows session (Fast User Switching, ADR-023 D-4).
    /// </param>
    internal NamedPipeActivationServer(string pipeName, int currentSessionId)
    {
        _pipeName = pipeName;
        _currentSessionId = currentSessionId;
    }

    /// <inheritdoc />
    public ActivationServerUnavailableReason? LastStartFailure { get; private set; }

    /// <inheritdoc />
    public bool TryStart(Action<ActivationRequest> onRequest, Func<ActivationResponseCode> statusProvider)
    {
        ArgumentNullException.ThrowIfNull(onRequest);
        ArgumentNullException.ThrowIfNull(statusProvider);

        if (_started)
        {
            // Idempotent per the interface contract.
            return true;
        }

        NamedPipeServerStream initialStream;
        try
        {
            initialStream = CreateServerStream();
        }
        catch (Exception ex)
        {
            // Either ERROR_PIPE_BUSY (another process's server already owns the single instance,
            // ADR-023 D-4) or the ACL could not be created (ADR-023 R-14). Both cases: caller must
            // treat this process as not-the-primary / activation-unavailable, never silently retry
            // without ACL. The classification below only names the failure for the diagnostics log;
            // it deliberately does not branch behavior.
            LastStartFailure = Classify(ex);
            return false;
        }

        LastStartFailure = null;
        _cts = new CancellationTokenSource();
        SetCurrentStream(initialStream);
        _started = true;

        _acceptThread = new Thread(() => AcceptLoop(onRequest, statusProvider, _cts.Token))
        {
            IsBackground = true,
            Name = "DesktopOrganizer.ActivationServer",
        };
        _acceptThread.Start();

        return true;
    }

    /// <inheritdoc />
    public void Stop()
    {
        if (!_started)
        {
            return;
        }

        _started = false;

        _cts?.Cancel();

        // Disposing the stream the accept loop may currently be blocked on inside
        // WaitForConnectionAsync is the only reliable way to abort that wait (there is no
        // connect-with-cancellation overload that also frees a genuinely pending OS-level wait).
        var pending = TakeCurrentStream();
        DisposeQuietly(pending);

        _acceptThread?.Join(TimeSpan.FromSeconds(StopJoinBudgetSeconds));
        _acceptThread = null;

        _cts?.Dispose();
        _cts = null;
    }

    /// <summary>
    /// Names the failure that stopped <see cref="CreateServerStream"/>, for the
    /// <c>ActivationServerUnavailable</c> diagnostics event only.
    /// </summary>
    /// <remarks>
    /// <c>ERROR_PIPE_BUSY</c> is the only evidence that another process actually owns the
    /// machine-global instance, so it is matched on the Win32 code rather than on the exception type:
    /// every other <see cref="IOException"/> (the pipe name is invalid, the object manager is out of
    /// resources, …) says nothing about a competing primary and is reported as
    /// <see cref="ActivationServerUnavailableReason.Faulted"/>. Reaching the busy classification at all
    /// depends on the existing pipe's DACL — see <see cref="CompetingCreateRights"/>, without which
    /// Windows denies the competing create before the instance-count check. The security-related
    /// exceptions all come from the ACL construction path (including this type's own
    /// <see cref="InvalidOperationException"/> for an identity with no SID).
    /// </remarks>
    private static ActivationServerUnavailableReason Classify(Exception exception) => exception switch
    {
        IOException io when (io.HResult & 0xFFFF) == ErrorPipeBusy => ActivationServerUnavailableReason.PipeBusy,
        UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException or ArgumentException =>
            ActivationServerUnavailableReason.AclFailed,
        _ => ActivationServerUnavailableReason.Faulted,
    };

    private static ReadOutcome TryReadLine(NamedPipeServerStream stream, CancellationToken ct, out string line)
    {
        var buffer = new byte[MaxMessageBytes];
        var length = 0;

        while (length < buffer.Length)
        {
            int read;
            try
            {
                read = stream.ReadAsync(buffer.AsMemory(length, 1), ct).AsTask().GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                line = string.Empty;
                return ReadOutcome.Disconnected;
            }

            if (read == 0)
            {
                line = string.Empty;
                return ReadOutcome.Disconnected;
            }

            length++;
            if (buffer[length - 1] == (byte)'\n')
            {
                line = Encoding.UTF8.GetString(buffer, 0, length);
                return ReadOutcome.Complete;
            }
        }

        // Filled the entire MaxMessageBytes budget without ever seeing a line terminator.
        line = string.Empty;
        return ReadOutcome.Oversized;
    }

    private static void WaitForDrainWithinBudget(NamedPipeServerStream stream, CancellationToken ct)
    {
        try
        {
            // WaitForPipeDrain has no cancellable overload; running it on a pool thread and bounding
            // the *wait* (not the call itself) keeps the accept loop's own budget intact even against a
            // client that never reads its response — the pool thread simply outlives this method and
            // is unblocked once Stop()/the next iteration disposes the stream.
            Task.Run(stream.WaitForPipeDrain).Wait(ct);
        }
        catch (Exception)
        {
            // Either the drain genuinely failed (broken pipe) or the budget ran out — either way,
            // Disconnect() proceeds unconditionally right after this call returns.
        }
    }

    /// <summary>
    /// Sleeps between two <see cref="CreateServerStream"/> attempts: linear, capped, and interruptible
    /// by cancellation so <see cref="Stop"/> never has to wait out a backoff.
    /// </summary>
    private static void BackOffBeforeRecreate(int consecutiveFailures, CancellationToken ct)
    {
        var delayMs = Math.Min(RecreateBackoffStepMs * consecutiveFailures, RecreateBackoffMaxMs);

        try
        {
            // WaitHandle rather than Task.Delay/Thread.Sleep: wakes immediately on Cancel().
            ct.WaitHandle.WaitOne(delayMs);
        }
        catch (Exception)
        {
            // The CTS was disposed under us (Stop() raced ahead) — the loop's own cancellation check
            // handles it; a missed backoff is harmless.
        }
    }

    private static void DisposeQuietly(NamedPipeServerStream? stream)
    {
        if (stream is null)
        {
            return;
        }

        try
        {
            stream.Dispose();
        }
        catch (Exception)
        {
            // Best-effort teardown only.
        }
    }

    /// <remarks>
    /// The stream an iteration serves lives in the <c>stream</c> local — <see cref="_currentStream"/> is
    /// only a <i>borrowed handle</i> published for <see cref="Stop"/> (disposing it is the only way to
    /// abort a pending <c>WaitForConnectionAsync</c>), never the loop's own bookkeeping. That
    /// distinction is the fix for the leak that previously bricked the server after its first
    /// activation: the loop used to read the field with <see cref="TakeCurrentStream"/> (which nulls it)
    /// and only re-published a stream it had created itself, so the very first iteration — the one that
    /// adopts the stream created by <see cref="TryStart"/> — left the field <see langword="null"/> and
    /// its <c>finally</c> disposed nothing. With <c>maxNumberOfServerInstances: 1</c>, that one leaked
    /// instance holds the machine-global name forever, so every later
    /// <see cref="CreateServerStream"/> throws <c>ERROR_PIPE_BUSY</c>, gets swallowed, and is retried
    /// with no delay — a permanent 100%-CPU spin, with every activation after the first answered by
    /// nobody.
    /// </remarks>
    private void AcceptLoop(Action<ActivationRequest> onRequest, Func<ActivationResponseCode> statusProvider, CancellationToken ct)
    {
        var consecutiveCreateFailures = 0;

        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? stream = null;

            try
            {
                // Iteration 1 adopts the stream TryStart already created (creating it there, eagerly and
                // synchronously, is what lets TryStart answer PipeBusy at all); every later iteration
                // creates its own replacement, because a NamedPipeServerStream cannot be reused after a
                // client has been served and disconnected.
                stream = TakeCurrentStream() ?? CreateServerStream();
                consecutiveCreateFailures = 0;

                SetCurrentStream(stream);

                stream.WaitForConnectionAsync(ct).GetAwaiter().GetResult();
                HandleConnection(stream, onRequest, statusProvider, ct);
            }
            catch (Exception)
            {
                // Cancellation (Stop()), a client that connects then vanishes, an IO error recreating
                // the stream, anything — one bad iteration must never end the loop (discipline item 5).
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                // stream is still null ⟺ CreateServerStream() itself threw. Retrying that immediately
                // is the one failure mode that can spin a core, so it is the one that gets a bounded
                // backoff and a hard give-up.
                if (stream is null)
                {
                    consecutiveCreateFailures++;
                    if (consecutiveCreateFailures >= MaxConsecutiveCreateFailures)
                    {
                        return;
                    }

                    BackOffBeforeRecreate(consecutiveCreateFailures, ct);
                }
            }
            finally
            {
                // Unpublish before disposing, so Stop() can never hand a disposed stream to anyone, and
                // dispose exactly the instance this iteration owned — the pipe name must be free again
                // before the next CreateServerStream().
                ClearCurrentStream(stream);
                DisposeQuietly(stream);
            }
        }
    }

    private void HandleConnection(
        NamedPipeServerStream stream,
        Action<ActivationRequest> onRequest,
        Func<ActivationResponseCode> statusProvider,
        CancellationToken loopToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(loopToken);
        budget.CancelAfter(TimeSpan.FromMilliseconds(ConnectionBudgetMs));

        // Set only on the one path that accepts the request; handed off *after* the connection is
        // finished with (see the finally below), never before the response is on the wire.
        ActivationRequest? handoff = null;

        try
        {
            // Queried before the request is read/decoded, per ADR-023 D-9: reflects only whether the
            // primary is still accepting activations (Ok) or already in EnterShutdown() (ShuttingDown).
            // OtherSession/Unsupported are decided below, once the request itself is known.
            var primaryStatus = statusProvider();

            var outcome = TryReadLine(stream, budget.Token, out var line);
            if (outcome != ReadOutcome.Complete)
            {
                // Disconnected mid-read, or oversized without a terminator — nothing sensible to
                // answer to (Unsupported requires a decodable, bounded line); let it drop.
                return;
            }

            string responseLine;
            if (!TryDecodeRequest(line, out var request, out var decodeError))
            {
                responseLine = EncodeResponse(decodeError);
            }
            else if (primaryStatus == ActivationResponseCode.ShuttingDown)
            {
                responseLine = EncodeResponse(ActivationResponseCode.ShuttingDown);
            }
            else if (request.SessionId != _currentSessionId)
            {
                responseLine = EncodeResponse(ActivationResponseCode.OtherSession);
            }
            else
            {
                responseLine = EncodeResponse(ActivationResponseCode.Ok);

                // Deliberately NOT invoked here. onRequest ends up (via ActivationRequestRelay) on the
                // UI dispatcher; calling it before the response is written would make the client's
                // round-trip wait for the primary's UI thread — and the per-connection budget below
                // cannot cancel a blocking Dispatcher.Invoke, so a busy UI thread would stall the accept
                // loop and, at shutdown, burn the whole Stop() join budget. Answer first, hand off after.
                handoff = request;
            }

            var bytes = Encoding.UTF8.GetBytes(responseLine);
            stream.WriteAsync(bytes, budget.Token).AsTask().GetAwaiter().GetResult();
            stream.FlushAsync(budget.Token).GetAwaiter().GetResult();

            // Disconnect() below tears the connection down hard and can discard bytes that were
            // written but not yet read by the client — WaitForPipeDrain is the documented way to avoid
            // that race. Bounded by the same per-connection budget: a client that never reads its
            // response must not stall the accept loop indefinitely (discipline item 2).
            WaitForDrainWithinBudget(stream, budget.Token);
        }
        catch (Exception)
        {
            // Budget exceeded, client dropped mid-conversation, pipe error — swallow. The connection
            // is torn down unconditionally below regardless of how this method exits.
        }
        finally
        {
            try
            {
                stream.Disconnect();
            }
            catch (Exception)
            {
                // Already disconnected/disposed/never connected — not fatal.
            }

            // The hand-off happens here, off the critical path of the conversation: the client already
            // has its response (or the connection already failed — in which case the user still asked
            // for an activation and still gets one). It runs in the finally rather than at the end of
            // the try so that a response that timed out mid-write cannot silently swallow the request.
            if (handoff is { } request)
            {
                try
                {
                    // The only work the pipe thread does on behalf of the caller: hand the request off
                    // to the (thread-safe, buffering) relay. No application logic runs here.
                    onRequest(request);
                }
                catch (Exception)
                {
                    // A throwing handler is the handler's problem; it must never kill the accept loop.
                }
            }
        }
    }

    private NamedPipeServerStream CreateServerStream()
    {
        var currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Current Windows identity has no SID; cannot build the activation pipe ACL.");

        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(currentUser, CompetingCreateRights, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            _pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: MaxMessageBytes,
            outBufferSize: MaxMessageBytes,
            pipeSecurity: security);
    }

    /// <summary>
    /// Detaches and returns the published stream. Used by <see cref="Stop"/> (which then disposes it to
    /// abort a pending <c>WaitForConnectionAsync</c>) and once by the accept loop, to adopt the stream
    /// <see cref="TryStart"/> created before the thread existed.
    /// </summary>
    private NamedPipeServerStream? TakeCurrentStream()
    {
        lock (_streamLock)
        {
            var stream = _currentStream;
            _currentStream = null;
            return stream;
        }
    }

    /// <summary>Publishes the stream the accept loop is about to block on, for <see cref="Stop"/>.</summary>
    private void SetCurrentStream(NamedPipeServerStream stream)
    {
        lock (_streamLock)
        {
            _currentStream = stream;
        }
    }

    /// <summary>
    /// Unpublishes <paramref name="stream"/> if (and only if) it is still the published one — a
    /// <see cref="Stop"/> that already took it must not have its <see langword="null"/> overwritten by
    /// a stale iteration finishing up.
    /// </summary>
    private void ClearCurrentStream(NamedPipeServerStream? stream)
    {
        if (stream is null)
        {
            return;
        }

        lock (_streamLock)
        {
            if (ReferenceEquals(_currentStream, stream))
            {
                _currentStream = null;
            }
        }
    }

    private enum ReadOutcome
    {
        Disconnected,
        Complete,
        Oversized,
    }
}
