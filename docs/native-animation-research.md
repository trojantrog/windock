# 整窗飞入、飞出动画的技术路线

研究日期：2026-10-28。本文保留源码与系统文件分析的依据。后续已开发 `0.6.0-preview.7`，实现见 [原生组件说明](../native/WinDock.Motion/README.md)；尚未启用组件或验证桌面效果。

## 目标与结论

目标是清晰、连续的整窗飞入和飞出，并分别处理首次打开、点击 ×、最小化与恢复。效果优先；修改 Windows 原生动画是候选路线，不能因为采用 DWM 内实现就认定效果最好。

主方案为：保留 DWM 原有动作与画面寿命，在目标窗口自身的原生时间线中调整整窗位置、缩放、透明度和总时长。本机静态分析已对应到普通显示、销毁、最小化与恢复的实际调用分支；打开和销毁存在主题平移限制，预览版因此直接替换原生时间线。四类普通动作已写入实现，实际效果尚未验证，应用内前置适配仍保留为后备方向。

## 已核对的项目

| 项目 | 实际实现 | 对整窗飞入、飞出的价值与边界 |
| --- | --- | --- |
| Wobbly Windows 0.115 | 在 `dwm.exe` 中修改真实窗口和过渡视觉对象的矩阵变换，配合 DWM 场景线程和时间线 | 提供原生窗口图元接管依据；现成功能是拖动、缩放、贴靠和最大化晃动，未实现完整首次打开、关闭与 Dock 收放替换 |
| OpenGlass | 在 DWM 内修改玻璃、窗框、标题等渲染 | 提供私有符号、对象投影、版本适配与恢复设计参考；项目目标不包含完整窗口飞入飞出 |
| DWMBlurGlass | 自定义模式介入 DWM 绘制背景，系统背景模式使用公开接口 | 可研究 DWM 集成；其交叉淡化参数用于背景画刷，不能据此认定可定制窗口开关动画 |
| Windows Animations | 拦截应用侧窗口动作，使用独立画面层；部分路径用 D3D11 和 DirectComposition | 覆盖多种开关与收放效果，适合作为效果和生命周期处理的参考；不属于替换 DWM 内原生动画 |

### Wobbly Windows：真实合成图元，不是现成的开关动画替换

