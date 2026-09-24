namespace DesktopOrganizer.UI.Lifecycle;

public sealed class SingleInstanceCoordinator : IDisposable
{
    private readonly Mutex _mutex;
    private bool _ownsMutex;

    public SingleInstanceCoordinator(string name)
    {
        _mutex = new Mutex(initiallyOwned: false, name);
    }

    public bool TryAcquire()
    {
        try
        {
            _ownsMutex = _mutex.WaitOne(0, false);
        }
        catch (AbandonedMutexException)
        {
            _ownsMutex = true;
        }
        return _ownsMutex;
    }

    public void Dispose()
    {
        if (_ownsMutex)
        {
            try { _mutex.ReleaseMutex(); }
            catch (ApplicationException) { }
        }
        _mutex.Dispose();
    }
}
