using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;

namespace LumaLauncher.Services;

internal readonly record struct GlassRegionPart(FrameworkElement Element, double RadiusDip);

/// <summary>Keeps an input-transparent Composition backdrop behind a layered WPF window.</summary>
internal sealed class WindowsGlassService : IDisposable
{
    private readonly Window _window;
    private readonly FrameworkElement _root;
    private readonly GlassRegionPart[] _parts;
    private HwndSource? _source;
    private IntPtr _handle;
    private CompositionGlassHost? _host;
    private readonly GlassShape?[] _shapes;
    private readonly GlassShape?[] _lastShapes;
    private bool _hasLastShapes;
    private bool _attached;
    private bool _disposed;
    private bool _refreshQueued;
    private bool _zOrderDirty;

    internal int RegionScanCount { get; private set; }
    internal int ShapeUpdateCount { get; private set; }

    internal WindowsGlassService(Window window, FrameworkElement root, params GlassRegionPart[] parts)
    {
        _window = window;
        _root = root;
        _parts = parts;
        _shapes = new GlassShape?[parts.Length];
        _lastShapes = new GlassShape?[parts.Length];
    }

    internal bool IsAcrylicActive { get; private set; }
    internal bool IsDarkMode { get; private set; }
    internal IntPtr BackdropHandle => _host?.Handle ?? IntPtr.Zero;
    internal int LastBackdropHResult { get; private set; } = unchecked((int)0x80004005);
    internal event Action<bool>? AcrylicStateChanged;

