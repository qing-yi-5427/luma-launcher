using System.Diagnostics;
using System.Windows;
using LumaLauncher.Models;

namespace LumaLauncher.Services;

public static class ResultExecutionService
{
    public static bool Open(LauncherResult result, bool runAsAdministrator = false)
    {
        try
        {
            if (result.Kind == LauncherResultKind.Calculation)
            {
                Clipboard.SetText(string.IsNullOrEmpty(result.CopyText) ? result.Target : result.CopyText);
                return true;
            }

            var info = new ProcessStartInfo(result.Target)
            {
                UseShellExecute = true,
                Arguments = result.Arguments
            };
            if (!string.IsNullOrWhiteSpace(result.WorkingDirectory) && Directory.Exists(result.WorkingDirectory))
                info.WorkingDirectory = result.WorkingDirectory;
            if (runAsAdministrator)
                info.Verb = "runas";
            Process.Start(info);
            return true;
        }
        catch (System.ComponentModel.Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            return false;
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(exception.Message, "Luma", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    public static void Reveal(LauncherResult result)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{result.Target}\"") { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(exception.Message, "Luma", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public static void CopyPath(LauncherResult result)
    {
        try { Clipboard.SetText(string.IsNullOrEmpty(result.CopyText) ? result.Target : result.CopyText); } catch { }
    }

    public static void CopyParent(LauncherResult result)
    {
        var path = result.Kind == LauncherResultKind.Folder ? result.Target : Path.GetDirectoryName(result.Target);
        if (string.IsNullOrWhiteSpace(path))
            return;
        try { Clipboard.SetText(path); } catch { }
    }

    public static void OpenWith(LauncherResult result)
    {
        if (result.Kind != LauncherResultKind.File)
            return;
        TryStart(new ProcessStartInfo("rundll32.exe", $"shell32.dll,OpenAs_RunDLL \"{result.Target}\"")
        {
            UseShellExecute = true
        }, "无法打开“打开方式”窗口");
    }

    public static void ShowProperties(LauncherResult result)
    {
        if (!result.IsFileSystemItem)
            return;
        TryStart(new ProcessStartInfo(result.Target) { UseShellExecute = true, Verb = "properties" }, "无法打开属性窗口");
    }

    public static void OpenTerminal(LauncherResult result)
    {
        var directory = result.Kind == LauncherResultKind.Folder ? result.Target : Path.GetDirectoryName(result.Target);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return;
        try
        {
            var info = new ProcessStartInfo("wt.exe") { UseShellExecute = true };
            info.ArgumentList.Add("-d");
            info.ArgumentList.Add(directory);
            Process.Start(info);
        }
        catch
        {
            var info = new ProcessStartInfo("powershell.exe") { UseShellExecute = true };
            info.ArgumentList.Add("-NoExit");
            info.ArgumentList.Add("-Command");
            info.ArgumentList.Add("Set-Location -LiteralPath $args[0]");
            info.ArgumentList.Add(directory);
            TryStart(info, "无法打开终端");
        }
    }

    private static void TryStart(ProcessStartInfo info, string title)
    {
        try { Process.Start(info); }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
