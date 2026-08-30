namespace LumaLauncher.Services;

public sealed class InstanceCoordinator : IDisposable
{
    private const string MutexName = @"Local\LumaLauncher.Primary";
    private const string EventName = @"Local\LumaLauncher.Activate";
    private readonly EventWaitHandle _activationEvent;
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task? _waitTask;
    private readonly bool _ownsMutex;
    private int _pendingActivation;

    public InstanceCoordinator()
    {
        _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        _mutex = new Mutex(true, MutexName, out _ownsMutex);
        IsPrimary = _ownsMutex;

        if (IsPrimary)
            _waitTask = Task.Run(WaitLoop);
        else
            _activationEvent.Set();
    }

    public bool IsPrimary { get; }
    public event Action? ActivationRequested;

    private void WaitLoop()
    {
        var handles = new WaitHandle[] { _cancellation.Token.WaitHandle, _activationEvent };
        while (!_cancellation.IsCancellationRequested)
        {
            if (WaitHandle.WaitAny(handles) != 1)
                break;
            var handler = ActivationRequested;
            if (handler is null)
                Interlocked.Exchange(ref _pendingActivation, 1);
            else
                handler();
        }
    }

    public void DrainPendingActivation()
    {
        if (Interlocked.Exchange(ref _pendingActivation, 0) != 0)
            ActivationRequested?.Invoke();
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        try { _waitTask?.Wait(TimeSpan.FromSeconds(1)); } catch { }
        if (_ownsMutex)
            _mutex.ReleaseMutex();
        _mutex.Dispose();
        _activationEvent.Dispose();
        _cancellation.Dispose();
    }
}
