# Luma 启动器调研与优化取舍

调研时间：2026-08-31，输入调度与排序补充于 2026-09-03。目标不是复制功能，而是提炼对“稳定、美观、低占用”有直接帮助的做法。

## 参考项目

### Flow Launcher

- 仓库：[Flow-Launcher/Flow.Launcher](https://github.com/Flow-Launcher/Flow.Launcher)，审阅提交 `f245ed60cc2b8abc206e74681aa70973022e530b`。
- `MainViewModel` 用 `CancellationTokenSource` 淘汰旧查询，并通过 `Channel` 在约 20 ms 的窗口内合并结果更新，避免插件每返回一批结果就重绘一次。
- 用户键入时可按搜索源应用 `SearchDelayTime`，延迟任务同样使用当前查询的取消令牌；程序主动改写查询或重新查询时则跳过延迟。
- 最终排序会把用户历史选择次数加入插件匹配分数，并允许固定置顶结果。
- `ResultViewModel` 先查图像缓存，未命中时再异步加载；搜索结果不应被图标 I/O 阻塞。
- 对 Luma 的采用：保留查询代次与取消令牌；使用尾沿防抖，仅在输入停止后查询，按 Enter 时跳过延迟；结果先呈现，再异步补图标；图标缓存设硬上限并在隐藏后收缩。

### Wox

- 仓库：[Wox-launcher/Wox](https://github.com/Wox-launcher/Wox)，审阅提交 `d5ccfb51b940785000c0913988396a842076ddba`。
- 查询结果带 session/query 标识，UI 只接受仍处于活动状态的查询结果，避免并发插件覆盖新输入。
- 项目有独立的图像缓存清理流程，并为冷/热图标转换路径保留基准测试。
- 对 Luma 的采用：沿用 generation 校验，所有异步图标回写再次验证 generation；缓存使用 LRU 淘汰，默认最多 192 项。

### Microsoft PowerToys Run

- 资料：[PowerToys Run 官方文档](https://learn.microsoft.com/windows/powertoys/run)。
- 搜索源模块化、支持直接激活命令；官方特别指出文件缩略图生成可能影响速度和稳定性。
- 官方提供 Input smoothing（等待更多输入再执行搜索）、Results order tuning 和 Selected item weight，说明输入防抖与使用历史加权是成熟启动器的常规能力。
- 对 Luma 的采用：维持应用与 Everything 两个轻量搜索源，不引入常驻插件宿主，不生成缩略图，只读取小尺寸 Shell 图标；提供智能、匹配度、常用/收藏和名称四种排序。

### Ueli

- 仓库：[oliverschwendener/ueli](https://github.com/oliverschwendener/ueli)。
- 项目强调异步执行以避免阻塞 UI，并提供 Everything、应用、网页等大量插件。
- 对 Luma 的取舍：采用异步边界，但不采用 Electron 与大插件面；Luma 继续使用原生 WPF/Win32 和固定的搜索管线，以换取更小的常驻范围与更少故障面。

### Everything

- 资料：[命令行选项](https://www.voidtools.com/support/everything/command_line_options/)、[SDK](https://www.voidtools.com/support/everything/sdk/)、[INI 配置](https://www.voidtools.com/support/everything/ini/)。
- 官方支持 `-startup` 后台启动、`-exit` 退出；`show_tray_icon=0` 可隐藏托盘图标；SDK 要求 Everything 客户端在后台运行。
- 对 Luma 的采用：仅在 IPC 不可用时启动 Everything；启动前原子写入 `show_tray_icon=0` 与 `run_in_background=1`；退出时等待活动查询结束后调用 SDK 退出。

## 最终架构原则

1. 键入路径只做必要工作：预归一化应用索引、250 ms 尾沿防抖、旧查询取消和代次校验；防抖期间保留但禁用旧结果，按 Enter 可立即提交查询。
2. 首屏结果不等待图标；完整页把候选上限扩到 512，并通过 WPF 回收虚拟化只为可见条目异步加载图标，图标缓存仍保留硬上限。
3. 本地数据采用临时文件、落盘刷新和原子替换，损坏文件保留副本。
4. 托盘与多显示器定位使用 Win32，移除仅为托盘引入的 Windows Forms 运行时。
5. 空闲维护任务去重，窗口重新显示时立即取消，避免重复裁剪造成抖动。
6. 保持单一 WPF 进程与两个内建搜索源，不引入插件宿主、浏览器运行时或额外索引服务。
