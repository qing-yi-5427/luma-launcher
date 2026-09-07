using System.Diagnostics;
using System.Windows;
using LumaLauncher.Models;

namespace LumaLauncher.Services;

public sealed class SystemCommandsService
{
    private sealed record SystemCommand(string Keyword, string Title, string Subtitle, Action Action, bool Dangerous);

    private readonly SystemCommand[] _commands;

    public SystemCommandsService()
    {
        _commands =
        [
            new("lock", "锁定电脑", "立即锁定当前用户会话", () => Run("rundll32.exe", "user32.dll,LockWorkStation"), false),
            new("sleep", "睡眠", "将电脑置于睡眠状态", () => SetSuspendState(false), false),
            new("hibernate", "休眠", "将电脑置于休眠状态", () => SetSuspendState(true), false),
            new("shutdown", "关机", "关闭这台电脑", () => Run("shutdown.exe", "/s /t 0"), true),
            new("restart", "重启", "重新启动这台电脑", () => Run("shutdown.exe", "/r /t 0"), true),
            new("signout", "注销", "结束当前用户会话", () => Run("shutdown.exe", "/l"), true),
            new("recycle", "打开回收站", "打开回收站文件夹", () => Run("explorer.exe", "shell:RecycleBinFolder"), false),
            new("empty-recycle", "清空回收站", "清空回收站中的所有项目", EmptyRecycleBin, true),
            new("settings", "打开 Windows 设置", "ms-settings:", () => Run("ms-settings:"), false),
            new("display", "显示设置", "打开显示设置", () => Run("ms-settings:display"), false),
            new("bluetooth", "蓝牙设置", "打开蓝牙设置", () => Run("ms-settings:bluetooth"), false),
            new("network", "网络设置", "打开网络设置", () => Run("ms-settings:network-status"), false),
            new("apps", "已安装应用", "打开已安装应用列表", () => Run("ms-settings:appsfeatures"), false),
            new("taskmgr", "任务管理器", "打开任务管理器", () => Run("taskmgr.exe"), false),
            new("control", "控制面板", "打开控制面板", () => Run("control.exe"), false),
            new("explorer", "文件资源管理器", "打开用户文件夹", () => Run("explorer.exe"), false)
        ];
    }

    public IReadOnlyList<LauncherResult> Search(string query, int limit)
    {
        var prepared = FuzzyMatcher.Prepare(query);
        var matches = new List<LauncherResult>();
        foreach (var command in _commands)
        {
            var score = FuzzyMatcher.Score(prepared,
                FuzzyMatcher.PrepareCandidate($"{command.Keyword} {command.Title}"),
                FuzzyMatcher.PrepareCandidate(command.Subtitle));
            if (double.IsNegativeInfinity(score))
                continue;
            matches.Add(new LauncherResult
            {
                Title = command.Title,
                Subtitle = command.Dangerous ? command.Subtitle + " · 需确认" : command.Subtitle,
                Target = "system:" + command.Keyword,
                Kind = LauncherResultKind.System,
                Score = score + 400,
                CopyText = "system:" + command.Keyword
            });
        }

        return matches
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Title.Length)
            .Take(limit)
            .ToList();
    }

    public static bool Execute(LauncherResult result)
    {
        if (result.Kind != LauncherResultKind.System || !result.Target.StartsWith("system:", StringComparison.OrdinalIgnoreCase))
            return false;
        var keyword = result.Target["system:".Length..];
        var catalog = new SystemCommandsService();
        var command = catalog._commands.FirstOrDefault(c => c.Keyword.Equals(keyword, StringComparison.OrdinalIgnoreCase));
        if (command is null)
            return false;
        if (command.Dangerous)
        {
            var answer = MessageBox.Show(
                $"{command.Title}？\n\n{command.Subtitle}",
                "Luma · 系统命令",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.OK)
                return false;
        }
        command.Action();
        return true;
    }

    private static void Run(string fileName, string arguments = "")
    {
        try
        {
            Process.Start(new ProcessStartInfo(fileName)
            {
                UseShellExecute = true,
                Arguments = arguments
            });
        }
        catch (Exception exception)
        {
            DiagnosticsService.Log("system-command", exception);
            MessageBox.Show(exception.Message, "Luma", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static void SetSuspendState(bool hibernate)
    {
        SetSuspendStateInternal(false, hibernate, true);
    }

    private static void EmptyRecycleBin()
    {
        // SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND
        SHEmptyRecycleBin(IntPtr.Zero, null!, 0x1 | 0x2 | 0x4);
    }

    [System.Runtime.InteropServices.DllImport("powrprof.dll")]
    private static extern bool SetSuspendStateInternal(bool hibernate, bool forceCritical, bool disableWakeEvent);

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? rootPath, uint flags);
}
