using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using LumaLauncher.Services;

namespace LumaLauncher;

internal sealed class TrayIconService : IDisposable
{
    private const uint IconId = 1;
    private const int CallbackMessage = 0x8000 + 0x155;
    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint NimSetVersion = 0x00000004;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NifInfo = 0x00000010;
    private const uint NotifyIconVersion4 = 4;
    private const int WmLeftButtonUp = 0x0202;
    private const int WmRightButtonUp = 0x0205;
    private const int WmContextMenu = 0x007B;
    private const uint NiifWarning = 0x00000002;

    private readonly IntPtr _window;
    private readonly Action _toggleWindow;
    private readonly Action _openSettings;
    private readonly Func<Task> _reloadApps;
    private readonly Action _exit;
    private readonly int _taskbarCreatedMessage;
    private ContextMenu? _menu;
    private MenuItem? _hotkeyItem;
    private string _activeHotkey;
    private IntPtr _iconHandle;
    private bool _added;

    internal TrayIconService(IntPtr window, Action toggleWindow, Action openSettings, Func<Task> reloadApps, Action exit, string activeHotkey)
    {
        _window = window;
        _toggleWindow = toggleWindow;
        _openSettings = openSettings;
        _reloadApps = reloadApps;
        _exit = exit;
        _activeHotkey = activeHotkey;
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

        _iconHandle = ExtractApplicationIcon();
        AddIcon();
    }

    internal bool HandleMessage(int message, IntPtr wParam, IntPtr lParam)
    {
        if (message == _taskbarCreatedMessage)
        {
            AddIcon();
            return true;
        }

        if (message != CallbackMessage)
            return false;

        var notification = unchecked((ushort)lParam.ToInt64());
        if (notification == WmLeftButtonUp)
            _toggleWindow();
        else if (notification is WmRightButtonUp or WmContextMenu)
            ShowMenu();
        return true;
    }

    internal void UpdateHotkey(string activeHotkey)
    {
        _activeHotkey = activeHotkey;
        if (_hotkeyItem is not null)
            _hotkeyItem.Header = $"快捷键  {activeHotkey}";
    }

    internal void ShowHotkeyFallback(string requested, string active)
    {
        var data = CreateData(NifInfo);
        data.InfoTitle = "Luma 快捷键已回退";
        data.Info = $"{requested} 已被占用，当前使用 {active}。";
        data.InfoFlags = NiifWarning;
        ShellNotifyIcon(NimModify, ref data);
    }

    private void AddIcon()
    {
        var data = CreateData(NifMessage | NifIcon | NifTip);
        if (!ShellNotifyIcon(NimAdd, ref data))
        {
            DiagnosticsService.Log("tray", "Shell_NotifyIcon(NIM_ADD) failed.");
            return;
        }

        _added = true;
        data.TimeoutOrVersion = NotifyIconVersion4;
        ShellNotifyIcon(NimSetVersion, ref data);
    }

    private void ShowMenu()
    {
        // Must not open the WPF Popup synchronously inside the Shell_NotifyIcon callback
        // (WndProc): an exception there strands the native callback and wedges the UI thread.
        // Defer to idle so the menu opens on a clean dispatcher stack.
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        dispatcher.BeginInvoke(() =>
        {
            SetForegroundWindow(_window);
            (_menu ??= CreateMenu()).IsOpen = true;
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private ContextMenu CreateMenu() => BuildMenu(
        _toggleWindow, _openSettings, _reloadApps, _exit, _activeHotkey,
        out _hotkeyItem);

    /// <summary>构建托盘右键菜单。测试直接驱动此方法：验证 Separator 与 MenuItem 均能完成容器生成/布局而不抛异常。</summary>
    internal static ContextMenu BuildMenu(Action toggleWindow, Action openSettings, Func<Task> reloadApps, Action exit,
        string activeHotkey, out MenuItem hotkeyItem)
    {
        var resources = System.Windows.Application.Current;
        var menu = new ContextMenu
        {
            Placement = PlacementMode.MousePoint,
            Background = (System.Windows.Media.Brush)resources.FindResource("PanelBrush"),
            Foreground = (System.Windows.Media.Brush)resources.FindResource("TextBrush"),
            BorderBrush = (System.Windows.Media.Brush)resources.FindResource("StrokeBrush"),
            BorderThickness = new System.Windows.Thickness(1),
            Padding = new System.Windows.Thickness(4)
        };
        menu.Items.Add(CreateItem("显示 Luma", toggleWindow));
        hotkeyItem = new MenuItem { Header = $"快捷键  {activeHotkey}", IsEnabled = false, Style = MenuStyle() };
        menu.Items.Add(hotkeyItem);
        menu.Items.Add(CreateSeparator(resources));
        menu.Items.Add(CreateItem("设置", openSettings));
        menu.Items.Add(CreateAsyncItem("重建应用索引", reloadApps));
        menu.Items.Add(CreateSeparator(resources));
        menu.Items.Add(CreateItem("退出", exit));
        return menu;
    }

    private static Separator CreateSeparator(System.Windows.Application resources) => new()
    {
        Style = (System.Windows.Style)resources.FindResource("LumaMenuSeparator")
    };

    private NotifyIconData CreateData(uint flags) => new()
    {
        Size = Marshal.SizeOf<NotifyIconData>(),
        Window = _window,
        Id = IconId,
        Flags = flags,
        CallbackMessage = CallbackMessage,
        Icon = _iconHandle,
        Tip = "Luma Launcher",
        Info = string.Empty,
        InfoTitle = string.Empty
    };

    private static MenuItem CreateItem(string title, Action action)
    {
        var item = new MenuItem { Header = title, Style = MenuStyle() };
        item.Click += (_, _) => action();
        return item;
    }

    private static MenuItem CreateAsyncItem(string title, Func<Task> action)
    {
        var item = new MenuItem { Header = title, Style = MenuStyle() };
        item.Click += async (_, _) =>
        {
            try { await action(); }
            catch (Exception exception) { DiagnosticsService.Log("tray-action", exception); }
        };
        return item;
    }

    private static System.Windows.Style MenuStyle() =>
        (System.Windows.Style)System.Windows.Application.Current.FindResource("LumaMenuItem");

    private static IntPtr ExtractApplicationIcon()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
            return IntPtr.Zero;
        ExtractIconEx(executable, 0, out var large, out var small, 1);
        if (small != IntPtr.Zero)
        {
            if (large != IntPtr.Zero)
                NativeMethods.DestroyIcon(large);
            return small;
        }
        return large;
    }

    public void Dispose()
    {
        if (_menu is not null)
            _menu.IsOpen = false;
        if (_added)
        {
            var data = CreateData(0);
            ShellNotifyIcon(NimDelete, ref data);
            _added = false;
        }
        if (_iconHandle != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_iconHandle);
            _iconHandle = IntPtr.Zero;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "ExtractIconExW")]
    private static extern uint ExtractIconEx(string file, int index, out IntPtr largeIcon, out IntPtr smallIcon, uint icons);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        internal int Size;
        internal IntPtr Window;
        internal uint Id;
        internal uint Flags;
        internal int CallbackMessage;
        internal IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Tip;
        internal uint State;
        internal uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string Info;
        internal uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] internal string InfoTitle;
        internal uint InfoFlags;
        internal Guid ItemGuid;
        internal IntPtr BalloonIcon;
    }
}
