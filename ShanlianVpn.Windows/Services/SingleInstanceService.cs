using System.Threading;
using System.Windows;

namespace ShanlianVpn.Windows.Services;

public static class SingleInstanceService
{
    private static Mutex? _mutex;
    private static EventWaitHandle? _activateEvent;
    private static CancellationTokenSource? _listenCts;

    public static bool TryAcquire(string appId, Action onActivateRequested)
    {
        var mutexName = $@"Local\{appId}";
        var activateName = $@"Local\{appId}.Activate";

        _mutex = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, activateName);

        if (!createdNew)
        {
            _activateEvent.Set();
            _mutex.Dispose();
            _mutex = null;
            return false;
        }

        _listenCts = new CancellationTokenSource();
        _ = Task.Run(() => ListenForActivationAsync(onActivateRequested, _listenCts.Token));
        return true;
    }

    public static void Release()
    {
        _listenCts?.Cancel();
        _activateEvent?.Dispose();
        _activateEvent = null;

        if (_mutex is not null)
        {
            _mutex.ReleaseMutex();
            _mutex.Dispose();
            _mutex = null;
        }
    }

    private static Task ListenForActivationAsync(Action onActivateRequested, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            while (!cancellationToken.IsCancellationRequested && _activateEvent is not null)
            {
                _activateEvent.WaitOne();
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                System.Windows.Application.Current.Dispatcher.BeginInvoke(onActivateRequested);
            }
        }, cancellationToken);
    }
}