    internal void Attach()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_attached) return;
        _handle = new WindowInteropHelper(_window).EnsureHandle();
        _source = HwndSource.FromHwnd(_handle);
        if (_source?.CompositionTarget is null) return;
        _attached = true;
        _root.SizeChanged += OnPartSizeChanged;
        foreach (var part in _parts)
        {
            part.Element.SizeChanged += OnPartSizeChanged;
            part.Element.IsVisibleChanged += OnPartVisibleChanged;
        }
        _window.IsVisibleChanged += OnVisibleChanged;
        _window.Activated += OnActivated;
        _source.AddHook(OnWindowMessage);
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
        RefreshMaterial();
        RefreshRegion();
    }

    internal void RefreshMaterial()
    {
        if (!_attached || _disposed) return;
        bool allowed = Environment.OSVersion.Version.Build >= 22621 &&
            !SystemParameters.HighContrast && SystemTransparencyEnabled() &&
            GlassNative.DwmIsCompositionEnabled(out var compositionEnabled) == 0 && compositionEnabled &&
            (GlassNative.GetWindowLongPtr(_handle, -20).ToInt64() & GlassNative.WsExLayered) != 0;
        if (allowed && _host is null)
        {
            try
            {
                _host = new CompositionGlassHost(_handle, _parts.Length);
                _host.SetDarkMode(IsDarkMode);
                LastBackdropHResult = _host.LastHostBackdropHResult;
                _hasLastShapes = false;
            }
            catch (Exception exception) when (exception is COMException or Win32Exception or InvalidOperationException or TypeLoadException)
            {
                LastBackdropHResult = exception.HResult;
                _host?.Dispose();
                _host = null;
            }
        }
        else if (!allowed && _host is not null)
        {
            _host.Dispose();
            _host = null;
            _hasLastShapes = false;
        }
        bool active = _host is not null;
        if (active) RefreshRegion();
        if (IsAcrylicActive == active) return;
        IsAcrylicActive = active;
        AcrylicStateChanged?.Invoke(active);
    }

    internal void SetDarkMode(bool dark)
    {
        IsDarkMode = dark;
        _host?.SetDarkMode(dark);
    }

    internal void RefreshRegion()
    {
        if (!_attached || _disposed || _source?.CompositionTarget is null || _host is null) return;
        RegionScanCount++;
        _host.SyncWindow(_window.IsVisible);
        var scale = _source.CompositionTarget.TransformToDevice;
        Array.Clear(_shapes);
        for (int index = 0; index < _parts.Length; index++)
        {
            var part = _parts[index];
            if (!part.Element.IsVisible || part.Element.ActualWidth < 1 || part.Element.ActualHeight < 1)
                continue;
            Rect bounds;
            try
            {
                bounds = part.Element.TransformToAncestor(_window)
                    .TransformBounds(new Rect(0, 0, part.Element.ActualWidth, part.Element.ActualHeight));
            }
            catch (InvalidOperationException) { continue; }
            var topLeft = scale.Transform(bounds.TopLeft);
            var bottomRight = scale.Transform(bounds.BottomRight);
            int left = (int)Math.Floor(topLeft.X);
            int top = (int)Math.Floor(topLeft.Y);
            int right = (int)Math.Ceiling(bottomRight.X);
            int bottom = (int)Math.Ceiling(bottomRight.Y);
            if (right <= left || bottom <= top) continue;
            _shapes[index] = new GlassShape(left, top, right - left, bottom - top,
                Math.Max(1, (int)Math.Round(part.RadiusDip * scale.M11)));
        }
        if (_hasLastShapes && _shapes.AsSpan().SequenceEqual(_lastShapes)) return;
        _host.SetShapes(_shapes);
        Array.Copy(_shapes, _lastShapes, _shapes.Length);
        _hasLastShapes = true;
        ShapeUpdateCount++;
    }

    private void OnPartSizeChanged(object sender, SizeChangedEventArgs e) => QueueRegionRefresh();

    private void OnPartVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => QueueRegionRefresh();

    private void QueueRegionRefresh(bool forceZOrder = false)
    {
        if (!_window.IsVisible || _window.Dispatcher.HasShutdownStarted) return;
        _zOrderDirty |= forceZOrder;
        if (_refreshQueued) return;
        _refreshQueued = true;
        _window.Dispatcher.BeginInvoke(() =>
        {
            _refreshQueued = false;
            bool reorder = _zOrderDirty;
            _zOrderDirty = false;
            if (reorder) _host?.SyncWindow(_window.IsVisible, forceZOrder: true);
            RefreshRegion();
        }, DispatcherPriority.Loaded);
    }

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!_window.IsVisible)
        {
            _host?.SyncWindow(false);
            return;
        }
        RefreshMaterial();
        if (!_window.Dispatcher.HasShutdownStarted)
            _window.Dispatcher.BeginInvoke(() =>
            {
                _host?.SyncWindow(_window.IsVisible, forceZOrder: true);
                RefreshRegion();
            }, DispatcherPriority.Loaded);
    }

    private void OnActivated(object? sender, EventArgs e)
    {
        _host?.SyncWindow(_window.IsVisible, forceZOrder: true);
    }

    private IntPtr OnWindowMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message is 0x0047 or 0x02E0 or 0x007E)
            QueueRegionRefresh(forceZOrder: message == 0x0047);
        return IntPtr.Zero;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (!_window.Dispatcher.HasShutdownStarted)
            _window.Dispatcher.BeginInvoke(RefreshMaterial);
    }

    private void OnSystemParameterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(SystemParameters.HighContrast) or null) ||
            _window.Dispatcher.HasShutdownStarted) return;
        if (_window.Dispatcher.CheckAccess()) RefreshMaterial();
        else _window.Dispatcher.BeginInvoke(RefreshMaterial);
    }

    private static bool SystemTransparencyEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("EnableTransparency") is not int value || value != 0;
        }
        catch { return true; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_attached) return;
        _root.SizeChanged -= OnPartSizeChanged;
        foreach (var part in _parts)
        {
            part.Element.SizeChanged -= OnPartSizeChanged;
            part.Element.IsVisibleChanged -= OnPartVisibleChanged;
        }
        _window.IsVisibleChanged -= OnVisibleChanged;
        _window.Activated -= OnActivated;
        _source?.RemoveHook(OnWindowMessage);
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;
        _host?.Dispose();
        _host = null;
        AcrylicStateChanged = null;
        _source = null;
        _attached = false;
    }
}
