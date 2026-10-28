# 托盘图标矩形查询

## 目标和当前状态

部分应用通过 `Shell_NotifyIconGetRect` 查询图标的屏幕位置，再将自绘窗口或菜单放在该位置。系统接口返回的是 Windows 通知区中图标的位置。WinDock 显示的是另一个图标副本，因此应用可能在原任务栏附近打开菜单。

当前 WinDock 的 Explorer 钩子只观察通知区更新请求，不修改矩形查询的结果。窗口移动方案可以调整部分已经出现的菜单，但无法等同于向调用方提供正确的原始矩形；窗口在辅助进程中、菜单被自行重定位或应用只查询尺寸时，仍可能无法处理。

## 当前系统上的观察结果

以下是对本机文件的静态分析结果，不是 Windows 对此内部协议的稳定承诺：

| 文件 | SHA-256 | 导出函数 RVA |
| --- | --- | --- |
| x64 `System32\shell32.dll` | `c55a5f3b6ef059871fe2a0c8d82f086e0998d1154023e3ecf49698b97f9341f6` | `0x1a9e00` |
| x86 `SysWOW64\shell32.dll` | `bcfcc320cdf0e73e34c26f186083e2d39e28981f9f8781ab433550e77288e8db` | `0x17ce40` |

两个版本先按 `Shell_TrayWnd` 窗口类查找目标，再以 `WM_COPYDATA` 同步发送两次查询。发送数据中的 `dwData` 是 `3`，载荷长度为 40 字节，包含签名 `0x34753423`、查询类型、内部结构长度、窗口句柄、图标 ID 和 GUID。第一个查询的类型为 2，返回值低、高 16 位分别表示宽度和高度；第二个查询的类型为 1，返回值低、高 16 位分别表示 X 和 Y，随后调用方将位置与尺寸组合成 `RECT`。坐标按有符号 16 位值解码。

消息中的 `wParam` 来自 `NOTIFYICONIDENTIFIER.hWnd`；它标识图标使用的窗口，并不能单独证明发起查询的进程或线程。GUID 查询还可能没有窗口句柄。[InSendMessageEx](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-insendmessageex) 可以区分发送方式，不能提供上述身份。私有载荷没有查询事务标识，同一图标的并发查询可能交错，单靠最近一次点击、GUID、窗口句柄和超时时间，无法证明两次请求属于同一个调用。

## 方案比较

| 路径 | 能力 | 并存时的主要限制 |
| --- | --- | --- |
| 在 Explorer 窗口处理链应答私有查询 | 不必向每个应用加载组件，可根据图标身份和最近交互提供 Dock 矩形。 | 不能从上述载荷严格确定请求来源或配对两次查询；可能改变同一图标的其他查询。需要处理窗口线程、版本变化和组件卸载。 |
| 在目标应用内限制回调线程的查询结果 | 可将改写范围限定到实际处理 Dock 通知的线程和时间段，并直接返回一个 `RECT`。 | 需要在目标进程内安装按架构匹配的组件，权限和安全策略可能拒绝；跨线程或异步查询不能覆盖。消息本身也不携带可证明来源的 WinDock 标识。 |
| 调整已打开的菜单窗口位置 | 不需要改写 `Shell_NotifyIconGetRect` 返回值。 | 应用可能看到原矩形；自绘浮窗、辅助进程窗口、受限制的高权限进程和应用自身重新布局仍有缺口。 |
| 提供完整替代通知区并接管任务栏接口 | 能在替代托盘内部定义图标位置。 | 需要完整承担原生任务栏、输入法、通知和系统面板的职责，并可能与原任务栏冲突。 |

[Windhawk taskbar-multi-tray](https://github.com/ramensoftware/windhawk-mods/blob/main/mods/taskbar-multi-tray.wh.cpp) 处理的是 Explorer 内原生任务栏 XAML 图标及其跨屏坐标，依据最近操作和线程缓存选择已有的原生元素。WinDock 的 WPF 图标不是该 XAML 元素，不能直接套用元素映射。[ManagedShell](https://github.com/cairoshell/ManagedShell/blob/855222035e927910ca53fd3215caef056337a048/src/ManagedShell.WindowsTray/NotificationArea.cs) 有完整的替代通知区服务，职责不同。[Zebar 的托盘实现](https://github.com/glzr-io/zebar/blob/main/crates/systray-util/README.md)使用同名 `Shell_TrayWnd` 窗口接收并转发消息；其[公开问题](https://github.com/glzr-io/zebar/issues/301)报告过与原生任务栏菜单和恢复过程的冲突。

## 后续实现条件

系统级方案需默认关闭，在无法确认身份、图标可见位置、屏幕缩放或系统版本时交回 Windows 原始处理。两次查询必须返回来自同一份位置快照，不能一半使用原生结果、一半使用 Dock 结果。原生任务栏上的操作不能被当作 Dock 操作；若无法区分，应优先保留原生结果。

Explorer 子类化需要在目标窗口所属线程安装和移除；微软明确说明 [`SetWindowSubclass` 不能跨线程使用](https://learn.microsoft.com/en-us/windows/win32/api/commctrl/nf-commctrl-setwindowsubclass)。组件必须在确认回调已从窗口处理链移除、且不再有调用执行后才卸载；安装失败、Explorer 重建、连接丢失或关闭功能时应回退原始处理。按应用进程拦截同样需要分别处理 32 位和 64 位、进程结束、模块卸载及权限拒绝，不得绕过系统安全限制。

窗口移动已作为默认关闭的实验功能接入，仅处理能确认归属的单个标准菜单。Explorer 查询拦截和目标进程拦截尚未接入。上述路径均未进行桌面或应用兼容验证。