固定仓库提交为 `bc98c1452d77b3ef8abfcf840d61acb4f18d9240`。源码声明 `@include dwm.exe`、`amd64` 和 `GPL-3.0-only`。代码通过 `CCompositor::CreateProxy<CMatrixTransformProxy>`、`CMatrixTransformProxy::Update` 和 `CVisualProxy::SetTransform` 把矩阵作用到真实窗口与过渡图元；本文件没有创建动画覆盖窗口或调用 `UpdateLayeredWindow` 的实现。[源码与许可](https://github.com/ramensoftware/windhawk-mods/blob/bc98c1452d77b3ef8abfcf840d61acb4f18d9240/mods/wobbly-windows.wh.cpp)

它挂接 `CWindowList::WindowTransitionChange`、`CTopLevelWindow3D::StartAnimation`、`CDesktopManager::AdvanceTimelines` 等私有入口。`WindowTransitionChangeHook` 排入晃动工作后继续调用原函数；`TopLevelWindow3DStartAnimationHook` 继续执行原始动画并刷新已存在的晃动对象，没有解码各类 `animationType`。因此，准确描述是“随原生过渡修改真实视觉对象”，不能称为“已替换打开和关闭动画”。[过渡入口](https://github.com/ramensoftware/windhawk-mods/blob/bc98c1452d77b3ef8abfcf840d61acb4f18d9240/mods/wobbly-windows.wh.cpp#L1051)、[场景时间线](https://github.com/ramensoftware/windhawk-mods/blob/bc98c1452d77b3ef8abfcf840d61acb4f18d9240/mods/wobbly-windows.wh.cpp#L3422)

其 4×4 弹簧结果最终拟合成整窗二维仿射矩阵，现有缩放值限制在 0.1–2.0。几何上，平移与缩放足以表达保持矩形的整窗飞入、飞出，这是从矩阵能力作出的推断；复杂的灯神弯曲需要更丰富的形变路径。现成晃动参数不能直接覆盖从大窗口缩到一个 Dock 图标的全部尺寸范围。[矩阵生成](https://github.com/ramensoftware/windhawk-mods/blob/bc98c1452d77b3ef8abfcf840d61acb4f18d9240/mods/wobbly-windows.wh.cpp#L5315)

首次打开时如何在第一张有效画面显示前接管、关闭时如何在图元释放前保留最后画面，仍缺少完整实现证据。该项目的析构钩子主要清除对象映射；最小化生命周期处理会取消竞争的晃动，并主动抑制任务栏恢复时的状态晃动。这些行为证明它没有直接交付所需的四类动画。[对象销毁处理](https://github.com/ramensoftware/windhawk-mods/blob/bc98c1452d77b3ef8abfcf840d61acb4f18d9240/mods/wobbly-windows.wh.cpp#L2327)、[最小化与恢复处理](https://github.com/ramensoftware/windhawk-mods/blob/bc98c1452d77b3ef8abfcf840d61acb4f18d9240/mods/wobbly-windows.wh.cpp#L6372)

### OpenGlass 与 DWMBlurGlass：可参考底层，不应误认功能

OpenGlass 固定提交 `39e710bd71ad597d82891f5cb01af15f6b3a6654`，GPLv3。README 声明支持 Windows 11 26H2（26300）正式版本，但没有单独证明本机补丁版本适配；该声明是作者支持范围，不是本机验证结果。其服务源码包含 DWM 模块加载和初始化完成确认，可参考生命周期管理。[支持范围与许可](https://github.com/ALTaleX531/OpenGlass/blob/39e710bd71ad597d82891f5cb01af15f6b3a6654/README.md)、[服务实现](https://github.com/ALTaleX531/OpenGlass/blob/39e710bd71ad597d82891f5cb01af15f6b3a6654/OpenGlass/GlassService.cpp)

DWMBlurGlass 固定提交 `e3a160c849c35415a4b959462815eebc6870270b`，LGPLv3。`CreateCrossFadeBrush` 与 `CreateCrossFadeAnimation` 在背景画刷之间插值；它们不能证明项目已经控制整窗打开、关闭的路径。[项目范围](https://github.com/Maplespe/DWMBlurGlass/blob/e3a160c849c35415a4b959462815eebc6870270b/README.md)、[背景交叉淡化源码](https://github.com/Maplespe/DWMBlurGlass/blob/e3a160c849c35415a4b959462815eebc6870270b/DWMBlurGlassExt/Backdrops/DCompBackdrop.hpp)

Windows Animations 的原生函数拦截与独立渲染层是另一条路线。应比较它的实际画面质量、首尾交接和窗口兼容性，不应因使用 DirectComposition 就把它称为 DWM 原生动画替换。[固定提交源码](https://github.com/ramensoftware/windhawk-mods/blob/bc98c1452d77b3ef8abfcf840d61acb4f18d9240/mods/windows-animations.wh.cpp)

## 本机适配依据与待确认项

本轮只读记录：系统标记为 26H2、`26300.9711`；`dwm.exe`、`dwmcore.dll`、`udwm.dll` 的文件版本均为 `10.0.26100.9949`。需要按实际模块及匹配 PDB 核对私有入口、函数签名和对象布局，不能只凭“支持 26H2”的文字启用。

原生路径首先需要明确以下问题：

1. 四种动作各自使用哪个过渡入口，如何区分首次显示、隐藏到托盘、真正销毁、最小化和恢复。
2. 第一张有效画面和关闭图元的保留、释放时机，能否在同一合成流程中控制。
3. 自定义路径如何与原生变换组合或替换，避免两套动画相互叠加。
4. 真实窗口、过渡视觉对象、圆角、阴影、裁剪和透明背景是否能统一变换。
5. 失败或禁用后是否能完整恢复；DWM 更新或符号不匹配时是否能拒绝启用。

建议顺序：优先评估 DWM 内整窗平移缩放能否连续完成四类动作；若其生命周期接管或版本维护无法满足质量要求，再评估应用内同步拦截配合 GPU 画面层。任何路线都以无白屏、无明显首尾闪烁、窗口内容清晰、位置准确为目标。现阶段没有运行证据，不能认定哪条路线已经达到目标。

GPL/LGPL 项目可用于学习；若直接复用代码并分发，需要按相应许可规划组件与源码发布方式，不能将 GPL 实现直接当作现有 MIT 项目的代码使用。

## 本机离线符号与导入表核对

2026-10-28 继续核对本机 `uDWM.dll`，读取 PE 调试标识后，从微软公共符号服务器取得对应 PDB；只解析磁盘文件，没有附加、注入或暂停 DWM 进程，没有调用这些私有函数。[微软符号服务器说明](https://learn.microsoft.com/en-us/windows-hardware/drivers/debugger/microsoft-public-symbols)

| 项目 | 实际读取结果 |
| --- | --- |
| DLL 文件版本 | `10.0.26100.9949` |
| DLL SHA-256 | `de3a7bacdcbb49a98bfff41010e772d18a329b77d9bc6b2416e1bc9f5b415863` |
| PE/PDB GUID | `28cf5a3b-d2bc-549f-2f4e-4efcb7fbf89c` |
| PE Age / PDB Info Age / DBI Age | `1 / 2 / 1` |
| PDB SHA-256 | `22574dcfc82ae997196d7c0ddeddbf4386c87e3940cc3a2675046a617bfcd894` |
| PDB 大小与公共符号数 | 2,469,888 字节；9,699 个公共符号 |
| 类型信息 | TPI 流仅 56 字节，没有类型记录；不能从该 PDB 直接取得私有对象布局或枚举取值 |

PDB 匹配采用微软 `PDB1::OpenValidate5` 的规则：GUID 相同，PDB Info Age 不小于映像 Age，DBI Age 与映像 Age 相同。公开 PDB 的 Info Age 为 2 不表示它与 Age 为 1 的映像不匹配；这里的 DBI Age 为 1。[微软验证代码](https://github.com/Microsoft/microsoft-pdb/blob/master/PDB/dbi/pdb.cpp)

匹配符号中实际存在以下候选入口：

| 用途候选 | 找到的符号 | 当前能够确认的范围 |
| --- | --- | --- |
| 原生动画调度 | `CTopLevelWindow3D::StartAnimation`、`StopAnimation`、`GetAnimationIds` | 函数名称和公开签名存在；后续反汇编已识别本机普通四类路径，见下节 |
| 窗口变换时间线 | `CTopLevelWindow3D::CreateTimelineForTransform`、`CDesktopManager::AdvanceTimelines` | 具有进一步核对时间线的具体入口；未调用或改写 |
| 过渡创建与结束 | `CWindowList::StartTransition`、`EndTransition`、`WindowTransitionChange` | 存在原生过渡处理路径；各参数的实际语义仍待核对 |
| 关闭图元的寿命 | `CTopLevelWindow3D::BeginDestroySprite`、`CWindowList::QueueDestroySprite`、`DestroyPendingSprites` | 可追查图元排队销毁；不能据名称推断可任意延迟关闭 |
| 原生过渡画面 | `CTransitionWindowSnapshot::Create`、`CAnimatedTransitionVisual::SetEndRect`、`SetEndAlpha`、`OnStoryboardEnd` | 可追查过渡画面与目标几何；尚未证明能保留并控制某次关闭的最后一帧 |
| 动画完成通知 | `CTopLevelWindow3D::RegisterAnimationCompletedHandler`、`UnregisterAnimationCompletedHandler` | 完成回调入口存在；对象寿命、线程与回调约定尚未验证 |

PE 导入表同时确认 `uDWM.dll` 引用 `GetThemeAnimationProperty`、`GetThemeAnimationTransform` 和 `GetThemeTimingFunction`。微软文档说明它们分别读取主题动画属性、变换及时间函数；它们是读取接口，不是设置系统动画的接口。导入关系也不能证明所有开关动画均经由这些接口。[动画属性](https://learn.microsoft.com/en-us/windows/win32/api/uxtheme/nf-uxtheme-getthemeanimationproperty)、[动画变换](https://learn.microsoft.com/en-us/windows/win32/api/uxtheme/nf-uxtheme-getthemeanimationtransform)、[时间函数](https://learn.microsoft.com/en-us/windows/win32/api/uxtheme/nf-uxtheme-getthemetimingfunction)

这使原生路线的研究重点更明确：优先核对 DWM 原有过渡中读取和调度动画参数的环节，评估能否仅为目标窗口改写平移、缩放、透明度和时间参数，并沿用其画面与生命周期管理。这个方向属于基于符号和导入表的工程推断，尚无运行证据。它优先于继续给原生动画叠加 Wobbly 变换，也不要求修改磁盘系统文件或关闭主题签名检查。

需要排除三种误判：找到名称不等于签名和调用约定已验证；读取动画参数的接口不等于通用修改接口；显示在场景中的视觉对象不等于应用已经完成第一张有意义的内容画面。

本地证据保存在工作目录的 `native-motion-symbols/udwm-metadata.json` 与 `udwm-public-motion-symbols.txt`。PDB 和离线分析脚本均保留在临时研究目录，没有纳入应用、安装包或运行中的 WinDock。

## 本机静态调用链与实施边界

继续读取同一份已匹配 DLL 的机器指令，用 PE 异常目录确定函数范围、PDB 公共符号定位函数，离线解码 x64 指令并沿直接分支核对。没有加载目标 DLL、执行其私有函数或读取运行中 DWM 的内存。分析工具 Capstone 5.0.9 仅放在 `work/native-motion-tools`，来源为项目官方指向的 PyPI 二进制包；下载地址与包哈希记录在 `capstone-install-source.json`。[Capstone 官方说明](https://www.capstone-engine.org/lang_python.html)、[包来源](https://pypi.org/project/capstone/5.0.9/)

下列 RVA 是这一映像的相对地址，用于复核指令；不能直接当作其他系统补丁的函数地址或通用对象布局。

### 四类普通路径已经对应到实际分支

| 动作路径 | 本机 raw type | storyboard / target | 指令依据 |
| --- | --- | --- | --- |
| 普通显示 | 1 | 100 / 1 | `CTopLevelWindow3D::ShowWindow` 普通显示分支在 `0x12f7f` 传入 1，于 `0x12f87` 调用 `StartAnimation` |
| 销毁时退场 | 6 | 101 / 1 | `BeginDestroySprite` 在条件满足时于 `0x129b4` 传入 6，于 `0x129bc` 开始动画 |
| 最小化 | 3 | 94 / 1 | `OnWindowStyleUpdated` 检查 `0x20000000`（`WS_MINIMIZE`）的变化；进入最小化分支于 `0xc390` 传入 3 |
| 从最小化恢复 | 5 | 95 / 1 | 退出最小化分支登记延迟状态；`ValidateVisual` 满足条件后于 `0x8d844` 传入 5 |

`GetAnimationIds`（`0xc1c4`）给出上述数字映射；动作解释来自其上游状态分支。微软 SDK 的公开 `vsanimation.h` 未提供 94、95、100、101 的窗口常量名，故这里不为它们杜撰跨版本枚举名。[公开 storyboard 定义](https://github.com/microsoft/win32metadata/blob/64ed9177fb76eb244fc9051c74b8a93fdfe20873/generation/WinSDK/RecompiledIdlHeaders/um/vsanimation.h)

普通显示不等于“进程首次打开”：托盘重新显示、临时启动窗和其他可见性变化仍需按窗口身份、动作来源和原生状态区分。`ShowWindow` 还存在 raw type 7 的延迟显示；本节没有把这些特殊分支视作已经覆盖。

### 关闭画面有系统自身的保留与释放链

`BeginDestroySprite` 开始 type 6 成功后，可把输出布尔值设为 false，通知调用方暂缓立即销毁。`CWindowList::DestroySprite` 的相应分支设置待销毁状态；在没有已有快照且窗口不属于 immersive 类型的分支，调用 `CWindowData::SnapshotWindow`，成功时提交 DWM 通道，然后加入待处理集合。其他条件分支不保证走同样的快照路径。

动画到期后，`ValidateVisual` 可进入 `StopAnimation`；后者在待销毁标志存在时于 `0x14f7d` 调用 `QueueDestroySprite`。`DestroyPendingSprites` 再调用 `ImmediateDestroySprite` 完成释放。这说明可以研究保留系统现有退场画面的生命周期，尚不能证明改写之后所有应用、异常或中途反向都能正确处理。

两项不能混为成功条件：`SnapshotWindow` 返回失败也可经 `0x809b9` 跳到 `0x809e1` 加入待处理集合，队列存在不证明画面有效；`StopAnimation` 还会在恢复开始前用于中断旧动画（`0x8d819`），它的调用或回调不证明最后一帧已呈现。需要分别记录画面可用、自然结束、中途停止和实际呈现状态。

这里的画面由 DWM 的 `CSecondaryWindowRepresentation` 等内部对象提供。原生动画本身也可能使用系统内部的过渡画面；“原生接管”不意味着让已经被应用销毁的 HWND 继续绘制。拟议方案保留这一内部机制，不另外创建 WinDock 的截图覆盖窗口。

点击 × 后隐藏到托盘必须单独处理。`ShowWindow(false)` 有直接隐藏并停止动画的路径，也有 type 8 的延迟隐藏路径。它们不能冒充 type 6；微信等应用的后台进程、托盘状态与重新打开语义必须保留。静态映射没有证明此类 × 动作已能播放所需退场。

### 主题参数单独不足以完成整窗飞行

已确认 `StartAnimation` 内部真实调用三种主题读取函数，而不只是导入了名称：`GetThemeAnimationProperty` 位于调用点 `0xc9ce`，`GetThemeAnimationTransform` 位于 `0xca36` / `0xca97`，`GetThemeTimingFunction` 位于 `0xcaec` / `0xcb29`。但是消费方式存在以下约束：

- `IsTransformSupported`（`0xdecc`）仅接受类型值 0–2，对应平移、缩放、透明度；SDK 的 clip=3 不被这一消费者接受。圆角、阴影、祖先裁剪与跨屏可见范围仍需沿实际图元树处理。
- 平移分支（`0xd02c`）要求 `TATF_TARGETVALUES_USER`。type 3/5 使用专门的目标几何；其他动作经过类型限制，type 1/6 会走 `E_INVALIDARG` 返回。因此向打开/销毁的主题数据简单追加平移条目不可行。
- 不应把 type 1/6 改成 type 3 来避开限制。动作类型还参与透明度、时间、停止和释放的原生逻辑。
- 公开主题 API 没有 HWND 参数，按 storyboard 或 timing id 全局替换返回值会丢失当前窗口与 Dock 锚点上下文。

微软 WinUI 也提供一个可复核的消费者例子：其 `CreateTranslate2DTimelines` 要求调用方提供目标几何，读取 helper 的起止 offset，而不是任意读取 `TA_TRANSFORM_2D.rX/rY`。这能解释公开结构与实际消费者之间的区别，不能直接用来替代上述 DWM 指令证据。[微软消费者源码](https://github.com/microsoft/microsoft-ui-xaml/blob/5a610a98e9c92546632bf0a71b723f19035f4d3e/dxaml/xcp/dxaml/lib/ThemeGenerator.cpp#L441)

### 可继续实施的切入点

`CreateTimelineForTransform`（`0xbf2c`）按窗口对象与属性创建原生子时间线，`UpdateAnimatedResources` 读取这些时间线，交给 `CreateTransformMatrix` 和 `CVisual::SetOpacity`。从本机主题分支与消费关系已能识别 X/Y 平移属性 0/1、X/Y 缩放 6/7、透明度 9；它们仍是这一版本的内部编号，不是公开 API。

这把主方案从“改主题数据”推进到“按本次窗口动作改原生时间线”。保留原始 raw type、原生图元和停止处理，研究在原生构造阶段补足或重定向这些轨道。还需确认缺少某条轨道时的添加位置与首次场景提交边界，不能仅因为函数接受一个属性值就直接在任意线程调用。

两项必须一并解决：

1. **最终目标几何。**type 3/5 先调用 `GetWindowMinimizeRect`（调用点 `0xd355`），再调用 `GetFinalMinRect`（`0xd390`）。后者会按设置改变宽度、按窗口比例生成高度，并再次平移。仅把第一步的 RECT 替换为 Dock 图标 RECT，仍不能保证落点准确。应在最终几何确定处接入保持窗口比例的图标内目标矩形，并核对物理坐标、窗口边框、显示器偏移和 DPI。
2. **主时间线。**`StartAnimation` 对有效 transform 的 duration 求最大值，以此创建主时间线或调用 `Restart`（`0xcded`）。只延长子时间线，原主时间线仍可能提前触发清理；只缩短子时间线也可能留下无意义的等待。所有几何与 alpha 轨道必须共享同一时间约定，主时间线覆盖全部有效轨道。此消费者读取了 `dwDurationTime`，不能未经核对就假定它像 WinUI 一样完整使用 `dwStartTime`。

当前优先级：最小化/恢复接入最终目标几何；普通显示/销毁在原生构造阶段补足整窗时间线；隐藏到托盘独立适配。若缺少有效首帧、原生画面寿命或同一合成流程中的交接控制，按效果优先原则转向其他后端。已经发生了一半的原生动作不再临时补播第二层动画。

失败恢复同样是待完成的实现工作。`StartAnimation` 公共错误出口 `0xce1b..0xce46` 主要记录错误并清理临时缓冲区；没有从这段看到此前创建的全部子时间线被统一回滚。`CreateTimelineForTransform` 还会先释放同属性的旧轨道再创建新轨道。不能把 HRESULT 失败视作“原生状态已自动恢复”，也不能在失败后重新写回可能已释放的指针。所有参数须先完整核对；开始改变轨道后的恢复方式、引用寿命与可中断状态仍须实现专门契约。

### 仍不能从静态证据得出的结论

- 原生第一次显示并不证明应用内容加载完成；不能把“全白”判断当成通用的未就绪信号。
- 尚无证据证明边框、阴影、圆角与整窗在所有缩放比例和多屏组合下均无锯齿或裁切。
- 中途反向、连续点击、取消关闭、显示器变化及后台隐藏仍缺乏改写后的运行证据。
- 私有接口与对象关系绑定系统补丁。实际原生模块须拒绝未知版本，保持可禁用；若影响 DWM 稳定性，应停止使用该后端。当前没有加载此类模块。

离线记录位于仓库上一级 `work/native-motion-symbols/`：`udwm-selected-disassembly.txt`、`udwm-lifecycle-disassembly.txt`、`udwm-presentation-disassembly.txt`、`udwm-timeline-disassembly.txt`。复核助手为 `work/disassemble_native_motion.py`，分析依赖与系统符号均未进入应用依赖或安装包。
