using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Reflection;
using LumaLauncher.Models;
using LumaLauncher.Services;

namespace LumaLauncher.Tests;

internal static class GlassNativeTests
{
    internal static void Run()
    {
        if (Environment.OSVersion.Version.Build < 22621)
        {
            Console.WriteLine("SKIP native Desktop Acrylic probe: Windows 11 build 22621+ required.");
            return;
        }
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                ProbeOnStaThread();
                ProbeRealLauncherOnStaThread();
                ProbeRealSettingsOnStaThread();
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new InvalidOperationException("Glass native probe failed.", failure);
        Console.WriteLine("PASS launcher/settings native Acrylic, dark backdrop, rounded regions and show/help transitions; visible desktop blur still requires manual inspection.");
    }

    private static void ProbeRealLauncherOnStaThread()
    {
        var app = new App();
        app.InitializeComponent();
        ThemeService.Apply(ThemeService.AppleDark);
        var store = new SettingsStore();
        store.Save(new AppSettings { Theme = ThemeService.AppleDark });
        var main = new MainWindow(store, previewMode: true)
        {
            ShowActivated = false,
            ShowInTaskbar = false,
            Topmost = false,
            Left = -10000,
            Top = -10000
        };
        try
        {
            main.Show();
            main.UpdateLayout();
            IntPtr handle = new WindowInteropHelper(main).Handle;
            Require(handle != IntPtr.Zero, "MainWindow did not create a real HWND.");
            var glassField = typeof(MainWindow).GetField("_glass", BindingFlags.NonPublic | BindingFlags.Instance);
            var glass = glassField?.GetValue(main) as WindowsGlassService;
            Require(glass is not null, "MainWindow did not attach WindowsGlassService.");
            if (glass!.IsAcrylicActive)
                Require(GlassNative.DwmGetWindowAttribute(handle, GlassNative.DwmSystemBackdropType,
                    out int actualBackdrop, sizeof(int)) == 0 &&
                    actualBackdrop == GlassNative.DwmBackdropTransientWindow,
                    "MainWindow lost the requested native Desktop Acrylic attribute.");
            Require(GlassNative.DwmGetWindowAttribute(handle, GlassNative.DwmUseImmersiveDarkMode,
                out int darkMode, sizeof(int)) == 0 && darkMode == 1,
                "MainWindow did not request the dark native backdrop for AppleDark.");
            AssertNonLayered(handle, "after Show");
            var animateShow = typeof(MainWindow).GetMethod("AnimateShow", BindingFlags.NonPublic | BindingFlags.Instance);
            Require(animateShow is not null, "MainWindow show animation was not found.");
            animateShow!.Invoke(main, [false]);
            AssertNonLayered(handle, "at animation start");
            var frame = new DispatcherFrame();
            var sample = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(35)
            };
            int samples = 0;
            sample.Tick += (_, _) =>
            {
                AssertNonLayered(handle, "during show animation");
                if (++samples >= 5)
                {
                    sample.Stop();
                    frame.Continue = false;
                }
            };
            sample.Start();
            Dispatcher.PushFrame(frame);
            AssertNonLayered(handle, "after show animation");
            IntPtr region = GlassNative.CreateRectRgn(0, 0, 0, 0);
            try
            {
                Require(GlassNative.GetWindowRgn(handle, region) == 3,
                    "MainWindow did not retain a disjoint complex glass region after animation.");
            }
            finally { GlassNative.DeleteObject(region); }

            // F1 expands the same HWND with a large rounded card. Change only
            // visual state here so the test never focuses or repositions the desktop window.
            var help = (FrameworkElement)main.FindName("HelpOverlay");
            main.Width = 720;
            main.Height = 420;
            help.Visibility = Visibility.Visible;
            main.UpdateLayout();
            glass.RefreshRegion();
            var helpBounds = help.TransformToAncestor(main)
                .TransformBounds(new Rect(0, 0, help.ActualWidth, help.ActualHeight));
            var helpPoint = new Point(helpBounds.Left + helpBounds.Width / 2,
                helpBounds.Top + helpBounds.Height * 0.7);
            AssertRegionPoint(handle, helpPoint, true, "help open");
            help.Visibility = Visibility.Collapsed;
            main.Width = 660;
            main.Height = 82;
            main.UpdateLayout();
            glass.RefreshRegion();
            AssertRegionPoint(handle, helpPoint, false, "help closed");
        }
        finally
        {
            main.CloseForExit();
        }
    }

    private static void ProbeRealSettingsOnStaThread()
    {
        var settings = new SettingsWindow(new AppSettings { Theme = ThemeService.AppleDark })
        {
            ShowActivated = false,
            ShowInTaskbar = false,
            Topmost = false,
            Left = -10000,
            Top = -10000
        };
        try
        {
            settings.Show();
            settings.UpdateLayout();
            IntPtr handle = new WindowInteropHelper(settings).Handle;
            AssertNonLayered(handle, "in SettingsWindow");
            var glassField = typeof(SettingsWindow).GetField("_glass", BindingFlags.NonPublic | BindingFlags.Instance);
            var glass = glassField?.GetValue(settings) as WindowsGlassService;
            Require(glass is not null, "SettingsWindow did not attach WindowsGlassService.");
            if (glass!.IsAcrylicActive)
                Require(GlassNative.DwmGetWindowAttribute(handle, GlassNative.DwmSystemBackdropType,
                    out int backdrop, sizeof(int)) == 0 &&
                    backdrop == GlassNative.DwmBackdropTransientWindow,
                    "SettingsWindow lost the requested native Desktop Acrylic attribute.");
            Require(GlassNative.DwmGetWindowAttribute(handle, GlassNative.DwmUseImmersiveDarkMode,
                out int darkMode, sizeof(int)) == 0 && darkMode == 1,
                "SettingsWindow did not request the dark native backdrop for AppleDark.");
            IntPtr region = GlassNative.CreateRectRgn(0, 0, 0, 0);
            try
            {
                Require(GlassNative.GetWindowRgn(handle, region) != 0,
                    "SettingsWindow has no rounded native region.");
                Require(GlassNative.PtInRegion(region, 100, 100),
                    "SettingsWindow's content falls outside its native region.");
                Require(!GlassNative.PtInRegion(region, 0, 0),
                    "SettingsWindow's native region lost its rounded corner.");
            }
            finally { GlassNative.DeleteObject(region); }
        }
        finally { settings.Close(); }
    }

    private static void AssertRegionPoint(IntPtr handle, Point dipPoint, bool expected, string phase)
    {
        var source = HwndSource.FromHwnd(handle);
        Require(source?.CompositionTarget is not null, "Missing WPF composition target.");
        var pixel = source!.CompositionTarget.TransformToDevice.Transform(dipPoint);
        IntPtr region = GlassNative.CreateRectRgn(0, 0, 0, 0);
        try
        {
            Require(GlassNative.GetWindowRgn(handle, region) == 3, $"Complex region missing with {phase}.");
            bool covered = GlassNative.PtInRegion(region, (int)Math.Round(pixel.X), (int)Math.Round(pixel.Y));
            Require(covered == expected, $"Help card region coverage wrong with {phase}.");
        }
        finally { GlassNative.DeleteObject(region); }
    }

    private static void AssertNonLayered(IntPtr handle, string phase)
    {
        Require((GlassNative.GetWindowLongPtr(handle, -20).ToInt64() & GlassNative.WsExLayered) == 0,
            $"MainWindow became WS_EX_LAYERED {phase}.");
    }

    private static void ProbeOnStaThread()
    {
        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = false,
            ShowActivated = false,
            ShowInTaskbar = false,
            Width = 660,
            Height = 400,
            Left = -10000,
            Top = -10000
        };
        IntPtr handle = new WindowInteropHelper(window).EnsureHandle();
        try
        {
            Require((GlassNative.GetWindowLongPtr(handle, -20).ToInt64() & GlassNative.WsExLayered) == 0,
                "The Acrylic host must be non-layered.");
            var frame = new GlassNative.Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            Require(GlassNative.DwmExtendFrameIntoClientArea(handle, ref frame) == 0,
                "Full-client DWM frame extension failed.");
            int type = GlassNative.DwmBackdropTransientWindow;
            Require(GlassNative.DwmSetWindowAttribute(handle, GlassNative.DwmSystemBackdropType, ref type, sizeof(int)) == 0,
                "Desktop Acrylic DWM attribute was rejected.");
            Require(GlassNative.DwmGetWindowAttribute(handle, GlassNative.DwmSystemBackdropType, out int readBack, sizeof(int)) == 0 &&
                    readBack == type, "Desktop Acrylic DWM attribute did not read back.");

            IntPtr top = GlassNative.CreateRoundRectRgn(15, 9, 645, 73, 64, 64);
            IntPtr bottom = GlassNative.CreateRoundRectRgn(15, 90, 645, 395, 40, 40);
            IntPtr union = GlassNative.CreateRectRgn(0, 0, 0, 0);
            try
            {
                Require(GlassNative.CombineRgn(union, top, bottom, GlassNative.RgnOr) == 3,
                    "The capsule and results panel must form a disjoint complex region.");
                Require(GlassNative.SetWindowRgn(handle, union, false) != 0,
                    "Windows rejected the disjoint glass region.");
                union = IntPtr.Zero; // Window now owns the region.
                IntPtr verify = GlassNative.CreateRectRgn(0, 0, 0, 0);
                try { Require(GlassNative.GetWindowRgn(handle, verify) == 3, "The HWND lost its complex region."); }
                finally { GlassNative.DeleteObject(verify); }
            }
            finally
            {
                GlassNative.DeleteObject(top);
                GlassNative.DeleteObject(bottom);
                if (union != IntPtr.Zero) GlassNative.DeleteObject(union);
            }
        }
        finally
        {
            window.Close();
        }
    }

    private static void Require(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
