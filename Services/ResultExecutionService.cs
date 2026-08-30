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
            var info = new ProcessStartInfo(result.Target) { UseShellExecute = true };
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
        try { System.Windows.Clipboard.SetText(result.Target); } catch { }
    }
}
