using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.SingleInstance;

using static DesktopOrganizer.Services.SingleInstance.SingleInstanceHandshakePolicy;

namespace DesktopOrganizer.Tests.Unit.SingleInstance;

/// <summary>
/// Unit tests for <see cref="SingleInstanceHandshakePolicy"/> (ADR-023 D-6). Drives the state machine
/// using the exact loop documented on the type itself — see its XML doc <c>&lt;remarks&gt;</c>.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class SingleInstanceHandshakePolicyTests
{
    // T-POL-01
    [Fact]
    public void NextStep_AlternatesTryAcquireAndTrySend_UntilGiveUp()
    {
        var policy = new SingleInstanceHandshakePolicy(
            maxAttempts: 3,
            connectTimeout: TimeSpan.FromMilliseconds(300),
            budget: TimeSpan.FromSeconds(10), // large: exercise the attempt-count limit, not the budget
            backoff: [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero]);

        var steps = new List<HandshakeStep>();
        int attempt = 0;
        ActivationSendResult? last = null;

        while (true)
        {
            var step = policy.NextStep(attempt, elapsedMs: 0, last, out _);
            steps.Add(step);

            if (step == HandshakeStep.GiveUp)
            {
                break;
            }

            if (step == HandshakeStep.TryAcquire)
            {
                attempt++;
                last = null; // acquire always fails in this scenario — send is still pending
            }
            else
            {
                last = ActivationSendResult.NoListener; // never delivered, never other-session
            }
        }

        Assert.Equal(
            [
                HandshakeStep.TryAcquire, HandshakeStep.TrySend,
                HandshakeStep.TryAcquire, HandshakeStep.TrySend,
                HandshakeStep.TryAcquire, HandshakeStep.TrySend,
                HandshakeStep.GiveUp,
            ],
            steps);
    }

    // T-POL-02
    [Fact]
    public void NextStep_TryAcquireDelays_FollowBackoffSchedule_InOrder()
    {
        var backoff = new[]
        {
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(50),
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(200),
            TimeSpan.FromMilliseconds(400),
            TimeSpan.FromMilliseconds(800),
        };
        var policy = new SingleInstanceHandshakePolicy(6, TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(10), backoff);

        var observedDelays = new List<TimeSpan>();
        int attempt = 0;
        ActivationSendResult? last = null;

        while (true)
        {
            var step = policy.NextStep(attempt, elapsedMs: 0, last, out var delay);

            if (step == HandshakeStep.GiveUp)
            {
                break;
            }

            if (step == HandshakeStep.TryAcquire)
            {
                observedDelays.Add(delay);
                attempt++;
                last = null;
            }
            else
            {
                last = ActivationSendResult.NoListener;
            }
        }

        Assert.Equal(backoff, observedDelays);
    }

    // T-POL-03
    [Fact]
    public void NextStep_StopsWhenBudgetExhausted_EvenWithAttemptsRemaining()
    {
        var policy = new SingleInstanceHandshakePolicy(
            maxAttempts: 100, // deliberately far from being the limiting factor
            connectTimeout: TimeSpan.FromMilliseconds(300),
            budget: TimeSpan.FromMilliseconds(250),
            backoff: [TimeSpan.Zero]);

        // First attempt: acquire fails, send fails — completed "instantly" (elapsed still small).
        var first = policy.NextStep(0, elapsedMs: 0, last: null, out _);
        Assert.Equal(HandshakeStep.TryAcquire, first);

        var second = policy.NextStep(1, elapsedMs: 0, last: null, out _);
        Assert.Equal(HandshakeStep.TrySend, second);

        // Budget already exceeded by the time the send completed.
        var afterBudgetExceeded = policy.NextStep(1, elapsedMs: 9999, last: ActivationSendResult.NoListener, out _);

        Assert.Equal(HandshakeStep.GiveUp, afterBudgetExceeded);
    }

    // T-POL-04
    [Fact]
    public void NextStep_PrimaryShuttingDown_NextStepIsTryAcquire()
    {
        var policy = SingleInstanceHandshakePolicy.Default;

        var step = policy.NextStep(1, elapsedMs: 100, ActivationSendResult.PrimaryShuttingDown, out _);

        Assert.Equal(HandshakeStep.TryAcquire, step);
    }

    // T-POL-05
    [Fact]
    public void NextStep_Delivered_GivesUpImmediately()
    {
        var policy = SingleInstanceHandshakePolicy.Default;

        var step = policy.NextStep(1, elapsedMs: 100, ActivationSendResult.Delivered, out var delay);

        Assert.Equal(HandshakeStep.GiveUp, step);
        Assert.Equal(TimeSpan.Zero, delay);
    }

    [Fact]
    public void NextStep_OtherSession_GivesUpImmediately()
    {
        var policy = SingleInstanceHandshakePolicy.Default;

        var step = policy.NextStep(1, elapsedMs: 100, ActivationSendResult.OtherSession, out _);

        Assert.Equal(HandshakeStep.GiveUp, step);
    }

    [Fact]
    public void Default_HasDocumentedSchedule()
    {
        var policy = SingleInstanceHandshakePolicy.Default;

        Assert.Equal(TimeSpan.FromMilliseconds(300), policy.ConnectTimeout);
    }

    [Fact]
    public void NextStep_VeryFirstCall_ReturnsTryAcquireWithZeroDelay()
    {
        var policy = SingleInstanceHandshakePolicy.Default;

        var step = policy.NextStep(0, elapsedMs: 0, last: null, out var delay);

        Assert.Equal(HandshakeStep.TryAcquire, step);
        Assert.Equal(TimeSpan.Zero, delay);
    }

    [Fact]
    public void Constructor_NegativeConnectTimeout_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SingleInstanceHandshakePolicy(6, TimeSpan.FromMilliseconds(-1), TimeSpan.FromSeconds(1), []));
    }

    [Fact]
    public void Constructor_NullBackoff_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new SingleInstanceHandshakePolicy(6, TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(1), null!));
    }
}
