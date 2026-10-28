# WinDock 中文版

基于 [Breno Alvim 的 WinDock 0.5.0](https://github.com/obrenoalvim/windock) 维护的分支，增加简体中文、Everything 搜索和 PecoFence 收纳管理。当前版本为 `0.6.0-preview.9`。

## 使用

右击 Dock 空白处，选择“Dock 设置”。界面右上角可切换中文、英语和葡萄牙语。新增功能提供中文和英语文字。

### 全局搜索

点击 Dock 的放大镜，或按 `Alt+Space`。文件结果来自本机正在运行的 [Everything](https://www.voidtools.com/)，同时搜索已安装应用。支持 Everything 搜索语法，例如 `ext:pdf 赛车`。按 Enter 打开选中项目，Esc 关闭搜索。

“搜索设置”可更换快捷键和 Everything 实例名。普通安装的实例名留空；命名实例需要填写对应名称。快捷键被其他程序占用时，设置页会提示，Dock 按钮仍可使用。

搜索支持全部、应用、文档、文件夹、图片、视频、音频七种分类。文件分类会直接限制 Everything 的查询，再取前 100 项；PDF、Word、表格、演示文稿、媒体文件和文件夹使用不同图标或标记。应用结果使用应用图标，并只显示可启动的应用和快捷方式。

右上角可将搜索范围设为全部索引、桌面、下载、文档目录或自选文件夹，文件夹范围包含子文件夹。选择“应用”时搜索已安装应用与快捷方式，文件范围选择暂时停用。文件夹必须已经被 Everything 索引，选择范围不会修改 Everything 的索引设置。

搜索通过 Everything 的[本机 IPC 接口](https://www.voidtools.com/support/everything/sdk/ipc/)读取索引，不需要开启 HTTP 服务。Everything 的索引范围和排除项继续生效。Everything 未运行或查询失败时会明确提示，此时只显示应用快捷方式。每次最多显示 100 项，输入更具体的条件可缩小结果范围。

### 桌面收纳

需要先安装并运行 [PecoFence](https://github.com/DayuanJiang/PecoFence)，并保留与程序配套的 `pecofence-cli.exe`。已验证接口版本为 0.2.3。点击 Dock 的收纳按钮，可查看现有分区及分页、新建或重命名分区、归类所选项目、打开分区设置，以及保存和恢复布局。

归类只支持虚拟分区，原文件保留在原文件夹。可将资源管理器中已受 PecoFence 管理的桌面项目拖入所选分区；其他位置的文件不会被移动或导入。文件夹门户不支持在此归类。每次更改归类或分区名称前自动保存布局快照，恢复布局前也会保存当前布局。

连接失败时，点击“选择 PecoFence”定位程序。操作超时后应先刷新并核对状态，避免重复执行已经完成的更改。

左侧选择分区，右侧查看和筛选项目。选中项目后显示归类操作；新建分区在侧栏底部，重命名和分区设置在标题右侧的菜单中。顶部提供保存布局和历史布局入口。

### 界面

搜索和收纳共用简约浮窗、轻量边框和圆角控件。Windows 11 22H2 及以上使用系统 Desktop Acrylic 背景。系统关闭透明效果、高对比度模式或窗口未激活时，Windows 可能使用实色背景；旧版系统保留可读的深色背景。

开始、搜索和收纳入口使用矢量图形。网络、音量、麦克风和电池按钮随 Dock 图标尺寸放大，缩减上下留白。WinDock 自身的菜单使用圆角边框、矢量标记和像素对齐；其他应用弹出的菜单由该应用绘制。

可单独运行 `WinDock.exe --preview-search` 或 `WinDock.exe --preview-organizer` 查看新界面，这两个入口不启动 Dock、登录项或任务栏接管。收纳入口连接实际 PecoFence，执行归类和布局操作会作用于当前分区。

### 预览与动画

预览窗口使用 DWM 实时缩略图，默认尺寸扩大至 320 × 200，并随窗口数量和屏幕空间调整。缩略图按实际屏幕像素定位，跨屏时更新 DPI。

启动图标动效改为 360 毫秒的短抬升和轻微回落，默认保留关闭状态；可在设置中启用。浮窗内容用 160 毫秒进入，Dock 图标分批出现总长不超过 260 毫秒。悬停位置通过按实际帧间隔计算的临界阻尼弹簧平滑跟随，稳定后停止逐帧更新。系统关闭动画时跳过这些动效。

整窗动画预览版已接入 Windows 原生动画时间线：打开和恢复从 Dock 图标展开，最小化收向图标，关闭采用短距离下移、轻微收缩和淡出。默认时长分别为 240、240、320、180 毫秒，四类动作可独立开关和调整。设置还提供运动曲线、关闭幅度与淡出延后程度。

功能默认关闭，在“Dock 设置 → 动画”中点击“启用到本次登录”，通过正常的 Windows 管理员确认后连接。仅适配经完整文件校验的 `udwm.dll 10.0.26100.9949`。本版没有安装提权启动任务，注销或重启后需再次启用。旧截图覆盖层已停止由 Dock 调用，原生组件不可用时使用系统动画。

最大化恢复保留原窗口状态。动画启用时将校验过的组件放入 `%ProgramData%\WinDock.NativeMotion` 专用目录，并保存启用结果；系统权限拦截仍会停止启用。本版移除过严的同线程限制，改为检查原生窗口状态锁，并分别记录跳过原因。设置状态分别说明组件连接与实际处理动作，实际视觉效果尚待确认。

四类普通动作的代码路径已实现，尚未验证实际桌面效果。首次画面、关闭内容、快速反向、圆角阴影和跨屏仍有兼容性风险；组件运行在 DWM 内部，异常可能引起桌面闪黑或会话中断。关闭此选项或退出 WinDock 可停止新的自定义过渡，注销可完全卸载本次加载的组件。适用范围、启用方法及回滚见 [整窗动画说明](docs/native-window-animation.md)。

### 独立托盘

在 Dock 设置中开启“独立托盘”后，可显示后台应用提供给 Windows 的托盘图标、悬停说明及托盘提示。默认使用紧凑图标网格，完整名称与动态状态在悬停时显示。点击和菜单操作交还原应用处理。可选择在主屏左下角常驻；空间不足时收起，避免挤动主 Dock。点击 Dock 的托盘入口不会临时唤出整条 Windows 任务栏。

通过“更多 → 整理图标”调整顺序和选择常驻图标。正常模式保留应用的原生右键菜单；整理模式用于拖动和固定。名称显示、图标大小、网格列数和排序可在 Dock 设置中调整。刷新、最近提示和通知中心收在菜单中，正常连接时不再显示状态说明。

托盘在应用菜单打开期间保留面板与来源图标，刷新延后到操作结束；取消菜单、点击外部、Esc、来源按钮切换和自动隐藏使用统一的关闭逻辑。搜索、收纳与设置中的 Esc 优先处理内层菜单或下拉框。改动范围与兼容限制见[菜单与浮窗交互](docs/popup-interactions.md)。

本版还修正托盘断线后的重连，以及管理员权限运行时 Everything 回复受阻的问题。应用目录加载失败时，文件检索仍可继续；这部分改动尚待实际使用确认。

此功能默认关闭。开启后会把随程序提供的本机组件加载到 Explorer 的托盘线程，关闭功能或退出 WinDock 会断开连接。组件在本机从源码编译，目前没有数字签名；遇到 Windows 或安全软件阻止时，不应绕过检查。该功能与隐藏 Windows 任务栏的设置相互独立。

图标蓝点表示本次连接收到了应用的托盘提示，不代表未读消息数。最近 50 条提示仅保存在内存中；现代 Windows 通知中心仍由系统提供。连接或刷新时会请求运行中的应用重新登记图标，能否补齐仍取决于应用回应；历史图标记录不会被伪造成当前托盘图标。部分原生菜单仍可能出现在 Windows 原任务栏附近。当前只支持 x64，Windows 更新可能影响兼容性。完整行为与限制见 [独立托盘说明](docs/system-tray.md)。

### 外观与应用显示

Dock 设置提供图标大小（24–96）、间距、底板留白、圆角、不透明度和底部距离。运行标记可选择短线、圆点、方点或关闭，并调整大小和当前应用的强调色。消息角标与悬停名称可以分别关闭。

未固定应用可保持顺序、按最近使用或按名称排列，位置可设在固定应用之前或之后。固定应用继续使用手动顺序。

“显示后台运行的应用”默认开启。曾登记托盘、仍在运行且有窗口的程序，即使当前没有可见主窗口，也会保留在运行区；与固定或可见窗口的入口合并。后台应用状态在后台每 5 秒更新一次，点击通过应用自己的启动入口恢复，避免把微信等程序隐藏主窗口误认为已经退出。这个入口提供程序访问，不伪造托盘通知或未读数。

## 构建与检查

需要 Windows、.NET 10 SDK 和 Zig 0.15.2。发布前先编译两个本机组件，再打包整个发布目录。

```powershell
& .\native\WinDock.TrayHook\build.ps1 -Zig 'C:\path\to\zig.exe'
& .\native\WinDock.Motion\build.ps1 -Zig 'C:\path\to\zig.exe'
dotnet publish src/WinDock -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

运行 `publish/WinDock.exe`，保留发布目录中的 DLL、`WinDock.MotionHost.exe`、校验文件和 `licenses` 目录。`--tray` 参数用于 Dock 登录启动，原生动画组件仍需按上述方式启用。默认配置位于 `%APPDATA%\WinDock`，固定应用和环境配置可沿用上游版本。编译成功不代表已经验证桌面交互和视觉效果。

## 恢复上游版本

先关闭整窗动画并从托盘菜单退出 WinDock，确认完整任务栏恢复，再把原版 `WinDock.exe` 放回原位置。启用过原生动画时，注销并重新登录可完全卸载组件。更换程序前备份 `%APPDATA%\WinDock`。PecoFence 布局由 PecoFence 单独管理，可在收纳窗口选择已保存的布局恢复；布局快照不备份文件内容。

## 许可

沿用 [MIT 许可](LICENSE)。原 WinDock 作者为 Breno Alvim。Everything 与 PecoFence 是独立软件，本项目通过它们公开的本机接口连接，未打包这两个软件。

背景实现参考 [WPF UI](https://github.com/lepoco/wpfui/blob/main/src/Wpf.Ui/Controls/Window/WindowBackdrop.cs) 的系统背景设置方式；动效设计参考 [Motion Primitives](https://github.com/ibelick/motion-primitives/blob/main/components/core/dock.tsx) 的弹簧跟随思路。相应许可记录见 `THIRD_PARTY_NOTICES.txt`。

整窗动画的源码对照与适用边界见 [窗口动画参考记录](docs/window-motion-references.txt)。托盘实现对照了 ManagedShell、Cairo、YASB 和 Seelen，出处与许可见 [独立托盘说明](docs/system-tray.md)；YASB 的 MIT 许可随程序提供。

原生动画组件使用 MinHook 1.3.4，保留其完整 [许可文件](third_party/minhook/LICENSE.txt)；原生接口的实现依据见 [组件说明](native/WinDock.Motion/README.md)。
