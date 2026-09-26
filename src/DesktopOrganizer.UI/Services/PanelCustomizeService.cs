using System.Windows.Threading;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.ViewModels;
using DesktopOrganizer.UI.Views;

namespace DesktopOrganizer.UI.Services;

/// <summary>
/// Testable seam over the small slice of <see cref="System.Windows.Window"/>'s API
/// <see cref="PanelCustomizeService"/> actually drives — <see cref="Views.PanelCustomizeWindow"/>
/// implements it, letting <see cref="PanelCustomizeService"/> be unit tested against a fake dialog
/// instead of spinning up a live WPF window (FIX-B fix-cycle MAJOR finding M-3/M-4).
/// </summary>
internal interface IPanelCustomizeDialog
{
    /// <summary>The dialog's ViewModel — read by <see cref="PanelCustomizeService"/> to compare against a removed panel (M-3).</summary>
    PanelCustomizeViewModel ViewModel { get; }

    /// <summary>Raised once the dialog has closed. <see cref="System.Windows.Window.Closed"/> already matches this shape.</summary>
    event EventHandler? Closed;

    /// <summary>Re-targets the dialog at a different panel.</summary>
    /// <param name="model">The panel to edit.</param>
    void Retarget(ChildWindowModel model);

    /// <summary>Positions the dialog near the panel it is about to edit.</summary>
    /// <param name="model">The panel being customized.</param>
    void PositionForPanel(ChildWindowModel model);

    /// <summary>
    /// Shows (or re-activates) the dialog and brings it to the foreground, recovering from a minimized
    /// or foreground-locked state (FIX-B fix-cycle MAJOR finding M-4).
    /// </summary>
    void ShowAndActivate();

    /// <summary>Closes the dialog. <see cref="System.Windows.Window.Close"/> already matches this shape.</summary>
    void Close();
}

/// <summary>
/// Implementation of <see cref="IPanelCustomizeService"/> (docs/prompts/settings-window-redesign.md
/// D-3, WP-B). Keeps at most one <see cref="PanelCustomizeWindow"/> alive for the whole application:
/// a second <see cref="Show"/> call while the dialog is already open re-targets that same window to
/// the newly requested panel instead of opening a second one.
/// </summary>
public sealed class PanelCustomizeService : IPanelCustomizeService
{
    private readonly Func<IPanelCustomizeDialog> _dialogFactory;
    private readonly IWindowManager _windowManager;
    private readonly Action<Action> _defer;

    private IPanelCustomizeDialog? _dialog;

    /// <summary>
    /// Initializes a new instance of the <see cref="PanelCustomizeService"/> class.
    /// </summary>
    /// <param name="windowFactory">
    /// Creates a new <see cref="PanelCustomizeWindow"/> on demand. A factory delegate (rather than an
    /// injected <see cref="IServiceProvider"/>) mirrors this codebase's existing per-window construction
    /// pattern (see <c>Configuration/DiConfig.cs</c>'s <c>ISystemIconsPanelManager</c> registration) and
    /// keeps this class from depending on the container directly.
    /// </param>
    /// <param name="windowManager">
    /// Raises <see cref="IWindowManager.OnChildWindowRemoved"/>, subscribed to below, so a panel deleted
    /// (Settings → Remove, page deletion, layout-snapshot restore — every path routes through
    /// <see cref="IWindowManager.RemoveChildWindow(Guid, bool, bool)"/>) closes its own open dialog
    /// instead of leaving it editing a panel that no longer exists (FIX-B fix-cycle MAJOR finding M-3).
    /// </param>
    public PanelCustomizeService(Func<PanelCustomizeWindow> windowFactory, IWindowManager windowManager)
        : this(() => (IPanelCustomizeDialog)windowFactory(), windowManager, DeferToUiThread)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PanelCustomizeService"/> class — test-only overload
    /// that takes the <see cref="IPanelCustomizeDialog"/> seam and the UI-thread deferral directly, so
    /// <c>PanelCustomizeServiceTests</c> can supply a fake dialog and a synchronous (immediate) defer
    /// instead of a live window and a real <see cref="Dispatcher"/>.
    /// </summary>
    /// <param name="dialogFactory">Creates a new dialog on demand.</param>
    /// <param name="windowManager">See the public constructor.</param>
    /// <param name="defer">Runs the given callback — immediately, or marshalled onto some other thread/priority.</param>
    internal PanelCustomizeService(Func<IPanelCustomizeDialog> dialogFactory, IWindowManager windowManager, Action<Action> defer)
    {
        _dialogFactory = dialogFactory;
        _windowManager = windowManager;
        _defer = defer;

        _windowManager.OnChildWindowRemoved += OnChildWindowRemoved;
    }

    /// <inheritdoc />
    public void Show(ChildWindowModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        // FIX-B fix-cycle MAJOR finding M-4: "Customize panel…" from the panel's own HeaderContextMenu
        // calls this synchronously while that WS_EX_NOACTIVATE menu is still closing. Showing/activating
        // the dialog right away can leave it painted but not actually focused/foreground. Deferring to
        // DispatcherPriority.Input runs this after the menu-close input has finished processing, by
        // which point Activate() reliably takes the foreground (see ShowAndActivate's own fallback for
        // the rare case it still doesn't).
        _defer(() => ShowCore(model));
    }

    private static void DeferToUiThread(Action action) =>
        UiDispatch.TryBeginInvoke(System.Windows.Application.Current?.Dispatcher, action, DispatcherPriority.Input);

    private void ShowCore(ChildWindowModel model)
    {
        // OPTIONAL guard (FIX round 2): Show() defers to the UI thread (see the remarks above), so the
        // panel this was originally requested for can be removed (Settings → Remove, page deletion, a
        // layout-snapshot restore) in the gap between the call and this deferred callback actually
        // running. Opening/retargeting the dialog onto a model IWindowManager no longer knows about would
        // let the user edit a panel that's already gone — reusing the same lookup
        // OnChildWindowRemoved below relies on for the symmetric "close while open" case.
        if (!_windowManager.GetAllChildWindows().Any(m => ReferenceEquals(m, model))) return;

        if (_dialog is { } existing)
        {
            existing.Retarget(model);
            existing.PositionForPanel(model);
            existing.ShowAndActivate();
            return;
        }

        var dialog = _dialogFactory();
        dialog.Closed += OnDialogClosed;
        dialog.Retarget(model);
        dialog.PositionForPanel(model);
        dialog.ShowAndActivate();

        _dialog = dialog;
    }

    /// <summary>
    /// Drops the cached dialog reference once it closes, so the next <see cref="Show"/> call builds a
    /// fresh one instead of reusing a disposed <see cref="System.Windows.Window"/>.
    /// </summary>
    private void OnDialogClosed(object? sender, EventArgs e)
    {
        if (sender is IPanelCustomizeDialog dialog)
            dialog.Closed -= OnDialogClosed;

        if (ReferenceEquals(_dialog, sender))
            _dialog = null;
    }

    /// <summary>
    /// Closes the open dialog when the panel it is currently editing is removed (FIX-B fix-cycle MAJOR
    /// finding M-3). A no-op when no dialog is open, or it's editing a different panel.
    /// </summary>
    private void OnChildWindowRemoved(object? sender, ChildWindowModel removed)
    {
        if (_dialog is { } dialog && ReferenceEquals(dialog.ViewModel.Target, removed))
            dialog.Close();
    }
}
