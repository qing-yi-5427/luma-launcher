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
        var handles = new WaitHandle[] { _activationEvent, _cancellation.Token.WaitHandle };
        while (!_cancellation.IsCancellationRequested)
        {
            if (WaitHandle.WaitAny(handles) != 0)
                break;
            ActivationRequested?.Invoke();
        }
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        _activationEvent.Set();
        try { _waitTask?.Wait(TimeSpan.FromSeconds(1)); } catch { }
        if (_ownsMutex)
            _mutex.ReleaseMutex();
        _mutex.Dispose();
        _activationEvent.Dispose();
        _cancellation.Dispose();
    }
}
