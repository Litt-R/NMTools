using System.Threading;

namespace NMTools.App.Infrastructure;

public sealed class SingleInstanceService : IDisposable
{
    private const string MutexName = "Local\\NMTools.App.Singleton";
    private const string ShowMainWindowEventName = "Local\\NMTools.App.ShowMainWindow";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showMainWindowEvent;
    private RegisteredWaitHandle? _registeredWait;
    private bool _disposed;

    private SingleInstanceService(Mutex mutex, EventWaitHandle showMainWindowEvent)
    {
        _mutex = mutex;
        _showMainWindowEvent = showMainWindowEvent;
        _registeredWait = ThreadPool.RegisterWaitForSingleObject(
            _showMainWindowEvent,
            OnShowMainWindowRequested,
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    public event EventHandler? ShowMainWindowRequested;

    public static SingleInstanceService? TryCreate()
    {
        var showMainWindowEvent = new EventWaitHandle(
            initialState: false,
            EventResetMode.AutoReset,
            ShowMainWindowEventName);
        var mutex = new Mutex(
            initiallyOwned: true,
            MutexName,
            out var createdNew);

        if (!createdNew)
        {
            try
            {
                showMainWindowEvent.Set();
            }
            finally
            {
                showMainWindowEvent.Dispose();
                mutex.Dispose();
            }

            return null;
        }

        try
        {
            return new SingleInstanceService(mutex, showMainWindowEvent);
        }
        catch
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
            showMainWindowEvent.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _registeredWait?.Unregister(null);
        _registeredWait = null;
        _showMainWindowEvent.Dispose();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
        GC.SuppressFinalize(this);
    }

    private void OnShowMainWindowRequested(object? state, bool timedOut)
    {
        if (!timedOut && !_disposed)
        {
            ShowMainWindowRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
