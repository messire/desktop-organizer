using DesktopOrganizer.UI.Interaction;

namespace DesktopOrganizer.Tests.Unit.Interaction;

/// <summary>
/// Unit tests for <see cref="TransitionLatch"/> (M3, release-hardening 1.0 review) — the pure one-bit
/// state machine backing <see cref="DesktopOrganizer.UI.Views.ChildWindow"/>'s DPI-transition span guard
/// and <see cref="DesktopOrganizer.UI.Views.PanelCustomizeWindow"/>'s one-shot re-centre guard.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class TransitionLatchTests
{
    /// <summary>A freshly constructed latch starts cleared.</summary>
    [Fact]
    public void NewLatch_IsNotSet()
    {
        var latch = new TransitionLatch();

        Assert.False(latch.IsSet);
    }

    // ── Span-guard usage (ChildWindow's DPI-transition guard) ──────────────────────────────────────

    /// <summary><see cref="TransitionLatch.Set"/> makes <see cref="TransitionLatch.IsSet"/> true, and reading it repeatedly never consumes it.</summary>
    [Fact]
    public void Set_MakesIsSetTrue_RepeatedReadsDoNotConsumeIt()
    {
        var latch = new TransitionLatch();

        latch.Set();

        Assert.True(latch.IsSet);
        Assert.True(latch.IsSet); // a second (and third) read must not clear it by itself.
        Assert.True(latch.IsSet);
    }

    /// <summary><see cref="TransitionLatch.Clear"/> makes <see cref="TransitionLatch.IsSet"/> false, regardless of how many times <see cref="TransitionLatch.Set"/> was called.</summary>
    [Fact]
    public void Clear_MakesIsSetFalse()
    {
        var latch = new TransitionLatch();
        latch.Set();
        latch.Set();

        latch.Clear();

        Assert.False(latch.IsSet);
    }

    /// <summary><see cref="TransitionLatch.Clear"/> on an already-clear latch is a harmless no-op.</summary>
    [Fact]
    public void Clear_WhenAlreadyClear_IsANoOp()
    {
        var latch = new TransitionLatch();

        latch.Clear();

        Assert.False(latch.IsSet);
    }

    // ── One-shot-consume usage (PanelCustomizeWindow's re-centre-once guard) ───────────────────────

    /// <summary><see cref="TransitionLatch.TryConsume"/> on a set latch returns true and clears it.</summary>
    [Fact]
    public void TryConsume_WhenSet_ReturnsTrue_AndClearsIt()
    {
        var latch = new TransitionLatch();
        latch.Set();

        var consumed = latch.TryConsume();

        Assert.True(consumed);
        Assert.False(latch.IsSet);
    }

    /// <summary><see cref="TransitionLatch.TryConsume"/> on an unset latch returns false and leaves it clear.</summary>
    [Fact]
    public void TryConsume_WhenNotSet_ReturnsFalse()
    {
        var latch = new TransitionLatch();

        Assert.False(latch.TryConsume());
        Assert.False(latch.IsSet);
    }

    /// <summary>
    /// Regression for M3's exact bug shape: after one <see cref="TransitionLatch.Set"/>, only the FIRST
    /// <see cref="TransitionLatch.TryConsume"/> call reports it armed — every later call (simulating a
    /// second, unrelated DpiChanged after the user moved the dialog themselves) reports it unarmed, until
    /// another explicit <see cref="TransitionLatch.Set"/> re-arms it.
    /// </summary>
    [Fact]
    public void TryConsume_OnlyTheFirstCallAfterSet_ReportsArmed()
    {
        var latch = new TransitionLatch();
        latch.Set();

        Assert.True(latch.TryConsume());
        Assert.False(latch.TryConsume()); // a second, later event must not re-trigger the correction.
        Assert.False(latch.TryConsume());

        latch.Set(); // a fresh explicit re-position re-arms it.
        Assert.True(latch.TryConsume());
    }
}
