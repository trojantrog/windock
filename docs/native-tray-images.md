# 当前托盘图像的接入方案

更新日期：2026-10-29。

当前图像可以沿“Explorer 图标线程取得有效引用 → 后台复制 SoftwareBitmap 像素 → 现有管道传输”的方向继续实现。已有钩子提供了进入 Explorer 的消息线程的条件；直接将远程对象指针交给 Dock、读取初始 HICON 缓存或使用程序 EXE 图标，都不能取得应用当前绘制的数字和文字。

本页记录源码检查、微软公开接口和本机 PE/PDB 的离线证据。尚未增加或运行图像获取代码，也未验证当前 Explorer 的线程值、像素输出、刷新速度或退出行为。

## 图像来源与版本范围

全部私有布局仅适用于 [native-tray-model.md](native-tray-model.md) 中的 Taskbar.dll：SHA-256 `ca1e335ac1fae5ff57519035302a2f365c4620d3a230f31f1231af7331760741`，RSDS `dab69a79-fee0-33f0-d81e-023fbf172b51`、age 1。符号来自该文档列出的微软符号服务器文件，PDB SHA-256 为 `20069f339a72cc1857f57e9cf612f8431e9742e018944ee020889eb38dead652`。

`NotificationAreaIcon2::SetIcon` 在实现对象 `+0xC8` 保存当前 `SoftwareBitmap`。成功转换后，它释放旧引用、保存新引用并发出变化通知。`+0x138` 的 HICON 仅在字段为空时复制，后续更新跳过复制。`get_Icon` 取得当前对象后执行 AddRef，返回调用者拥有的引用。

现有 `WM_COPYDATA` 钩子在发送方图标句柄仍有效时执行 `CopyIcon`，并在 Explorer 返回成功后接受事件。这仍是应用后续 `NIF_ICON` 更新的有效来源；新的 SoftwareBitmap 路径主要补齐接入前已登记的图像、漏包后的当前状态，以及原生模型中的变化。两条路径都传递实际图像，不能用应用图标覆盖未知结果。

## 线程证据与进入条件

[`SystemTrayInterop.cs`](../src/WinDock/Services/SystemTrayInterop.cs) 将 `WH_CALLWNDPROC` 和 `WH_CALLWNDPROCRET` 安装到经过进程、路径和窗口类核验的 `Shell_TrayWnd` 所属线程。[微软 SetWindowsHookEx 文档](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowshookexw)明确这两个钩子分别在目标窗口过程处理消息之前和之后执行。现有 [`tray_hook.c`](../native/WinDock.TrayHook/tray_hook.c) 的 `Worker` 是另行创建的线程，不能直接调用要求 UI 线程的私有图标 getter。

| 本机证据 | RVA / 指令 | 结论 |
| --- | --- | --- |
| `NotifyIconUtilities::g_tidUIThread` | `0x23D0B8`，PDB 类型为 `unsigned long` | 实际用于检查的线程号保存在独立全局字段 |
| `FailFastIfNotUIThread` | `0x1C2B8`，46 字节；`0x1C2BC` 调用 GetCurrentThreadId，`0x1C2C2` 与全局字段比较 | 不相等会进入 fail-fast，不能通过调用此函数来试探线程 |
| manager 构造函数 | `0x131314`；`0x13139A` 取得线程号，`0x1313A0` 写入上述全局字段 | 图标线程在 manager 构造时登记 |
| manager 构造链 | `CTaskListWnd::Initialize 0x1535E0 → make_shared 0x14F05C → 0x131314` | 线程来源是初始化任务列表及 manager 的执行线程 |
| 登记与销毁 | `ShellNotifyIcon 0x9C838`、`AddIcon 0x78DD8`、`ModifyIcon 0x4C6B4`、`DeleteIcon 0x131C10`、manager 析构 `0x1315B0` | 这些路径均进入同一个线程断言 |
| 当前图像 getter | 默认接口虚表 `0x1C7FD8` 的槽 8，函数 `0x1BEB0` | getter 也要求同一线程，并返回带 AddRef 的引用 |

现有钩子的目标线程与图标管理线程不能仅凭窗口名称判为相等。每次获取前，在匹配映像及函数证据的前提下直接比较三个值：钩子内 `GetCurrentThreadId()`、目标窗口的 `GetWindowThreadProcessId()` 和经过校验的 `g_tidUIThread`。同时核对窗口仍属于同一 Explorer 进程及当前连接。任何一项为零或不相等都停止获取。[GetWindowThreadProcessId 的返回语义](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getwindowthreadprocessid)支持窗口归属检查。

获取应安排在独立请求消息，或全部嵌套托盘消息已经返回后的边界；现有 `copyDepth` 可用于禁止在嵌套 `WM_COPYDATA` 内开始枚举。钩子不能等待后台、写管道或同步调用其他窗口。

## 对象引用与销毁

