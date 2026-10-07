using LumaLauncher.Services;
using System.Windows.Threading;

namespace LumaLauncher.Tests;

internal static class HotkeyReliabilityTests
{
    internal static void Run()
    {
        var other = new IntPtr(0x100);
        var launcher = new IntPtr(0x200);
        var newApp = new IntPtr(0x300);

        FocusRecoveryAction Decide(long elapsed, long sinceRetry = 120, int retries = 0,
            bool everActive = false, bool visible = true, bool active = false,
            bool menu = false, bool altHeld = false, IntPtr? foreground = null) =>
            LauncherFocusRecovery.Decide(elapsed, sinceRetry, retries, everActive, visible, active,
                menu, altHeld, foreground ?? other, other, launcher);

        Check(Decide(50, altHeld: true) == FocusRecoveryAction.Wait,
            "Holding Alt must not repeatedly activate the launcher");
        Check(Decide(150, altHeld: true) == FocusRecoveryAction.Wait,
            "Held shortcut repeat must remain blocked after the retry interval");
        Check(Decide(151) == FocusRecoveryAction.Retry,
            "An immediate loss of focus must be retried after Alt is released");
        Check(Decide(180, sinceRetry: 29, retries: 1) == FocusRecoveryAction.Wait,
            "Focus retry must be spaced, not a tight foreground loop");
        Check(Decide(301, retries: 1) == FocusRecoveryAction.Retry,
            "A failed first activation gets one bounded retry");
        Check(Decide(450, retries: 2) == FocusRecoveryAction.Wait,
            "Focus retry limit must prevent repeated foreground stealing");
        Check(Decide(100, everActive: true, foreground: other) == FocusRecoveryAction.Hide,
            "Returning to the original app after activation must not steal focus back");
        Check(Decide(100, foreground: newApp) == FocusRecoveryAction.Hide,
            "Switching to another app during the grace period must cancel recovery");
        Check(Decide(100, visible: false) == FocusRecoveryAction.Stop,
            "An explicit hide must stop recovery without reshowing");
        Check(Decide(100, menu: true) == FocusRecoveryAction.Stop,
            "An open launcher menu must not be taken for a focus failure");
        Check(Decide(901) == FocusRecoveryAction.Hide,
            "An unfocused launcher must not remain visible after the grace period");
        Check(Decide(901, active: true) == FocusRecoveryAction.Stop,
            "A focused launcher must remain open after the grace period");

        Check(HotkeyGesture.TryParse("Alt+Space", out var altSpace) &&
              (altSpace.Modifiers | NativeMethods.ModNoRepeat) ==
              (NativeMethods.ModAlt | NativeMethods.ModNoRepeat),
            "Alt+Space must retain the OS no-repeat hotkey modifier");
        VerifyLifecycle(other, launcher, newApp);
        Console.WriteLine("PASS hotkey focus recovery and held-shortcut policy");
    }

    private static void VerifyLifecycle(IntPtr original, IntPtr launcher, IntPtr settings)
    {
        long now = 1000;
        var visible = true;
        var active = false;
        var altHeld = true;
        var foreground = original;
        var attempts = 0;
        var hides = 0;
        var succeedNextActivation = false;
        var recovery = new LauncherFocusRecovery(Dispatcher.CurrentDispatcher, () => visible, () => active,
            () => false, () => altHeld, () => foreground,
            () =>
            {
                attempts++;
                if (succeedNextActivation)
                {
                    succeedNextActivation = false;
                    active = true;
                    foreground = launcher;
                    return true;
                }
                return active;
            }, () => { hides++; visible = false; }, () => now);

        recovery.Start(original, launcher, initiallyActive: false);
        now += 150;
        recovery.Tick();
        Check(attempts == 0 && hides == 0, "Holding Alt retriggered focus recovery");
        altHeld = false;
        recovery.Tick();
        Check(attempts == 1 && recovery.IsRunning, "Unfocused visible launcher was not retried after key release");
        now += 150;
        recovery.Tick();
        Check(attempts == 2, "A second bounded focus attempt was not made");
        now += 700;
        recovery.Tick();
        Check(hides == 1 && !recovery.IsRunning && !visible,
            "Lost deactivation in the grace period left an unfocused launcher visible");

        visible = true;
        active = true;
        foreground = launcher;
        recovery.Start(original, launcher, initiallyActive: true);
        active = false;
        foreground = original;
        now += 50;
        recovery.Tick();
        Check(attempts == 2 && hides == 2 && !recovery.IsRunning,
            "Returning to the original app after focus must hide without reclaiming focus");

        visible = true;
        foreground = original;
        recovery.Start(original, launcher, initiallyActive: false);
        foreground = settings;
        now += 50;
        recovery.Tick();
        Check(attempts == 2 && hides == 3 && !recovery.IsRunning,
            "Switching to settings must cancel recovery without activating the launcher");

        visible = true;
        foreground = original;
        recovery.Start(original, launcher, initiallyActive: false);
        recovery.Stop(); // Explicit HideLauncher/Closed path.
        now += 950;
        recovery.Tick();
        Check(attempts == 2 && hides == 3 && !recovery.IsRunning,
            "A stopped recovery resurrected the launcher after explicit hide or close");

        visible = true;
        foreground = original;
        succeedNextActivation = true;
        recovery.Start(original, launcher, initiallyActive: false);
        now += 150;
        recovery.Tick();
        Check(attempts == 3 && active, "The controlled focus retry did not succeed");
        // The user returns to the original app before the next timer tick.
        active = false;
        foreground = original;
        now += 50;
        recovery.Tick();
        Check(attempts == 3 && hides == 4 && !recovery.IsRunning,
            "A successful retry followed by immediate focus loss reclaimed focus again");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
