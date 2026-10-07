# Luma Apple Design 分支预览

双击 `Start-ApplePreview.cmd` 可打开独立交互预览。它使用临时数据目录，不注册全局热键，也不参与正式 Luma 的单实例唤醒；可搜索、打开设置、切换浅色与深色、查看结果详情。按 Esc 隐藏或 Alt+F4 关闭预览后，预览进程退出。预览不会验证系统的 Alt+Space 唤醒行为。

可用 `dotnet run --project Tests/Luma.SmokeTests.csproj -c Release -- --render` 生成不激活窗口的真实 WPF 截图，图片在 `Tests/bin/Release/net10.0-windows/renders`。包括主搜索、详情、设置、排序与托盘菜单，以及小屏 200% 缩放布局。

此分支保留原有搜索、排序、快捷键和设置业务逻辑，增加 AppleLight 与 AppleDark 色板；旧色板依然可在设置中选择。新配置默认随系统切换 Apple 浅色和深色，现有配置中的配色选择继续生效。

窗口仍使用 WPF 的分层透明窗口。半透明表面提供层级，但它不是实时桌面背景模糊。未开启逐帧 BlurEffect。窗口尺寸和出现位移采用可中途改向的临界阻尼弹簧；关闭 Windows 客户区动画时立即到达目标。高对比模式使用系统色。按钮按下立即改变透明度，键盘导航与焦点提示继续保留。
