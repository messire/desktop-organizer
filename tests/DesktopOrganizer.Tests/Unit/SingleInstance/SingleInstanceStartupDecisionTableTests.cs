using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.SingleInstance;

namespace DesktopOrganizer.Tests.Unit.SingleInstance;

/// <summary>Unit tests for <see cref="SingleInstanceStartupDecisionTable"/> (ADR-023 D-4/D-5/D-7/D-8).</summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class SingleInstanceStartupDecisionTableTests
{
    /// <summary>
    /// Verifies that <see cref="SingleInstanceStartupDecisionTable.Resolve"/> resolves to
    /// <see cref="SingleInstanceStartupAction.RunAsPrimary"/> when the guard was acquired and the
    /// server started (T-DEC-01).
    /// </summary>
    [Fact]
    public void Resolve_Acquired_ServerStarted_RunsAsPrimary()
    {
        var decision = SingleInstanceStartupDecisionTable.Resolve(acquired: true, serverStarted: true, last: null, attempts: 1, elapsedMs: 0);

        Assert.Equal(SingleInstanceStartupAction.RunAsPrimary, decision.Action);
        Assert.Equal(SingleInstanceRole.Primary, decision.Role);
    }

    /// <summary>
    /// Verifies that <see cref="SingleInstanceStartupDecisionTable.Resolve"/> still resolves to
    /// <see cref="SingleInstanceStartupAction.RunAsPrimary"/> (degraded) when the guard was acquired
    /// but the pipe server failed to start (T-DEC-02, D-4).
    /// </summary>
    [Fact]
    public void Resolve_Acquired_ServerNotStarted_StillRunsAsPrimary_Degraded()
    {
        var decision = SingleInstanceStartupDecisionTable.Resolve(acquired: true, serverStarted: false, last: null, attempts: 1, elapsedMs: 0);

        Assert.Equal(SingleInstanceStartupAction.RunAsPrimary, decision.Action);
        Assert.Equal(SingleInstanceRole.Primary, decision.Role);
    }

    /// <summary>
    /// Verifies that <see cref="SingleInstanceStartupDecisionTable.Resolve"/> resolves to
    /// <see cref="SingleInstanceStartupAction.ExitAfterActivation"/> when the guard was not acquired
    /// and the send was delivered (T-DEC-03).
    /// </summary>
    [Fact]
    public void Resolve_NotAcquired_Delivered_ExitsAfterActivation()
    {
        var decision = SingleInstanceStartupDecisionTable.Resolve(acquired: false, serverStarted: false, last: ActivationSendResult.Delivered, attempts: 2, elapsedMs: 40);

        Assert.Equal(SingleInstanceStartupAction.ExitAfterActivation, decision.Action);
        Assert.Equal(SingleInstanceRole.Secondary, decision.Role);
    }

    /// <summary>
    /// Verifies that <see cref="SingleInstanceStartupDecisionTable.Resolve"/> resolves to
    /// <see cref="SingleInstanceStartupAction.ExitOtherSession"/> when the guard was not acquired and
    /// the send result is <see cref="ActivationSendResult.OtherSession"/> (T-DEC-04).
    /// </summary>
    [Fact]
    public void Resolve_NotAcquired_OtherSession_ExitsOtherSession()
    {
        var decision = SingleInstanceStartupDecisionTable.Resolve(acquired: false, serverStarted: false, last: ActivationSendResult.OtherSession, attempts: 1, elapsedMs: 40);

        Assert.Equal(SingleInstanceStartupAction.ExitOtherSession, decision.Action);
        Assert.Equal(SingleInstanceRole.Secondary, decision.Role);
    }

    /// <summary>
    /// Verifies that <see cref="SingleInstanceStartupDecisionTable.Resolve"/> resolves to
    /// <see cref="SingleInstanceStartupAction.ExitActivationFailed"/> for every non-terminal-but-failed
    /// send result once the attempt budget is exhausted (T-DEC-05).
    /// </summary>
    /// <param name="last">The last send result observed before the budget was exhausted.</param>
    [Theory]
    [InlineData(ActivationSendResult.NoListener)]
    [InlineData(ActivationSendResult.Timeout)]
    [InlineData(ActivationSendResult.Faulted)]
    [InlineData(ActivationSendResult.ProtocolError)]
    [InlineData(ActivationSendResult.PrimaryShuttingDown)]
    public void Resolve_NotAcquired_BudgetExhausted_ExitsActivationFailed(ActivationSendResult last)
    {
        var decision = SingleInstanceStartupDecisionTable.Resolve(acquired: false, serverStarted: false, last, attempts: 6, elapsedMs: 3000);

        Assert.Equal(SingleInstanceStartupAction.ExitActivationFailed, decision.Action);
        Assert.Equal(SingleInstanceRole.Secondary, decision.Role);
    }

    /// <summary>
    /// Verifies that <see cref="SingleInstanceStartupDecisionTable.Resolve"/> resolves to
    /// <see cref="SingleInstanceStartupAction.ExitActivationFailed"/> when no send was ever attempted
    /// (e.g. connect never even started) (T-DEC-06).
    /// </summary>
    [Fact]
    public void Resolve_NotAcquired_NoSendEverAttempted_ExitsActivationFailed()
    {
        var decision = SingleInstanceStartupDecisionTable.Resolve(acquired: false, serverStarted: false, last: null, attempts: 0, elapsedMs: 0);

        Assert.Equal(SingleInstanceStartupAction.ExitActivationFailed, decision.Action);
        Assert.Equal(SingleInstanceRole.Secondary, decision.Role);
    }

    /// <summary>
    /// Verifies that <see cref="SingleInstanceStartupDecisionTable.Resolve"/> propagates the given
    /// attempt count, elapsed time, and last send result verbatim into the returned decision.
    /// </summary>
    [Fact]
    public void Resolve_PropagatesAttemptsAndElapsedMs()
    {
        var decision = SingleInstanceStartupDecisionTable.Resolve(acquired: false, serverStarted: false, last: ActivationSendResult.NoListener, attempts: 6, elapsedMs: 3001);

        Assert.Equal(6, decision.Attempts);
        Assert.Equal(3001, decision.ElapsedMs);
        Assert.Equal(ActivationSendResult.NoListener, decision.SendResult);
    }
}
