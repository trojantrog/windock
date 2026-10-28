# 当前托盘图像的接入方案

更新日期：2026-10-29。

当前图像路径采用“Explorer 图标线程取得有效引用 → 后台复制 SoftwareBitmap 像素 → 现有管道传输”。已有钩子提供了进入 Explorer 的消息线程的条件；直接将远程对象指针交给 Dock、读取初始 HICON 缓存或使用程序 EXE 图标，都不能取得应用当前绘制的数字和文字。

本页记录源码检查、微软公开接口和本机 PE/PDB 的离线证据。图像采集、后台像素复制和版本校验已实现并通过离线编译，精确版本资料由离线生成器生成。preview.22 正常启动时已收到当前图像；preview.23 的 v7 进程实例路径又恢复两个访问受限条目的图像，记录见文末。持续刷新曾出现停滞，桌面交互仍未验证。

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
2. 每次最多取两个候选。先在连续的无回调阶段核对集合边界、图标实现虚表、默认接口虚表和登记身份，为本次全部候选取得强引用，再进入图像 getter 或公开 COM 调用。默认接口为实现 `+0x10`。其 AddRef 槽 `0x68670` 调整 this 后进入 `0xA4A84`，后者只做原子计数操作；通过这个接口取得图标强引用，不手动增加私有字段。开始 COM 调用后不再读取旧 manager 或 vector 的裸地址，避免重入期间集合变化留下悬空指针。
3. 只通过完整校验过的默认接口槽 8 取得当前图像；检查 HRESULT 和空结果。成功后，图像引用独立于 manager 的后续删除或图标字段替换。释放图标和根引用也安排在所属线程；manager 不传入后台，不调用或仿造其私有 shared_ptr 复制/析构函数。
4. 图像对象只在 Explorer 进程内部移交。后台最终释放自己取得的引用。不得对这个共享 `SoftwareBitmap` 调用 `Close`，也不得将 COM 指针、内部对象地址或位图数据地址放进跨进程协议。

这个顺序利用了当前模型修改和析构的线程约束，避免在不持有引用时跨线程使用对象。它不证明整个 Explorer 生命周期永远不会改变：模型为空、窗口销毁、连接代际更换、映像不再匹配时必须终止获取并清理队列。指针可读性检查本身不提供对象寿命保证，异常捕获也无法补救 fail-fast。

两个可见集合的 `IVector.GetAt` 同样返回带引用的图标，但集合只覆盖 promoted 和 overflow。它们不能替代完整登记集合，也不能据此推断应用主动隐藏状态。

## 像素复制与传输

微软的 [SoftwareBitmap 文档](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.imaging.softwarebitmap?view=winrt-26100)将该运行时类标为 Agile、Threading Both。这支持同进程内、持有效引用后的跨 apartment 使用，不授予私有 `NotificationAreaIcon` 相同能力，也不允许跨进程直接传指针。

