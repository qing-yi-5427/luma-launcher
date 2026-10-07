using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace LumaLauncher.Services;

/// <summary>A visual surface that contributes one rounded piece to the launcher's native window region.</summary>
internal readonly record struct GlassRegionPart(FrameworkElement Element, double RadiusDip);

/// <summary>
/// Requests Windows 11 Desktop Acrylic for an ordinary (non-layered) WPF HWND and
/// clips the HWND to the separate launcher surfaces. DWM owns the background;
/// this class does not sample the desktop or blur the application's own pixels.
/// </summary>
internal sealed class WindowsGlassService : IDisposable
{
    private readonly Window _window;
    private readonly FrameworkElement _root;
    private readonly GlassRegionPart[] _parts;
    private HwndSource? _source;
    private IntPtr _handle;
    private Color _originalTargetColor;
    private string? _lastRegionKey;
    private bool _attached;
    private bool _disposed;

    internal WindowsGlassService(Window window, FrameworkElement root, params GlassRegionPart[] parts)
    {
        _window = window;
        _root = root;
        _parts = parts;
    }

    internal bool IsAcrylicActive { get; private set; }
    internal bool IsDarkMode { get; private set; }
    internal int LastBackdropHResult { get; private set; } = unchecked((int)0x80004005);
    internal int LastRegionResult { get; private set; }
    internal event Action<bool>? AcrylicStateChanged;

