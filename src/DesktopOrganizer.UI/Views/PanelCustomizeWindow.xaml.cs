using System.ComponentModel;
using System.Windows;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Interaction;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;
using DesktopOrganizer.UI.ViewModels;

namespace DesktopOrganizer.UI.Views;

/// <summary>
/// The "Customize panel…" dialog (docs/prompts/settings-window-redesign.md D-3): a normal,
/// activatable, modeless window (unlike <see cref="ChildWindow"/>, it does NOT carry
/// <c>WS_EX_NOACTIVATE</c> — see <c>DesktopIntegrationService</c>, which only ever embeds
/// <see cref="ChildWindow"/>/<see cref="SystemIconsPanelWindow"/> instances). Every edit applies live
/// through <see cref="PanelCustomizeViewModel.Target"/>; there is no OK/Cancel, only Close.
/// </summary>
public partial class PanelCustomizeWindow : Window, IPanelCustomizeDialog
{
    /// <summary>Defensive floor for <see cref="ClampHeightToWorkArea"/> — see that method's own remarks.</summary>
    private const double MinReasonableHeight = 200d;

    private readonly IMonitorService _monitorService;
    private readonly IDesktopScaleProvider _desktopScale;

    // M3 (release-hardening 1.0 review): armed by every explicit PositionForPanel call (a fresh Show()/
    // Retarget() from IPanelCustomizeService) and consumed by the very next OnDpiChanged, so this
    // window's real per-monitor DPI gets exactly ONE follow-up self-correction after being positioned at
    // a still-approximate scale (see PositionForPanel's own remarks) — never more than that. Without this
    // one-shot guard, EVERY later DpiChanged — including one raised by the user simply dragging this
    // dialog to a different-DPI monitor themselves — re-centred it right back, discarding their own move.
    // See TransitionLatch's own remarks for why this is a small pure/testable type rather than a bare
    // bool field.
    private readonly TransitionLatch _pendingInitialDpiCorrection = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="PanelCustomizeWindow"/> class.
    /// </summary>
    /// <param name="viewModel">The dialog's ViewModel, bound as this window's <see cref="FrameworkElement.DataContext"/>.</param>
    /// <param name="monitorService">Resolves the monitor to center the dialog on (<see cref="PositionForPanel"/>).</param>
    /// <param name="desktopScale">
    /// The per-monitor DPI snapshot provider (release-hardening F, ADR-037) — <see cref="PositionForPanel"/>
    /// derives the target monitor's physical-pixel work area from it (<see cref="Interaction.TopLevelPlacement"/>).
    /// </param>
    public PanelCustomizeWindow(PanelCustomizeViewModel viewModel, IMonitorService monitorService, IDesktopScaleProvider desktopScale)
    {
        InitializeComponent();
        ViewModel = viewModel;
        _monitorService = monitorService;
        _desktopScale = desktopScale;
        DataContext = ViewModel;
    }

    /// <summary>The dialog's ViewModel.</summary>
    public PanelCustomizeViewModel ViewModel { get; }

    /// <summary>
    /// Re-targets this window at a different panel (<see cref="Services.IPanelCustomizeService.Show(ChildWindowModel)"/> reusing
    /// the single open instance instead of opening a second dialog).
    /// </summary>
    /// <param name="model">The panel to edit.</param>
    public void Retarget(ChildWindowModel model) => ViewModel.Target = model;

    /// <summary>
    /// Positions this window centered on the monitor <paramref name="model"/>'s panel currently
    /// occupies (falling back to the primary monitor when that can't be resolved), clamped so the
    /// whole dialog stays inside that monitor's work area.
    /// </summary>
    /// <remarks>
    /// docs/prompts/settings-window-redesign.md D-3 also asks for positioning "near the mouse cursor".
    /// This codebase has no cursor-position primitive outside <c>INativeApi</c>, which is internal to
    /// <c>DesktopOrganizer.Services</c> and not reachable from the UI layer — adding one is a
    /// native-interop change out of WP-B's file ownership. Centering on the panel's own monitor is the
    /// scoped-down equivalent implemented here; see the WP-B hand-off report for the full rationale.
    /// <para>
    /// FIX-B fix-cycle MAJOR finding 10 (ADR-028): <paramref name="model"/>'s <see cref="ChildWindowModel.Position"/>
    /// is in desktop-DIP space, while every <see cref="IMonitorService"/> monitor rectangle — including
    /// the one <see cref="IMonitorService.FindMonitorAt"/> matches against — is in screen-DIP space. The
    /// two only coincide while <see cref="DesktopCoordinateSpace.IsIdentity"/> holds (today's default).
    /// Converting the anchor to screen space before the monitor lookup keeps this correct once a
    /// coordinate calibration is ever configured.
    /// </para>
    /// <para>
    /// release-hardening F (ADR-037, plan §5 #23): the WORK AREA itself is read as raw physical pixels
    /// from <see cref="IDesktopScaleProvider"/>'s current snapshot and divided by that monitor's own
    /// effective scale (<see cref="Interaction.TopLevelPlacement"/>) — the top-level-window rule (plan §2
    /// "always <c>px / s_M</c>") — rather than <see cref="IMonitorService"/>'s DIP rectangle, which is not
    /// yet per-monitor-correct on a mixed-DPI desktop (that rewiring is F2's). Falls back to
    /// <see cref="IMonitorService"/>'s own DIP work area (identity on a uniform snapshot) when the
    /// snapshot has no monitor by that name yet.
    /// </para>
    /// </remarks>
    /// <param name="model">The panel being customized.</param>
    public void PositionForPanel(ChildWindowModel model)
    {
        // M3: this is the one explicit, user/caller-intended re-position — arm exactly one follow-up
        // self-correction for whenever OnDpiChanged next fires (see that field's own remarks and
        // OnDpiChanged's).
        _pendingInitialDpiCorrection.Set();
        ApplyPositionForPanel(model);
    }

