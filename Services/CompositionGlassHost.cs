using System.ComponentModel;
using System.Numerics;
using System.Runtime.InteropServices;
using WinRT;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;

namespace LumaLauncher.Services;

internal readonly record struct GlassShape(int Left, int Top, int Width, int Height, int Radius);

/// <summary>
/// An input-transparent native window immediately behind the layered WPF window.
/// Its visual layer can sample the desktop, while WPF keeps its own alpha and input.
/// </summary>
internal sealed class CompositionGlassHost : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct QueueOptions
    {
        internal int Size;
        internal int ThreadType;
        internal int ApartmentType;
    }

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(QueueOptions options, out IntPtr controller);

    [ComImport]
    [Guid("29E691FA-4567-4DCA-B319-D0F207EB6807")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICompositorDesktopInterop
    {
        void CreateDesktopWindowTarget(IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool topmost, out IntPtr target);
    }

    [ThreadStatic] private static IntPtr s_dispatcherQueue;

    private readonly IntPtr _front;
    private readonly Compositor _compositor;
    private readonly DesktopWindowTarget _target;
    private readonly ContainerVisual _root;
    private readonly CompositionBackdropBrush _brush;
    private readonly SpriteVisual[] _sprites;
    private readonly CompositionRoundedRectangleGeometry[] _geometries;
    private readonly CompositionGeometricClip[] _clips;
    private IntPtr _rawTarget;
    private GlassNative.Rect _lastBounds;
    private bool _visible;
    private bool _disposed;

    internal IntPtr Handle { get; }
    internal int LastHostBackdropHResult { get; }

    internal CompositionGlassHost(IntPtr front, int partCount)
    {
        _front = front;
        AcquireQueue();
        try
        {
            Handle = GlassNative.CreateWindowEx(
                GlassNative.WsExNoRedirectionBitmap | GlassNative.WsExNoActivate |
                GlassNative.WsExToolWindow | GlassNative.WsExTransparent | GlassNative.WsExTopmost,
                "STATIC", "Luma glass backdrop", GlassNative.WsPopup,
                0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (Handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            int enabled = 1;
            LastHostBackdropHResult = GlassNative.DwmSetWindowAttribute(
                Handle, GlassNative.DwmUseHostBackdropBrush, ref enabled, sizeof(int));
            Marshal.ThrowExceptionForHR(LastHostBackdropHResult);

            _compositor = new Compositor();
            _compositor.As<ICompositorDesktopInterop>().CreateDesktopWindowTarget(Handle, false, out _rawTarget);
            _target = MarshalInterface<DesktopWindowTarget>.FromAbi(_rawTarget);
            _root = _compositor.CreateContainerVisual();
            _brush = _compositor.CreateHostBackdropBrush();
            _sprites = new SpriteVisual[partCount];
            _geometries = new CompositionRoundedRectangleGeometry[partCount];
            _clips = new CompositionGeometricClip[partCount];
            for (int index = 0; index < partCount; index++)
            {
                var sprite = _compositor.CreateSpriteVisual();
                var geometry = _compositor.CreateRoundedRectangleGeometry();
                var clip = _compositor.CreateGeometricClip(geometry);
                sprite.Brush = _brush;
                sprite.Clip = clip;
                sprite.IsVisible = false;
                _root.Children.InsertAtTop(sprite);
                _sprites[index] = sprite;
                _geometries[index] = geometry;
                _clips[index] = clip;
            }
            _target.Root = _root;
        }
        catch
        {
            if (_sprites is not null) foreach (var sprite in _sprites) sprite?.Dispose();
            if (_clips is not null) foreach (var clip in _clips) clip?.Dispose();
            if (_geometries is not null) foreach (var geometry in _geometries) geometry?.Dispose();
            _brush?.Dispose();
            _root?.Dispose();
            _target?.Dispose();
            _compositor?.Dispose();
            if (Handle != IntPtr.Zero) GlassNative.DestroyWindow(Handle);
            if (_rawTarget != IntPtr.Zero) Marshal.Release(_rawTarget);
            throw;
        }
    }

    internal void SetShapes(IReadOnlyList<GlassShape?> shapes)
    {
        if (_disposed) return;
        for (int index = 0; index < _sprites.Length; index++)
        {
            var shape = shapes[index];
            var sprite = _sprites[index];
            if (shape is null)
            {
                sprite.IsVisible = false;
                continue;
            }
            var bounds = shape.Value;
            var size = new Vector2(bounds.Width, bounds.Height);
            sprite.Offset = new Vector3(bounds.Left, bounds.Top, 0);
            sprite.Size = size;
            _geometries[index].Size = size;
            _geometries[index].CornerRadius = new Vector2(
                Math.Min(bounds.Radius, bounds.Width / 2f),
                Math.Min(bounds.Radius, bounds.Height / 2f));
            sprite.IsVisible = true;
        }
    }

    internal void SyncWindow(bool visible, bool forceZOrder = false)
    {
        if (_disposed) return;
        if (!visible)
        {
            if (_visible) GlassNative.ShowWindow(Handle, GlassNative.SwHide);
            _visible = false;
            return;
        }
        if (!GlassNative.GetWindowRect(_front, out var bounds)) return;
        if (bounds.Width < 1 || bounds.Height < 1) return;
        if (!_visible || forceZOrder || !_lastBounds.Equals(bounds))
        {
            GlassNative.SetWindowPos(Handle, _front, bounds.Left, bounds.Top,
                bounds.Width, bounds.Height, GlassNative.SwpNoActivate | GlassNative.SwpShowWindow);
            _visible = true;
            _lastBounds = bounds;
        }
    }

    internal void SetDarkMode(bool dark)
    {
        int value = dark ? 1 : 0;
        GlassNative.DwmSetWindowAttribute(Handle, GlassNative.DwmUseImmersiveDarkMode, ref value, sizeof(int));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _target.Root = null;
        foreach (var sprite in _sprites) sprite.Dispose();
        foreach (var clip in _clips) clip.Dispose();
        foreach (var geometry in _geometries) geometry.Dispose();
        _brush.Dispose();
        _root.Dispose();
        _target.Dispose();
        _compositor.Dispose();
        GlassNative.DestroyWindow(Handle);
        if (_rawTarget != IntPtr.Zero) Marshal.Release(_rawTarget);
    }

    private static void AcquireQueue()
    {
        if (Windows.System.DispatcherQueue.GetForCurrentThread() is not null) return;
        int result = CreateDispatcherQueueController(new QueueOptions
        { Size = Marshal.SizeOf<QueueOptions>(), ThreadType = 2, ApartmentType = 2 }, out s_dispatcherQueue);
        Marshal.ThrowExceptionForHR(result);
        System.Windows.Threading.Dispatcher.CurrentDispatcher.ShutdownFinished += (_, _) =>
        {
            if (s_dispatcherQueue == IntPtr.Zero) return;
            Marshal.Release(s_dispatcherQueue);
            s_dispatcherQueue = IntPtr.Zero;
        };
    }
}
