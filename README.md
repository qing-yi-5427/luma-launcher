# Luma Launcher

Luma 是 Windows 应用与文件启动器，也支持计算、网页搜索和常用命令。0.6.0 采用独立搜索胶囊、结果面板和侧栏式设置页。

## 下载与升级

**最新正式版：[GitHub Releases 下载 Luma.exe](https://github.com/qing-yi-5427/luma-launcher/releases/latest)**。适用于 Windows 10/11 x64；发布的单文件 EXE 已包含 .NET 运行时，无需安装程序。

1. 下载发布页中的 `Luma.exe`，放到固定目录，双击运行。可用同页的 `Luma.exe.sha256` 核对文件。
2. 升级时先从旧版托盘图标选择“退出”，再用新版 `Luma.exe` 替换原位置的文件并启动。不要在旧进程仍运行时覆盖。
3. 通常设置、收藏、历史和缓存在 `%LOCALAPPDATA%\LumaLauncher`，替换 EXE 不会删除这些数据。若启用便携模式（EXE 同目录有 `portable.txt` 或使用 `--portable`），数据改存于同目录的 `LumaData`；升级时请一并保留该目录。

旧配置会继续使用原主题。例如旧版的 `Light` 对应“素笺”，不会自动改成 Apple 风格。想使用新浅色外观，请在设置中选“Apple · 浅色”；想随系统切换，请选“自动（随系统日夜）”，并将日间/夜间配对设为 Apple 浅色/深色。

Luma 本体尚未进行 Authenticode 签名，Windows SmartScreen 可能提示确认。请从上述发布页下载并核对哈希。

## 主要功能

- 应用、文件和文件夹统一搜索；支持模糊匹配、拼音首字母、排序、筛选和展开结果。
- 计算、网页搜索、自定义命令、收藏、历史、文件操作及窗口切换。
- 可选连接普通版 Everything 以搜索索引文件；不连接时仍可搜索应用和使用内建工具。Everything Lite 不提供所需 IPC。
- 在标准打开/保存对话框中，可用 Quick Switch 快速切换到搜索结果文件夹。

搜索排序、高亮、筛选范围和自定义格式详见 [搜索与快捷操作](docs/SEARCH_BEHAVIOR.md)。

## 快捷键

| 操作 | 默认按键 |
|---|---|
| 显示或隐藏 | `Alt+Space`（可在设置中更改；若被占用，以界面显示的实际组合键为准） |
| 选择并打开 | `↑` / `↓`、`Enter` |
| 打开设置 | `Ctrl+,` |
| 隐藏窗口 | `Esc`（展开视图中先返回） |
| 在资源管理器中显示 | `Ctrl+Enter` |
| 切换打开/保存对话框的文件夹 | `Ctrl+G` |

隐藏后 Luma 仍在托盘运行；彻底关闭请用托盘菜单的“退出”。

## 玻璃效果

Windows 11 22H2（build 22621）及以上、系统透明效果开启且高对比模式关闭时，Luma 支持圆角玻璃背景。Windows 10、关闭透明效果或开启高对比模式时使用实色表面。技术说明见 [Windows 玻璃实现](docs/WINDOWS_GLASS.md)。

## 开发与文档

需要 .NET 10 SDK；仓库内的 Everything SDK DLL 会随构建复制。发布单文件：

```powershell
dotnet publish Launcher.csproj -p:PublishProfile=SingleFile
```

产物位于 `publish/win-x64/Luma.exe`。运行隔离测试：

```powershell
dotnet run --project Tests/Luma.SmokeTests.csproj -c Release
```

其他资料：[版本记录](CHANGELOG.md) · [独立交互预览](APPLE_DESIGN_PREVIEW.md) · [性能数据](docs/PERFORMANCE.md) · [验收记录](docs/ACCEPTANCE.md) · [发布流程](docs/AUTO_RELEASE.md)。
