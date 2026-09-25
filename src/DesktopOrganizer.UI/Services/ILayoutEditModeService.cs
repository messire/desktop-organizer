using System.ComponentModel;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.UI.Services;

/// <summary>
/// Where a <see cref="ILayoutEditModeService.Mode"/> change originated — recorded by
/// <see cref="Diagnostics.IGestureDiagnostics.LayoutModeChanged"/> (D-11) and used by
/// <see cref="ILayoutEditModeService.ModeChanged"/> subscribers that care about the source (e.g. to
/// decide whether to show a toast). A closed set of names, never free text (diagnostics privacy
/// invariant, docs/Refactoring/Testing/diagnostics-log-events.md).
/// </summary>
public enum LayoutModeChangeSource
{
    /// <summary>The tray icon's "Edit layout"/"Lock layout" menu items (D-9 #1 — the one guaranteed path).</summary>
    Tray,

    /// <summary>A panel's <c>HeaderContextMenu</c> "Edit layout" item (D-9 #2).</summary>
    PanelMenu,

    /// <summary>The settings window's toggle (D-9 #3).</summary>
    SettingsWindow,

    /// <summary><c>Ctrl+E</c> or <c>Escape</c> (D-9 #3/#4).</summary>
    Keyboard,

    /// <summary>Any other programmatic caller (tests, future automation).</summary>
    Programmatic,
}

/// <summary>
/// Event data for <see cref="ILayoutEditModeService.ModeChanged"/> — raised once per real
/// <see cref="LayoutInteractionMode"/> transition (never for a no-op re-entry into the mode already
/// active).
/// </summary>
public sealed class LayoutModeChangedEventArgs(LayoutInteractionMode from, LayoutInteractionMode to, LayoutModeChangeSource source) : EventArgs
{
    /// <summary>The mode being left.</summary>
    public LayoutInteractionMode From { get; } = from;

    /// <summary>The mode being entered.</summary>
    public LayoutInteractionMode To { get; } = to;

    /// <summary>What triggered this transition.</summary>
    public LayoutModeChangeSource Source { get; } = source;
}

/// <summary>
/// Process-wide singleton holding the application-global <see cref="LayoutInteractionMode"/>
/// (docs/PLANS/phase-6-edit-layout-ux.md §1 D-1) — the direct analogue of
/// <see cref="IPanelSelectionCoordinator"/>: a global fact no panel can learn through WPF focus,
/// because every panel carries <c>WS_EX_NOACTIVATE</c>.
/// </summary>
public interface ILayoutEditModeService : INotifyPropertyChanged
{
    /// <summary>
    /// The current mode. <b>Never persisted</b> (D-8/R-11) — always <see cref="LayoutInteractionMode.Normal"/>
    /// immediately after construction, regardless of what the previous session ended in.
    /// </summary>
    LayoutInteractionMode Mode { get; }

    /// <summary>
    /// <see langword="true"/> when the user has fixed the current panel geometry ("Lock layout", D-3).
    /// Persisted via <see cref="IAppSettings.IsLayoutLocked"/>. Setting this to <see langword="true"/>
    /// while <see cref="Mode"/> is <see cref="LayoutInteractionMode.EditLayout"/> forces an immediate
    /// exit back to <see cref="LayoutInteractionMode.Normal"/> (D-3/R-9) — the service is never left in
    /// the contradictory "locked but editing" state.
    /// </summary>
    bool IsLayoutLocked { get; set; }

    /// <summary><see langword="true"/> iff <c>!IsLayoutLocked</c> — whether <see cref="TryEnterEditLayout"/> can currently succeed.</summary>
    bool CanEnterEditLayout { get; }

    /// <summary>Bool facade over <c>Mode == LayoutInteractionMode.EditLayout</c>, for XAML bindings that cross a visual-tree boundary attached DPs cannot (D-12) — raises <see cref="INotifyPropertyChanged.PropertyChanged"/> synchronously with <see cref="Mode"/>.</summary>
    bool IsEditing { get; }

    /// <summary>
    /// Attempts to enter <see cref="LayoutInteractionMode.EditLayout"/>. Returns <see langword="false"/>
    /// without changing <see cref="Mode"/> when <see cref="IsLayoutLocked"/> is <see langword="true"/>.
    /// A no-op (returns <see langword="true"/>, raises nothing) when already in <c>EditLayout</c>.
    /// </summary>
    /// <param name="source">Where this transition originated, for diagnostics/subscribers.</param>
    bool TryEnterEditLayout(LayoutModeChangeSource source);

    /// <summary>Returns to <see cref="LayoutInteractionMode.Normal"/>. A no-op when already in <c>Normal</c>.</summary>
    /// <param name="source">Where this transition originated, for diagnostics/subscribers.</param>
    void ExitEditLayout(LayoutModeChangeSource source);

    /// <summary>
    /// Toggles <see cref="Mode"/> between <see cref="LayoutInteractionMode.Normal"/> and
    /// <see cref="LayoutInteractionMode.EditLayout"/> in one call (DoD-3 — "one action"). No-op,
    /// returns <see langword="false"/>, when <see cref="IsLayoutLocked"/> is <see langword="true"/>.
    /// Returns <see langword="true"/> when the mode actually changed.
    /// </summary>
    /// <param name="source">Where this transition originated, for diagnostics/subscribers.</param>
    bool Toggle(LayoutModeChangeSource source);

    /// <summary>
    /// Raised AFTER <see cref="Mode"/> changes — exactly once per real transition, never for a no-op
    /// (T-TRK-07 precedent). Subscribers (<c>ChildWindow</c>) must finish aborting any active gesture
    /// before repainting (D-4).
    /// </summary>
    event EventHandler<LayoutModeChangedEventArgs>? ModeChanged;
}
