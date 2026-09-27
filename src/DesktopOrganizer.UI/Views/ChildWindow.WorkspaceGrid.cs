using System.Windows.Controls;
using System.Windows.Data;
using DesktopOrganizer.UI.Interaction;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.UI.Views;

/// <summary>
/// Anti-conflict partial (docs/PLANS/workspace-grid-03-04-integration.md §1.4/§4.2): P03 owns this file
/// — the explicit "Align to workspace grid" command (§2.5) and the single entry point through which
/// <c>WindowManager</c> re-applies grid geometry to a live panel (§2.4, P03-W3).
/// </summary>
public partial class ChildWindow
{
    /// <summary>
    /// Applies workspace-grid geometry computed elsewhere (<c>WindowManager.OnMonitorsChanged</c>,
    /// P03-W3) to this live panel through its single <c>PanelGeometryWriter</c>, so the window and its
    /// model are written as one guarded act and the synchronous <c>OnLocationChanged</c>/
    /// <c>OnRenderSizeChanged</c> callbacks skip their own redundant model writes (plan §1.5).
    /// </summary>
    /// <remarks>
    /// While the panel is rolled up, only the POSITION is applied: its live <c>Height</c> is the collapsed
    /// header height, and <see cref="ChildWindowModel.Size"/> deliberately still holds the expanded height
    /// for restore — writing the span's full height onto the window would visually un-roll the panel
    /// behind the user's back. The span itself is still recorded, so the expanded height is correct the
    /// moment the panel is rolled back down.
    /// </remarks>
    /// <param name="boundsWindowSpace">The bounds to apply, in <c>Window.Left</c>/<c>Top</c> space.</param>
    /// <param name="span">The span to record on the model, or <see langword="null"/> to leave it unchanged.</param>
    /// <param name="monitorDeviceId">The monitor id to record, or <see langword="null"/> to leave it unchanged.</param>
    internal void ApplyWorkspaceGridGeometry(Rect boundsWindowSpace, GridSpan? span, string? monitorDeviceId)
    {
        if (_model is null) return;

        if (_model.IsRolledUp)
            _geometryWriter.ApplyPosition(boundsWindowSpace.TopLeft, span, monitorDeviceId);
        else
            _geometryWriter.Apply(boundsWindowSpace, span, monitorDeviceId);
    }

    /// <summary>
    /// This panel's bounds as the workspace grid should see them: live <c>Left</c>/<c>Top</c>/
    /// <c>ActualWidth</c>, and the EXPANDED height even while the panel is rolled up — aligning (or
    /// re-deriving a span from) a collapsed header would silently shrink the panel the moment it is
    /// rolled back down. Falls back to the model's persisted size on either axis that has not been
    /// measured yet (a panel on an inactive page has never been laid out).
    /// </summary>
    internal Rect GetExpandedBoundsWindowSpace()
    {
        // release-hardening F (ADR-037): (Left, Top) is converted through the adapter — desktop-DIP, the
        // space every consumer of this method (WorkspaceGridPlacement, WindowManager.AlignPanelsToWorkspaceGrid,
        // this partial's own AlignToWorkspaceGrid) already treats it as. Identity on any uniform snapshot.
        var position = PanelWindowCoordinates.ToDesktopBounds(this, new Rect(Left, Top, 0, 0), _desktopScale.Current).TopLeft;
        return WorkspaceGridPlacement.ExpandedBounds(position, new Size(ActualWidth, ActualHeight), _model);
    }

