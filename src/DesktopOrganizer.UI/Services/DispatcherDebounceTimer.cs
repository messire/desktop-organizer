using System.Windows.Threading;

namespace DesktopOrganizer.UI.Services;

/// <summary>
/// <see cref="IDebounceTimer"/> backed by a WPF <see cref="DispatcherTimer"/>, so the deferred
/// callback always runs on the UI thread the timer was created on. Required by
/// <see cref="WindowManager.SaveWindowsState"/>: the deferred work touches live <c>ChildWindow</c>
/// instances via <c>SyncToModel()</c>, which is only safe from the UI thread.
/// </summary>
internal sealed class DispatcherDebounceTimer : IDebounceTimer
{
    private readonly DispatcherTimer _timer;
    private Action? _callback;

    /// <summary>
    /// Initializes a new instance of the <see cref="DispatcherDebounceTimer"/> class bound to
    /// <paramref name="dispatcher"/>.
    /// </summary>
    /// <param name="dispatcher">The UI-thread dispatcher the deferred callback must run on.</param>
    public DispatcherDebounceTimer(Dispatcher dispatcher)
    {
        _timer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher);
        _timer.Tick += OnTick;
    }

    /// <inheritdoc />
    public void Start(TimeSpan window, Action callback)
    {
        _timer.Stop();
        _callback = callback;
        _timer.Interval = window;
        _timer.Start();
    }

    /// <inheritdoc />
    public void Stop()
    {
        _timer.Stop();
        _callback = null;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _timer.Stop();
        var callback = _callback;
        _callback = null;
        callback?.Invoke();
    }
}
