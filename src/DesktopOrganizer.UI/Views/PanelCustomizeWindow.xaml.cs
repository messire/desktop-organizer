using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using DesktopOrganizer.Services.Abstractions;
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
    private readonly IMonitorService _monitorService;

    /// <summary>
    /// Initializes a new instance of the <see cref="PanelCustomizeWindow"/> class.
    /// </summary>
    /// <param name="viewModel">The dialog's ViewModel, bound as this window's <see cref="FrameworkElement.DataContext"/>.</param>
    /// <param name="monitorService">Resolves the monitor to center the dialog on (<see cref="PositionForPanel"/>).</param>
    public PanelCustomizeWindow(PanelCustomizeViewModel viewModel, IMonitorService monitorService)
    {
        InitializeComponent();
        ViewModel = viewModel;
        _monitorService = monitorService;
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
    /// <c>DesktopOrganizer.Services</c> and not reachable from the UI layer (see
    /// <c>IMonitorService.GetSystemDpiScale</c>'s own remarks on that boundary) — adding one is a
    /// native-interop change out of WP-B's file ownership. Centering on the panel's own monitor is the
    /// scoped-down equivalent implemented here; see the WP-B hand-off report for the full rationale.
    /// <para>
    /// FIX-B fix-cycle MAJOR finding 10 (ADR-028): <paramref name="model"/>'s <see cref="ChildWindowModel.Position"/>
    /// is in <c>Window.Left</c>/<c>Top</c> ("window") space, while every <see cref="IMonitorService"/>
    /// monitor rectangle — including the one <see cref="IMonitorService.FindMonitorAt"/> matches against
    /// — is in screen-DIP space. The two only coincide while <see cref="DesktopCoordinateSpace.IsIdentity"/>
    /// holds (today's default). Converting the anchor to screen space before the monitor lookup, and the
    /// resulting work area back to window space before computing <c>Left</c>/<c>Top</c>,
    /// keeps this correct once a coordinate calibration is ever configured.
    /// </para>
    /// </remarks>
    /// <param name="model">The panel being customized.</param>
    public void PositionForPanel(ChildWindowModel model)
    {
        var anchorScreen = DesktopCoordinateSpace.ToScreenSpace(new Point(model.Position.X, model.Position.Y));
        var monitor = _monitorService.FindMonitorAt(anchorScreen) ?? _monitorService.GetPrimaryMonitor();
        var workArea = DesktopCoordinateSpace.ToWindowSpace(monitor.WorkArea);

        var left = workArea.Left + ((workArea.Width - Width) / 2);
        var top = workArea.Top + ((workArea.Height - Height) / 2);

        Left = Math.Clamp(left, workArea.Left, Math.Max(workArea.Left, workArea.Right - Width));
        Top = Math.Clamp(top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - Height));
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
    /// Forces the two colour hex <see cref="TextBox"/>es' pending edits into
    /// <see cref="PanelCustomizeViewModel.Target"/> before the window actually closes. Both boxes commit
    /// on <c>LostFocus</c> (<c>PanelCustomizeWindow.xaml</c>) so the swatch/model don't repaint on every
    /// keystroke of an in-progress hex value — but closing the window via the X button, Alt+F4, or
    /// <see cref="IWindowManager.OnChildWindowRemoved"/>-triggered <c>Close()</c> (M-3) never raises
    /// <c>LostFocus</c> for whichever box still has focus, silently dropping a typed value (FIX-B
    /// fix-cycle minor finding 8).
    /// </summary>
    /// <param name="e">Event data — forwarded to the base implementation unchanged; this override never cancels closing.</param>
    protected override void OnClosing(CancelEventArgs e)
    {
        // MINOR-3 (FIX round 2): only push the binding when it actually has a pending (uncommitted) edit
        // — UpdateSource() on a clean binding is a harmless no-op for the Target, but IS a needless extra
        // write through TwoWay's Source setter (Target -> ConvertBack -> Model.AccentColor/TextColor
        // setter -> PropertyChanged) for every single close of this dialog, whether or not the user ever
        // touched either box.
        if (AccentColorTextBox.GetBindingExpression(TextBox.TextProperty) is { IsDirty: true } accentBinding)
            accentBinding.UpdateSource();

        if (TextColorTextBox.GetBindingExpression(TextBox.TextProperty) is { IsDirty: true } textBinding)
            textBinding.UpdateSource();

        base.OnClosing(e);
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        ViewModel.Dispose();
        base.OnClosed(e);
    }
}
