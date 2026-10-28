# 系统托盘兼容范围

更新日期：2026-10-29。

WinDock 的适配目标是使用 Windows 通知区协议的各类应用，按注册、输入、菜单、定位和通知等行为处理。一个应用可以同时使用多种方式，例如图标使用标准协议，弹出面板却由另一个进程绘制。因此，显示图标、打开菜单和显示消息提示需要分别确认。

以下“已实现”表示当前源码包含对应处理，均未在本轮进行桌面实测。应用自己的菜单绘制、Windows 权限限制和未接入的系统接口仍会影响实际体验，不能据此承诺所有应用完全兼容。

Windows 输入法、语言栏和系统面板入口尚未完整替代，当前应保留 Windows 原生任务栏，并关闭“替换 Windows 任务栏”。Dock、搜索和收纳可与原生任务栏同时使用。

| 功能或应用类型 | 当前实现 | 仍需明确的范围 |
| --- | --- | --- |
| 标准图标注册、修改和删除 | 先复制请求，再读取 Explorer 的实际返回值；成功的 `NIM_ADD / MODIFY / DELETE` 才改变注册状态。发送回调前复核窗口及进程。 | 成功判定已对本机 `shell32.dll` 做静态核对；Explorer 私有传输格式仍可能随系统更新改变。失败 ADD 的显示补全有独立存在性检查，不能当作成功注册。[采集协议](tray-capture-protocol.md)、[微软协议](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shell_notifyiconw) |
| 按有效字段更新 | 已按 `NIF_MESSAGE / ICON / TIP / STATE / INFO` 处理，隐藏状态使用 `dwStateMask`；旧结构按 `cbSize` 屏蔽不存在的字段，并截断旧版提示文字。 | 未设置有效标志的字段保留已有信息；不同历史结构的实际应用覆盖仍待确认。[字段约定](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/ns-shellapi-notifyicondataw) |
| GUID 标识及仅携 GUID 的后续操作 | 已要求 `NIF_GUID` 有效后才使用 GUID，拒绝字段不完整或空 GUID；已知 GUID 可复用原有窗口及进程归属，不再退回可能冲突的窗口标识查找。 | 首次收到未知 GUID、没有有效窗口及回调时，不能凭 GUID 生成可点击图标。[GUID 约定](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/ns-shellapi-notifyicondataw) |
| 协议版本与重新注册 | 仅成功 ADD 重置默认版本，仅成功 `NIM_SETVERSION` 更新版本；重复失败 ADD 保留既知版本。回调消息和版本均已确认时才直接交互。 | 未知协议可尝试委托对应的原生可见控件执行默认操作；不猜测回调版本，未知右键仍需使用原生托盘。[原生默认操作范围](native-tray-actions.md)、[版本约定](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shell_notifyiconw) |
| 菜单结束后归还焦点 | 已接入 `NIM_SETFOCUS`；仅匹配当前图标及其原窗口、进程的活动菜单会话，等待菜单结束后按当前焦点状态归还焦点。 | 用户已切到其他程序时不强制抢回焦点；实际归焦仍受 Windows 前台规则限制。[焦点约定](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shell_notifyiconw) |
| 左键、右键、中键和双击 | 已发送原应用回调；版本 3/4 保留鼠标抬起与语义事件的兼容顺序，连续点击中的第 2、4 等偶数次按双击发送。拖出图标后松开时继续跟踪按下动作可能已打开的菜单。取得鼠标坐标失败时不发送伪造坐标。 | 在按下时打开菜单和拖出图标后松开等情况仍需逐类确认；不能把一次回调发送成功视为应用已经处理。 |
| 键盘选择与菜单键 | 已支持 Enter、Space、菜单键及 Shift+F10；版本 4 的键盘锚点使用图标左上角屏幕坐标。 | 原应用是否接收、是否正确聚焦其菜单仍待实测。[回调参数](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/ns-shellapi-notifyicondataw) |
| 只有状态图标、没有点击回调 | 已分开显示与点击能力：允许键盘聚焦、悬停文字和读屏提示，缺少点击回调时不发送点击，也不因此反复要求重新注册。 | 应用没有提供的提示文字和操作无法补造；读屏实际体验未测。 |
| 标准文字提示与自绘悬停面板 | 已处理单独设置 `NIF_SHOWTIP`；只更新图标时保留原提示方式。自绘提示使用独立悬停会话，配对发送 `NIN_POPUPOPEN / POPUPCLOSE`，不为悬停授予抢占前台权限。 | 特殊自绘提示窗口的显示和关闭仍待应用实测。[提示约定](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/ns-shellapi-notifyicondataw) |
| 动态图标、闪烁及绘在图标中的数字 | 已复制应用提供的图像并处理后续更新，保留透明度和传统掩码；相同图标的重复图像更新可在队列中合并，复制或转换失败时保留原图，并请求恢复更新。 | 能显示应用画出的数字，不能通用推导未读数量；高频变化受队列容量影响。 |
| `NIF_INFO` 提示 | 已保存最近 50 条非实时提示请求和图标提示点，保存在内存中；带 `NIF_REALTIME` 的请求不加入提示历史。提示点在用户清空记录时移除。 | 提示点表示收到请求，不能代表真实未读数或系统确实弹出过通知。静默时段、实时丢弃和气泡交互继续由 Explorer 处理。[通知字段](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/ns-shellapi-notifyicondataw) |
| 系统气泡点击、关闭和超时 | Explorer 继续接收原始请求并负责原生气泡回调。 | WinDock 的提示历史不是原生气泡，点开或清空历史不等于发送 `NIN_BALLOONUSERCLICK`，也不会标记其他应用消息已读。[气泡事件](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shell_notifyiconw) |
| Windows 通知中心的现代应用通知 | 保留进入系统通知中心的入口。 | 独立托盘尚未接入完整的通知中心列表、操作按钮及已读状态。微软的通知监听接口是另一项能力，需要相应声明和用户授权。[通知监听接口](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/notification-listener) |
| 应用主动隐藏的图标 | 已接收的 `NIS_HIDDEN` 状态继续生效，WinDock 的固定和排序独立保存。 | 原生登记快照没有应用隐藏状态，首次补入的图标要等待有效状态更新才能确认；不把折叠区偏好当作应用隐藏状态。当前不承诺复刻 Explorer 的排序和置顶结果。 |
| 标准 Win32 菜单和子菜单 | 已调用应用自己的菜单，观察菜单生命周期并处理焦点交接。 | 菜单字体、位图和抗锯齿由应用绘制；WinDock 的磨玻璃与圆角样式不能直接改善第三方菜单像素质量。 |
| WPF、Qt 等自绘菜单或面板 | 已有普通弹窗识别及菜单会话处理，按可确认的窗口所有者链关联辅助进程面板。 | 尚不能覆盖所有复用窗口、延迟显示、特殊样式和没有可确认所有者关系的跨进程面板。不能仅识别标准菜单窗口类。 |
| 使用 `Shell_NotifyIconGetRect` 定位的菜单 | 保留 Windows 原有的图标矩形查询。可选的菜单定位功能默认关闭；启用后，仅在点击 WinDock 图标后短时间内出现、且能确认线程和窗口归属的单个标准菜单靠近原生图标时，尝试将其移到 Dock 图标附近。 | 没有改写系统查询；自绘窗口、跨进程菜单和无法确认来源的窗口仍可能出现在原任务栏附近。移动请求异步发送，目标程序卡住时可能稍后才执行；该功能尚未进行桌面验证。[矩形接口](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shell_notifyicongetrect) |
| 由系统或辅助进程显示的面板 | 原始应用回调继续发送，有可确认窗口所有者关系的面板参与会话处理。 | 只凭图标进程无法证明另一个进程的面板归属；快速设置等系统界面也不能按普通应用菜单承诺完整复制。 |
| Windows 输入法和语言栏入口 | 可尝试从 WinDock 打开 Windows 原生输入法入口，继续保留原生任务栏；该操作不切换输入模式或修改输入法配置。 | WinDock 尚未完整替代输入法切换和语言栏入口；无法识别系统控件时应使用原任务栏。快捷键兼容仍待确认。 |
| 初始连接、Explorer 重建和断线重连 | 已重连并限频请求重新注册；另通过原生登记快照补全当前仍有效的窗口、进程、GUID、图标及提示。快照在回调退出后校验真实进程路径和图标存在性，连接变化或注册事件发生时丢弃过期结果。 | 快照接口不是公开稳定接口，历史记录须过滤；它不提供回调、协议版本及 `NIS_HIDDEN`。无法确认归属或权限不足的图标仍可能缺失。[登记快照边界](native-tray-inventory.md)、[任务栏重建](https://learn.microsoft.com/en-us/windows/win32/shell/taskbar#taskbar-creation-notification) |
| 丢事件后的恢复 | 已识别采集队列和界面队列丢失更新，显示状态提示，并限频请求 `TaskbarCreated` 重新注册；刷新间隔至少 10 秒，自动恢复间隔至少 30 秒。 | 忽略重新注册请求的应用仍可能缺失；丢失 DELETE 时，仍存活的接收窗口可能留下旧图标。不能仅凭应用没有重发图标就认定它已经删除图标。 |
| 高低权限应用 | 已校验窗口归属，保留 Windows 的消息和前台限制。 | 普通权限 WinDock 可能无法操作更高权限窗口；允许获得前台不等于解除消息权限限制。[UIPI](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-changewindowmessagefilterex) |
| 32/64 位应用与系统架构 | 当前组件面向 x64 Windows 10 19041 及更新系统的 x64 Explorer；采集点位于 Explorer。 | 这不等于仅支持 64 位应用，32 位应用也会向 Explorer 注册；实际跨架构覆盖未测。32 位系统与 ARM64 系统当前不支持。[钩子架构要求](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowshookexw) |
| 多屏、缩放和负坐标 | 已启用 PerMonitorV2，回调使用屏幕像素，保留负坐标；图标按设备像素对齐并高质量缩放。 | 混合缩放、热插拔及缩放改变后的重注册未测。协议坐标范围和应用原图分辨率仍有限制，低分辨率图标不能恢复缺失细节。 |
| 大量图标和突发更新 | 已限制采集队列 128 条、界面队列 256 条或 8 MiB、当前图标 512 个。 | 达到容量会丢更新或拒绝新增，不能声称数量和更新频率无限。 |
| 系统更新与协议变化 | 已核对组件哈希、协议版本、Explorer 来源及数据长度。 | Explorer 内部传输格式不是微软公开稳定接口；最低系统版本检查不能证明每个后续版本都兼容。 |

这些边界来自 `SystemTrayService`、`SystemTrayInterop`、`SystemTrayIconButton`、`TrayMenuSession`、`TrayReRegistration` 与 `native/WinDock.TrayHook/tray_hook.c` 的实现。界面编译成功和组件连接成功都不能替代真实应用交互结果。

对照的开源实现也有不同职责：

| 项目与固定源码 | 对适配有用的依据 | 不能据此推定的能力 |
| --- | --- | --- |
| [ManagedShell NotifyIcon](https://github.com/cairoshell/ManagedShell/blob/855222035e927910ca53fd3215caef056337a048/src/ManagedShell.WindowsTray/NotifyIcon.cs)、[TrayService](https://github.com/cairoshell/ManagedShell/blob/855222035e927910ca53fd3215caef056337a048/src/ManagedShell.WindowsTray/TrayService.cs)、[NotificationArea](https://github.com/cairoshell/ManagedShell/blob/855222035e927910ca53fd3215caef056337a048/src/ManagedShell.WindowsTray/NotificationArea.cs) | 分版本打包回调，保留旧鼠标事件与语义事件；其替代托盘接收器另外处理图标矩形查询。 | WinDock 当前没有矩形应答能力；本机离线分析也发现查询返回值语义与该项目的处理不同，不能直接照抄。 |
| [YASB trayhook](https://github.com/amnweb/yasb/blob/25c71861c1610f095269295a76fe2c8984e4a7b6/src/core/widgets/services/systray/hook/trayhook.cpp) | 展示 Explorer 消息中的固定 32 位字段布局及及时复制图标句柄的必要性。 | 此格式属于实现观察，不能当作微软对后续 Windows 版本的兼容保证。 |
| [Hardcodet TaskbarIcon](https://github.com/hardcodet/wpf-notifyicon/blob/0a9d1a92914db8fb6b1cca1f073469644cd9bf3b/src/NotifyIconWpf/TaskbarIcon.cs) | 同一托盘组件可分别创建 WPF 菜单、Popup 和 Tooltip；菜单自行获得前台和键盘焦点，部分 Popup 还按系统任务栏位置定位。 | 仅适配标准菜单类或传递点击坐标，不能覆盖这些不同路径。 |

后续覆盖应按应用行为组合确认：旧版与版本 4 协议、GUID 与窗口标识、标准与自绘菜单、普通与提升权限、32/64 位应用、多屏缩放、Explorer 重建和突发图标更新。结果应分别记录“显示正常”“输入正常”“菜单位置正常”“消息提示正常”，避免用一个应用的一次点击代表全部系统托盘应用。
