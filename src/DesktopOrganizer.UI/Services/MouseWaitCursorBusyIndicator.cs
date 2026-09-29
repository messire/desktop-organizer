using System.Windows.Input;

namespace DesktopOrganizer.UI.Services;

/// <summary>
/// Production <see cref="IBusyIndicator"/>: shows the system wait cursor via
/// <see cref="Mouse.OverrideCursor"/> for the duration of the returned <see cref="IDisposable"/> (LOW-3 fix,
/// code review, panel clone to page, backlog 1.x wave 3 review — replaces
/// <c>MainWindowViewModel.CopyPanelToPage</c>'s own <c>Thread.CurrentThread.GetApartmentState()</c> gate).
/// Only ever resolved through production DI, where the WPF UI thread is always STA — a unit test that wants
/// a busy indication supplies a fake <see cref="IBusyIndicator"/> instead of resolving this type.
/// </summary>
public sealed class MouseWaitCursorBusyIndicator : IBusyIndicator
{
    /// <inheritdoc />
    public IDisposable Show()
    {
        Mouse.OverrideCursor = Cursors.Wait;
        return new CursorScope();
    }

    /// <summary>Restores the default cursor exactly once when disposed.</summary>
    private sealed class CursorScope : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Mouse.OverrideCursor = null;
        }
    }
}
