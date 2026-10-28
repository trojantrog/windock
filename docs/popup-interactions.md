# 菜单与浮窗交互

适用于 WinDock 0.6.0-preview.10。本版修正托盘与菜单之间的关闭、焦点和刷新逻辑；已完成代码审查，实际桌面交互尚未验证。协议兼容范围见随程序提供的《托盘兼容说明》。

| 操作 | 处理方式 |
| --- | --- |
| 右键托盘应用 | 保留托盘面板和来源图标，等待应用自己的菜单结束；菜单移交焦点时不直接关闭面板 |
| 打开 WinDock 自身菜单 | 菜单关联到发起操作的托盘或 Dock；鼠标捕获先释放，再交给菜单 |
| 在更多、整理菜单中操作 | 图标增删、排序和名称更新延后到菜单或拖放结束，保留键盘位置和滚动位置 |
| 操作菜单及子菜单 | 移入子菜单属于同一次操作；点击外部或切换到其他应用才结束相应浮窗 |
| 再点来源按钮 | 同一次点击只关闭已打开的浮窗，不立即重开；之后的新点击正常响应 |
| 按 Esc | 优先交给当前菜单或下拉框，再退出整理、提示列表等模式，最后关闭所在浮窗 |
| 使用搜索、收纳或设置中的下拉框 | Esc 关闭内层选择，不连带关闭主窗口 |
| 使用 Dock 自动隐藏 | 菜单、拖放或相关浮窗正在操作时保留 Dock；最大化窗口不按全屏游戏处理 |
| 面板尺寸、状态或位置更新 | 更新位置时不主动切换前台，避免抢走应用菜单的焦点 |

应用的菜单命令仍由应用处理。取消菜单后的焦点处理需要识别原菜单所有者；选择命令打开其他窗口后，不把焦点拉回托盘。

关闭判断综合使用已打开的 WPF 菜单、系统菜单状态、应用浮窗和点击位置。鼠标观察仅在浮窗或菜单活动期间运行，始终传递原始输入，不保存点击或键盘记录。自绘菜单、特殊窗口样式及不同权限级别仍有兼容性差异，不能仅凭编译完成断定所有应用的菜单行为一致。

## 托盘连接与搜索

本版修正托盘断线后无法重连、短暂写入等待导致断线，以及重连后没有立即获取图标的问题。新版托盘组件使用第三版连接协议，补充图标焦点、有效字段和丢失更新的恢复处理，并与本次登录内可能仍驻留的旧组件隔离。升级时应退出旧 WinDock，并从完整的新版本目录启动，不能只复制主程序。

WinDock 以管理员权限运行时，托盘数据连接也可接受同一登录会话中的普通权限 Explorer。权限兼容仅限这条本机临时连接，仍核对 Explorer 进程身份并拒绝远程连接，不修改系统权限策略。

Everything 搜索修正了 WinDock 以管理员权限运行、Everything 以普通权限运行时回复被阻止的问题。兼容处理仅用于每次查询的临时接收窗口，仍核对发送进程、请求标识和数据长度。应用目录加载与文件检索分别进行，应用目录超时或失败时仍返回 Everything 结果，失败的目录加载可以重试。

托盘连接和搜索修订尚未进行实际交互验证。日常使用直接启动 WinDock 即可；整窗动画需要管理员确认时，由独立的启用程序申请权限。

## 参考依据

- [WPF ContextMenu 源码](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Controls/ContextMenu.cs)：菜单的捕获、关闭和弹出窗口生命周期。
- [Hardcodet WPF NotifyIcon](https://github.com/hardcodet/wpf-notifyicon/blob/master/src/NotifyIconWpf/TaskbarIcon.cs)：通知区域菜单的弹出窗口及前台处理。参考行为，未引入该库或复制其实现。
- [GetGUIThreadInfo](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getguithreadinfo)：读取菜单所有者和菜单模式，避免只按窗口是否激活来判断菜单结束。
- [Windows 菜单事件](https://learn.microsoft.com/en-us/windows/win32/winauto/event-constants)与 [TrackPopupMenuEx](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-trackpopupmenuex)：菜单事件、前台窗口与菜单关闭约定。
- [.NET 命名管道源码](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.IO.Pipes/src/System/IO/Pipes/NamedPipeServerStream.Windows.cs)与 [Windows 完整性级别](https://learn.microsoft.com/en-us/windows/win32/secauthz/mandatory-integrity-control)：不同运行权限下的连接访问控制。
- [Everything 官方 IPC SDK](https://www.voidtools.com/support/everything/sdk/ipc/)：查询协议与临时接收窗口的消息处理。

## 使用与回退

目前保留 Windows 原生任务栏，“替换 Windows 任务栏”保持关闭。输入法、语言栏与系统面板还没有完整的替代实现；恢复原生任务栏后，仍可使用 Dock、搜索和收纳。

托盘和菜单修订在退出旧版、启动完整的新版本目录后生效，不要求注销 Windows。原生整窗动画组件升级需要另行注销，操作方法见随程序提供的《整窗动画说明》。

需要回退菜单行为时，退出新版并运行保留的旧版本。托盘固定项、排序、Everything 搜索范围和 PecoFence 布局沿用现有设置。
