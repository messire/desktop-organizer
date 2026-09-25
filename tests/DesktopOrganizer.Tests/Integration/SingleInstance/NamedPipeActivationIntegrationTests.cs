using System.Collections.Concurrent;
using System.Diagnostics;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Interfaces;
using DesktopOrganizer.Services.SingleInstance;
using Moq;
using static DesktopOrganizer.Services.SingleInstance.ActivationMessageCodec;

namespace DesktopOrganizer.Tests.Integration.SingleInstance;

/// <summary>
/// End-to-end tests for <see cref="NamedPipeActivationServer"/>/<see cref="NamedPipeActivationClient"/>
/// (ADR-023 D-4/D-5) against real named pipes — server and client run in this same test process, on
/// a uniquely-named pipe per test so parallel runs (and the real application, if it happens to be
/// running on the same box) never collide. Named pipes are kernel objects, not GUI, so this is
/// expected to be green under <c>dotnet test</c> on any Windows host (phase-3 plan §5.5).
/// </summary>
[Trait(IntegrationTests, nameof(DesktopOrganizer.Services))]
public class NamedPipeActivationIntegrationTests
{
    /// <summary>
    /// Upper bound for every wait/connect budget in this file that stands in for "the round trip
    /// eventually completes", not "the round trip is fast" (see
    /// <see cref="Client_GetsItsResponse_WithoutWaitingForASlowOnRequestHandler"/> for the one test that
    /// genuinely asserts speed and therefore can't just use this constant). Named pipes are kernel
    /// objects — normally this resolves in single-digit milliseconds — but this test process shares a
    /// thread pool with the rest of the suite's leaked <see cref="System.Threading.Timer"/> callbacks
    /// (see docs/Refactoring/Testing/sta-test-conventions.md, "wall-clock тесты"), so a budget tight
    /// enough to fail fast on a genuine hang (the original 2s) has also been observed to fail on a
    /// merely-delayed-not-hung run. 10s is generous enough that a real server-side hang (this file's own
    /// <see cref="Stop_CompletesWithinBudget_WhileAcceptLoopIsBlockedWaitingForAConnection"/> already
    /// covers that failure mode on its own terms) would still be caught well before a human notices.
    /// </summary>
    private static readonly TimeSpan GenerousWaitBudget = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Verifies that an <see cref="ActivationRequest"/> sent by <see cref="NamedPipeActivationClient"/>
    /// is received intact by <see cref="NamedPipeActivationServer"/>'s <c>onRequest</c> callback, and
    /// that the client observes <see cref="ActivationSendResult.Delivered"/> (T-IPC-01).
    /// </summary>
    [Fact]
    public void Server_And_Client_DeliverActivationRequest_EndToEnd()
    {
        var pipeName = UniquePipeName();
        var server = new NamedPipeActivationServer(pipeName, currentSessionId: 5);
        ActivationRequest? received = null;
        using var receivedEvent = new ManualResetEventSlim(false);

        try
        {
            Assert.True(server.TryStart(
                onRequest: r =>
                {
                    received = r;
                    receivedEvent.Set();
                },
                statusProvider: () => ActivationResponseCode.Ok));

            var client = new NamedPipeActivationClient(pipeName, NoOpNativeApi);
            var request = new ActivationRequest(1, ActivationCommand.ActivateSettings, 5);

            var result = client.TrySend(request, GenerousWaitBudget);

            Assert.Equal(ActivationSendResult.Delivered, result);
            Assert.True(receivedEvent.Wait(GenerousWaitBudget), "onRequest callback was not invoked within the generous wait budget — see GenerousWaitBudget's doc comment.");
            Assert.Equal(request, received);
        }
        finally
        {
            server.Stop();
        }
    }

