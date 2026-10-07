using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LumaLauncher.Models;
using LumaLauncher.Services;

namespace LumaLauncher.Tests;

internal static class GlassNativeTests
{
    internal static void Run()
    {
        if (Environment.OSVersion.Version.Build < 22621)
        {
            Console.WriteLine("SKIP Composition glass probe: Windows 11 build 22621+ required.");
            return;
        }
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { ProbeOnStaThread(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new InvalidOperationException("Glass native probe failed.", failure);
        Console.WriteLine("PASS real MainWindow/SettingsWindow glass hosts, lifecycle, hit testing and own-checkerboard composite capture.");
    }

    private static void ProbeOnStaThread()
    {
        var app = new App();
        app.InitializeComponent();
        ThemeService.Apply(ThemeService.AppleDark);
        var store = new SettingsStore();
        // Program redirects AppDataPaths into an isolated temporary directory.
        store.Save(new AppSettings { Theme = ThemeService.AppleDark });
        var work = SystemParameters.WorkArea;
        var board = CreateBoard(work.Left + 50, work.Top + 50);
        try
        {
        board.Show();
        Pump(TimeSpan.FromMilliseconds(200));
        var gapBlack = new Point(280, 2);
        var gapWhite = new Point(330, 2);
        var interior = new Point(250, 45);
        var interiorWhitePoint = new Point(220, 45);
        var interiorBlackPoint = new Point(270, 45);
        uint beforeBlack = SampleRelative(board, gapBlack);
        uint beforeWhite = SampleRelative(board, gapWhite);
        uint beforeInterior = SampleRelative(board, interior);
        uint beforeInteriorWhite = SampleRelative(board, interiorWhitePoint);
        uint beforeInteriorBlack = SampleRelative(board, interiorBlackPoint);
        var farBackground = new[] { new Point(100, 220), new Point(400, 270), new Point(600, 350) };
        var beforeFar = farBackground.Select(point => SampleRelative(board, point)).ToArray();
        var main = new MainWindow(store, previewMode: true)
        {
            ShowActivated = false, ShowInTaskbar = false, Topmost = true,
            Left = board.Left + 20, Top = board.Top + 20
        };
        IntPtr mainHelper = IntPtr.Zero;
        try
        {
            main.Show();
            main.UpdateLayout();
            Pump(TimeSpan.FromMilliseconds(250));
            var glass = GetGlass(main);
            Require(glass.IsAcrylicActive, $"MainWindow Composition host unavailable: 0x{glass.LastBackdropHResult:X8}.");
            mainHelper = glass.BackdropHandle;
            IntPtr mainHandle = new WindowInteropHelper(main).Handle;
            AssertHost(mainHandle, glass, "MainWindow after Show");
            Require(GlassNative.IsWindowVisible(glass.BackdropHandle), "MainWindow helper is hidden while foreground is shown.");
            uint afterBlack = SampleRelative(board, gapBlack);
            uint afterWhite = SampleRelative(board, gapWhite);
            uint afterInterior = SampleRelative(board, interior);
            uint afterInteriorWhite = SampleRelative(board, interiorWhitePoint);
            uint afterInteriorBlack = SampleRelative(board, interiorBlackPoint);
            Require(beforeBlack < 0x101010 && (afterBlack & 0xff) < 90,
                $"Black gap became a material rectangle: before={beforeBlack}, after={afterBlack}.");
            Require(beforeWhite > 0xeeeeee && (afterWhite & 0xff) > 170,
                $"White gap became a material rectangle: before={beforeWhite}, after={afterWhite}.");
            Require(afterInterior != beforeInterior, "Inside the search capsule did not composite over the checkerboard.");
            Require(beforeInteriorWhite > 0xeeeeee && beforeInteriorBlack < 0x101010,
                "Interior blur samples did not start over white and black checker tiles.");
            int interiorContrast = Math.Abs((int)(afterInteriorWhite & 0xff) - (int)(afterInteriorBlack & 0xff));
            Require(interiorContrast is > 5 and < 180,
                $"Capsule stopped sampling blurred background: white={afterInteriorWhite}, black={afterInteriorBlack}, contrast={interiorContrast}.");
            Require(HitRelative(board, gapBlack) != glass.BackdropHandle &&
                HitRelative(board, gapWhite) != glass.BackdropHandle,
                "Input-transparent glass helper intercepted a gap point.");
            CaptureBoard(board, Path.Combine(AppContext.BaseDirectory, "glass-main-composite.png"));
            Require(farBackground.Select(point => SampleRelative(board, point)).SequenceEqual(beforeFar),
                "A remote checkerboard pixel changed outside the launcher rectangle.");
            store.Save(new AppSettings { Theme = ThemeService.AppleLight });
            main.ApplySettings();
            Pump(TimeSpan.FromMilliseconds(100));
            CaptureBoard(board, Path.Combine(AppContext.BaseDirectory, "glass-main-light-composite.png"));
            Require(farBackground.Select(point => SampleRelative(board, point)).SequenceEqual(beforeFar),
                "Apple Light changed remote checkerboard pixels outside the launcher rectangle.");
            store.Save(new AppSettings { Theme = ThemeService.AppleDark });
            main.ApplySettings();

            main.Left += 27;
            main.Top += 13;
            main.UpdateLayout();
            Pump(TimeSpan.FromMilliseconds(100));
            AssertHost(mainHandle, glass, "after reposition");
            main.Width = 840;
            main.Height = 430;
            main.UpdateLayout();
            Pump(TimeSpan.FromMilliseconds(100));
            AssertHost(mainHandle, glass, "after resize");
            main.Width = 660;
            main.Height = 82;
            main.UpdateLayout();
            Pump(TimeSpan.FromMilliseconds(100));
            AssertHost(mainHandle, glass, "after compact resize");
            var animateShow = typeof(MainWindow).GetMethod("AnimateShow", BindingFlags.NonPublic | BindingFlags.Instance);
            Require(animateShow is not null, "MainWindow show animation was not found.");
            animateShow!.Invoke(main, [false]);
            for (int index = 0; index < 5; index++)
            {
                Pump(TimeSpan.FromMilliseconds(35));
                AssertHost(mainHandle, glass, $"during show animation sample {index}");
            }
            main.Hide();
            Pump(TimeSpan.FromMilliseconds(80));
            Require(!GlassNative.IsWindowVisible(glass.BackdropHandle), "Glass helper stayed visible after MainWindow.Hide().");
            main.Show();
            main.UpdateLayout();
            Pump(TimeSpan.FromMilliseconds(100));
            AssertHost(mainHandle, glass, "after hide/show");
            Require(GlassNative.IsWindowVisible(glass.BackdropHandle), "Glass helper did not return after MainWindow.Show().");

            var help = (FrameworkElement)main.FindName("HelpOverlay");
            main.Width = 720;
            main.Height = 420;
            help.Visibility = Visibility.Visible;
            main.UpdateLayout();
            glass.RefreshRegion();
            Pump(TimeSpan.FromMilliseconds(100));
            AssertHost(mainHandle, glass, "with help open");
            CaptureBoard(board, Path.Combine(AppContext.BaseDirectory, "glass-main-help-composite.png"));
            help.Visibility = Visibility.Collapsed;
            main.Width = 660;
            main.Height = 82;
            main.UpdateLayout();
            glass.RefreshRegion();
            AssertHost(mainHandle, glass, "after help close");
            // Keep MainWindow's service alive while another host is created.
            main.Hide();
            board.Top = work.Top + 5;
            board.Height = Math.Min(700, work.Height - 5);
            board.UpdateLayout();
            Pump(TimeSpan.FromMilliseconds(80));
            ProbeSettings(board, capture: true);
        }
        finally { main.CloseForExit(); }
        Require(mainHelper == IntPtr.Zero || !GlassNative.IsWindow(mainHelper),
            "MainWindow glass helper survived foreground Close().");
        // Creating another host on the same STA after both previous hosts close
        // catches premature DispatcherQueue teardown.
        ProbeSettings(board, capture: false);
        }
        finally { board.Close(); }
    }

    private static void ProbeSettings(Window board, bool capture)
    {
        var settings = new SettingsWindow(new AppSettings { Theme = ThemeService.AppleDark })
        {
            ShowActivated = false, ShowInTaskbar = false, Topmost = true,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = board.Left + 20, Top = board.Top + 20
        };
        IntPtr helper = IntPtr.Zero;
        try
        {
            settings.Show();
            settings.UpdateLayout();
            Pump(TimeSpan.FromMilliseconds(200));
            var glass = GetGlass(settings);
            Require(glass.IsAcrylicActive, $"SettingsWindow Composition host unavailable: 0x{glass.LastBackdropHResult:X8}.");
            helper = glass.BackdropHandle;
            AssertHost(new WindowInteropHelper(settings).Handle, glass, "SettingsWindow after Show");
            if (capture)
                CaptureBoard(board, Path.Combine(AppContext.BaseDirectory, "glass-settings-composite.png"));
        }
        finally { settings.Close(); }
        Require(helper == IntPtr.Zero || !GlassNative.IsWindow(helper),
            "SettingsWindow glass helper survived foreground Close().");
    }

    private static WindowsGlassService GetGlass(Window window)
    {
        var field = window.GetType().GetField("_glass", BindingFlags.NonPublic | BindingFlags.Instance);
        return field?.GetValue(window) as WindowsGlassService ??
            throw new InvalidOperationException($"{window.GetType().Name} has no glass service.");
    }

    private static void AssertHost(IntPtr foreground, WindowsGlassService glass, string phase)
    {
        Require((GlassNative.GetWindowLongPtr(foreground, -20).ToInt64() & GlassNative.WsExLayered) != 0,
            $"WPF foreground is not layered {phase}.");
        var helper = glass.BackdropHandle;
        Require(helper != IntPtr.Zero, $"Composition helper is missing {phase}.");
        long style = GlassNative.GetWindowLongPtr(helper, -20).ToInt64();
        long required = GlassNative.WsExNoRedirectionBitmap | GlassNative.WsExNoActivate |
            GlassNative.WsExToolWindow | GlassNative.WsExTransparent;
        Require((style & required) == required, $"Composition helper lost safe extended styles {phase}: 0x{style:X}.");
        Require(glass.LastBackdropHResult == 0, $"HostBackdropBrush attribute failed {phase}: 0x{glass.LastBackdropHResult:X8}.");
        Require(GlassNative.GetWindowRect(foreground, out var frontRect) &&
            GlassNative.GetWindowRect(helper, out var glassRect) && frontRect.Equals(glassRect),
            $"Composition helper does not track foreground bounds {phase}.");
        Require(GlassNative.GetWindow(foreground, 2) == helper,
            $"Composition helper is not directly behind its foreground {phase}.");
    }

    private static Window CreateBoard(double left, double top)
    {
        var grid = new Grid();
        for (int row = 0; row < 10; row++) grid.RowDefinitions.Add(new RowDefinition());
        for (int column = 0; column < 16; column++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int row = 0; row < 10; row++)
        for (int column = 0; column < 16; column++)
        {
            var tile = new Border { Background = (row + column) % 2 == 0 ? Brushes.Black : Brushes.White };
            Grid.SetRow(tile, row);
            Grid.SetColumn(tile, column);
            grid.Children.Add(tile);
        }
        return new Window { WindowStyle = WindowStyle.None, AllowsTransparency = false,
            ShowActivated = false, ShowInTaskbar = false, Topmost = true, Background = Brushes.Black,
            Left = left, Top = top, Width = 800, Height = 500, Content = grid };
    }

    private static Point ScreenRelative(Window board, Point foregroundPoint) =>
        board.PointToScreen(new Point(20 + foregroundPoint.X, 20 + foregroundPoint.Y));

    private static uint SampleRelative(Window board, Point point)
    {
        var screen = ScreenRelative(board, point);
        IntPtr dc = GetDC(IntPtr.Zero);
        try { return GetPixel(dc, (int)Math.Round(screen.X), (int)Math.Round(screen.Y)); }
        finally { ReleaseDC(IntPtr.Zero, dc); }
    }

    private static IntPtr HitRelative(Window board, Point point)
    {
        var screen = ScreenRelative(board, point);
        return WindowFromPoint(new NativePoint { X = (int)Math.Round(screen.X), Y = (int)Math.Round(screen.Y) });
    }

    private static void CaptureBoard(Window board, string path)
    {
        var handle = new WindowInteropHelper(board).Handle;
        Require(GetClientRect(handle, out var client), "Checkerboard has no client rectangle.");
        var origin = new NativePoint();
        Require(ClientToScreen(handle, ref origin), "Could not locate checkerboard client origin.");
        int width = client.Right - client.Left, height = client.Bottom - client.Top;
        var pixels = new byte[width * height * 4];
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr memory = CreateCompatibleDC(screen);
        var bitmapInfo = new BitmapInfo { Header = new BitmapInfoHeader
        { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32, SizeImage = (uint)pixels.Length } };
        IntPtr bitmap = CreateDIBSection(screen, ref bitmapInfo, 0, out IntPtr bits, IntPtr.Zero, 0);
        Require(bitmap != IntPtr.Zero, "Could not create checkerboard capture bitmap.");
        IntPtr previous = SelectObject(memory, bitmap);
        try
        {
            Require(BitBlt(memory, 0, 0, width, height, screen, origin.X, origin.Y, 0x00CC0020),
                "Could not capture checkerboard client pixels.");
            Marshal.Copy(bits, pixels, 0, pixels.Length);
        }
        finally
        {
            SelectObject(memory, previous);
            GlassNative.DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
        for (int index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfoHeader
    {
        public uint Size; public int Width; public int Height; public ushort Planes; public ushort BitCount;
        public uint Compression; public uint SizeImage; public int XPixelsPerMeter; public int YPixelsPerMeter;
        public uint ColorsUsed; public uint ColorsImportant;
    }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
    { public BitmapInfoHeader Header; public uint Colors; }
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out GlassNative.Rect rectangle);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr dc, int x, int y);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr bitmap);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height,
        IntPtr source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info,
        uint usage, out IntPtr bits, IntPtr section, uint offset);
}
