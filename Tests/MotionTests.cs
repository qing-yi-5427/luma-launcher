using System.Reflection;
using LumaLauncher.Services;

namespace LumaLauncher.Tests;

internal static class MotionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void VerifySpring()
    {
        var spring = new CriticalSpring(0.34);
        spring.Reset(700);
        spring.Retarget(1040);
        for (var i = 0; i < 5; i++) spring.Step(1.0 / 60);
        var current = spring.Value;
        var velocity = spring.Velocity;
        spring.Retarget(600);
        Check(spring.Value == current && spring.Velocity == velocity, "Retarget jumped or lost velocity");
        for (var i = 0; i < 120; i++) spring.Step(1.0 / 60);
        Check(Math.Abs(spring.Value - 600) < 0.4 && spring.Velocity == 0, "Spring did not settle");
    }

    internal static void VerifyWindow(MainWindow window)
    {
        window.ShowActivated = false;
        window.Show();
        try
        {
        var animate = typeof(MainWindow).GetMethod("AnimateWindowSize", Private)!;
        var animateShow = typeof(MainWindow).GetMethod("AnimateShow", Private)!;
        var openHelp = typeof(MainWindow).GetMethod("OpenHelp", Private)!;
        var subscription = typeof(MainWindow).GetField("_motionSubscribed", Private)!;

        animate.Invoke(window, [700d, 600d]);
        Check(Math.Abs(window.Width - 700) < 0.35 && Math.Abs(window.Height - 600) < 0.35,
            "Native window size did not reach its target immediately");
        Check(!(bool)subscription.GetValue(window)!, "Window size subscribed to per-frame rendering");
        animate.Invoke(window, [1040d, 680d]);
        Check(Math.Abs(window.Width - 1040) < 0.35 && Math.Abs(window.Height - 680) < 0.35,
            "Native window resize did not reach its second target immediately");
        Check(!(bool)subscription.GetValue(window)!, "Window resize left rendering subscribed");
        animateShow.Invoke(window, [false]);
        if (System.Windows.SystemParameters.ClientAreaAnimation)
            Check((bool)subscription.GetValue(window)!, "Show visual motion was not subscribed");
        openHelp.Invoke(window, null);
        Check(!(bool)subscription.GetValue(window)!, "Help left show rendering subscribed");
        animateShow.Invoke(window, [false]);
        window.HideLauncher();
        Check(!(bool)subscription.GetValue(window)!, "Hide left rendering subscription active");
        }
        finally { window.CloseForExit(); }
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