    /// <summary>
    /// Verifies that a second <see cref="NamedPipeActivationServer"/> started on the same pipe name
    /// fails to start and reports <see cref="ActivationServerUnavailableReason.PipeBusy"/> (not
    /// <c>AclFailed</c>), matching the real <c>ERROR_PIPE_BUSY</c> Win32 error for a
    /// <c>maxNumberOfServerInstances: 1</c> pipe already owned by another server (T-IPC-02).
    /// </summary>
    [Fact]
    public void TryStart_SecondServerWithSameName_ReturnsFalse_PipeBusy()
    {
        var pipeName = UniquePipeName();
        var server1 = new NamedPipeActivationServer(pipeName, currentSessionId: 1);
        var server2 = new NamedPipeActivationServer(pipeName, currentSessionId: 1);

        try
        {
            Assert.True(server1.TryStart(_ => { }, () => ActivationResponseCode.Ok), "server1 (the only server on this pipe name so far) failed to start.");

            // maxNumberOfServerInstances: 1 (ADR-023 D-4) — a second server on the same machine-global
            // name must fail to start rather than silently queue up behind the first.
            Assert.False(server2.TryStart(_ => { }, () => ActivationResponseCode.Ok), "server2 unexpectedly started against a pipe name server1 already owns.");

            // ...and it must be *named* as the tie-breaker firing, not as a broken ACL. This asserts
            // the real Win32 error: the pipe's DACL grants CreateNewInstance to the current user, so
            // the competing create reaches the instance-count check and fails with ERROR_PIPE_BUSY
            // (IOException → PipeBusy). Granting only ReadWrite makes Windows answer
            // ERROR_ACCESS_DENIED first, which Classify maps to AclFailed — the FUS/R-8 diagnostics
            // would then point at the wrong cause.
            Assert.Equal(ActivationServerUnavailableReason.PipeBusy, server2.LastStartFailure);
        }
        finally
        {
            server1.Stop();
            server2.Stop();
        }
    }

    /// <summary>
    /// Regression for C-1: three activations in a row, not one.
    /// </summary>
    /// <remarks>
    /// The accept loop used to hand the first iteration the stream <c>TryStart</c> had created by
    /// <i>taking</i> it out of the <c>_currentStream</c> field, and only re-published a stream it had
    /// created itself — so on that first iteration the field stayed <see langword="null"/> and the
    /// iteration's <c>finally</c> disposed nothing. Since the pipe is created with
    /// <c>maxNumberOfServerInstances: 1</c>, that single leaked instance owned the name forever: every
    /// subsequent <c>CreateServerStream()</c> threw <c>ERROR_PIPE_BUSY</c> into the loop's blanket
    /// <c>catch</c> and was retried with no delay (100% of a CPU core), while every activation after
    /// the first got <see cref="ActivationSendResult.NoListener"/> — i.e. the third launch of the app
    /// showed the "not responding" dialog instead of activating the primary's window. One delivered
    /// activation therefore proves nothing; the second and third are the test.
    /// </remarks>
    [Fact]
    public void Server_ServesSequentialActivations_WithoutLeakingThePipeInstance()
    {
        var pipeName = UniquePipeName();
        var server = new NamedPipeActivationServer(pipeName, currentSessionId: 7);
        var received = new ConcurrentQueue<ActivationRequest>();
        using var delivered = new SemaphoreSlim(0);

        try
        {
            Assert.True(server.TryStart(
                onRequest: r =>
                {
                    received.Enqueue(r);
                    delivered.Release();
                },
                statusProvider: () => ActivationResponseCode.Ok));

            var client = new NamedPipeActivationClient(pipeName, NoOpNativeApi);

            for (var round = 1; round <= 3; round++)
            {
                var request = new ActivationRequest(1, ActivationCommand.ActivateSettings, 7);

                var result = client.TrySend(request, GenerousWaitBudget);

                Assert.True(
                    result == ActivationSendResult.Delivered,
                    $"Activation #{round} returned {result}; the server stopped listening after activation #{round - 1}.");
                Assert.True(
                    delivered.Wait(GenerousWaitBudget),
                    $"Activation #{round} was answered but never reached onRequest within the generous wait budget.");
            }

            Assert.Equal(3, received.Count);
        }
        finally
        {
            server.Stop();
        }
    }

