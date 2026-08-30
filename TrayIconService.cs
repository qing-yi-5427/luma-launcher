using System.Drawing;
using System.Windows.Forms;

namespace LumaLauncher;

internal sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _hotkeyItem;

    internal TrayIconService(Action toggleWindow, Action openSettings, Func<Task> reloadApps, Action exit, string activeHotkey)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("显示 Luma", null, (_, _) => toggleWindow());
        _hotkeyItem = new ToolStripMenuItem($"快捷键  {activeHotkey}") { Enabled = false };
        menu.Items.Add(_hotkeyItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("设置", null, (_, _) => openSettings());
        menu.Items.Add("重建应用索引", null, async (_, _) => await reloadApps());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => exit());

        _icon = new NotifyIcon
        {
            Text = "Luma Launcher",
            Icon = CreateIcon(),
            ContextMenuStrip = menu,
            Visible = true
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                toggleWindow();
        };
    }

    internal void UpdateHotkey(string activeHotkey) => _hotkeyItem.Text = $"快捷键  {activeHotkey}";

    internal void ShowHotkeyFallback(string requested, string active)
    {
        _icon.BalloonTipTitle = "Luma 快捷键已回退";
        _icon.BalloonTipText = $"{requested} 已被占用，当前使用 {active}。";
        _icon.ShowBalloonTip(3500);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Icon?.Dispose();
        _icon.Dispose();
    }

    private static Icon CreateIcon()
    {
        var resource = System.Windows.Application.GetResourceStream(
            new Uri("pack://application:,,,/Assets/Luma.ico", UriKind.Absolute))
            ?? throw new InvalidOperationException("Luma icon resource is missing.");

        using var stream = resource.Stream;
        using var icon = new Icon(stream);
        return (Icon)icon.Clone();
    }
}
