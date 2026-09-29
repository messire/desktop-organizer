namespace DesktopOrganizer.UI.Services;

/// <summary>
/// Shows a busy indication (a wait cursor in production) for the duration of a potentially slow,
/// synchronous UI-thread operation — extracted as its own seam (LOW-3 fix, code review, panel clone to
/// page, backlog 1.x wave 3 review) so callers like <see cref="PanelCloneFlow"/> stay fully unit-testable:
/// the production implementation (<see cref="MouseWaitCursorBusyIndicator"/>) touches
/// <see cref="System.Windows.Input.Mouse.OverrideCursor"/>, which requires an STA thread and throws on a
/// plain unit test's MTA thread-pool thread — a test supplies a fake <see cref="IBusyIndicator"/> instead,
/// with no apartment-state gate anywhere in production code.
/// </summary>
public interface IBusyIndicator
{
    /// <summary>
    /// Starts showing the busy indication. Disposing the result stops showing it again — callers wrap the
    /// operation in a <see langword="using"/> block (or an equivalent try/finally) so the indication is
    /// always cleared, even if the operation throws.
    /// </summary>
    /// <returns>A disposable that stops the busy indication when disposed.</returns>
    IDisposable Show();
}
