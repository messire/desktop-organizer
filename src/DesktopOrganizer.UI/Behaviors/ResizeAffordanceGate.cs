namespace DesktopOrganizer.UI.Behaviors;

/// <summary>
/// Composes two independent conditions — "not rolled up" and "allowed by the current layout-edit
/// policy" — into the single <see cref="IResizeAffordance.SetEnabled"/> call the underlying affordance
/// (<see cref="NativeSizingAffordance"/>) actually understands
/// (docs/PLANS/phase-6-edit-layout-ux.md §1 D-5). <see cref="RollUpBehavior"/> receives THIS object in
/// its <c>resizeAffordance</c> constructor parameter and keeps calling <see cref="SetEnabled"/> exactly
/// as before — unaware that resize can now also be gated by layout-edit policy — while
/// <see cref="Views.ChildWindow"/> drives the new <see cref="SetAllowedByPolicy"/> input whenever
/// <see cref="Services.ILayoutEditModeService.ModeChanged"/> fires or the Normal-mode gesture
/// preference/lock changes.
/// </summary>
/// <remarks>
/// Pure, headless, and WPF-free: depends only on <see cref="IResizeAffordance"/>. On the native
/// <c>WM_SIZING</c> path, <c>NativePanelResizeController.isResizeEnabled</c> still reads a flat
/// <c>NativeSizingAffordance.IsEnabled</c> field on every <c>WM_NCHITTEST</c> (R-2) — this gate WRITES
/// into that affordance ahead of time, on mode/policy change, never from inside the hook callback
/// itself (CLAUDE.md invariant: minimum work in the hook).
/// </remarks>
public sealed class ResizeAffordanceGate : IResizeAffordance
{
    private readonly IResizeAffordance _inner;

    // Both inputs default to true: RollUpBehavior only calls SetEnabled(false) once the panel is
    // actually rolled up (never at construction), and ChildWindow applies the real policy value
    // immediately after constructing this gate (D-5) — so neither default is ever observed as the
    // gate's own steady-state value in production. True/true also matches the wrapped affordances'
    // own default-enabled state (NativeSizingAffordance's `_enabled = true` field), so a gate that is never touched behaves
    // exactly like the affordance it wraps would have on its own.
    private bool _notCollapsed = true;
    private bool _allowedByPolicy = true;
    private bool _appliedEnabled = true;

    /// <summary>
    /// Initializes a new instance of the <see cref="ResizeAffordanceGate"/> class.
    /// </summary>
    /// <param name="inner">The underlying affordance (<see cref="NativeSizingAffordance"/>) that actually gates resize input.</param>
    public ResizeAffordanceGate(IResizeAffordance inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <summary>
    /// The "not rolled up" input — called by <see cref="RollUpBehavior"/> exactly as it always has
    /// (<c>SetEnabled(!collapsed)</c>), with no awareness that it is no longer talking directly to the
    /// underlying affordance.
    /// </summary>
    /// <param name="enabled"><see langword="true"/> when the panel is not rolled up.</param>
    public void SetEnabled(bool enabled)
    {
        _notCollapsed = enabled;
        Recompute();
    }

    /// <summary>
    /// The new "allowed by layout-edit policy" input (D-5) — driven by <see cref="Views.ChildWindow"/>
    /// from <see cref="Models.LayoutGesturePolicy.Resolve"/>'s <c>CanResize</c>, recomputed whenever
    /// <see cref="Services.ILayoutEditModeService.ModeChanged"/> fires or the Normal-mode gesture
    /// preference/lock setting changes.
    /// </summary>
    /// <param name="allowed"><see langword="true"/> when the current layout-edit policy permits resizing.</param>
    public void SetAllowedByPolicy(bool allowed)
    {
        _allowedByPolicy = allowed;
        Recompute();
    }

    /// <summary>
    /// Recomputes <c>_notCollapsed &amp;&amp; _allowedByPolicy</c> and forwards to
    /// <see cref="_inner"/>'s <see cref="IResizeAffordance.SetEnabled"/> ONLY when the resulting value
    /// actually differs from what was last applied — idempotent, and never spams the underlying
    /// affordance (WPF visibility/hit-test-visible writes, or the native path's flat field write) with a
    /// redundant call on every independent input change.
    /// </summary>
    private void Recompute()
    {
        var effective = _notCollapsed && _allowedByPolicy;
        if (effective == _appliedEnabled) return;

        _appliedEnabled = effective;
        _inner.SetEnabled(effective);
    }
}
