# 原生托盘默认操作

更新日期：2026-10-29。

WinDock 未获得某个图标的回调协议，或因所属进程访问受限而不能直接调用时，可以尝试委托 Windows 原生图标执行默认操作。此能力已加入源码，尚未进行桌面交互验证；Windows 原生任务栏继续保留。

## 使用范围

- 仅接入左键松开、Enter 和 Space。它们触发原生控件提供的默认操作，具体行为由 Windows 和原应用决定，不能保证等同于应用的单击或双击。双击中的第二次松开不会追加一次默认操作。
- 图标必须在原生任务栏上可见，或位于已经展开的原生溢出区。被其他窗口遮挡、收起、移出屏幕或无法确认身份时停止操作。
- 已知回调和协议版本、且实际进程创建时间校验有效的图标继续使用现有回调。默认操作代理不会填写、猜测或升级协议版本。
- 已确认没有点击回调的状态图标只显示内容。未知协议的右键、中键、双击语义和自绘悬停面板不在代理范围内。

## 身份与提交

请求固定绑定当前托盘连接中的 Explorer 窗口、线程、PID、真实创建时间和 nonce；后台持有对应 Explorer 进程句柄，不能在操作途中另选一个 Explorer。先按登记的 GUID，或窗口和图标 ID，通过 `Shell_NotifyIconGetRect` 获取真实矩形。核对所属应用实例，确认矩形位于该 Explorer 的可见任务栏或溢出窗口内，再查找唯一匹配的 UI Automation 按钮。需要同时满足可见、可用、矩形覆盖、窗口归属、控件祖先关系和实际命中位置检查。名称、图标顺序及上次缓存的位置都不作为身份依据。

普通应用继续使用进程句柄、创建时间和存活检查。只有当前原生模型已核验的访问受限条目，才接受 `SystemSequence` 实例：在请求前、两次控件解析后以及提交前分别取得新序号，匹配登记实例；过程中的轻量检查另行核对窗口归属、连接和代际。系统查询始终在后台执行，不把序号当作创建时间，也不会据此发送直接 owner 回调。窗口重新登记、Explorer 重启或连接中断都会使旧请求失效。

全部检查从原生矩形再做一遍。回到界面线程确认当前图标和登记代际未变，并建立短时菜单会话后，再核对矩形及命中控件，最多提交一次 `Invoke`。Windows 没有按通知区图标标识原子执行 UI Automation 的公开接口，因此无法保证最后一次核对后界面绝不变化；无法确认的情况会停止。

UI Automation 始终运行在一个独立 MTA 线程上；同一时间最多一个请求，调用方最多等待 2.5 秒。关闭面板、切走、按 Esc、改点同一面板的另一个图标、图标重新登记和超时都会阻止尚未提交的动作。已经跨进程提交的调用无法保证撤回，失败或超时后不会改发另一种点击。若系统控件的调用一直不返回，该工作线程继续占用唯一请求位置，其他请求立即结束，需重启 WinDock 才能重新尝试。

等待期间保留原 Dock 面板；交接时仅临时接纳已核对的 Explorer 窗口，应用菜单仍按应用进程和窗口关系跟踪。提交时可将前台许可授予已确认的 Explorer 进程，仍受 Windows 的前台限制；不会强制激活窗口，也不会为了执行操作拉出整条任务栏、改变隐藏设置、模拟全局鼠标点击或发送 Win+B。[前台许可](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-allowsetforegroundwindow)

## 依据与边界

- 微软要求 UI Automation 客户端在独立、无窗口的 MTA 线程操作其他界面：[线程约定](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-threading)。
- 默认动作的行为由提供者定义，调用可能阻塞；返回成功不能单独证明应用已打开：[Invoke 约定](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-implementinginvoke)、[Invoke 接口](https://learn.microsoft.com/en-us/windows/win32/api/uiautomationclient/nf-uiautomationclient-iuiautomationinvokepattern-invoke)。
- `IsOffscreen` 不包含全部遮挡情况，控件矩形也不等于可点击区域，因此另外核对窗口命中和可点击点：[元素属性](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-automation-element-propids)。
- 通用 `ShowContextMenu` 可能回退到父控件菜单，不能用于保证打开某个应用自己的托盘菜单；本实现不使用这个回退：[ShowContextMenu](https://learn.microsoft.com/en-us/windows/win32/api/uiautomationclient/nf-uiautomationclient-iuiautomationelement3-showcontextmenu)。
- 使用 GUID 查询时，系统忽略查询结构中的窗口和 ID。因此 GUID 必须来自已有可信登记关联，不能用一次矩形查询补造所属应用：[通知区标识](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/ns-shellapi-notifyiconidentifier)。
- 参考 [Windhawk 托盘展开实现](https://github.com/ramensoftware/windhawk-mods/blob/a2e932eb009c3476b3ae0de439ee3777fe513af0/mods/tray-hover-expand.wh.cpp) 的独立工作线程和菜单生命周期处理；其中按名称、首个位置兜底的识别方式未采用。本次也未实现自动展开折叠箭头。

原生输入法入口、系统通知中心、权限更高的应用、没有默认操作模式的控件及不提供可点击点的图标仍可能无法由此代理操作，应使用 Windows 原生任务栏。
