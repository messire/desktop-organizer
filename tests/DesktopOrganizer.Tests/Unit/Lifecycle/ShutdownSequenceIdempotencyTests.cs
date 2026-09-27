using DesktopOrganizer.UI.Lifecycle;

namespace DesktopOrganizer.Tests.Unit.Lifecycle;

/// <summary>
/// Unit tests for <see cref="ShutdownSequence"/> (ADR-033 part 2, plan §2 Q2) — the exact idempotency
/// guarantee <c>App.OnExit</c>/<c>SessionEnding</c>/a UI-thread crash's own "save state" step all rely on.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class ShutdownSequenceIdempotencyTests
{
    /// <summary>Every constructor-supplied step runs, in the order given.</summary>
    [Fact]
    public void Run_ExecutesEveryStepExactlyOnceInOrder()
    {
        var order = new List<int>();
        var sut = new ShutdownSequence(
            () => order.Add(0),
            () => order.Add(1),
            () => order.Add(2));

        sut.Run();

        Assert.Equal(new List<int> { 0, 1, 2 }, order);
    }

    /// <summary>A second, third, … call to <see cref="ShutdownSequence.Run"/> never re-runs any step.</summary>
    [Fact]
    public void Run_CalledAgain_IsANoOp()
    {
        var count = 0;
        var sut = new ShutdownSequence(() => count++);

        sut.Run();
        sut.Run();
        sut.Run();

        Assert.Equal(1, count);
    }

    /// <summary>
    /// The scenario this type exists for: a <c>SessionEnding</c> handler runs the sequence first, then
    /// <c>App.OnExit</c> reaches the very same instance afterwards — the second call must be a no-op,
    /// not a second save/dispose.
    /// </summary>
    [Fact]
    public void Run_SessionEndingThenOnExit_OnlyTheFirstCallRunsTheSteps()
    {
        var order = new List<string>();
        var sut = new ShutdownSequence(
            () => order.Add("EnterShutdown"),
            () => order.Add("RestoreIcons"),
            () => order.Add("SaveNow"),
            () => order.Add("Dispose"));

        // Simulates SessionEnding firing first…
        sut.Run();

        // …then App.OnExit reaching the same instance moments later.
        sut.Run();

        Assert.Equal(new List<string> { "EnterShutdown", "RestoreIcons", "SaveNow", "Dispose" }, order);
    }

    /// <summary>The constructor rejects a <see langword="null"/> steps array.</summary>
    [Fact]
    public void Constructor_NullSteps_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ShutdownSequence(null!));
    }

    /// <summary>
    /// M7 (release-hardening review): a step that throws (e.g. a misbehaving <c>EnterShutdown</c>) must
    /// not abort the sequence — every later step (icons restore, save, log flush, dispose) still runs,
    /// and <see cref="ShutdownSequence.Run"/> itself does not propagate the exception.
    /// </summary>
    [Fact]
    public void Run_StepThrows_LaterStepsStillRunAndExceptionDoesNotEscape()
    {
        var order = new List<string>();
        var sut = new ShutdownSequence(
            () => throw new InvalidOperationException("EnterShutdown boom"),
            () => order.Add("RestoreIcons"),
            () => order.Add("SaveNow"),
            () => order.Add("FlushLog"),
            () => order.Add("Dispose"));

        var exception = Record.Exception(() => sut.Run());

        Assert.Null(exception);
        Assert.Equal(new List<string> { "RestoreIcons", "SaveNow", "FlushLog", "Dispose" }, order);
    }

    /// <summary>A step throwing mid-sequence does not stop it from being marked as having run once.</summary>
    [Fact]
    public void Run_StepThrows_SubsequentRunCallIsStillANoOp()
    {
        var count = 0;
        var sut = new ShutdownSequence(
            () => throw new InvalidOperationException("boom"),
            () => count++);

        sut.Run();
        sut.Run();

        Assert.Equal(1, count);
    }
}
