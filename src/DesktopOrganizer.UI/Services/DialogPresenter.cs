using DesktopOrganizer.UI.Theming;
using DesktopOrganizer.UI.Views;
using DesktopOrganizer.UI.Views.Dialogs;

namespace DesktopOrganizer.UI.Services;

/// <summary>
/// Default <see cref="IDialogPresenter"/> — constructs and shows a real modal <see cref="AppDialogWindow"/>
/// (design-system-1x.md §2.8, ADR-047 D-9).
/// </summary>
internal sealed class DialogPresenter : IDialogPresenter
{
    private readonly IAppThemeService _appThemeService;
    private readonly IWindowFrameTheming _windowFrameTheming;
    private readonly ILocalizationService _localization;

    /// <summary>
    /// Initializes a new instance of the <see cref="DialogPresenter"/> class.
    /// </summary>
    /// <param name="appThemeService">Forwarded to each shown <see cref="AppDialogWindow"/> (<see cref="Behaviors.ThemedWindowBehavior"/>).</param>
    /// <param name="windowFrameTheming">Forwarded to each shown <see cref="AppDialogWindow"/>.</param>
    /// <param name="localization">Forwarded to each shown <see cref="AppDialogWindow"/> (resolves the severity glyph's accessible name).</param>
    public DialogPresenter(IAppThemeService appThemeService, IWindowFrameTheming windowFrameTheming, ILocalizationService localization)
    {
        _appThemeService = appThemeService;
        _windowFrameTheming = windowFrameTheming;
        _localization = localization;
    }

    /// <inheritdoc/>
    public AppDialogResult Show(AppDialogRequest request)
    {
        var dialog = new AppDialogWindow(request, _appThemeService, _windowFrameTheming, _localization);

        // Owner = the currently active top-level chrome window, if any; else the visible MainWindow; else
        // none (design-system-1x.md §2.8's own owner-resolution order) — same convention
        // Services.InputDialogService used before this phase folded it into AppDialogWindow. There
        // legitimately may be neither: a ChildWindow (WS_EX_NOACTIVATE, ADR-001/013) can never itself be
        // "active", so a confirmation raised from a panel's own header context menu — e.g. "Apply to all
        // panels" (docs/PLANS/design-system-1x.md §7 R-6) — reaches this branch even though the desktop is
        // visibly full of panels AND MainWindow (Settings) happens to be hidden (its normal state — see
        // that window's own remarks). AppDialogWindow is NEVER owned by a ChildWindow regardless (see that
        // class's own remarks) — there is no third option here.
        var owner = FindActiveWindow();
        if (owner is not null)
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            dialog.ShowInTaskbar = false;
        }
        else
        {
            // R-6 mitigation: no chrome window is active to own/center on, and nothing else is guaranteed
            // to have just granted this process the right to steal foreground focus (Windows' foreground-
            // lock timeout). Topmost plus this window's own explicit Activate() call (see its constructor)
            // is this phase's mitigation — the window closes itself (modal, one-shot) shortly after, so
            // Topmost never needs to be cleared back out again. win32-interop-specialist review is
            // recommended if manual verify M-6.10 finds it insufficient on a real system.
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.ShowInTaskbar = true;
            dialog.Topmost = true;
        }

        // R-5 (docs/PLANS/design-system-1x.md §7): ShowDialog disables every window on THIS thread, not
        // merely dialog.Owner — unlike the MessageBox this phase replaces, which only ever disabled its
        // owner. Every ChildWindow/settings/Customize window is created on this same UI thread, so they
        // are all disabled for the duration of this call; WPF re-enables them all automatically once
        // ShowDialog returns (whether via a button click or the window being closed any other way), so no
        // manual try/finally re-enable is needed here. win32-interop-specialist review is recommended to
        // confirm panels are never left disabled by some path that bypasses this normal return (e.g. an
        // unhandled exception while the dialog is up).
        var shown = dialog.ShowDialog();

        var button = shown == true ? dialog.ResultButton : AppDialogButton.Secondary;
        var inputText = button == AppDialogButton.Primary ? dialog.ResponseText : null;
        return new AppDialogResult(button, inputText);
    }

    /// <summary>
    /// Whether <paramref name="window"/> may ever be assigned as an <see cref="AppDialogWindow"/>'s
    /// <see cref="Window.Owner"/> — <see langword="false"/> for a <see cref="ChildWindow"/>
    /// (<c>WS_EX_NOACTIVATE</c>, ADR-001/013: can never be meaningfully "activated" as an owner, and
    /// <see cref="AppDialogWindow"/> is never owned by one regardless — see that class's own remarks) or a
    /// <see cref="SystemIconsPanelWindow"/> (a chromeless, deliberately-never-activated background
    /// surface, ADR-019). Excluded explicitly here — rather than relying on <see cref="Window.IsActive"/>
    /// happening to always be <see langword="false"/> for both — so a future code path that ever makes
    /// either briefly report active could never mistakenly become this dialog's owner.
    /// </summary>
    /// <param name="window">The window to test.</param>
    /// <returns><see langword="true"/> if <paramref name="window"/> is eligible; otherwise <see langword="false"/>.</returns>
    internal static bool IsEligibleOwner(Window window) => window is not ChildWindow && window is not SystemIconsPanelWindow;

    private static Window? FindActiveWindow()
    {
        // M-2 fix (review round): the pre-existing "just take the first Window.IsActive" loop never
        // explicitly excluded ChildWindow/SystemIconsPanelWindow — it happened to work only because
        // IsActive is accidentally always false for both (see IsEligibleOwner's own remarks), and it never
        // fell back to a hidden-but-still-open MainWindow (design-system-1x.md §2.8's actual order: active
        // chrome window → visible MainWindow → none).
        Window? visibleMainWindow = null;

        foreach (Window window in Application.Current.Windows)
        {
            if (!IsEligibleOwner(window)) continue;

            if (window.IsActive) return window;

            if (window is MainWindow { IsVisible: true }) visibleMainWindow ??= window;
        }

        return visibleMainWindow;
    }
}
