using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.SingleInstance;

namespace DesktopOrganizer.Tests.Unit.SingleInstance;

/// <summary>Unit tests for <see cref="SingleInstanceStartupDecisionTable"/> (ADR-023 D-4/D-5/D-7/D-8).</summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class SingleInstanceStartupDecisionTableTests
{
    // T-DEC-01
    [Fact]
    public void Resolve_Acquired_ServerStarted_RunsAsPrimary()
    {
        var decision = SingleInstanceStartupDecisionTable.Resolve(acquired: true, serverStarted: true, last: null, attempts: 1, elapsedMs: 0);

        Assert.Equal(SingleInstanceStartupAction.RunAsPrimary, decision.Action);
        Assert.Equal(SingleInstanceRole.Primary, decision.Role);
    }

    // T-DEC-02: degraded primary (D-4) — pipe server failed to start, still primary.
    [Fact]
    public void Resolve_Acquired_ServerNotStarted_StillRunsAsPrimary_Degraded()
    {
        var decision = SingleInstanceStartupDecisionTable.Resolve(acquired: true, serverStarted: false, last: null, attempts: 1, elapsedMs: 0);

        Assert.Equal(SingleInstanceStartupAction.RunAsPrimary, decision.Action);
        Assert.Equal(SingleInstanceRole.Primary, decision.Role);
    }

    // T-DEC-03
    [Fact]
    public void Resolve_NotAcquired_Delivered_ExitsAfterActivation()
    {
        var decision = SingleInstanceStartupDecisionTable.Resolve(acquired: false, serverStarted: false, last: ActivationSendResult.Delivered, attempts: 2, elapsedMs: 40);

        Assert.Equal(SingleInstanceStartupAction.ExitAfterActivation, decision.Action);
        Assert.Equal(SingleInstanceRole.Secondary, decision.Role);
    }

    // T-DEC-04
    [Fact]
    public void Resolve_NotAcquired_OtherSession_ExitsOtherSession()
    {
        var decision = SingleInstanceStartupDecisionTable.Resolve(acquired: false, serverStarted: false, last: ActivationSendResult.OtherSession, attempts: 1, elapsedMs: 40);

        Assert.Equal(SingleInstanceStartupAction.ExitOtherSession, decision.Action);
        Assert.Equal(SingleInstanceRole.Secondary, decision.Role);
    }

    // T-DEC-05: budget exhausted without delivery — various non-terminal-but-failed send results.
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

    // T-DEC-06: no send was ever attempted (e.g. connect never even started) — still a failure.
    [Fact]
    public void Resolve_NotAcquired_NoSendEverAttempted_ExitsActivationFailed()
    {
        var decision = SingleInstanceStartupDecisionTable.Resolve(acquired: false, serverStarted: false, last: null, attempts: 0, elapsedMs: 0);

        Assert.Equal(SingleInstanceStartupAction.ExitActivationFailed, decision.Action);
        Assert.Equal(SingleInstanceRole.Secondary, decision.Role);
    }

    [Fact]
    public void Resolve_PropagatesAttemptsAndElapsedMs()
    {
        var decision = SingleInstanceStartupDecisionTable.Resolve(acquired: false, serverStarted: false, last: ActivationSendResult.NoListener, attempts: 6, elapsedMs: 3001);

        Assert.Equal(6, decision.Attempts);
        Assert.Equal(3001, decision.ElapsedMs);
        Assert.Equal(ActivationSendResult.NoListener, decision.SendResult);
    }
}
