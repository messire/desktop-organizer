using DesktopOrganizer.UI.Behaviors;

namespace DesktopOrganizer.Tests.Unit.Behaviors;

/// <summary>
/// Unit tests for <see cref="ResizeAffordanceGate"/> (docs/PLANS/phase-6-edit-layout-ux.md §1 D-5,
/// §7.1 item 3) — the composite <see cref="IResizeAffordance"/> sitting between <see cref="RollUpBehavior"/>'s
/// "not rolled up" input and <see cref="Views.ChildWindow"/>'s new "allowed by layout-edit policy" input.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class ResizeAffordanceGateTests
{
    /// <summary>Records every <see cref="IResizeAffordance.SetEnabled"/> call made on it, in order.</summary>
    private sealed class RecordingAffordance : IResizeAffordance
    {
        public List<bool> Calls { get; } = [];

        public void SetEnabled(bool enabled) => Calls.Add(enabled);
    }

    // ------------------------------------------------------------------
    // 2x2 truth table: SetEnabled (not-collapsed) x SetAllowedByPolicy
    // ------------------------------------------------------------------

    [Fact]
    public void BothTrue_InnerNeverDisabled()
    {
        var inner = new RecordingAffordance();
        var gate = new ResizeAffordanceGate(inner);

        gate.SetEnabled(true);
        gate.SetAllowedByPolicy(true);

        Assert.DoesNotContain(false, inner.Calls);
    }

    [Fact]
    public void NotCollapsedTrue_PolicyFalse_InnerDisabled()
    {
        var inner = new RecordingAffordance();
        var gate = new ResizeAffordanceGate(inner);

        gate.SetEnabled(true);
        gate.SetAllowedByPolicy(false);

        Assert.Equal([false], inner.Calls);
    }

    [Fact]
    public void NotCollapsedFalse_PolicyTrue_InnerDisabled()
    {
        var inner = new RecordingAffordance();
        var gate = new ResizeAffordanceGate(inner);

        gate.SetEnabled(false);
        gate.SetAllowedByPolicy(true);

        Assert.Equal([false], inner.Calls);
    }

    [Fact]
    public void BothFalse_InnerDisabledOnlyOnce()
    {
        var inner = new RecordingAffordance();
        var gate = new ResizeAffordanceGate(inner);

        gate.SetEnabled(false);
        gate.SetAllowedByPolicy(false);

        // Both inputs default to true (see ResizeAffordanceGate's remarks), so SetEnabled(false) alone
        // already flips the effective value to false — the subsequent SetAllowedByPolicy(false) must be
        // a no-op relative to the underlying affordance (idempotency, see below).
        Assert.Equal([false], inner.Calls);
    }

    // ------------------------------------------------------------------
    // Idempotency: inner.SetEnabled invoked ONLY when the effective (AND) value actually changes.
    // ------------------------------------------------------------------

    [Fact]
    public void Construction_NeverCallsInner()
    {
        var inner = new RecordingAffordance();
        _ = new ResizeAffordanceGate(inner);

        Assert.Empty(inner.Calls);
    }

    [Fact]
    public void SetEnabled_SameValueAsDefault_DoesNotCallInner()
    {
        var inner = new RecordingAffordance();
        var gate = new ResizeAffordanceGate(inner);

        // Both inputs already default to true — re-asserting true changes nothing about the effective
        // value, so the underlying affordance must never be touched.
        gate.SetEnabled(true);

        Assert.Empty(inner.Calls);
    }

    [Fact]
    public void SetAllowedByPolicy_SameValueAsDefault_DoesNotCallInner()
    {
        var inner = new RecordingAffordance();
        var gate = new ResizeAffordanceGate(inner);

        gate.SetAllowedByPolicy(true);

        Assert.Empty(inner.Calls);
    }

    [Fact]
    public void RepeatedIdenticalCalls_OnlyFirstTransitionReachesInner()
    {
        var inner = new RecordingAffordance();
        var gate = new ResizeAffordanceGate(inner);

        gate.SetEnabled(false);
        gate.SetEnabled(false);
        gate.SetEnabled(false);

        Assert.Equal([false], inner.Calls);
    }

    [Fact]
    public void ToggleBackAndForth_InnerSeesEveryRealTransition()
    {
        var inner = new RecordingAffordance();
        var gate = new ResizeAffordanceGate(inner);

        gate.SetEnabled(false); // effective: false -> inner call
        gate.SetEnabled(true);  // effective: true (policy still default true) -> inner call
        gate.SetAllowedByPolicy(false); // effective: false -> inner call
        gate.SetAllowedByPolicy(true);  // effective: true -> inner call

        Assert.Equal([false, true, false, true], inner.Calls);
    }

    [Fact]
    public void OneInputFalse_OtherInputTogglingHasNoEffectiveChange_InnerNotCalledAgain()
    {
        var inner = new RecordingAffordance();
        var gate = new ResizeAffordanceGate(inner);

        gate.SetEnabled(false); // effective becomes false -> 1 call
        inner.Calls.Clear();

        // Toggling policy while not-collapsed is already false: effective value stays false throughout.
        gate.SetAllowedByPolicy(false);
        gate.SetAllowedByPolicy(true);
        gate.SetAllowedByPolicy(false);

        Assert.Empty(inner.Calls);
    }

    // ------------------------------------------------------------------
    // Interface conformance / null-guard
    // ------------------------------------------------------------------

    [Fact]
    public void ResizeAffordanceGate_ImplementsIResizeAffordance()
    {
        IResizeAffordance gate = new ResizeAffordanceGate(new RecordingAffordance());

        gate.SetEnabled(false);
    }

    [Fact]
    public void Constructor_NullInner_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ResizeAffordanceGate(null!));
    }
}