    /// <summary>
    /// How long the deliberately slow handler in
    /// <see cref="Client_GetsItsResponse_WithoutWaitingForASlowOnRequestHandler"/> sleeps before
    /// signalling <c>handlerFinished</c>. Comfortably larger than any plausible pipe round-trip —
    /// including one delayed by thread-pool contention from the rest of the suite (see
    /// <see cref="GenerousWaitBudget"/>'s doc comment) — so "the handler had not finished yet when
    /// TrySend returned" stays a reliable signal instead of a coin flip against a tight wall-clock
    /// margin (the original assertion compared a fixed 800ms ceiling against a fixed 1200ms handler
    /// delay — a 400ms margin that thread-pool jitter could and did close). Deliberately kept below
    /// <c>NamedPipeActivationServer</c>'s own 2s <c>Stop()</c> join budget (not editable from this test
    /// file) so the <c>finally</c> block's <c>server.Stop()</c> still cleanly joins the accept thread
    /// before this method's <see langword="using"/> locals are disposed, instead of racing a
    /// disposed <see cref="ManualResetEventSlim"/> against a handler still sleeping in the background.
    /// </summary>
    private static readonly TimeSpan SlowHandlerDelay = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// Regression for M-1: the response must be on the wire before <c>onRequest</c> runs.
    /// </summary>
    /// <remarks>
    /// In the real application <c>onRequest</c> reaches a blocking <c>Dispatcher.Invoke</c> on the
    /// primary's UI thread; invoking it before writing the response made the secondary process's
    /// round-trip wait for that thread — unbounded, since the per-connection budget cannot cancel a
    /// blocking dispatcher call — and made <c>Stop()</c> burn its full 2s join budget at shutdown. A
    /// deliberately slow handler stands in for the busy UI thread here.
    /// </remarks>
    /// <remarks>
    /// Asserts a condition (<c>handlerFinished</c> not yet set), not a wall-clock ceiling: the previous
    /// version measured <c>TrySend</c>'s elapsed time against a fixed 800ms budget, which raced the
    /// suite's own thread-pool contention (docs/Refactoring/Testing/sta-test-conventions.md, "wall-clock
    /// тесты") rather than the property under test.
    /// </remarks>
    [Fact]
    public void Client_GetsItsResponse_WithoutWaitingForASlowOnRequestHandler()
    {
        var pipeName = UniquePipeName();
        var server = new NamedPipeActivationServer(pipeName, currentSessionId: 3);
        using var handlerEntered = new ManualResetEventSlim(false);
        using var handlerFinished = new ManualResetEventSlim(false);

        try
        {
            Assert.True(server.TryStart(
                onRequest: _ =>
                {
                    handlerEntered.Set();
                    Thread.Sleep(SlowHandlerDelay);
                    handlerFinished.Set();
                },
                statusProvider: () => ActivationResponseCode.Ok));

            var client = new NamedPipeActivationClient(pipeName, NoOpNativeApi);

            var result = client.TrySend(new ActivationRequest(1, ActivationCommand.ActivateSettings, 3), GenerousWaitBudget);

            Assert.Equal(ActivationSendResult.Delivered, result);
            Assert.False(handlerFinished.IsSet, $"TrySend returned only after the {SlowHandlerDelay.TotalSeconds}s handler had already finished; the response must be written before onRequest runs (M-1 regression).");
            Assert.True(handlerEntered.Wait(GenerousWaitBudget), "onRequest never ran within the generous wait budget.");
        }
        finally
        {
            server.Stop();
        }
    }

    /// <summary>
    /// Verifies that <see cref="NamedPipeActivationServer.Stop"/> completes within its bounded join
    /// budget even while the background accept loop is blocked waiting for a connection that never
    /// arrives (ADR-023 R-10, T-IPC-03).
    /// </summary>
    [Fact]
    public void Stop_CompletesWithinBudget_WhileAcceptLoopIsBlockedWaitingForAConnection()
    {
        var server = new NamedPipeActivationServer(UniquePipeName(), currentSessionId: 1);
        Assert.True(server.TryStart(_ => { }, () => ActivationResponseCode.Ok));

        // Give the background accept thread a moment to actually enter WaitForConnectionAsync — this
        // is the "hung, no client ever shows up" case Stop() must still recover from (ADR-023 R-10).
        // Not a success condition (nothing below asserts the loop actually got there by this point),
        // only a best-effort nudge to exercise that path more often than not.
        Thread.Sleep(200);

        var stopwatch = Stopwatch.StartNew();
        server.Stop();
        stopwatch.Stop();

        // Production's own bounded join is 2s (NamedPipeActivationServer.StopJoinBudgetSeconds, not
        // editable from this test file) — 5s leaves generous slack for thread-pool/scheduler jitter
        // from the rest of the suite (docs/Refactoring/Testing/sta-test-conventions.md, "wall-clock
        // тесты") while still catching a genuine regression that makes Stop() hang indefinitely.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Stop() took {stopwatch.Elapsed}, expected well under the 2s bounded join plus slack.");
    }