    /// <summary>
    /// <c>HeaderContextMenu</c>'s "Align to workspace grid" item — the explicit, user-driven switch
    /// between <see cref="PanelGeometryMode.GridAligned"/> and <see cref="PanelGeometryMode.LegacyFreeform"/>
    /// for THIS panel (ADR-028 «Явная команда»; OQ-3 = B: the same checkable item both turns alignment on
    /// and takes it off).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Gating (ADR-026): blocked by <see cref="Services.ILayoutEditModeService.IsLayoutLocked"/> only —
    /// an explicit menu command is never gated by <c>NormalModeLayoutGestures</c>, which governs mouse
    /// GESTURES (D3-6: the command stays reachable when gestures are set to <c>None</c>, so the
    /// keyboard/menu route never degrades). The item is also disabled through its <c>IsEnabled</c>
    /// binding; this early return is the defence-in-depth half, mirroring <c>EditLayoutMenu_Click</c>.
    /// </para>
    /// <para>
    /// The menu item's <c>IsChecked</c> is bound <c>OneWay</c> to the model's mode. WPF toggles a checkable
    /// item with <c>SetCurrentValue</c>, which leaves that binding intact, but a refused or failed command
    /// would still leave the checkmark showing the state the user asked for rather than the state the
    /// panel is in — so the binding is re-read at the end of every invocation, on every path.
    /// </para>
    /// </remarks>
    private void AlignToGridMenu_Click(object sender, RoutedEventArgs e)
    {
        var menuItem = sender as MenuItem;
        try
        {
            if (_model is null) return;
            if (_layoutEditMode.IsLayoutLocked) return;

            // The checkable item has already flipped by the time Click is raised, so its new state IS the
            // requested one; fall back to "the opposite of whatever the panel is not already" only if the
            // sender is not the expected item (defensive — e.g. a future keyboard binding).
            var enable = menuItem?.IsChecked ?? !IsGridAligned();
            if (enable)
            {
                // m-4 fix-cycle 1: a refused alignment (see AlignToWorkspaceGrid's own remarks) leaves
                // nothing to persist — the checkbox already re-reads the panel's real, unchanged
                // GeometryMode in the `finally` block below, so no further action is needed here beyond
                // not forcing an unnecessary save.
                if (!AlignToWorkspaceGrid()) return;
            }
            else
            {
                WorkspaceGridPlacement.ClearAlignment(_model);
            }

            _windowManager.SaveWindowsState();
        }
        finally
        {
            if (menuItem is not null)
                BindingOperations.GetBindingExpression(menuItem, MenuItem.IsCheckedProperty)?.UpdateTarget();
        }
    }

    /// <summary>
    /// Reads the panel's CURRENT mode for the sole purpose of deciding which direction the explicit
    /// command runs in. Not a geometry decision (Инвариант В1 governs those, and they all go through
    /// <see cref="PanelGridStateResolver"/>) — this is the one place where the question "is the panel
    /// currently labelled grid-aligned" is itself the subject of the operation.
    /// </summary>
    private bool IsGridAligned() => _model?.GeometryMode == PanelGeometryMode.GridAligned;

    /// <summary>
    /// Turns alignment ON for this panel: resolves the monitor snapshot, runs the pure
    /// <see cref="WorkspaceGridPlacement.TryAlign"/> against the panel's live bounds and its neighbours,
    /// and commits the result through the single geometry writer.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the panel was actually aligned; <see langword="false"/> when
    /// <see cref="WorkspaceGridPlacement.TryAlign"/> refused (no monitors, or a degenerate/never-measured
    /// current size — e.g. a panel on an inactive page) — <see cref="ChildWindowModel.GeometryMode"/> is
    /// guaranteed unchanged in that case (<see cref="WorkspaceGridPlacement.TryAlign"/>'s own contract).
    /// </returns>
    /// <remarks>
    /// m-4 fix-cycle 1 (docs/PLANS/workspace-grid-03-04-integration.md §2, code-review): a refusal used to
    /// be entirely silent — the checkbox already re-reads reality via the caller's
    /// <c>BindingOperations.UpdateTarget</c> (so it was never left in a wrong state), but nothing
    /// diagnosable recorded WHY a click appeared to do nothing. The refusal is recorded through
    /// <see cref="Diagnostics.IGestureDiagnostics.AlignToWorkspaceGridRefused"/> (panel-scoped, reason code
    /// only) — "log-only, do not throw", never <c>Console.Error</c>, which nobody reads in a WinExe process.
    /// </remarks>
    private bool AlignToWorkspaceGrid()
    {
        if (_model is not { } model) return false;

        var monitors = new List<(string DeviceName, Rect Bounds, Rect WorkArea)>();
        foreach (var monitor in _monitorService.GetMonitors())
            monitors.Add((monitor.DeviceName, monitor.Bounds, monitor.WorkArea));

        var current = GetExpandedBoundsWindowSpace();

        var obstacles = new List<Rect>();
        _windowManager.CollectOtherPanelBounds(model.Id, obstacles);

        if (!WorkspaceGridPlacement.TryAlign(model, current, monitors, obstacles, out var aligned))
        {
            // No usable monitor, or the panel's current size has not been measured yet (a panel on an
            // inactive page). Panel-scoped diagnostics event, never a title/path (privacy invariant).
            _diagnostics.AlignToWorkspaceGridRefused(model.Id, monitors.Count == 0 ? "NoMonitor" : "Unmeasured");
            return false;
        }

        ApplyWorkspaceGridGeometry(aligned.Bounds, aligned.Span, aligned.MonitorDeviceId);
        return true;
    }
}