    /// <summary>
    /// Shows (if not already visible) and brings this window to the foreground, recovering from a
    /// minimized state and — best-effort, without any P/Invoke — from Windows' foreground-lock
    /// restriction (FIX-B fix-cycle MAJOR finding M-4: opening from the panel's own
    /// <c>WS_EX_NOACTIVATE</c> <c>HeaderContextMenu</c> could otherwise leave this window shown but not
    /// actually focused). The <c>Topmost</c> flip is the standard WPF-only trick for this — see
    /// <c>ChildWindow</c>'s own remarks on why a genuine <c>SetForegroundWindow</c> call would require
    /// <c>INativeApi</c>, out of this file's ownership.
    /// </summary>
    public void ShowAndActivate()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;

        Activate();

        if (!IsActive)
        {
            Topmost = true;
            Topmost = false;
        }
    }

    /// <summary>
    /// H-1 fix (review round): the pure clamp math behind <see cref="ApplyCentered"/> — never returns more
    /// than <paramref name="desiredHeight"/> (this dialog's own designed <c>640</c>, via the same
    /// <see cref="IDesktopScaleProvider"/> plumbing <see cref="ResolveWorkArea"/> already resolves), and
    /// never less than <see cref="MinReasonableHeight"/> (a defensive floor against a degenerate/zero
    /// work-area snapshot — this dialog would rather overflow off-screen than collapse to nothing).
    /// <see langword="internal"/> (not <see langword="private"/>) so it is unit-testable as a pure function
    /// without needing a real <see cref="Window"/>/monitor.
    /// </summary>
    /// <param name="desiredHeight">The dialog's own designed height (its XAML <c>Height="640"</c>).</param>
    /// <param name="workAreaHeight">The resolved monitor work area's height, in DIP.</param>
    internal static double ClampHeightToWorkArea(double desiredHeight, double workAreaHeight)
    {
        if (!double.IsFinite(workAreaHeight) || workAreaHeight <= 0) return desiredHeight;

        return Math.Clamp(Math.Min(desiredHeight, workAreaHeight), Math.Min(MinReasonableHeight, desiredHeight), desiredHeight);
    }

    /// <summary>
    /// Forces the colour editor's two hex <see cref="System.Windows.Controls.TextBox"/>es AND the icon-size editor's manual-entry
    /// box's pending edits into <see cref="PanelCustomizeViewModel.Target"/> before the window actually
    /// closes. All three commit on <c>LostFocus</c> (<c>PanelCustomizeWindow.xaml</c>,
    /// <c>Controls/PanelColorEditor.xaml</c>/<c>Controls/IntegerInputBox.cs</c>) so the swatch/model
    /// don't repaint on every keystroke of an in-progress edit — but closing the window via the X button,
    /// Alt+F4, or <see cref="IWindowManager.OnChildWindowRemoved"/>-triggered <c>Close()</c> (M-3) never
    /// raises <c>LostFocus</c> for whichever control still has focus, silently dropping a typed value
    /// (FIX-B fix-cycle minor finding 8).
    /// </summary>
    /// <param name="e">Event data — forwarded to the base implementation unchanged; this override never cancels closing.</param>
    protected override void OnClosing(CancelEventArgs e)
    {
        // MINOR-3 (FIX round 2): ColorEditor.CommitPendingEdits() only pushes a binding that actually has
        // a pending (uncommitted) edit — see that method's own remarks — the same "no needless extra
        // write on every close" guard this dialog always had.
        ColorEditor.CommitPendingEdits();
        IconSizeEditor.CommitPendingEdit();

        base.OnClosing(e);
    }

    /// <summary>
    /// Re-applies <see cref="PositionForPanel"/>'s centering for the currently targeted panel once this
    /// window's real per-monitor DPI is known (release-hardening F, ADR-037, plan §5 #23) — the position
    /// <see cref="PositionForPanel"/> computed before the window was ever shown used a best-effort scale
    /// (no monitor was known to host it yet).
    /// </summary>
    /// <remarks>
    /// M3 (release-hardening 1.0 review): only runs while <see cref="_pendingInitialDpiCorrection"/> is
    /// set — i.e. for the very next DpiChanged after an explicit <see cref="PositionForPanel"/> call, and
    /// never again after that until another explicit call re-arms it. A prior version re-centred on EVERY
    /// DpiChanged unconditionally, which also fired — and stole the user's own placement right back — the
    /// moment they manually dragged this (ordinary, activatable, non-<c>WS_EX_NOACTIVATE</c>) dialog onto
    /// a monitor with a different DPI.
    /// </remarks>
    /// <param name="oldDpiScale">Unused — the position is re-derived fresh, not incrementally adjusted.</param>
    /// <param name="newDpiScale">Unused for the same reason.</param>
    protected override void OnDpiChanged(DpiScale oldDpiScale, DpiScale newDpiScale)
    {
        base.OnDpiChanged(oldDpiScale, newDpiScale);

        if (!_pendingInitialDpiCorrection.TryConsume()) return;

        if (ViewModel.Target is { } model) ApplyPositionForPanel(model);
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        ViewModel.Dispose();
        base.OnClosed(e);
    }

    /// <summary>
    /// Resolves <paramref name="deviceName"/>'s work area as raw physical pixels through the current
    /// desktop-scale snapshot, falling back to <paramref name="fallbackDip"/> (the pre-F, <see cref="IMonitorService"/>-DIP
    /// work area) when the snapshot has no monitor by that name.
    /// </summary>
    private Rect ResolveWorkArea(string deviceName, Rect fallbackDip)
    {
        var scale = _desktopScale.Current;
        var monitorScale = scale.Monitors.FirstOrDefault(m => string.Equals(m.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
        if (monitorScale is null) return DesktopCoordinateSpace.ToWindowSpace(fallbackDip);

        var workAreaPx = monitorScale.WorkAreaPx;
        return TopLevelPlacement.ToDip(
            new Rect(workAreaPx.Left, workAreaPx.Top, workAreaPx.Width, workAreaPx.Height),
            monitorScale.DeviceName,
            scale);
    }

    /// <summary>
    /// The actual centering math behind <see cref="PositionForPanel"/> — split out (M3) so
    /// <see cref="OnDpiChanged"/>'s own one-shot self-correction can reuse it WITHOUT re-arming
    /// <see cref="_pendingInitialDpiCorrection"/> the way calling the public <see cref="PositionForPanel"/>
    /// again would.
    /// </summary>
    /// <param name="model">The panel being customized.</param>
    private void ApplyPositionForPanel(ChildWindowModel model)
    {
        var anchorScreen = DesktopCoordinateSpace.ToScreenSpace(new Point(model.Position.X, model.Position.Y));
        var monitor = _monitorService.FindMonitorAt(anchorScreen) ?? _monitorService.GetPrimaryMonitor();

        ApplyCentered(ResolveWorkArea(monitor.DeviceName, monitor.WorkArea));
    }

    /// <summary>
    /// H-1 fix (review round): this dialog's fixed <c>Height="640"</c> (<c>ResizeMode="NoResize"</c>) does
    /// not fit a 1366x768 @125% work area (~582 DIP) — clamps <see cref="FrameworkElement.MaxHeight"/> to
    /// the resolved work area's height so WPF itself coerces the actual rendered height down to that cap
    /// (never the declared <see cref="FrameworkElement.Height"/> itself — <see cref="FrameworkElement.MaxHeight"/>'s
    /// own coercion is what actually shrinks a <see cref="Window"/> smaller than its <c>Height</c>), letting
    /// the existing <c>ScrollViewer</c> (<c>PanelCustomizeWindow.xaml</c>) absorb whatever content no
    /// longer fits. <paramref name="workArea"/>'s height then also drives the vertical centering math
    /// below, so the dialog centers against its OWN actual (possibly clamped) height, not the nominal 640.
    /// </summary>
    private void ApplyCentered(Rect workArea)
    {
        var effectiveHeight = ClampHeightToWorkArea(Height, workArea.Height);
        MaxHeight = effectiveHeight;

        var left = workArea.Left + ((workArea.Width - Width) / 2);
        var top = workArea.Top + ((workArea.Height - effectiveHeight) / 2);

        Left = Math.Clamp(left, workArea.Left, Math.Max(workArea.Left, workArea.Right - Width));
        Top = Math.Clamp(top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - effectiveHeight));
    }
}
