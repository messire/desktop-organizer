using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.Tests.Unit.Models;

/// <summary>
/// Unit tests for <see cref="DesktopLayerRecoveryPolicy"/> (ADR-034): coalescing, generations and the
/// bounded retry budget — pure logic, no timers.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class DesktopLayerRecoveryPolicyTests
{
    /// <summary>The documented timing constants: 1.5 s coalescing window, 500 ms between retries.</summary>
    [Fact]
    public void Timing_MatchesAdr034()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(1500), DesktopLayerRecoveryPolicy.CoalesceWindow);
        Assert.Equal(TimeSpan.FromMilliseconds(500), DesktopLayerRecoveryPolicy.RetryInterval);
        Assert.Equal(DesktopLayerRecoveryPolicy.DefaultMaxAttempts, new DesktopLayerRecoveryPolicy().MaxAttempts);
    }

    /// <summary>A signal starts a generation and asks the caller to wait for the coalescing window.</summary>
    [Fact]
    public void OnRestartSignal_StartsGeneration_AndReturnsCoalesceWindow()
    {
        var policy = new DesktopLayerRecoveryPolicy();

        var delay = policy.OnRestartSignal();

        Assert.Equal(DesktopLayerRecoveryPolicy.CoalesceWindow, delay);
        Assert.Equal(1, policy.Generation);
        Assert.True(policy.IsActive);
        Assert.Equal(0, policy.AttemptsMade);
    }

    /// <summary>
    /// Five signals in a burst → only the last generation may run an attempt: one recovery per burst.
    /// </summary>
    [Fact]
    public void Burst_OfFiveSignals_AllowsExactlyOneRecovery()
    {
        var policy = new DesktopLayerRecoveryPolicy();
        var scheduled = new List<int>();

        for (var i = 0; i < 5; i++)
        {
            policy.OnRestartSignal();
            scheduled.Add(policy.Generation);
        }

        var ran = scheduled.Count(policy.TryBeginAttempt);

        Assert.Equal(1, ran);
        Assert.Equal(1, policy.AttemptsMade);
    }

    /// <summary>A new signal supersedes an in-flight attempt: its completion is reported as Superseded.</summary>
    [Fact]
    public void NewSignal_SupersedesInFlightAttempt()
    {
        var policy = new DesktopLayerRecoveryPolicy();
        policy.OnRestartSignal();
        var old = policy.Generation;
        Assert.True(policy.TryBeginAttempt(old));

        policy.OnRestartSignal();

        Assert.Equal(DesktopLayerRecoveryStep.Superseded, policy.OnAttemptCompleted(old, needsRetry: true));
        Assert.False(policy.TryBeginAttempt(old));
        Assert.True(policy.IsActive);
        Assert.True(policy.TryBeginAttempt(policy.Generation));
    }

    /// <summary>Success on the first attempt completes the recovery and goes idle.</summary>
    [Fact]
    public void AttemptWithoutRetryNeed_Completes()
    {
        var policy = new DesktopLayerRecoveryPolicy();
        policy.OnRestartSignal();
        policy.TryBeginAttempt(policy.Generation);

        var step = policy.OnAttemptCompleted(policy.Generation, needsRetry: false);

        Assert.Equal(DesktopLayerRecoveryStep.Completed, step);
        Assert.False(policy.IsActive);
        Assert.False(policy.TryBeginAttempt(policy.Generation));
    }

    /// <summary>
    /// A WorkerW that never appears (Windows 11 24H2+ top-level mode) → Retry until the budget is spent,
    /// then Exhausted — bounded, never an endless loop.
    /// </summary>
    [Fact]
    public void RetryBudget_IsBounded_ThenExhausted()
    {
        var policy = new DesktopLayerRecoveryPolicy(maxAttempts: 5);
        policy.OnRestartSignal();
        var generation = policy.Generation;
        var steps = new List<DesktopLayerRecoveryStep>();

        while (policy.TryBeginAttempt(generation))
            steps.Add(policy.OnAttemptCompleted(generation, needsRetry: true));

        Assert.Equal(5, steps.Count);
        Assert.All(steps.Take(4), s => Assert.Equal(DesktopLayerRecoveryStep.Retry, s));
        Assert.Equal(DesktopLayerRecoveryStep.Exhausted, steps[^1]);
        Assert.False(policy.IsActive);
    }

    /// <summary>A WorkerW that appears on the third attempt completes the recovery there.</summary>
    [Fact]
    public void Retry_ThenSuccess_Completes()
    {
        var policy = new DesktopLayerRecoveryPolicy();
        policy.OnRestartSignal();
        var generation = policy.Generation;

        policy.TryBeginAttempt(generation);
        Assert.Equal(DesktopLayerRecoveryStep.Retry, policy.OnAttemptCompleted(generation, needsRetry: true));
        policy.TryBeginAttempt(generation);
        Assert.Equal(DesktopLayerRecoveryStep.Retry, policy.OnAttemptCompleted(generation, needsRetry: true));
        policy.TryBeginAttempt(generation);

        Assert.Equal(DesktopLayerRecoveryStep.Completed, policy.OnAttemptCompleted(generation, needsRetry: false));
        Assert.Equal(3, policy.AttemptsMade);
    }

    /// <summary>A signal after an exhausted recovery starts a fresh generation with a fresh budget.</summary>
    [Fact]
    public void SignalAfterExhausted_ResetsBudget()
    {
        var policy = new DesktopLayerRecoveryPolicy(maxAttempts: 1);
        policy.OnRestartSignal();
        policy.TryBeginAttempt(policy.Generation);
        Assert.Equal(DesktopLayerRecoveryStep.Exhausted, policy.OnAttemptCompleted(policy.Generation, needsRetry: true));

        policy.OnRestartSignal();

        Assert.Equal(0, policy.AttemptsMade);
        Assert.True(policy.TryBeginAttempt(policy.Generation));
    }

    /// <summary>Cancel abandons the active recovery: scheduled attempts no longer run.</summary>
    [Fact]
    public void Cancel_BlocksScheduledAttempts()
    {
        var policy = new DesktopLayerRecoveryPolicy();
        policy.OnRestartSignal();
        var generation = policy.Generation;

        policy.Cancel();

        Assert.False(policy.IsActive);
        Assert.False(policy.TryBeginAttempt(generation));
        Assert.Equal(DesktopLayerRecoveryStep.Superseded, policy.OnAttemptCompleted(generation, needsRetry: false));
    }

    /// <summary>A non-positive budget is clamped to one attempt.</summary>
    /// <param name="requested">The requested (invalid) attempt budget.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void MaxAttempts_IsAtLeastOne(int requested)
    {
        Assert.Equal(1, new DesktopLayerRecoveryPolicy(requested).MaxAttempts);
    }

    /// <summary>An attempt is never begun without a signal.</summary>
    [Fact]
    public void TryBeginAttempt_WithoutSignal_IsFalse()
    {
        Assert.False(new DesktopLayerRecoveryPolicy().TryBeginAttempt(0));
    }
}