`g_trayUI` 是 `ComPtr<ITrayUI>`。`TrayUI` 构造函数在 `0xD07B4` 起为新根引用执行 AddRef，随后在 `0xD07CB` 替换全局值，再释放旧根。`0x1B4490` 的完整函数范围内则先清空该全局值，再 Release。根指针与 manager 指针都不能跨请求永久缓存。

根的 `ITrayUI` 虚表为 `0x1CE030`，完整 504 字节。其 AddRef 槽指向 `0xD3250`，调整接口 this 后进入 `0x83CA0`；后者和其调用的 `0x1DA60` 仅进行原子引用计数处理。根持有 TaskbarModel 的引用，model 持有 manager 的 `shared_ptr`。根接口视角的 model `+0x270`、model 接口视角的 manager `+0x248`，不能与实现对象的偏移混用：`SetNotificationAreaIconManager2` 在实现 this `+0x258` 保存 shared_ptr，调用方明确将默认 model 接口减去 `0x10` 后传入。

可采用的引用顺序如下：

1. 在已确认的图标线程上，重新核对根接口虚表及所有依赖函数，取得根的 IUnknown 引用。沿当前根读取 model、manager 和有界登记集合。此阶段连续执行，不发消息、不泵消息、不触发用户回调。
2. 对每个候选核对集合边界、图标实现虚表、默认接口虚表和登记身份。默认接口为实现 `+0x10`。其 AddRef 槽 `0x68670` 调整 this 后进入 `0xA4A84`，后者只做原子计数操作；通过这个接口取得图标强引用，不手动增加私有字段。
3. 只通过完整校验过的默认接口槽 8 取得当前图像；检查 HRESULT 和空结果。成功后，图像引用独立于 manager 的后续删除或图标字段替换。释放图标和根引用也安排在所属线程；manager 不传入后台，不调用或仿造其私有 shared_ptr 复制/析构函数。
4. 图像对象只在 Explorer 进程内部移交。后台最终释放自己取得的引用。不得对这个共享 `SoftwareBitmap` 调用 `Close`，也不得将 COM 指针、内部对象地址或位图数据地址放进跨进程协议。

这个顺序利用了当前模型修改和析构的线程约束，避免在不持有引用时跨线程使用对象。它不证明整个 Explorer 生命周期永远不会改变：模型为空、窗口销毁、连接代际更换、映像不再匹配时必须终止获取并清理队列。指针可读性检查本身不提供对象寿命保证，异常捕获也无法补救 fail-fast。

两个可见集合的 `IVector.GetAt` 同样返回带引用的图标，但集合只覆盖 promoted 和 overflow。它们不能替代完整登记集合，也不能据此推断应用主动隐藏状态。

## 像素复制与传输

微软的 [SoftwareBitmap 文档](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.imaging.softwarebitmap?view=winrt-26100)将该运行时类标为 Agile、Threading Both。这支持同进程内、持有效引用后的跨 apartment 使用，不授予私有 `NotificationAreaIcon` 相同能力，也不允许跨进程直接传指针。