    internal void Attach()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_attached) return;
        _handle = new WindowInteropHelper(_window).EnsureHandle();
        _source = HwndSource.FromHwnd(_handle);
        if (_source?.CompositionTarget is null) return;
        _originalTargetColor = _source.CompositionTarget.BackgroundColor;
        _attached = true;
        _root.LayoutUpdated += OnLayoutUpdated;
        _window.IsVisibleChanged += OnVisibleChanged;
        _source.AddHook(OnWindowMessage);
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
        RefreshMaterial();
        RefreshRegion();
    }

    internal void RefreshMaterial()
    {
        if (!_attached || _source?.CompositionTarget is null) return;
        bool active = false;
        int backdrop = GlassNative.DwmBackdropNone;
        if (Environment.OSVersion.Version.Build >= 22621 &&
            !SystemParameters.HighContrast && SystemTransparencyEnabled() &&
            GlassNative.DwmIsCompositionEnabled(out var compositionEnabled) == 0 && compositionEnabled &&
            (GlassNative.GetWindowLongPtr(_handle, -20).ToInt64() & GlassNative.WsExLayered) == 0)
        {
            var fullClient = new GlassNative.Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            _source.CompositionTarget.BackgroundColor = Colors.Transparent;
            int frameResult = GlassNative.DwmExtendFrameIntoClientArea(_handle, ref fullClient);
            backdrop = GlassNative.DwmBackdropTransientWindow;
            LastBackdropHResult = GlassNative.DwmSetWindowAttribute(
                _handle, GlassNative.DwmSystemBackdropType, ref backdrop, sizeof(int));
            int readResult = GlassNative.DwmGetWindowAttribute(
                _handle, GlassNative.DwmSystemBackdropType, out int readBack, sizeof(int));
            active = frameResult == 0 && LastBackdropHResult == 0 && readResult == 0 && readBack == backdrop;
        }
        if (!active)
        {
            backdrop = GlassNative.DwmBackdropNone;
            LastBackdropHResult = GlassNative.DwmSetWindowAttribute(
                _handle, GlassNative.DwmSystemBackdropType, ref backdrop, sizeof(int));
            var noFrame = new GlassNative.Margins();
            GlassNative.DwmExtendFrameIntoClientArea(_handle, ref noFrame);
            _source.CompositionTarget.BackgroundColor = _originalTargetColor;
        }

        // A custom HRGN supplies the silhouette. DWM cannot auto-round region windows.
        int noRound = GlassNative.DwmDoNotRound;
        GlassNative.DwmSetWindowAttribute(
            _handle, GlassNative.DwmWindowCornerPreference, ref noRound, sizeof(int));
        if (IsAcrylicActive == active) return;
        IsAcrylicActive = active;
        AcrylicStateChanged?.Invoke(active);
    }

    internal void SetDarkMode(bool dark)
    {
        IsDarkMode = dark;
        if (!_attached || Environment.OSVersion.Version.Build < 22621) return;
        int value = dark ? 1 : 0;
        GlassNative.DwmSetWindowAttribute(
            _handle, GlassNative.DwmUseImmersiveDarkMode, ref value, sizeof(int));
    }

    internal void RefreshRegion()
    {
        if (!_attached || _source?.CompositionTarget is null) return;
        var scale = _source.CompositionTarget.TransformToDevice;
        var shapes = new List<(int Left, int Top, int Right, int Bottom, int Radius)>();
        foreach (var part in _parts)
        {
            if (!part.Element.IsVisible || part.Element.ActualWidth < 1 || part.Element.ActualHeight < 1)
                continue;
            Rect bounds;
            try
            {
                bounds = part.Element.TransformToAncestor(_window)
                    .TransformBounds(new Rect(0, 0, part.Element.ActualWidth, part.Element.ActualHeight));
            }
            catch (InvalidOperationException)
            {
                continue;
            }
            var upperLeft = scale.Transform(bounds.TopLeft);
            var lowerRight = scale.Transform(bounds.BottomRight);
            int left = (int)Math.Floor(upperLeft.X);
            int top = (int)Math.Floor(upperLeft.Y);
            int right = (int)Math.Ceiling(lowerRight.X);
            int bottom = (int)Math.Ceiling(lowerRight.Y);
            int radius = (int)Math.Round(part.RadiusDip * scale.M11);
            if (right > left && bottom > top)
                shapes.Add((left, top, right, bottom, Math.Max(1, radius)));
        }
        if (shapes.Count == 0) return;
        string key = string.Join(';', shapes.Select(static shape =>
            $"{shape.Left},{shape.Top},{shape.Right},{shape.Bottom},{shape.Radius}"));
        if (key == _lastRegionKey) return;

        IntPtr region = GlassNative.CreateRectRgn(0, 0, 0, 0);
        if (region == IntPtr.Zero) return;
        try
        {
            foreach (var shape in shapes)
            {
                int diameter = shape.Radius * 2;
                IntPtr piece = GlassNative.CreateRoundRectRgn(
                    shape.Left, shape.Top, shape.Right, shape.Bottom, diameter, diameter);
                if (piece == IntPtr.Zero) return;
                try
                {
                    if (GlassNative.CombineRgn(region, region, piece, GlassNative.RgnOr) == 0)
                        return;
                }
                finally
                {
                    GlassNative.DeleteObject(piece);
                }
            }
            LastRegionResult = GlassNative.SetWindowRgn(_handle, region, true);
            if (LastRegionResult != 0)
            {
                // SetWindowRgn takes ownership of the HRGN on success.
                region = IntPtr.Zero;
                _lastRegionKey = key;
            }
        }
        finally
        {
            if (region != IntPtr.Zero) GlassNative.DeleteObject(region);
        }
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_window.IsVisible) RefreshRegion();
    }

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_window.IsVisible)
        {
            RefreshMaterial();
            _window.Dispatcher.BeginInvoke(RefreshRegion, DispatcherPriority.Loaded);
        }
    }

    private IntPtr OnWindowMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // WM_DPICHANGED / WM_DISPLAYCHANGE: WPF will re-layout after this message.
        if ((message is 0x02E0 or 0x007E) && _window.IsVisible && !_window.Dispatcher.HasShutdownStarted)
            _window.Dispatcher.BeginInvoke(RefreshRegion, DispatcherPriority.Loaded);
        return IntPtr.Zero;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (!_window.Dispatcher.HasShutdownStarted)
            _window.Dispatcher.BeginInvoke(RefreshMaterial);
    }

    private void OnSystemParameterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if ((e.PropertyName is nameof(SystemParameters.HighContrast) or null) &&
            !_window.Dispatcher.HasShutdownStarted)
        {
            if (_window.Dispatcher.CheckAccess()) RefreshMaterial();
            else _window.Dispatcher.BeginInvoke(RefreshMaterial);
        }
    }

    private static bool SystemTransparencyEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("EnableTransparency") is not int value || value != 0;
        }
        catch
        {
            // DWM itself still applies system policy, including battery saver.
            return true;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_attached) return;
        _root.LayoutUpdated -= OnLayoutUpdated;
        _window.IsVisibleChanged -= OnVisibleChanged;
        _source?.RemoveHook(OnWindowMessage);
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;
        AcrylicStateChanged = null;
        _source = null;
        _attached = false;
    }
}