优先在 UI 线程确认公开 `ISoftwareBitmap` 与敏捷接口支持，然后交给专门的像素线程。该线程通过 [RoInitialize](https://learn.microsoft.com/en-us/windows/win32/api/roapi/nf-roapi-roinitialize) 初始化自己的 MTA，并平衡成功的初始化与释放；不能改变 Explorer UI 线程已有的 apartment 模式。敏捷性确认失败时先拒绝这一项。若将来需要支持非敏捷对象，可使用 [RoGetAgileReference / Resolve](https://learn.microsoft.com/en-us/windows/win32/api/combaseapi/nf-combaseapi-rogetagilereference)，但其代理调用可能回到原线程，不能把它当成无阻塞保证。

使用公开 [`ISoftwareBitmap.CopyToBuffer`](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.imaging.softwarebitmap.copytobuffer?view=winrt-26100)，由 `Windows.Storage.Streams.Buffer` 的 factory 创建有界缓冲区，再通过 [`IBufferByteAccess`](https://learn.microsoft.com/en-us/windows/win32/api/robuffer/ns-robuffer-ibufferbyteaccess) 读取自己持有的缓冲区。后台持有微软 SDK 对象，不把本 DLL 实现的 Buffer 回调交给图像对象。尺寸、格式和容量在分配前确认：宽高 1–256，BGRA8，预乘 alpha，stride 为经过溢出检查的 `width × 4`，数据长度为 `stride × height`，单图最多 262,144 字节。超过范围或格式不符返回未获取，后续缩放支持须另用公开图像接口完成。

SDK ABI 以微软 [win32metadata 固定提交的 imaging.idl](https://github.com/microsoft/win32metadata/blob/64ed9177fb76eb244fc9051c74b8a93fdfe20873/generation/WinSDK/RecompiledIdlHeaders/winrt/windows.graphics.imaging.idl)为依据：`ISoftwareBitmap` IID 为 `689E0708-7EEF-483F-963F-DA938818E073`，继承 IInspectable，CopyToBuffer 在该接口的槽 18。实现应使用正式 SDK 声明，避免按方法名称猜槽位。

C ABI 声明还对照同一提交的 [streams.idl](https://github.com/microsoft/win32metadata/blob/64ed9177fb76eb244fc9051c74b8a93fdfe20873/generation/WinSDK/RecompiledIdlHeaders/winrt/windows.storage.streams.idl)、[robuffer.h](https://github.com/microsoft/win32metadata/blob/64ed9177fb76eb244fc9051c74b8a93fdfe20873/generation/WinSDK/RecompiledIdlHeaders/winrt/robuffer.h) 和 [ObjIdlbase.Idl](https://github.com/microsoft/win32metadata/blob/64ed9177fb76eb244fc9051c74b8a93fdfe20873/generation/WinSDK/RecompiledIdlHeaders/um/ObjIdlbase.Idl)：IBuffer 和 IBufferFactory 继承 IInspectable，Factory.Create 为槽 6；IBufferByteAccess 和 IAgileObject 继承 IUnknown。图像格式取 IDL 中的 Bgra8 = 87、Premultiplied = 0，不依赖语言枚举名称的隐式转换。

另有一个容易引入崩溃的 ABI 差异：微软 [interop SDK 头文件](https://github.com/microsoft/win32metadata/blob/64ed9177fb76eb244fc9051c74b8a93fdfe20873/generation/WinSDK/RecompiledIdlHeaders/um/windows.graphics.imaging.interop.h)明确 `ISoftwareBitmapNative` 继承 IInspectable，GetData 为槽 6；部分 Learn 页面将继承关系写成 IUnknown。若尝试 WIC 路径，以 SDK 为准，且 GetData 是否支持目标 IID 要检查 HRESULT。本方案不依赖该替代路径。

像素线程与现有协议写入线程分开，避免一次图像复制阻塞应用登记、菜单协议及断线消息。只把已经拥有的像素数组交给现有 IPC 写入线程。每次最多取得两个图标引用，单个像素线程仍忙时不积压新请求；完成后下一次请求沿游标继续。像素发送队列最多 8 项、合计最多 2 MiB，容量不足时丢弃本次图像并报告状态，不挤占应用登记或菜单事件。模块引用持续到后台退出和全部 COM 引用释放为止。

v7 的图像包使用独立 kind 5，状态包使用 kind 6，结构见 [`tray_images.h`](../native/WinDock.TrayHook/tray_images.h) 和[协议说明](tray-capture-protocol.md)。图像包包含连接标识、登记身份、图像修订号、尺寸、格式及长度。修订号在图标线程取得当前引用时登记，并与成功的 ADD、DELETE、NIF_ICON 更新共用顺序来源，后台像素复制完成时不重新编号。接收方只对仍然存在且代际、身份与修订号都有效的登记项更新图像；旧任务晚完成、窗口句柄复用、GUID 重新登记或丢包后的旧结果均须丢弃。图像包不能伪装为 NIM_MODIFY，也不能据此重新确认回调消息、协议版本或 owner。

状态中的 `sampled` 表示本连接累计取得有效引用并入队的数量，`published` 表示累计成功交给发送队列的数量。它们均不表示 Dock 已完成绘制。取消连接不会强行终止 COM 调用：旧任务返回后释放引用，连接标识和内部代际不符的结果不能进入新会话。像素调用长时间未完成时保留一个工作线程，不反复创建替代线程。

启动接入、断线恢复、队列丢失、托盘面板打开可触发有界重取；实时 NIF_ICON 继续驱动更新。仅做启动快照并不能覆盖所有图像变化。PDB 另有 `INotificationAreaIcon4::add_Changed`（`0xB78C0`）和 `remove_Changed`（`0x1488E0`），但其 IID、事件参数、撤销与对象寿命尚未形成完整证据，当前不能直接调用。完整事件接入完成前，可用受限的低频重取补充，并在开发记录中保留这一刷新限制。

## v7 进程实例证据

v7 使用独立的 `WinDock.TrayHook.v7.x64.dll`，三个注册消息分别为 `Connect.v7`、`Disconnect.v7`、`Images.v7`。旧 DLL 保留原文件名，避免覆盖 Explorer 已加载的映像。kind 5 的正文仍为 80 字节：偏移 36 改为 `instanceKind:uint32`，偏移 56 改为 `instanceValue:uint64`。类型 1 表示创建时间，类型 2 表示系统进程序号；类型与值共同参与身份比较，两者不能互换。其他消息结构保持不变。

优先使用 `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` 与 `GetProcessTimes` 取得创建时间，并保留原有进程会话检查。只有打开进程明确返回 `ERROR_ACCESS_DENIED`，才允许尝试系统序号。其他失败不会进入该路径，失败帧不会改变现有图像。

微软公开的 [SystemBasicProcessInformation](https://learn.microsoft.com/en-us/windows/win32/api/winternl/nf-winternl-ntquerysysteminformation) 从 Windows 11 build 26100.4770 提供识别 PID 复用的 `SequenceNumber`。[固定 SDK 头文件](https://github.com/microsoft/win32metadata/blob/64ed9177fb76eb244fc9051c74b8a93fdfe20873/generation/WinSDK/RecompiledIdlHeaders/um/winternl.h#L270) 定义枚举值 252；x64 记录为 48 字节，下一项偏移、PID、父 PID、序号和名称结构分别位于 0、8、16、24、32。实现对这些大小与偏移执行编译期断言，仅解析链、PID、序号。

[`tray_process_sequences.c`](../native/WinDock.TrayHook/tray_process_sequences.c) 在像素线程初始化时解析已加载 ntdll 的公开导出，并一次分配 1 MiB 查询区和 64 KiB PID 去重表。退出时清零释放。图标线程每轮最多进行两次同步内核查询：先为最多两个候选取得新序号，完成全部图像 getter 和尺寸检查后再次查询。只有前后序号非零且相同、窗口归属未变的帧，才交给后台复制。该限制约束查询次数与数据规模，不提供硬执行时限。

每次查询要求返回成功、长度在容量内、链偏移按 8 字节对齐且不越界，并以零偏移结束。记录最多 8192 条；任意 PID 重复、候选序号为零、去重探测超过 64 次或响应超限都拒绝本轮序号证据。UI 线程不分配、扩容、重试或等待后台。未找到的候选不发帧，具有有效创建时间的候选仍可继续。系统响应中的名称既不解析，也不进入日志或 IPC；整个查询区在每次成功或失败后清零。

[ProcessIdToSessionId](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-processidtosessionid) 也要求查询目标进程的权限。序号路径不调用该函数，也没有独立取得 owner 的 SessionId；其来源限定为当前已核实 Explorer 模型中的真实登记项，并检查窗口 PID/TID、两次新序号、图标身份、UI 线程、模型、连接和消息代际。它不接受外部自报登记，也不单凭序号授予回调调用能力。

## 线程不匹配时

首先停用当前图像请求，保留已有协议接收。继续核对 `g_tidUIThread` 对应线程确实仍属于同一个 Explorer 进程，并查找该线程拥有的有效窗口。确认后可向该窗口安装同位数、指定线程的消息钩子，只发送带连接标识的注册请求消息，在新钩子内再次检查实际线程号。请求消息只携带整数标识，不携带跨进程指针；不替换系统窗口过程，不直接跳到私有函数地址。

这需要扩展现有只接受 Shell_TrayWnd 的连接检查，是独立的兼容分支。若找不到有效窗口，应报告该路径不可用。[PostThreadMessage](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-postthreadmessagew) 的消息不关联窗口，普通 modal loop 可能丢弃它；若采用此方式，需要另一个经过设计的线程消息钩子和握手，不能假定现有 WH_CALLWNDPROC 会收到它。

[SendMessageTimeout](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendmessagetimeoutw)的等待超时不能作为接收线程中任意 COM 调用的执行时限。像素字节数有上限也不代表调用耗时有硬上限。发生长时间未完成的图像任务时停止追加任务并保留真实故障状态，不能强制结束 Explorer 线程。

## 可复现的版本资料

[`Generate-TrayImageProfile.py`](../scripts/Generate-TrayImageProfile.py) 从系统 PE 和匹配的微软 PDB 生成 [`tray_image_profile.h`](../native/WinDock.TrayHook/tray_image_profile.h)。输入必须同时匹配整文件 SHA-256、AMD64 PE 身份、唯一 RSDS GUID/age、PDB GUID/DBI age、PDB 文件哈希和 PE 节表。PDB Info age 3、DBI age 1 的匹配规则沿用 [Microsoft PDB 固定源码](https://github.com/microsoft/microsoft-pdb/blob/805655a28bd8198004be2ac27e6e0290121a5e89/PDB/dbi/pdb.cpp#L808)，不把 Info age 直接当成映像 age。目标映像或 PDB 变化时拒绝生成。

生成器通过精确公开符号选择函数，再按 PE 展开表或下一公开符号确定完整范围。少数尾跳转叶函数还须以同映像的命名代码为目标。匿名根释放函数通过已确认的 `g_trayUI` 读写、Release 槽访问及完整展开范围唯一确定。线程检查中的调用被解析到 PE 实际导入的 `GetCurrentThreadId`，不能只凭相似指令认作同一个 API。

发行头包括 36 个完整函数哈希、2 个完整虚表哈希，以及下列字段。函数范围共 9,345 字节；头文件本身不包含这些字节，也不包含 PDB 或反汇编。头文件 SHA-256 为 `7ac3d53965a4a0c17dbf480f6ef3021b875e754287511fba12c9ee5c4e72722a`。

| 字段 | 本机值 | 独立依据 |
| --- | --- | --- |
| UI 线程号全局 | RVA `0x23D0B8` | PDB 类型、构造时写入与 fail-fast 比较均对应实际 GetCurrentThreadId 导入 |
| 根全局、根 ITrayUI 虚表 | RVA `0x23E998`、`0x1CE030` | PDB ComPtr 类型、构造/替换/释放及完整 63 槽虚表 |
| 根接口 → model → manager | `+0x270`、`+0x248` | ITrayUI 槽 31 的 HandleCopyData 中通知与位置查询两条路径相同；根构造/析构及 model 的 shared_ptr 赋值交叉核对 |
| manager 登记集合 | begin `+0`、end `+8`、capacity `+0x10` | GetRect/findIdentity 的逐项查找与 AddIcon 的追加、容量判断 |
| 图标实现与默认接口虚表 | RVA `0x1C6048`、`0x1C7FD8` | PDB 类型与完整 15 槽默认接口；每槽都在同映像可执行节并有 DIR64 重定位 |
| 图标默认接口 / 图像 getter | 实现 `+0x10` / 槽 8 | AddRef、Release 调整 this；getter 与 SetIcon 一致指向实现 `+0xC8` |
| 图标 HWND、uID、GUID | 实现 `+0x50`、`+0x58`、`+0x5C` | Identity 返回结构与 findIdentity 的比较字段一致 |

新增的释放证据包括根 Release 调整入口 `0xDFAB0`、实体 `0x836E0`、原子递减 `0x1ECC8`，以及图标 `subtract_final_reference 0x58624`、弱引用递减 `0x52CD8`。引用获取路径单独检查：根和图标 AddRef 的主体只进行原子计数，根可调用已确认的原子递增函数；它们不发送窗口消息或调用图标 owner。Release 可能触发对象析构，必须保留前述线程和清理顺序。

安装 pefile、capstone 后，在项目根目录可复现生成。已有 PDB 默认从 `publish/checks/tray-model/` 读取；缺失时 `--download` 只使用精确的微软符号服务器地址。`--decoder-path` 可指定已有 Python 依赖目录。

```powershell
python scripts/Generate-TrayImageProfile.py --download
python scripts/Generate-TrayImageProfile.py --check
```

`--check` 对照生成结果，不改发行头。生成和检查仅解析磁盘文件、解码指令和修饰符号名，不加载目标 DLL，不枚举或读取当前进程，不调用 COM，不安装钩子。本轮离线检查确认：生成头可重复得到相同字节；原有 27 函数和 2 虚表的 RVA、范围、哈希与早期证据逐项一致。未运行图像模块或进行桌面交互测试。

本地完整证据保存在 Git 忽略目录 `publish/checks/tray-images/`：

- `generated-image-evidence.json`：36 函数、2 虚表、完整字节、PDB 身份、全局交叉引用和生成头哈希。
- `generated-image-disassembly.txt`：上述函数的完整离线反汇编。
- `image-code-evidence.json`、`image-disassembly.txt`：最初 27 函数 / 2 虚表的对照证据。

运行时还须重新验证磁盘映像、加载函数哈希和完整虚表。虚表的磁盘哈希检查之后，逐槽将首选基址指针换算为实际加载地址，再与当前槽值比较。`g_trayUI` 与线程号属于可变数据，不对其当前值套用磁盘哈希。单纯模型登记读取成功不能代表图像能力已经可用。

[`tray_image_verify.c`](../native/WinDock.TrayHook/tray_image_verify.c) 实现了独立的校验组件。后台创建时仅取得已经加载的 Taskbar.dll 引用，核对模块路径和文件最终路径均为系统目录中的 Taskbar.dll，再检查整文件 SHA-256、PE/RSDS、完整函数和完整虚表。哈希采用 [BCryptCreateHash](https://learn.microsoft.com/en-us/windows/win32/api/bcrypt/nf-bcrypt-bcryptcreatehash) 及配对的销毁接口；文件读取、哈希、分配均在创建阶段完成。

通过校验后，组件保留 9,969 字节的预期加载内容。图标线程每次只用固定 1,024 字节栈缓冲比较这些范围，不进行磁盘读取、哈希、堆分配或等待后台。[ReadProcessMemory](https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-readprocessmemory) 限定当前进程，单次最多 4,096 字节，要求完整返回；它只提供可读性判断。持有模块直到后台结束且所有 UI 使用已退出，才释放缓存和模块引用。这一组件已通过 C11 严格警告编译检查，尚未执行。

资料中的私有布局来自本机映像和匹配的微软 PDB，公开 ABI 来自微软 SDK，没有引入 GPL 项目的实现。系统文件变化后重新取证，不套用邻近版本偏移。SEH、指针可读性和等待超时均不能保证对象寿命，也不能限制 COM 调用耗时；异常后不能重新使用已失去所有权依据的指针。像素更新、退出及应用兼容性仍需实际验证。

## 本机正常启动记录：preview.22

2026-10-29 06:17 安装源码提交 `76e45102bf4cbaf4c83e60a91e565e83fbe493ce` 编译的 preview.22。原生 DLL SHA-256 为 `cc574de0c02f957dc0bcc4552387f856f8e4bee732abcbb5d32a2e2758026e2c`。完整原生/托管编译、发布文件校验和 Defender 扫描通过；没有进行桌面交互或登录周期测试。

正常启动后的内置诊断在 06:18:03 记录：当前模型 12 项、身份验证通过 10 项，10 项均有图像；之前缺少图像的 `BluetoothNotificationAreaIconWindowClass` 与 `SystemTray_Main` 已收到当前图像。该时刻图像收到 34 帧、应用 32 帧、拒绝 2 帧，原生丢包、宿主丢包和图像队列丢弃均为 0。帧数是处理计数，不能替代实际画面验收。

另两项属于 `NVDisplay.Container` 和 `nvcontainer`。所需访问权限与最低查询权限均返回系统错误 5；不能通过删减等待权限解决。保持原生任务栏可见，不更改这些进程的访问控制。

随后诊断文件未继续更新，而 Dock UI 线程 CPU 仍增长。该文件按内容去重且异步写入，因此当前证据尚不能区分图像刷新停滞与诊断写入停滞；持续刷新仍在排查，不能将这份启动快照当作持续运行证明。Explorer、DWM 进程未重启，应用错误日志未新增记录。

登录入口仍为 `D:\WinDock\WinDock.exe --tray`，动画登录任务启用，18 个固定应用保持不变。本次登录仍驻留旧动画组件 build 10，新版 build 13 待下一次 Windows 登录加载。

## 本机正常启动记录：preview.23

2026-10-29 07:02 安装源码提交 `98e00741e6659d7f66e4ede89ef6d726be069ff1` 编译的 preview.23。v7 原生 DLL SHA-256 为 `1dfa5bbe28d615e8ce13c260a9e6c827c4992b9c57c6c4bb3b3b3e384422c35f`。原生与托管编译、发布文件校验和 Defender 扫描通过。

启动后的模型 12 项全部通过校验，12 项均有图像；两个原本访问被拒绝的 NVIDIA 所属条目通过系统进程序号恢复，`OwnerAccessRecovered=2`，剩余 `OwnerAccess=0`。它们仍不允许直接 owner 回调；一个位于原生溢出区，另一个由应用隐藏、处于 `NotVisible`，没有强行显示或触发操作。

07:03:28 后原生包计数停在 318，当前图像收到 102、应用 101、拒绝 1；原生丢包、宿主丢包、图像队列丢弃均为 0。07:04:32 的诊断心跳仍继续写入、请求发送计数增至 97，管道处于 `waiting header`，队列为空，模型读取也仍在运行。这把停滞范围缩小到钩子与原生管道输入，排除了诊断写入本身停止。随后定位到 [钩子安装线程寿命缺陷](tray-capture-protocol.md#钩子的线程寿命)；不能将初始图像成功当作持续刷新成功。

安装未重启 Explorer 或 DWM，18 个固定应用保持不变，原生任务栏可见。应用点击、菜单、消息、输入法和 Windows 登录周期仍由用户验收。
