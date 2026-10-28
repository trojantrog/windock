# 托盘采集协议

当前本地传输版本为 5。WinDock 在选定的 Explorer 托盘线程安装 `WH_CALLWNDPROC` 和 `WH_CALLWNDPROCRET`，保持原窗口过程和原钩子链。组件与宿主必须使用相同协议版本。

## 按处理结果更新

前置钩子复制有效字段和 `HICON`，返回钩子只读取保存的副本及 `CWPRETSTRUCT.lResult`，不再解引用原消息的数据指针。微软规定 [`WM_COPYDATA` 数据只在消息处理期间有效](https://learn.microsoft.com/en-us/windows/win32/dataxchg/wm-copydata)，[`CWPRETSTRUCT.lResult` 是窗口过程的返回值](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-cwpretstruct)。

对本机 `C:\Windows\System32\shell32.dll` 的静态分析确认：`Shell_NotifyIconW` 在调用 `SendMessageTimeoutW` 成功后，将窗口过程结果与整数 1 比较；不相等时返回失败。文件 SHA-256 为 `c55a5f3b6ef059871fe2a0c8d82f086e0998d1154023e3ecf49698b97f9341f6`，函数导出 RVA 为 `0x1b5f0`，结果比较位于 `0x1b9f9`。这只证明该文件上的行为，不构成 Windows 对私有传输协议的稳定承诺。

| 消息类型 | 含义 | 宿主处理 |
| --- | --- | --- |
| 1 | Explorer 返回成功的注册操作 | 按 ADD、MODIFY、DELETE、SETFOCUS、SETVERSION 处理。 |
| 2 | 已核对管道来源的连接建立 | 开始当前连接会话。 |
| 3 | 采集或传输丢失更新 | 显示恢复状态，并限频请求重新注册。 |
| 4 | Explorer 拒绝的 ADD | 仅作为显示补全候选，不改写既知协议。 |

被拒绝的 MODIFY、DELETE、SETVERSION 和 SETFOCUS 不更新宿主状态。ADD 成功时才建立默认协议，SETVERSION 成功时才改变协议；ADD 中与超时共用的字段不作为版本。嵌套消息最多保存 32 份副本，按请求进入顺序提交已返回结果，防止内层 SETVERSION 排到外层 ADD 之前。断开连接、发送失败和窗口销毁时释放尚未提交的图标副本。图像转换和管道写入继续在后台线程执行。

## 晚接入与显示补全

[Qt 6.8 的官方实现](https://github.com/qt/qtbase/blob/6.8/src/plugins/platforms/windows/qwindowssystemtrayicon.cpp)在 `TaskbarCreated` 后更新图像并尝试 ADD；仅 ADD 成功后才发送 SETVERSION。这说明向已注册应用请求重新注册，不能保证获取它的版本，也不能把被拒绝的 ADD 当作新注册。

对于失败 ADD，宿主在后台通过 `Shell_NotifyIconGetRect` 确认相同窗口和 ID 的图标仍存在，再补充尚未确认的空显示字段。GUID 查询本身不能证明所属窗口：缺少此前成功操作所建立的 GUID、窗口、进程关联时，不生成候选图标。异步结果会重新核对连接、窗口及进程；已被删除或重新成功注册的候选作废。成功修改过的空图像、空提示或空回调也属于已知值，候选不会覆盖它们。

存在性查询同时最多执行一次，最多保留 32 个候选，每次等待上限 2 秒。若系统的同步查询迟迟不返回，该调用继续占有单个查询位置，新的候选会超时结束，避免累积阻塞线程；不会阻塞 Dock 界面或 Explorer 的窗口线程。

`callbackObserved` 与 `versionObserved` 分别记录回调消息和版本是否来自成功操作。两者都确认后才允许直接发送点击回调。`registrationObserved` 表示是否看到成功 ADD。原生位置查询不会确认版本或回调，不能将版本未知的项目解释为旧版协议。诊断中的 `rejectedRegistrations` 和 `confirmedCandidates` 分别统计被拒绝的 ADD 与通过存在性检查的显示补全。

此实现尚未进行桌面交互验证。晚接入的部分应用仍可能只显示图标，未知 GUID 图标仍可能缺失；Windows 原生任务栏需保留。下一步要补全这类交互，需取得可靠的原生图标状态或通过原生托盘控件执行操作，不能仅凭应用类型猜测回调协议。