    // Beyond the plan's required T-IPC-01..03: exercises the server's own status/session branching,
    // which is new logic introduced by this commit and not covered by the pure ActivationMessageCodec
    // unit tests (those only round-trip strings, they never see a real connection).

    /// <summary>
    /// Verifies that when the server's <c>statusProvider</c> reports
    /// <see cref="ActivationResponseCode.ShuttingDown"/>, the client observes
    /// <see cref="ActivationSendResult.PrimaryShuttingDown"/> and <c>onRequest</c> is never invoked.
    /// </summary>
    [Fact]
    public void Client_ReceivesShuttingDown_WhenStatusProviderReportsShuttingDown_AndOnRequestNeverFires()
    {
        var pipeName = UniquePipeName();
        var server = new NamedPipeActivationServer(pipeName, currentSessionId: 5);
        var onRequestCalls = 0;

        try
        {
            Assert.True(server.TryStart(_ => Interlocked.Increment(ref onRequestCalls), () => ActivationResponseCode.ShuttingDown));

            var client = new NamedPipeActivationClient(pipeName, NoOpNativeApi);
            var result = client.TrySend(new ActivationRequest(1, ActivationCommand.ActivateSettings, 5), GenerousWaitBudget);

            Assert.Equal(ActivationSendResult.PrimaryShuttingDown, result);
            Assert.Equal(0, onRequestCalls);
        }
        finally
        {
            server.Stop();
        }
    }

    /// <summary>
    /// Verifies that when the request's session id differs from the server's own session (Fast User
    /// Switching, ADR-023 D-4), the client observes <see cref="ActivationSendResult.OtherSession"/> and
    /// <c>onRequest</c> is never invoked.
    /// </summary>
    [Fact]
    public void Client_ReceivesOtherSession_WhenRequestSessionDiffersFromServersOwnSession_AndOnRequestNeverFires()
    {
        var pipeName = UniquePipeName();
        var server = new NamedPipeActivationServer(pipeName, currentSessionId: 5);
        var onRequestCalls = 0;

        try
        {
            Assert.True(server.TryStart(_ => Interlocked.Increment(ref onRequestCalls), () => ActivationResponseCode.Ok));

            var client = new NamedPipeActivationClient(pipeName, NoOpNativeApi);

            // Session 999 != the server's own session (5) — Fast User Switching scenario (ADR-023 D-4).
            var result = client.TrySend(new ActivationRequest(1, ActivationCommand.ActivateSettings, 999), GenerousWaitBudget);

            Assert.Equal(ActivationSendResult.OtherSession, result);
            Assert.Equal(0, onRequestCalls);
        }
        finally
        {
            server.Stop();
        }
    }

    /// <summary>
    /// Verifies that <see cref="NamedPipeActivationClient.TrySend"/> returns
    /// <see cref="ActivationSendResult.NoListener"/> promptly when no server is listening on the pipe.
    /// </summary>
    [Fact]
    public void TrySend_NoServerListening_ReturnsNoListener()
    {
        var client = new NamedPipeActivationClient(UniquePipeName(), NoOpNativeApi);

        var result = client.TrySend(new ActivationRequest(1, ActivationCommand.ActivateSettings, 1), TimeSpan.FromMilliseconds(300));

        Assert.Equal(ActivationSendResult.NoListener, result);
    }

    /// <summary>
    /// The client's only use of <see cref="INativeApi"/> is the <c>AllowSetForegroundWindow</c>
    /// handoff (ADR-023 D-8/R-13), which is a pure side effect on the Windows foreground state and has
    /// nothing to do with what these tests exercise (the wire protocol). A loose mock returns
    /// <see langword="false"/> — exactly what the real call returns for a process that holds no
    /// foreground right, e.g. a headless test runner.
    /// </summary>
    private static INativeApi NoOpNativeApi => new Mock<INativeApi>().Object;

    private static string UniquePipeName() => $"DesktopOrganizer.Test.Activation.{Guid.NewGuid():N}";
}