优先在 UI 线程确认公开 `ISoftwareBitmap` 与敏捷接口支持，然后交给专门的像素线程。该线程通过 [RoInitialize](https://learn.microsoft.com/en-us/windows/win32/api/roapi/nf-roapi-roinitialize) 初始化自己的 MTA，并平衡成功的初始化与释放；不能改变 Explorer UI 线程已有的 apartment 模式。敏捷性确认失败时先拒绝这一项。若将来需要支持非敏捷对象，可使用 [RoGetAgileReference / Resolve](https://learn.microsoft.com/en-us/windows/win32/api/combaseapi/nf-combaseapi-rogetagilereference)，但其代理调用可能回到原线程，不能把它当成无阻塞保证。

使用公开 [`ISoftwareBitmap.CopyToBuffer`](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.imaging.softwarebitmap.copytobuffer?view=winrt-26100) 和自己拥有的有界 IBuffer。自定义 IBuffer 还需提供 [`IBufferByteAccess`](https://learn.microsoft.com/en-us/windows/win32/api/robuffer/ns-robuffer-ibufferbyteaccess)。尺寸、格式和容量在分配前确认：宽高 1–256，BGRA8，预乘 alpha，stride 为经过溢出检查的 `width × 4`，数据长度为 `stride × height`，单图最多 262,144 字节。超过范围或格式不符要明确返回未获取，后续缩放支持须另用公开图像接口完成。

SDK ABI 以微软 [win32metadata 固定提交的 imaging.idl](https://github.com/microsoft/win32metadata/blob/64ed9177fb76eb244fc9051c74b8a93fdfe20873/generation/WinSDK/RecompiledIdlHeaders/winrt/windows.graphics.imaging.idl)为依据：`ISoftwareBitmap` IID 为 `689E0708-7EEF-483F-963F-DA938818E073`，继承 IInspectable，CopyToBuffer 在该接口的槽 18。实现应使用正式 SDK 声明，避免按方法名称猜槽位。

另有一个容易引入崩溃的 ABI 差异：微软 [interop SDK 头文件](https://github.com/microsoft/win32metadata/blob/64ed9177fb76eb244fc9051c74b8a93fdfe20873/generation/WinSDK/RecompiledIdlHeaders/um/windows.graphics.imaging.interop.h)明确 `ISoftwareBitmapNative` 继承 IInspectable，GetData 为槽 6；部分 Learn 页面将继承关系写成 IUnknown。若尝试 WIC 路径，以 SDK 为准，且 GetData 是否支持目标 IID 要检查 HRESULT。本方案不依赖该替代路径。

像素线程与现有协议写入线程分开，避免一次图像复制阻塞应用登记、菜单协议及断线消息。只把已经拥有的像素数组交给现有 IPC 写入线程。队列、持有的图像总数和总字节均设置上限；每个图标最多保留一个等待任务，并合并为最近一次请求。模块引用持续到后台退出和全部 COM 引用释放为止。

新图像包使用独立类型和明确的协议版本，包含连接代际、登记身份、图像修订号、尺寸、格式及长度。修订号在图标线程取得当前引用时登记，并与成功的 ADD、DELETE、NIF_ICON 更新共用顺序来源。接收方只对仍然存在且代际、身份与修订号都有效的登记项更新图像；旧任务晚完成、窗口句柄复用、GUID 重新登记或丢包后的旧结果均须丢弃。图像包不能伪装为 NIM_MODIFY，也不能据此重新确认回调消息、协议版本或 owner。

启动接入、断线恢复、队列丢失、托盘面板打开可触发有界重取；实时 NIF_ICON 继续驱动更新。仅做启动快照并不能覆盖所有图像变化。PDB 另有 `INotificationAreaIcon4::add_Changed`（`0xB78C0`）和 `remove_Changed`（`0x1488E0`），但其 IID、事件参数、撤销与对象寿命尚未形成完整证据，当前不能直接调用。完整事件接入完成前，可用受限的低频重取补充，并在开发记录中保留这一刷新限制。

## 线程不匹配时

首先停用当前图像请求，保留已有协议接收。继续核对 `g_tidUIThread` 对应线程确实仍属于同一个 Explorer 进程，并查找该线程拥有的有效窗口。确认后可向该窗口安装同位数、指定线程的消息钩子，只发送带连接标识的注册请求消息，在新钩子内再次检查实际线程号。请求消息只携带整数标识，不携带跨进程指针；不替换系统窗口过程，不直接跳到私有函数地址。

这需要扩展现有只接受 Shell_TrayWnd 的连接检查，是独立的兼容分支。若找不到有效窗口，应报告该路径不可用。[PostThreadMessage](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-postthreadmessagew) 的消息不关联窗口，普通 modal loop 可能丢弃它；若采用此方式，需要另一个经过设计的线程消息钩子和握手，不能假定现有 WH_CALLWNDPROC 会收到它。

[SendMessageTimeout](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendmessagetimeoutw)的等待超时不能作为接收线程中任意 COM 调用的执行时限。像素字节数有上限也不代表调用耗时有硬上限。发生长时间未完成的图像任务时停止追加任务并保留真实故障状态，不能强制结束 Explorer 线程。

## 后续实现边界与证据文件

实施前需要把线程全局、根/图标接口虚表、AddRef 路径及 getter 完整函数哈希纳入单独的图像能力资料，并在 Explorer 内重新验证加载映像。现有模型读取成功和 `capabilities.icon = false` 都不应被静默改写成已经具备图像能力。系统文件变化后重新取证，不用邻近版本或模式扫描猜地址。

本次新增的本地离线文件位于 Git 忽略目录 `publish/checks/tray-images/`：

- `inspect-images.py`：只读取系统 PE、已有 PDB 并核对身份；不加载 Taskbar.dll、不调用 COM、不读取进程。
- `image-code-evidence.json`：27 个完整函数范围、2 个完整虚表、全局符号与交叉引用，函数合计 7,775 字节。
- `image-disassembly.txt`：上述函数的完整离线反汇编。无展开信息的叶函数按下一公开符号确定边界，并确认末尾为返回或已确认的跳转目标。

关键新增哈希：`FailFastIfNotUIThread` 为 `c56ffaafe479839af0e557fad85fc2eba4edde38b6fd3fe7f3602a129611de9f`；manager 析构为 `276c4cdff323c04dfee79767ff2765c938d04543d84360e16876f718f1a061fb`；图标 AddRef 实体为 `9c198385d1eb90d1a517f447f2254616f823a39a19847ab19760276636483556`；根完整 ITrayUI 虚表为 `725c8256255b00235b09bfdaeafc247c50d0ab418e7769c82f6b139a33ef7b46`。虚表指针比较须处理加载基址重定位。

资料中的私有布局来自本机映像和匹配的微软 PDB，公开 ABI 来自微软 SDK，没有引入 GPL 项目的实现。当前尚未实现取图模块；像素更新、退出和桌面交互也未验证。
