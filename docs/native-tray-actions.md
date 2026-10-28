# 原生托盘操作

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

## 受限进程的完整操作方案

以下为 2026-10-29 的开发方案，尚未实现。现有默认操作代理继续按前述范围工作。图标和图像已能读到，不代表右键、双击和悬停已经兼容。

建议在现有 Explorer 托盘线程内增加固定动作代理：按当前登记找到真实图标对象，由经过精确版本核验的 Windows 对象处理点击、菜单和提示。代理不接收任意窗口消息，不修改应用权限，也不要求打开受限应用的进程句柄。菜单仍由原应用生成，WinDock 只负责请求和菜单期间的面板保持。

### 公开接口与项目参考

| 依据 | 能确认的行为 | 对方案的影响 |
| --- | --- | --- |
| [微软通知区协议](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shell_notifyiconw)与[登记结构](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/ns-shellapi-notifyicondataw) | 回调版本影响坐标、ID 和事件解释；富提示有独立的打开、关闭事件 | 保留当前真实登记协议，分别处理点击和悬停；不通过补发猜测的事件兼容应用 |
| [微软 NotificationIcon 示例](https://github.com/microsoft/Windows-classic-samples/blob/434f6002bdf9cf9829406c3ff2b33387982d6168/Samples/Win7Samples/winui/shell/appshellintegration/NotificationIcon/NotificationIcon.cpp) | 示例处理 `NIN_SELECT` 和 `WM_CONTEXTMENU`，由应用创建菜单；部分弹窗另查 `Shell_NotifyIconGetRect` 定位 | 使用应用自己的菜单；回调坐标不能保证所有应用都把弹窗放在 Dock 旁 |
| [Qt Windows 托盘实现](https://github.com/qt/qtbase/blob/6a3eb4302168084cac2d081ef38351e52d55bd79/src/plugins/platforms/windows/qwindowssystemtrayicon.cpp) | 双击后会抑制随后一次激活；菜单同时依赖消息坐标、当前鼠标位置和前台窗口 | 不能把双击拆成多次默认激活；需要保留前台交接和真实菜单生命周期 |
| [YASB 托盘实现](https://github.com/amnweb/yasb/blob/538959f4723d6f34f6194f01ebde89b06492ef4b/src/core/widgets/services/systray/systray_widget.py) | 根据已保存版本发送回调，版本较新时追加选择或菜单事件；源码也记录了部分晚接入图标不收消息的问题 | 可用于比较协议行为，不能据此宣称直接发送回调兼容所有应用 |
| [Windhawk Tray Overflow List](https://github.com/ramensoftware/windhawk-mods/blob/5f7c8fdebcc26e7d3df215688a438ba94f06ba49/mods/tray-overflow-list.wh.cpp) | 保留原控件处理点击、菜单和拖动，只调整原生溢出区布局 | 保留原控件是另一条可行方向，但会改变原生浮层；不能直接当成独立 Dock 的操作接口 |

微软示例和 YASB 的项目许可为 MIT；Qt 文件列出商业、LGPL 和 GPL 许可选项，所引 Windhawk 文件为 GPL-3.0-only。本方案只记录行为与接口事实，没有复制或翻译 Qt、Windhawk 的实现。

公开 UI Automation 的 `Invoke` 仍适合可见控件的默认操作；通用 `ShowContextMenu` 可能调用父控件菜单。它们不能单独补齐每个应用的双击、富提示和隐藏区操作。继续保留现有 UIA 路径，作为不支持原生动作能力时的默认操作入口。

### 当前 Windows 映像中的确证

仅对 [原生模型文档](native-tray-model.md) 固定的 Taskbar.dll 生效：SHA-256 为 `ca1e335ac1fae5ff57519035302a2f365c4620d3a230f31f1231af7331760741`，PDB 为 `DAB69A79FEE033F0D81E023FBF172B51`、映像 age 为 1。以下结论来自对应公开 PDB、磁盘 PE 导入表和函数指令；没有加载目标 DLL、调用接口或操作桌面。地址均为本映像 RVA，不能直接推广到另一 Windows 更新。

| 入口 | 已确认行为 | 可用性结论 |
| --- | --- | --- |
| `INotificationAreaIcon` 虚表 `0x1C7FD8`，槽 13 `Clicked`，包装入口 `0x8D490` | 接口指针减 `0x10` 后调用 `0x145998`；核对托盘 UI 线程和窗口，再开始系统异步点击处理 | 可作为鼠标单击候选，需把完整依赖链加入动作能力校验 |
| 同虚表槽 14 `ContextRequested`，包装入口 `0x145E00` | 转到 `0x145E50`；协程续体 `0x145F30` 先等待 `AllowWindowSetForegroundAsync`，再取得对象弱引用；发送右键按下、松开，版本至少为 3 时追加 `WM_CONTEXTMENU` | 保留系统的前台交接与协议选择，优于另造右键消息组合 |
| `DoubleClicked` 包装入口 `0x146240` | 接口指针减 `0x18` 后调用 `0x146290`，再进入 `DoubleClickedAsync`；不能复用默认接口偏移 | 独立动作能力，双击使用自己的入口；接口虚表、协程和调用链仍需补进生成器 |
| 默认接口槽 11 `ToolTipRequested`，包装入口 `0x69170` | 先检查 `AppOwnsToolTips`；参数为真时发 `NIN_POPUPOPEN`（`0x406`），为假时发 `NIN_POPUPCLOSE`（`0x407`） | 可形成配对的富提示打开、关闭能力；普通文字提示仍由 Dock 显示 |
| `MousePointerMoved` 包装入口 `0x63A40` | 接口指针减 `0x20`，确认窗口后发送 `WM_MOUSEMOVE` | 只代表移动通知，不能替代富提示开关 |
| `MouseInteracted` 实现 `0x129B34` | 实际进入共享的 `hresult_not_implemented` 抛异常代码 | 禁止用它作为通用鼠标入口；符号名字不足以证明能力 |
| `SendMessageToOwnerWindow` `0x63E90` | 使用对象当前 HWND、ID、回调、版本打包，调用 PE 导入项中的 `SendNotifyMessageW` | 作为原生语义入口的依赖核验，不向 WinDock 暴露通用发送接口 |

还有两项必须保留的约束：

- `Clicked`、`ContextRequested` 会调用 `MoveMouseCursorIfNecessary`（`0x147238`）。指令明确表明，版本小于 4 且布尔参数为真时会调用 `SetCursorPos`。第一阶段只开放来自真实鼠标操作的路径，并使用已证实不移动指针的参数分支；键盘操作暂沿用 UIA。不能把这个布尔值随意解释为“允许激活”，也不能为了兼容旧应用移动用户鼠标。
- 点击和菜单是系统的 `fire_and_forget` 协程。包装入口返回仅表示已经交给系统；进入异步处理后不能承诺取消。原生菜单协程会在等待前台许可后取得对象弱引用，这有助于处理对象销毁，但不构成应用一定完成处理的证明。`SendNotifyMessageW` 对其他线程也不等待其处理完成。[异步发送约定](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendnotifymessagew)

### 最小接口与执行顺序

建议新增独立 `NativeTrayBroker`，复用 `NativeTrayIdentity`、`NativeTrayOperation`、`TrayActionRouter` 的取消和提交规则。接口形式如下；这里仅规定语义，尚无对应运行代码：

```text
Capabilities(session) -> supported action mask
PrepareAsync(identity, action, cancellation) -> short-lived registration ticket
ExecuteAsync(ticket, screenPoint, cancellation) -> action result
CancelPending(session, requestId) -> cancelled | already submitted

action = PrimaryClick | ContextMenu | DoubleClick | HoverOpen | HoverClose
result = Rejected | Cancelled | Submitted | UnknownAfterSubmit
```

`MouseMove` 如需接入，应作为独立、可合并的低频能力，不能挤占点击或登记消息。键盘默认激活继续走已存在的 UIA；拖放、气泡点击及通知中心操作不在此动作集合中。

准备请求包含会话 nonce、单调请求号、有效期、HWND/PID/TID/窗口类、ID/GUID、带类型的进程实例键和固定动作。原生端返回短期、不透明的登记票据；票据绑定当前对象、登记和操作，后续确认只能使用该票据。托管的 `RegistrationGeneration` 仅在托管侧比较，不能假定它与原生内部序号相等。原生端需另行使已接受的同项 ADD、DELETE、协议变更及连接丢失撤销票据，避免同一应用重复使用图标 ID 后执行旧请求。

回调消息号、协议版本、对象指针、函数地址、任意 `wParam/lParam` 均由代理从当前真实登记解析，不能由调用方指定。提交坐标由当前 Dock 图标和真实输入产生，核对显示器范围及版本可表达范围；不改原生对象的 `Bounds`。

执行顺序：

1. Dock 界面确认图标仍存在、连接与登记未变、用户动作仍有效；建立现有菜单会话，取消尚未提交的旧动作。每次操作只有一个提交点。
2. 通过绑定当前 Explorer 实例的受校验通道传递固定请求。现有 v7 管道为单向读取，只接收 Explorer 事件；需增加版本化的命令通道或双向协议。连接两端都核对实际对端 PID、实例和现有映像约束。nonce 用于区分会话，不能单独当作身份认证；公开注册窗口消息最多用于唤醒，不承载未经认证的操作参数。
3. Explorer 托盘 UI 线程仅在登记报文处理已完成、无复制嵌套且模型稳定时准备票据。从当前模型重新解析相同登记，核对 HWND/PID/TID/窗口类、当前协议和实例键。票据至多保存一个对象的临时强引用，并核对接口虚表和本动作完整代码证据。Dock 收到准备结果后再次检查原始用户动作及托管登记代际，完成菜单交接后才发送确认。原生提交前再核模型中仍为同一对象、票据未失效及当前实例；`SystemSequence` 两端检查都使用新快照，不转换为创建时间。
4. 调用经过核验的固定 ABI 包装入口，随即释放本次临时强引用；准备超时、取消和断开也在所属 UI 线程释放。不能把指针交给托管代码，或把等待菜单结束当成对象存活依据。调用进入系统后记为 `Submitted`；重复请求号只返回原状态，不再次调用。确认已发出但尚未收到结果时，也必须按可能提交处理，返回 `UnknownAfterSubmit`，禁止改走 UIA 或追加另一套消息。
5. 原应用拥有菜单及消息循环。`TrayMenuSession` / `PopupInteraction` 在提交前完成交接，继续处理 Esc、切走、点其他图标和真实菜单关闭；前台许可只针对已确认的 Explorer 或应用。原生协程自身也会进行前台交接，Dock 不反复强制激活窗口。[前台许可规则](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-allowsetforegroundwindow)

动作队列应独立于登记、图像队列：同一连接最多一个等待提交的点击动作；同一图标最多保留最新悬停状态。关闭、取消和连接丢失不会排队累积。动作状态记录请求号、动作、拒绝原因和是否已提交，不记录提示文本、应用内容或任意请求载荷。

富提示使用有期限的成对状态：同一实例只提交一次打开，指针移出、面板关闭、取消或会话结束时尝试关闭；关闭只作用于仍匹配的当前登记。实例变化后丢弃旧状态，不能把关闭发给复用 HWND 的另一应用。系统富提示和 Dock 普通提示互斥，避免两个提示重叠。系统拒绝、应用退出或断开后无法保证已提交的自绘面板即时消失。

### 能力降级与未覆盖范围

- 每个动作独立声明所需 ABI、虚表、函数与依赖链哈希；磁盘映像、PDB 和加载态证据都必须匹配。代码支持读取模型，不意味着它已经支持调用这些方法。未知版本或某动作校验失败时，仅关闭相应操作能力，继续显示已核验的图标，并保留原生任务栏和现有 UIA 默认操作。
- 此路径预期可以操作模型中仍登记、但原控件折叠而不可见的图标，因为它不依赖 UIA 的可见按钮；这是设计预期，尚无交互结果。它不改变应用隐藏状态、用户固定偏好或原生溢出区布局，也不把隐藏条目自动显示在 Dock。
- 应用可能自行查询原生图标矩形或鼠标位置，菜单、提示和弹窗仍可能锚定原生位置。不能通过临时改写共享 `Bounds` 来承诺全部改到 Dock 位置；该写入会影响原生控件和其他查询者。
- `OpenProcess` 返回访问拒绝，只证明该次进程访问失败。它不能证明消息必然被 UIPI 阻止，也不能证明需要管理员权限。Explorer 内执行同样受系统消息和前台规则约束；不修改 DACL、不启用 SeDebug、不放宽全局消息过滤，也不把动作代理变成通用高权限发送器。[消息权限边界](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendmessagew)
- 输入法、语言栏、系统通知中心、快速设置不都是第三方通知区图标。不能把此代理等同于完整替代这些系统入口；原生任务栏继续保留。
- 调用私有系统接口可能影响 Explorer 稳定性。精确版本限制可以减少误调用，不能免除异常、系统更新和异步处理风险。首个实现应按单击/右键、双击、富提示分别开放能力；仍需用户确认实际行为，不能用图标数量或提交计数宣称兼容完成。
