# Windows 11 当前托盘模型

更新日期：2026-10-29。

当前版本资料支持从一份精确匹配的 Taskbar 映像定位正在登记的图标集合，并读取 HWND、图标 ID、GUID、回调消息和协议版本。它没有证明所有 Windows 版本、所有应用或完整托盘操作已经兼容。布局依据来自系统文件和微软公开 PDB 的离线分析。

`0.6.0-preview.21` 安装后的正常启动诊断报告：12 个登记候选中，10 个通过校验并合并，另 2 个在打开所属进程时未取得句柄。10 个有效条目的回调、版本、显示集合、提示文字和提示模式均成功读取，可选信息的证据失败计数均为 0；其中 QQ 的提示文字本来为空。8 个条目已有图像，蓝牙和一个系统通知区条目仍缺少图像。7 个条目的应用 `NIS_HIDDEN` 状态未从协议中观察到，实际显示集合则已分别确认。旧 COM 登记接口尚未返回有效条目。这些结果只确认正常启动时的数据读取，应用点击、菜单、消息、输入法及重新登录效果尚未实测。

## 映像与符号来源

分析文件为 `C:\Windows\System32\Taskbar.dll`。Windows 签名检查结果为 `Valid`，签名者为 Microsoft Windows，证书指纹为 `667BBA1E1F478221AA65DE4F75187A751B03A453`。资源版本为 `10.0.10011.16384`；匹配实际使用下表中的 PE、RSDS 和哈希信息，不根据资源版本推断布局。PE 时间戳只作为原始标识值使用，不解释为编译日期，参见[微软 PE 格式](https://learn.microsoft.com/en-us/windows/win32/debug/pe-format)。

| 项目 | 值 |
| --- | --- |
| Machine | AMD64，`0x8664` |
| 文件长度 | 3,217,192 字节 |
| PE TimeDateStamp | `1989904251` |
| SizeOfImage | `3178496` |
| 文件 SHA-256 | `ca1e335ac1fae5ff57519035302a2f365c4620d3a230f31f1231af7331760741` |
| RSDS GUID | `dab69a79-fee0-33f0-d81e-023fbf172b51` |
| RSDS age | `1` |
| PDB 名称 | `Taskbar.pdb` |
| PDB 文件 SHA-256 | `20069f339a72cc1857f57e9cf612f8431e9742e018944ee020889eb38dead652` |
| PDB Info age / DBI age | `3 / 1` |

PDB 下载自[微软符号服务器的精确 GUID/age 路径](https://msdl.microsoft.com/download/symbols/Taskbar.pdb/DAB69A79FEE033F0D81E023FBF172B511/Taskbar.pdb)。Info age 大于映像 age 并不单独构成不匹配：微软的 `PDB1::OpenValidate4` 要求 GUID 相同、PDB age 不小于映像 age，并进一步核对 DBI age。本次 DBI age 与映像完全一致，未使用旧 DBI age 为零的例外。[微软源代码，固定提交](https://github.com/microsoft/microsoft-pdb/blob/805655a28bd8198004be2ac27e6e0290121a5e89/PDB/dbi/pdb.cpp#L808)

离线生成器还核对了 PDB section header 与 PE section header，确认没有需要转换地址的 OMAP。发布资料中的函数范围均已完整反汇编，其范围内没有基址重定位项。

## 已确认的读取布局

所有 RVA 相对于 Taskbar 模块基址。所有字段偏移只属于上表这份映像。

| 内容 | 值 | 本机指令证据 |
| --- | --- | --- |
| `g_trayUI` 存储位置 | RVA `0x23E998` | 微软 PDB 的 `ComPtr<ITrayUI> g_trayUI` 符号 |
| 图标实现虚表 | RVA `0x1C6048` | `NotificationAreaIcon2` 实现虚表符号 |
| Taskbar model 指针 | 根对象 `+0x270` | `TrayUI::HandleCopyData` 的登记和取矩形两条分支 |
| manager shared_ptr | model `+0x248`，取首个指针 | 两条分支均调用同一个 shared_ptr 复制函数，再将首指针传给 manager |
| 全登记集合 begin / end | manager `+0 / +8` | `ShellNotifyIconGetRect`、`ModifyIcon`、`SetVersion` 的身份查找参数相同 |
| 集合步长 | `8` 字节 | 身份查找每次前进 8 字节，`AddIcon` 写入一个对象指针后将 end 增加 8 |
| HWND | 图标实现 `+0x50`，8 字节 | `Identity` 与集合查找中的内联身份读取一致 |
| 图标 ID | 图标实现 `+0x58`，4 字节 | `Identity` 与消息发送的旧协议参数读取一致 |
| GUID | 图标实现 `+0x5C`，16 字节 | `Identity` 与集合查找均复制完整 16 字节 |
| 协议版本 | 图标实现 `+0x94`，4 字节 | `SetVersion` 写入；`SendMessageToOwnerWindow` 按值 4 选择消息编码 |
| 回调消息 | 图标实现 `+0x98`，4 字节 | `ModifyIcon` 在消息字段更新时写入；发送函数读取同一字段 |

关键函数包括 `HandleCopyData`（`0x90EC0`）、`ShellNotifyIcon`（`0x9C838`）、`ShellNotifyIconGetRect`（`0x9C8EC`）、`Identity`（`0x1C9E8`）、`SendMessageToOwnerWindow`（`0x63E90`）、`ModifyIcon`（`0x4C6B4`）、`SetVersion`（`0xA49D0`）、`AddIcon`（`0x78DD8`）。生成文件同时保存这些函数的完整长度和 SHA-256。

`AddIcon` 先把对象加入 manager 的集合，再处理图标状态和可见集合。`DeleteIcon` 的实现另有对象删除和集合维护。因此这里读取的是原生登记集合，不能把集合成员直接解释为图标当前应当显示。登记集合与两个显示集合的区别必须保留。

根变量位于 PE 的零填充可写数据区，其值在运行时变化。虚表包含需要按加载基址调整的地址。根变量和虚表不能与磁盘原始字节直接逐字节比较；函数可按完整范围核对哈希，图标对象的虚表指针则需要核对相应的模块 RVA。

## 可选元数据与能力边界

- **应用主动隐藏状态：** `ModifyIcon` 接收到 `NIF_STATE`、`NIS_HIDDEN` 时调用可见集合移除路径；没有确证独立、可读取的 `applicationHidden` 字段。promoted 与 overflow 集合指针分别位于 manager `+0x18 / +0x20`，但集合归属不能替代该字段。返回值应为未知，并保留实时协议已确认的状态。
- **图标句柄：** 本机 `SetIcon` 的实现字段 `+0xC8` 保存的是 `SoftwareBitmap` 对象引用。`INotificationAreaIcon::get_Icon` 取得它后通过虚表执行 AddRef，再作为接口返回。不得将它作为 HICON 传给 `CopyIcon`。另一个 HICON 缓存不保证随图标更新刷新，详见下文。
- **提示文字：** 资料中的可选 `metadata.tip` 保存图标实现的 HSTRING 字段及 getter/setter 证据。读取采用 `WindowsInspectString`，不把另一进程的 HSTRING 交给本进程的普通字符串 API。header 长度与 buffer 偏移保留作证据，不作为静默替代路径。
- **悬停提示模式：** 独立的 `metadata.tooltipMode` 保存提示模式的原始状态字段和 getter 的最低协议版本。它可以在文字不可读取时单独返回；未知模式保留已有协议观测，不覆盖其值。
- **系统显示集合：** 可选 `metadata.placement` 保存两个 manager 字段、容器接口和底层数组布局。它与 `applicationHidden` 是独立属性，不根据分区位置改写应用主动隐藏状态。

资料保持 `schema: 1`。`metadata` 缺失时，登记身份、回调和版本仍可按原有路径读取，分区、文字和提示模式保持未知。每个可选部分列出自己的 `requiredFunctions`。整份资料的磁盘身份、结构或磁盘哈希失败仍拒绝模型；加载态的 12 个核心符号失败也拒绝模型。加载态的可选证据和数据则分组检查，某组函数、虚表、数据读取失败或前后不一致时，该组独立退回未知。`capabilities.placement`、`tip` 和 `tooltipMode` 表示已有匹配资料；`applicationHidden`、`icon` 和 `nativeInvocation` 仍为 false。这些能力标记与安装后验证结果分开记录。

## 补充离线取证：显示集合与文字

### 可见状态的语义

`NotificationAreaIconManager2::IsIconCurrentlyVisible`（RVA `0x4F1A0`，346 字节）先在 manager `+0x18` 的集合按身份查找，再查 `+0x20`，任一包含即返回 true。`RemoveIconFromVisibleCollection`（`0x4E4D8`）分别从两者删除；`OnIconPromotionChanged`（`0x133A70`）先确认 currently visible，再删除、重新插入。这为“当前是否属于系统显示集合”提供了独立证据。

分区状态使用 `Promoted`、`Overflow`、`NotVisible`、`Unknown`。其中 `NotVisible` 仅表示当前未进入两个系统显示集合。折叠区成员属于系统的可见集合，不代表它此刻已经绘制到屏幕。`NotVisible` 也不能直接断言应用设置了 `NIS_HIDDEN`；中途修改、删除、状态检查等路径均须结合稳定快照区分。读取失败、集合异常或前后不一致时应保留未知，不能据此隐藏图标。

### 容器类型与接口位置

两个显示集合的完整实现类型为 `winrt::impl::convertible_observable_vector<NotificationAreaIcon, std::vector<NotificationAreaIcon>, winrt::impl::single_threaded_collection_base>`。该类型名称来自本机 PDB；[微软 C++/WinRT 固定提交中的定义](https://github.com/microsoft/cppwinrt/blob/30ac599beedc03d0f38f5fc84c390ee2f8551204/strings/base_collections_vector.h#L42)另行确认它同时提供 typed IObservableVector、IVector 和 IVectorView，并保存底层容器。下表的偏移来自本机指令，未根据模板源代码推测。

设 `C` 为容器实现对象，`P` 为 manager 字段保存的接口指针：

| 内容 | 当前映像的关系 | 指令证据 |
| --- | --- | --- |
| manager 两个字段 | `+0x18`、`+0x20` | 构造函数 `0x131314` 两次调用同一个 factory |
| 保存的接口 | `P = C + 0x10`，typed IObservableVector | factory `0x130E4C` 分配 `0x88` 字节，构造后返回实现 `+0x10` |
| typed IVector | `C + 0x18 = P + 8` | 构造函数 `0x130ECC` 将 `C + 0x18` 传给 typed IVector producer；`get_Size` 与 `GetAt` 均将接口 this 减 `0x18` |
| 底层 vector begin / end / capacity | `C + 0x70 / +0x78 / +0x80`，亦即 `P +0x60 / +0x68 / +0x70` | 构造函数移动三个容器指针；`get_Size`（`0x6B070`）按 `(end-begin)/8` 计算大小 |
| 数组元素 | 8 字节 `NotificationAreaIcon` 接口指针 | `GetAt`（`0x674D0`）及 `vector_view_base::GetAt`（`0x67520`）读取一个指针并 AddRef |
| 元素对应的图标实现 | 当前插入路径为 `图标实现 + 0x10` | `AddIconToVisibleCollection`（`0x4F300`）明确将此地址交给 typed `InsertAt` |
| IObservableVector 虚表 | RVA `0x1C6178`，64 字节、8 槽 | producer 构造链 `0x13123C → 0x13114C → 0x79DE0` 写入该表 |
| typed IVector 虚表 | RVA `0x1CFCE8`，144 字节、18 槽 | producer 构造链 `0x13126C → 0x13117C → 0x130FEC` 写入该表；槽 6、7 分别为上述 `GetAt`、`get_Size` |
| 图标默认接口虚表 | RVA `0x1C7FD8`，120 字节、15 槽 | 本机 `produce<NotificationAreaIcon2, INotificationAreaIcon>` 符号；槽 6 为提示文字 getter，槽 8 为图像 getter |

这些关系支持精确匹配当前系统后的只读集合解析，不需要在指针附近扫描接口。`metadata.placement` 的容器接口和数组偏移均相对 `C`；先从 manager 保存的 `P` 减去 `observableInterfaceOffset`，然后解析其余字段。每个数组元素减去 `itemInterfaceOffset` 后还必须与全登记集合中的实现对象一致。

三个完整虚表均按 PDB 相邻边界和各接口方法数交叉核对，所有槽都有 PE 重定位项且指向 Taskbar 执行节。资料只保存它们的 RVA、完整长度和磁盘 SHA-256；远端比较需要将磁盘每个指针按实际加载基址换算。集合边界、图标身份和前后快照也要一致，不能只看到合理数量就接受。容器为单线程实现；离线证据没有提供与 Explorer 修改操作同步的锁或原子快照。

### HSTRING 长度与公开读取入口

`SetTooltip`（`0x1BF2C`）计算传入 UTF-16 文字长度，调用 `hstring` 构造函数（`0x1C2EC`），再用 `attach`（`0x1C78C`）替换图标实现 `+0xA0` 的句柄。构造函数调用 `precreate_hstring_on_heap`（`0x1C67C`），后者将传入字符数写入 header `+4`（DWORD），将 buffer 指针写入 `+0x10`，并在 buffer 的第 length 个 UTF-16 单元写终止零。`hstring::c_str`（`0x1C744`）读取同一个 buffer 字段。这与[微软固定提交的 hstring 定义和分配代码](https://github.com/microsoft/cppwinrt/blob/30ac599beedc03d0f38f5fc84c390ee2f8551204/strings/base_string.h#L50)一致。

文字读取优先使用微软公开的 [WindowsInspectString](https://learn.microsoft.com/en-us/windows/win32/api/winstring/nf-winstring-windowsinspectstring)：它专门接受另一个地址空间中的 HSTRING，由调用方的读取回调提供字节，返回长度和目标 buffer 地址。[回调](https://learn.microsoft.com/en-us/windows/win32/api/winstring/nc-winstring-pinspect_hstring_callback)参数中的 length 是读取字节数，返回值为 HRESULT；不能把它与输出的字符串长度混淆。跨 32/64 位地址宽度可使用 [WindowsInspectString2](https://learn.microsoft.com/en-us/windows/win32/api/winstring/nf-winstring-windowsinspectstring2)。生成资料的离线检查没有调用这些 API。

无论使用 API 还是已核实布局，后续实现仍须设置长度与读取总量上限，检查指针和乘法溢出，复制 UTF-16 数据后再次核对句柄、长度和 buffer 地址。HSTRING 不可变并不等于其旧句柄永远有效；`attach` 会释放被替换的字符串。上述前后检查只能排除可观察的不一致，不能提供持有对象引用或暂停修改所能提供的寿命保证。

### 悬停提示模式

`NotificationAreaIcon2::AppOwnsToolTips`（RVA `0x69224`，43 字节）读取图标实现 `+0xE0` 的单字节状态。仅当该状态非零且 `+0x94` 的协议版本不小于 4 时返回 true。默认接口的 `get_AppOwnsToolTips`（`0x691F0`，43 字节）将接口 this 减去 `0x10` 后调用它，返回一个字节的布尔值；其虚表槽为 7。

`ModifyIcon` 的 `0x4C77C–0x4C787` 根据请求 flags 的 `NIF_SHOWTIP` 位写入 `+0xE0`，值为该位的逻辑反。写入发生在每次已接受的修改路径，包括仅更新图标的请求，并非只发生在 `NIF_TIP` 或 `NIF_SHOWTIP` 存在时。因此不能从“已取得提示文字”推断提示由谁显示，也不能只在有文字标志的更新中维护模式。

`metadata.tooltipMode.field` 为 224，`minimumVersion` 为 4；它要求两个 getter 和既有 `modify` 函数的证据。读取器返回原始 `ShowStandardTip = (byte[+0xE0] == 0)`，版本判断继续由界面应用，不能把 getter 已经结合当前版本计算出的 false 当成原始状态。这样在版本 0 读取后收到 `SETVERSION 4`，仍能正确选择提示方式。运行时模式读取独立于 HSTRING 成功与否；它不更改回调地址、协议版本或系统显示集合。

## 进程访问诊断

进程访问失败会在 `tray-status.json` 的 `nativeModelOwnerAccessFailures` 中保存最多 16 条去重记录，包含窗口、PID、TID、窗口类、请求权限和系统错误码。读取器仍要求查询与等待权限（`0x101000`），以便持续持有进程句柄并确认存活；失败后另尝试一次最小查询权限（`0x1000`），仅记录能否读取身份及前后窗口归属是否一致。对照结果不用于放行图标，也不保存路径、窗口标题或提示文字。

[`WaitForSingleObject`](https://learn.microsoft.com/en-us/windows/win32/api/synchapi/nf-synchapi-waitforsingleobject) 要求句柄具有 `SYNCHRONIZE` 权限；查询创建时间可使用 [`PROCESS_QUERY_LIMITED_INFORMATION`](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getprocesstimes)。[`GetExitCodeProcess`](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getexitcodeprocess) 返回的 259 也可能是应用实际退出码，不能据此替代存活证明。诊断将其保留为原始数值。

preview.22 正常启动时，两项 NVIDIA 后台进程对上述两种访问均返回错误 5。后续可研究微软公开的 [`SystemBasicProcessInformation`](https://learn.microsoft.com/en-us/windows/win32/api/winternl/nf-winternl-ntquerysysteminformation)：Windows 11 build 26100.4770 起提供用于识别 PID 复用的 `SequenceNumber`，[固定 SDK 头文件](https://github.com/microsoft/win32metadata/blob/64ed9177fb76eb244fc9051c74b8a93fdfe20873/generation/WinSDK/RecompiledIdlHeaders/um/winternl.h#L270) 明确枚举值为 252。本机文件版本符合门槛，但尚未调用验证它是否返回这两个候选进程。

该方案需要显式区分创建时间与进程序号，按候选 PID 保留最小信息并在读取前后核对；不能将序号填进 `ProcessStarted`，或让未知创建时间落入宽松的窗口存在判断。初期操作应通过同一 Explorer 会话中的真实托盘控件，继续核对控件唯一性、范围、登记代际及提交前身份；这只能覆盖控件实际支持的默认动作。原生图像消息也需同步增加明确的身份类型。上述路径尚未实现，不能据此声称右键、自定义悬停、关闭的溢出区或受限进程已兼容。

## 补充离线取证：当前图像

`SetIcon`（`0x4CAE0`）通过 `NotifyIconUtilities::TryGetSoftwareBitmapFromHicon`（`0x4CDBC`）取得位图并更新图标实现 `+0xC8`。转换函数的本机符号和调用链明确使用 `ISoftwareBitmapNativeFactory` 与 `SoftwareBitmap`；当前代码检查/转换至 BGRA8、预乘 alpha。`get_Icon`（`0x1BEB0`）返回带 AddRef 的对象，其入口也调用 `FailFastIfNotUIThread`。

图标实现 `+0x138` 确实另有 HICON：`SetIcon` 在它为零时调用 `USER32!CopyIcon`，并由 `DestroyIcon` 资源策略管理。但字段非零时，后续更新跳过该复制。因此这个句柄不能作为词脉等应用实时数据图标的可靠来源。直接读取 `+0xC8` 并调用 `CopyIcon` 则会把 COM 指针误当句柄。

图像读取路径在所属线程取得有效的 `SoftwareBitmap` 引用，核对实际尺寸、像素格式、alpha，再用公开的 [CopyToBuffer](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.imaging.softwarebitmap.copytobuffer?view=winrt-26100) 复制为有界像素数据供 Dock 使用。[SoftwareBitmap](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.imaging.softwarebitmap?view=winrt-26100) 标注 Agile 和 Both，可支持持有有效引用后的线程使用；这不意味着另一个进程取得的裸指针可直接调用。图像实现和独立线程、引用证据见 [原生托盘图像](native-tray-images.md)，宿主仅接收带身份和序号的像素，不调用远端裸指针。

最初的补充资料保存在被忽略的 `publish/checks/tray-model/metadata-disassembly.txt`、`metadata-code-evidence.json`、`metadata-vtables.json`，只读分析脚本为同目录的 `inspect-metadata.py`。已确认的分区和文字字段及其证据链现已纳入正式生成器；图像使用独立生成器，原生动作仍只作后续研究。所有资料绑定本页同一份 PE/PDB；生成过程不执行 COM、进程读取或 UI 验证。

## 原生操作入口的边界

本机公开符号中存在 `Clicked(bool, Point const&)`（`0x145998`）、`ContextRequested(bool, Point const&)`（`0x145E50`）、`DoubleClicked` 和 `MouseInteracted`。前两个方法的入口均直接调用 `NotifyIconUtilities::FailFastIfNotUIThread`（`0x1C2B8`），然后经过 `MoveMouseCursorIfNecessary` 和异步调用路径。

这证明存在比 UI Automation 更底层的原生动作实现，也证明不能从任意后台线程直接调用这些地址。实例获取、所属线程、引用寿命、布尔参数语义、异步完成和系统更新适配仍需单独解决。发布资料没有提供可调用的函数指针，也没有启用原生动作能力。

## 生成与校验

生成器为 [`scripts/Inspect-TrayModel.py`](../scripts/Inspect-TrayModel.py)，依赖 `pefile` 和 `capstone`。本次使用 `pefile 2024.8.26`、`capstone 5.0.9`。可指定已经存在的解码器目录，不需要改变全局 Python 环境：

```powershell
python scripts/Inspect-TrayModel.py --download --decoder-path '<包含 capstone 的目录>'
```

已有精确 PDB 时使用 `--pdb '<PDB 文件>'`，省略 `--download`。默认输出目录为 `publish/checks/tray-model`，已被 Git 忽略。生成器只接受已审阅映像的完整 SHA-256；系统更新后需要重新分析，不自动采用相近版本偏移。

当前 profile 约 10 KB，包含 45 个符号。分区、文字和提示模式的 30 个新增必需函数累计 3,852 字节，全部符号范围合计 7,069 字节。生成器核对完整构造链、字段读写、getter/setter、可见性查找、三个接口虚表及其方法槽；发布文件中没有函数字节。运行时还须限制集合数量、字符串长度、累计读取字节和取消时间，这些限制由读取器独立执行。

输出包括：

- `Taskbar-DAB69A79FEE033F0D81E023FBF172B511.json`：版本身份、布局、符号 RVA/长度/哈希和能力边界。
- `profile-code-evidence.json`：本地完整函数字节、符号名称及补充证据。
- `profile-disassembly.txt`、`supporting-disassembly.txt`：本地反汇编，包含字段交叉验证、图像类型及原生动作线程限制。

发布使用 [`src/WinDock/Profiles/NativeTray/Taskbar-DAB69A79FEE033F0D81E023FBF172B511.json`](../src/WinDock/Profiles/NativeTray/Taskbar-DAB69A79FEE033F0D81E023FBF172B511.json)。该文件不包含微软函数字节、PDB 或系统文件副本。运行时仍须核对磁盘映像身份、加载模块身份和完整函数哈希，并对跨进程快照做前后身份与集合一致性检查。离线生成成功不能代替这些检查。

## 公开参考

- [Tray Order Lock：枚举当前 overflow 集合](https://github.com/Yusseter/tray-order-lock/blob/f8734634232017789eef198c965ceb87c75bbb93/tray-order-lock.wh.cpp#L2137)。其 `ITaskbarModel6` 路径通过 Explorer 内部钩子取得实例；构造时建立的 64 位设置身份映射不包含 callback/version。
- [SnowDesktop：符号和指令布局](https://github.com/FreeFallingSnow/SnowDesktop/blob/81e4e4f81d1dcbb47068de5dbfce490168389063/src/taskbar_hook/tray_modern_adapter.h)、[当前模型读取](https://github.com/FreeFallingSnow/SnowDesktop/blob/81e4e4f81d1dcbb47068de5dbfce490168389063/src/system/tray/tray_modern_bootstrap.h)。该仓库使用 [GPLv3](https://github.com/FreeFallingSnow/SnowDesktop/blob/81e4e4f81d1dcbb47068de5dbfce490168389063/LICENSE)；此处参考其研究方向和符号名称，布局值由本机文件重新取证，未引入其读取器实现。
- [TrayPilot：验证原生图像对象和身份](https://github.com/HCLonely/TrayPilot/blob/596699db6e8e45ca238b5ce8adcba8d70bea06cb/source/native/ApplicationModelImages.h#L123)。此参考也从原生图像接口取图，未把接口指针当作 HICON。
- [微软 PDB 匹配规则](https://github.com/microsoft/microsoft-pdb/blob/805655a28bd8198004be2ac27e6e0290121a5e89/PDB/dbi/pdb.cpp#L808)、[LLVM PDB Info stream](https://llvm.org/docs/PDB/PdbStream.html)、[DBI stream](https://llvm.org/docs/PDB/DbiStream.html)。
