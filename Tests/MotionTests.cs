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
        var openHelp = typeof(MainWindow).GetMethod("OpenHelp", Private)!;
        var subscription = typeof(MainWindow).GetField("_motionSubscribed", Private)!;
        var widthSpring = (CriticalSpring)typeof(MainWindow).GetField("_widthSpring", Private)!.GetValue(window)!;

        animate.Invoke(window, [700d, 600d]);
        if (!System.Windows.SystemParameters.ClientAreaAnimation) return;
        Check((bool)subscription.GetValue(window)!, "Window size motion was not subscribed");
        animate.Invoke(window, [1040d, 680d]);
        Check(widthSpring.Target == 1040, "Window size retarget was lost");
        openHelp.Invoke(window, null);
        Check(!(bool)subscription.GetValue(window)!, "Help left stale motion active");
        animate.Invoke(window, [700d, 500d]);
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
