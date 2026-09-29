namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// Reports the current Windows colour-mode preferences (<see cref="SystemThemeSnapshot"/>) and raises
/// <see cref="Changed"/> when the user switches light/dark mode or high contrast (design system 1.x,
/// docs/PLANS/design-system-1x.md §3 phase 6.1, ADR-047 D-2).
/// </summary>
/// <remarks>
/// <para>
/// Change detection uses a hidden top-level listener window of its own (the same technique as
/// <see cref="IExplorerRestartMonitor"/>/<see cref="IMonitorService"/>): <c>WM_SETTINGCHANGE</c> (the
/// <c>"ImmersiveColorSet"</c> broadcast Windows sends on a light/dark switch, and
/// <c>SPI_SETHIGHCONTRAST</c>), <c>WM_SYSCOLORCHANGE</c> and <c>WM_THEMECHANGED</c> restart a short
/// debounce timer — Windows sends a burst of these per switch — and the snapshot is re-read once when it
/// fires. Nothing is persisted.
/// </para>
/// <para>
/// Never throws: an unreadable registry keeps the last known snapshot (or <see cref="SystemThemeSnapshot.Default"/>).
/// </para>
/// </remarks>
public interface ISystemThemeProvider : IDisposable
{
    /// <summary>
    /// Raised only when the snapshot actually changed, with the new snapshot. Raised on the thread that
    /// called <see cref="Start"/> (the WPF UI thread in production) from inside a window procedure — the
    /// consumer marshals if it needs another thread and must keep handlers cheap. A handler that throws is
    /// logged and does not prevent the remaining handlers from running.
    /// </summary>
    event EventHandler<SystemThemeSnapshot>? Changed;

    /// <summary>
    /// Gets the current snapshot. After a successful <see cref="Start"/> this is the last snapshot read
    /// by the listener (at most one debounce interval stale); before <see cref="Start"/>, or when the
    /// listener could not be created, it is read live on every call. Thread-safe; never throws.
    /// </summary>
    SystemThemeSnapshot Current { get; }

    /// <summary>
    /// Gets a value indicating whether the hidden listener window exists, i.e. whether
    /// <see cref="Start"/> succeeded and <see cref="Changed"/> can fire. When <see langword="false"/> the
    /// theme is still readable through <see cref="Current"/>, only live updates are missing — and the
    /// reason has been logged.
    /// </summary>
    bool IsListening { get; }

    /// <summary>
    /// Reads the baseline snapshot and creates the hidden listener window. Must be called from a thread
    /// with a running message loop (the WPF UI thread). Idempotent; never throws.
    /// </summary>
    void Start();
}
