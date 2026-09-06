using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LumaLauncher;
using LumaLauncher.Models;
using LumaLauncher.Services;

namespace LumaLauncher.Tests;

/// <summary>
/// 回归测试：托盘右键菜单曾因 ItemContainerStyle 指向 MenuItem 样式而包含 Separator 时，
/// 在 Shell_NotifyIcon 回调里抛 InvalidOperationException，导致右键托盘图标后 UI 线程卡死。
/// 此处在 STA 线程上驱动真实菜单构建 + 容器生成 + 布局，旧实现必红。
/// </summary>
internal static class TrayMenuTests
{
    internal static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { Verify(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw failure;
    }

    private static void Verify()
    {
        // App.xaml 的资源（含 LumaMenuItem / LumaMenuSeparator）通过生成的 InitializeComponent 加载。
        var app = new App();
        app.InitializeComponent();
        if (Application.Current is null)
            throw new InvalidOperationException("WPF Application.Current 未初始化");

        var menu = TrayIconService.BuildMenu(
            () => { }, () => { }, () => System.Threading.Tasks.Task.CompletedTask, () => { },
            "Alt+Space", out _);

        var separators = 0;
        var items = 0;
        foreach (object entry in menu.Items)
        {
            if (entry is Separator) separators++;
            if (entry is MenuItem) items++;
        }
        if (separators < 2 || items < 5)
            throw new InvalidOperationException($"托盘菜单结构异常：{items} 项 + {separators} 分隔线");

        // 触发原崩溃路径：容器生成 + 逐项 Measure（Separator 套 MenuItem 样式时在此抛异常）。
        // ContextMenu 不能直接做 Window 子级，先挂到占位控件上再打开。
        var host = new Window
        {
            Content = new Border(),
            Width = 20, Height = 20, ShowInTaskbar = false, WindowStyle = WindowStyle.None
        };
        // Do not show a native window or popup: tests must not steal foreground focus.
        host.ShowActivated = false;
        try
        {
            menu.PlacementTarget = host;
            menu.ApplyTemplate();
            menu.Measure(new Size(240, double.PositiveInfinity));
            menu.Arrange(new Rect(new Point(), menu.DesiredSize));
            menu.UpdateLayout();
            foreach (object entry in menu.Items)
            {
                var container = menu.ItemContainerGenerator.ContainerFromItem(entry)
                    ?? throw new InvalidOperationException($"菜单项 '{entry}' 未生成容器");
                if (container is not UIElement element)
                    throw new InvalidOperationException($"菜单项 '{entry}' 容器类型异常：{container.GetType()}");
                element.Measure(new Size(240, double.PositiveInfinity));
                element.UpdateLayout();
            }
        }
        finally
        {
            menu.IsOpen = false;
            host.Close();
        }

        if (!double.IsFinite(menu.DesiredSize.Height) || menu.DesiredSize.Height <= 0)
            throw new InvalidOperationException($"托盘菜单测量尺寸异常：{menu.DesiredSize}");

        var store = new SettingsStore();
        var launcher = new MainWindow(store, true);
        var settings = new SettingsWindow(store.Current.Copy());
        launcher.ResultSortChanged += settings.SyncResultSort;
        foreach (var (mode, label) in ResultRanker.Options)
        {
            var sortMenu = launcher.CreateSortMenu();
            var item = sortMenu.Items.Cast<MenuItem>().Single(item => Equals(item.Tag, mode));
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            if (store.Current.ResultSort != mode || !((Button)launcher.FindName("SortButton")).Content.ToString()!.Contains(label) ||
                !Equals(((ComboBox)settings.FindName("ResultSortBox")).SelectedValue, mode))
                throw new InvalidOperationException("Sort menu click/persistence/settings sync failed: " + mode);
            if (launcher.CreateSortMenu().Items.Cast<MenuItem>().Count(item => item.IsChecked) != 1)
                throw new InvalidOperationException("Sort check state is not exclusive");
        }
        var changed = store.Current.Copy(); changed.ResultSort = ResultRanker.Name;
        store.Save(changed); launcher.ApplySettings();
        if (!Equals(((Button)launcher.FindName("SortButton")).Tag, ResultRanker.Name))
            throw new InvalidOperationException("Settings-to-search sort sync failed");
        settings.Close(); launcher.CloseForExit();
        VerifyFullResultsLayout();
    }

    private static void VerifyFullResultsLayout()
    {
        var launcher = new MainWindow(new SettingsStore());
        try
        {
            var enter = typeof(MainWindow).GetMethod("EnterFullResultsMode",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("找不到完整结果模式入口");
            var leave = typeof(MainWindow).GetMethod("LeaveFullResultsMode",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("找不到完整结果模式出口");
            var apply = typeof(MainWindow).GetMethod("ApplyCurrentPage",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("找不到结果刷新入口");
            var allResultsField = typeof(MainWindow).GetField("_allResults",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("找不到完整结果集合");
            var details = launcher.FindName("DetailsPane") as FrameworkElement
                ?? throw new InvalidOperationException("找不到结果详情面板");
            var more = launcher.FindName("MoreButton") as Button
                ?? throw new InvalidOperationException("找不到完整结果按钮");
            var results = launcher.FindName("ResultsList") as ListBox
                ?? throw new InvalidOperationException("找不到结果列表");

            var allResults = allResultsField.GetValue(launcher) as List<LauncherResult>
                ?? throw new InvalidOperationException("完整结果集合类型异常");
            for (var index = 0; index < 600; index++)
            {
                allResults.Add(new LauncherResult
                {
                    Title = $"测试结果 {index + 1}",
                    Subtitle = "布局回归测试",
                    Target = $@"C:\Test\item-{index + 1}.txt",
                    Kind = LauncherResultKind.File,
                    Score = 100 - index
                });
            }
            apply.Invoke(launcher, ["没有结果"]);

            results.SelectedIndex = 1;
            var selectedTarget = ((LauncherResult)results.SelectedItem).Target;
            var applyBatch = typeof(MainWindow).GetMethod("ApplyBatch",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            applyBatch.Invoke(launcher, [new SearchBatch(allResults.ToArray(), "final", true), "empty", true]);
            if ((results.SelectedItem as LauncherResult)?.Target != selectedTarget)
                throw new InvalidOperationException("渐进搜索的最终结果重置了用户选择");

            enter.Invoke(launcher, null);
            launcher.Measure(new Size(1040, 680));
            launcher.Arrange(new Rect(0, 0, 1040, 680));
            launcher.UpdateLayout();
            if (details.Visibility != Visibility.Visible || !Equals(more.Content, "收起") || results.Items.Count != 600)
                throw new InvalidOperationException("完整结果模式未正确展开");
            if (!VirtualizingPanel.GetIsVirtualizing(results))
                throw new InvalidOperationException("完整结果列表未启用虚拟化");

            leave.Invoke(launcher, [true]);
            launcher.UpdateLayout();
            if (details.Visibility != Visibility.Collapsed || !Equals(more.Content, "展开结果"))
                throw new InvalidOperationException("完整结果模式未正确收起");
        }
        finally
        {
            launcher.CloseForExit();
        }
    }
}
