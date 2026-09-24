using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Services;

namespace DesktopOrganizer.UI.Lifecycle;

/// <summary>
/// Reacts to an <see cref="ActivationRequest"/> delivered by a Secondary process (ADR-023 D-5/D-8) by
/// showing/activating the existing settings window — and doing absolutely nothing else.
/// </summary>
/// <remarks>
/// ADR-023 D-8: this is the machine-checkable definition of "a second launch does not change desktop
/// panels" — <c>SingleInstanceActivationHandlerTests</c> pins it with
/// <c>Mock&lt;IWindowManager&gt;.VerifyNoOtherCalls()</c> after exactly one <see cref="IWindowManager.ShowMainWindow"/>
/// call. No panel, page, <c>SystemIconsPanel</c>, or desktop-icon method is ever reachable from here.
/// </remarks>
/// <param name="windowManager">Used for exactly one thing: <see cref="IWindowManager.ShowMainWindow"/>.</param>
/// <param name="uiInvoke">
/// Marshals the actual work to the UI thread — <see cref="ActivationRequest"/>s arrive on the
/// activation server's own background accept thread (see
/// <c>DesktopOrganizer.Services.SingleInstance.NamedPipeActivationServer</c>), never the WPF dispatcher
/// thread. Production wiring is <c>a =&gt; Current.Dispatcher.Invoke(a)</c>; tests inject a synchronous
/// <c>a =&gt; a()</c> so <see cref="Handle"/> can be exercised with no live WPF <c>Application</c> (the
/// same delegate seam used by <c>NativePanelResizeController</c>, Phase 2 commit d7858e4).
/// </param>
internal sealed class SingleInstanceActivationHandler(IWindowManager windowManager, Action<Action> uiInvoke)
{
    /// <summary>
    /// Handles one <see cref="ActivationRequest"/>. Unknown/future commands (protocol <c>DO/2</c> and
    /// beyond, ADR-023 D-5) are ignored rather than guessed at — <see cref="IWindowManager"/> is never
    /// touched for a command this build doesn't recognize.
    /// </summary>
    public void Handle(ActivationRequest request)
    {
        if (request.Command != ActivationCommand.ActivateSettings)
        {
            return;
        }

        uiInvoke(windowManager.ShowMainWindow);
    }
}
