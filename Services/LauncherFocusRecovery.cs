using System.Windows.Threading;

namespace LumaLauncher.Services;

internal enum FocusRecoveryAction { Wait, Retry, Hide, Stop }

/// <summary>Bounds focus recovery after a global hotkey without reclaiming focus from a new foreground app.</summary>
internal sealed class LauncherFocusRecovery
{
    private const int GraceMilliseconds = 900;
    private const int RetrySpacingMilliseconds = 120;
    private const int MaximumRetries = 2;
    private readonly DispatcherTimer _timer;
    private readonly Func<bool> _isVisible;
    private readonly Func<bool> _isActive;
    private readonly Func<bool> _hasContextMenu;
    private readonly Func<bool> _altHeld;
    private readonly Func<IntPtr> _foreground;
    private readonly Func<bool> _activate;
    private readonly Action _hide;
    private readonly Func<long> _clock;
    private IntPtr _previousForeground;
    private IntPtr _launcherHandle;
    private long _started;
    private long _lastRetry;
    private int _retries;
    private bool _everActive;

    internal LauncherFocusRecovery(Dispatcher dispatcher, Func<bool> isVisible, Func<bool> isActive,
        Func<bool> hasContextMenu, Func<bool> altHeld, Func<IntPtr> foreground, Func<bool> activate, Action hide,
        Func<long>? clock = null)
    {
        _isVisible = isVisible;
        _isActive = isActive;
        _hasContextMenu = hasContextMenu;
        _altHeld = altHeld;
        _foreground = foreground;
        _activate = activate;
        _hide = hide;
        _clock = clock ?? (() => Environment.TickCount64);
        _timer = new DispatcherTimer(DispatcherPriority.Input, dispatcher) { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += (_, _) => Tick();
    }

    internal bool IsRunning => _timer.IsEnabled;

    internal void Start(IntPtr previousForeground, IntPtr launcherHandle, bool initiallyActive)
    {
        Stop();
        _previousForeground = previousForeground;
        _launcherHandle = launcherHandle;
        _started = _clock();
        _lastRetry = _started;
        _retries = 0;
        _everActive = initiallyActive;
        _timer.Start();
    }

    internal void Stop() => _timer.Stop();

    internal void Tick()
    {
        if (!_timer.IsEnabled) return;
        var now = _clock();
        var active = _isActive();
        _everActive |= active;
        var action = Decide(now - _started, now - _lastRetry, _retries, _everActive,
            _isVisible(), active, _hasContextMenu(), _altHeld(),
            _foreground(), _previousForeground, _launcherHandle);
        if (action == FocusRecoveryAction.Retry)
        {
            _retries++;
            _lastRetry = now;
            var activated = _activate();
            _everActive |= activated;
            DiagnosticsService.Log("hotkey-focus", $"retry={_retries}; activated={activated}");
        }
        else if (action == FocusRecoveryAction.Hide)
        {
            DiagnosticsService.Log("hotkey-focus", $"lost-focus; ever_active={_everActive}; retries={_retries}");
            Stop();
            _hide();
        }
        else if (action == FocusRecoveryAction.Stop)
        {
            if (!_isActive() && _isVisible())
                DiagnosticsService.Log("hotkey-focus", $"recovery-ended; ever_active={_everActive}; retries={_retries}");
            Stop();
        }
    }

    internal static FocusRecoveryAction Decide(long elapsed, long sinceRetry, int retries, bool everActive,
        bool visible, bool active, bool contextMenu, bool altHeld, IntPtr foreground,
        IntPtr previousForeground, IntPtr launcherHandle)
    {
        if (!visible || contextMenu) return FocusRecoveryAction.Stop;
        // Once focus was obtained, a later switch away is intentional. Hide; never take it back.
        if (everActive && !active) return FocusRecoveryAction.Hide;
        // A user deliberately switched to a different app. Never pull focus back from it.
        if (!active && foreground != IntPtr.Zero && foreground != launcherHandle &&
            foreground != previousForeground) return FocusRecoveryAction.Hide;
        if (elapsed >= GraceMilliseconds) return active ? FocusRecoveryAction.Stop : FocusRecoveryAction.Hide;
        if (active || altHeld || retries >= MaximumRetries || sinceRetry < RetrySpacingMilliseconds)
            return FocusRecoveryAction.Wait;
        return FocusRecoveryAction.Retry;
    }
}
