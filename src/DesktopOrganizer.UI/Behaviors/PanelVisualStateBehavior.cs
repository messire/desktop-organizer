using System.Windows;
using System.Windows.Input;
using DesktopOrganizer.UI.Interaction;
using DesktopOrganizer.UI.Theming;

namespace DesktopOrganizer.UI.Behaviors;

/// <summary>
/// Bridges <see cref="PanelInteractionTracker.StateChanged"/> (Phase 1, ADR-021 — the single source of
/// truth for which gesture owns a panel) plus two independent XAML-level facts into
/// <see cref="PanelTheme.VisualStateProperty"/> (D-6, docs/PLANS/phase-5-ambient-accent-ui.md §1 D-6),
/// the first real subscriber to an event <c>PanelInteractionTracker</c> has documented since Phase 1 as
/// "reserved for future use binding visual gesture-affordance states to this one source of truth".
/// </summary>
/// <remarks>
/// Deliberately does not touch <see cref="PanelInteractionTracker"/>/<see cref="PanelInteractionState"/>
/// (§0) — it only reads <see cref="PanelInteractionTracker.State"/> and reacts to
/// <see cref="PanelInteractionTracker.StateChanged"/>. All the actual state-resolution logic already
/// lives in the pure, unit-tested <see cref="InteractionVisualState.Resolve"/> (C2) — this class is pure
/// WPF glue (event subscription and one <see cref="DependencyObject.SetValue(DependencyProperty, object)"/>
/// call) with no additional mapping logic of its own to extract/test.
/// </remarks>
public sealed class PanelVisualStateBehavior : IDisposable
{
    private readonly FrameworkElement _root;
    private readonly PanelInteractionTracker _tracker;
    private readonly Func<bool> _isTitleEditing;

    private bool _isPointerOver;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="PanelVisualStateBehavior"/> class and immediately
    /// publishes the initial resolved state.
    /// </summary>
    /// <param name="root">
    /// The panel's root element — <see cref="FrameworkElement.IsMouseOver"/> on this element is the
    /// "pointer over the whole panel" fact (D-6), and this is also the element
    /// <see cref="PanelTheme.VisualStateProperty"/> is written to (it is an inherited attached property,
    /// so every descendant — including <c>PanelChrome</c> — sees the resolved value without needing its
    /// own subscription).
    /// </param>
    /// <param name="tracker">The panel's shared <see cref="PanelInteractionTracker"/> (ADR-021) — read-only here, never mutated.</param>
    /// <param name="isTitleEditing">
    /// Accessor for <see cref="PanelTitleRenameBehavior.IsEditingTitle"/>. A <see langword="delegate"/>
    /// rather than a direct reference because <c>PanelTitleRenameBehavior</c> raises no event of its own
    /// (§0 — it is not touched by this phase); <c>ChildWindow.xaml.cs</c> calls <see cref="Refresh"/>
    /// explicitly right after each rename-state transition (<c>BeginRename</c>/<c>CommitRename</c>/<c>CancelRename</c>)
    /// instead.
    /// </param>
    public PanelVisualStateBehavior(FrameworkElement root, PanelInteractionTracker tracker, Func<bool> isTitleEditing)
    {
        _root = root;
        _tracker = tracker;
        _isTitleEditing = isTitleEditing;

        _root.MouseEnter += OnMouseEnter;
        _root.MouseLeave += OnMouseLeave;
        _tracker.StateChanged += OnTrackerStateChanged;

        Refresh();
    }

    /// <summary>
    /// Recomputes <see cref="InteractionVisualState.Resolve"/> from the current
    /// <see cref="PanelInteractionTracker.State"/>/pointer-over/title-editing facts and republishes the
    /// result via <see cref="PanelTheme.SetVisualState"/>. Called automatically on every pointer
    /// enter/leave and tracker state change; <c>ChildWindow.xaml.cs</c> also calls this directly after a
    /// title-rename transition, since that fact has no change notification of its own to subscribe to.
    /// </summary>
    public void Refresh()
    {
        var resolved = InteractionVisualState.Resolve(_tracker.State, _isPointerOver, _isTitleEditing());
        PanelTheme.SetVisualState(_root, resolved);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _root.MouseEnter -= OnMouseEnter;
        _root.MouseLeave -= OnMouseLeave;
        _tracker.StateChanged -= OnTrackerStateChanged;
    }

    private void OnMouseEnter(object sender, MouseEventArgs e)
    {
        _isPointerOver = true;
        Refresh();
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        _isPointerOver = false;
        Refresh();
    }

    private void OnTrackerStateChanged(object? sender, PanelInteractionState e) => Refresh();
}
