# 整窗飞入、飞出动画选型

日期：2026-10-28。本文保留方案研究阶段的依据与限制；后续实现已进入 `0.6.0-preview.7`，见 [原生组件说明](../native/WinDock.Motion/README.md) 和 [使用说明](native-window-animation.md)。实际桌面效果尚未验证。

## 推荐方案

选定方案为 **保留 DWM 原有动作和画面寿命，在目标窗口自身的时间线中改整窗路径**。本机静态分析已识别普通显示、销毁、最小化与恢复的调用分支和销毁画面的释放链。主题数据层存在动作限制，单改其返回值不足以完成目标；实施方向落在原生时间线的构造与最终目标几何。保持窗口形状，控制位置、缩放、透明度和总时长。预览版已实现四条普通动作路径，但尚无实际运行结果证明视觉效果。

若原有参数无法表达所需的飞入、飞出，再考虑修改真实 DWM 图元的整窗变换。Wobbly Windows 提供这一层的源码依据，但不应作为最先全量重写动画引擎的理由。

同时规定一个明确的转向条件：如果无法在匹配的系统模块上取得“首次有效画面显示前的控制权”和“关闭前最后画面的保留权”，或不能完整交还原生显示状态，就转向 **应用内前置拦截 + 独立 GPU 呈现**。不继续用进程外事件通知后补播的方式修补首尾时序。

两条路线使用同一套动作定义、Dock 图标位置与轨迹参数，一次过渡只由一个后端控制。DWM 后端与应用侧后端是可替换方案，不叠加运行两套动画。

参考优先级：本机匹配的 DWM 符号及主题动画读取接口用于确定原生过渡参数入口；Wobbly Windows 用于补充 DWM 对象、场景线程和变换操作；Windows Animations 用于研究应用侧动作拦截、状态归属和恢复；MacOS Minimize Animation 仅补充收放的几何及兼容性案例。不会直接把任一完整 Mod 当作最终实现。

## 本机离线分析新增的依据

本机匹配的 `uDWM.pdb` 中存在 `CTopLevelWindow3D::StartAnimation`、`CreateTimelineForTransform`、`BeginDestroySprite`、`RegisterAnimationCompletedHandler`，以及 `CWindowList::QueueDestroySprite` / `DestroyPendingSprites`、`CTransitionWindowSnapshot::Create`、`CAnimatedTransitionVisual::SetEndRect` / `SetEndAlpha` / `OnStoryboardEnd` 等公开符号。它们为原生生命周期与参数研究提供了比晃动项目更直接的候选入口。PDB 中没有可用类型记录，因此不能据名称猜测类布局或私有函数 ABI。精确匹配依据见同目录的 [原生动画研究记录](native-animation-research.md)。

本机 `uDWM.dll` 的导入表还确认其使用 `GetThemeAnimationProperty`、`GetThemeAnimationTransform`、`GetThemeTimingFunction`。微软文档说明这些公开函数用于读取动画属性、变换和节奏；它们本身不提供修改系统动画的接口。可研究的是 DWM 内部特定消费者或时间线构造处的改写，不能把读取 API 当成直接设置 API。[动画属性](https://learn.microsoft.com/en-us/windows/win32/api/uxtheme/nf-uxtheme-getthemeanimationproperty)、[动画变换](https://learn.microsoft.com/en-us/windows/win32/api/uxtheme/nf-uxtheme-getthemeanimationtransform)、[节奏函数](https://learn.microsoft.com/en-us/windows/win32/api/uxtheme/nf-uxtheme-getthemetimingfunction)

后续静态分析已把普通显示、销毁、最小化、恢复对应到本机 raw type 与 storyboard/target，详见本文后半部分的映射表。首次启动、托盘显示、× 隐藏和特殊过渡仍不能仅凭这个表归类。不得全局改写所有主题动画返回值，也不能据符号名就认定某个关闭图元可以无限延长寿命。

## 参考项目对四类动作的覆盖

本轮核对 Windhawk Mods 固定提交 `bc98c1452d77b3ef8abfcf840d61acb4f18d9240`，涉及 Wobbly Windows 0.115、Windows Animations 1.3.7、MacOS Minimize Animation 3.2.0。

| 动作 | Wobbly 源码已确认 | 应用内拦截已确认 | 尚未解决的关键问题 |
| --- | --- | --- | --- |
| 首次打开 | 可访问真实整窗图元、过渡图元和 DWM 时间线；未证明首次可见前完整接管 | 两个动画 Mod 都在部分显示 API 前准备隐藏，再调用原生显示，随后取得画面并开始动画 | 首个窗口显示不等于 GPU 内容已完成；固定等待和 `PrintWindow` 成功不构成有效首帧证据 |
| 点击 × 后关闭 | 析构钩子主要清除对象关联；没有保留关闭图元并播放自定义退场的完整实现 | 普通主窗口在所属线程的 `DestroyWindow` 入口动画；隐藏到托盘另拦截 `SW_HIDE` 等路径 | 应用可能在 `DestroyWindow` 前已销毁内容；特殊关闭消息路径还需保证保存、取消与重入逻辑 |
| 最小化 | 原生状态过渡入口存在；现有 Wobbly 主要避免其晃动与最小化相争 | 原生最小化调用前准备画面与首帧等待；Windows Animations 另有原生状态提交屏障 | 首帧等待超时不应被当作已呈现；最小化完成与动画消失需要对应同一次操作 |
| 恢复 | 可以绑定正在进行的过渡图元，但现成项目主动抑制任务栏恢复的晃动 | 先准备隐藏，执行原生恢复，再展开独立画面并交还真窗口；含最大化恢复与反向处理 | 缓存过期、跨屏与 DPI 变化、真实窗口最终重绘及边框交接仍需明确处理 |

### Wobbly：有图元控制证据，未实现四类生命周期替换

Wobbly 的 `WindowTransitionChangeHook` 在排入晃动后继续调用原函数；`TopLevelWindow3DStartAnimationHook` 同样保留原始动画，不解释 `animationType`。`AdvanceTimelinesHook` 在 DWM 场景线程提交及重新绑定变换。这证明可以随系统合成更新真实图元，不能证明已经替换四类动画。[过渡钩子](https://github.com/ramensoftware/windhawk-mods/blob/bc98c1452d77b3ef8abfcf840d61acb4f18d9240/mods/wobbly-windows.wh.cpp#L1051)、[时间线钩子](https://github.com/ramensoftware/windhawk-mods/blob/bc98c1452d77b3ef8abfcf840d61acb4f18d9240/mods/wobbly-windows.wh.cpp#L3422)

它把 4×4 弹簧模型拟合为二维仿射矩阵，再交给 `CMatrixTransformProxy::Update` 与 `CVisualProxy::SetTransform`。从几何表达能力推断，这足以研究保持矩形的整窗移动与缩放；原生图元在大范围位移、缩到 Dock 图标、裁剪和透明背景上的表现尚未验证。现成晃动代码把缩放限制在 0.1–2.0，不能直接复用这组限制。[整窗绑定](https://github.com/ramensoftware/windhawk-mods/blob/bc98c1452d77b3ef8abfcf840d61acb4f18d9240/mods/wobbly-windows.wh.cpp#L3169)、[矩阵拟合](https://github.com/ramensoftware/windhawk-mods/blob/bc98c1452d77b3ef8abfcf840d61acb4f18d9240/mods/wobbly-windows.wh.cpp#L5315)

销毁钩子清除 `CWindowData` / 顶层窗口映射，并继续析构；最小化生命周期清除待处理过渡并抑制任务栏恢复的晃动。这些代码可参考对象寿命处理，但不提供关闭最后帧保留或 Dock 收放。[销毁路径](https://github.com/ramensoftware/windhawk-mods/blob/bc98c1452d77b3ef8abfcf840d61acb4f18d9240/mods/wobbly-windows.wh.cpp#L2327)、[最小化路径](https://github.com/ramensoftware/windhawk-mods/blob/bc98c1452d77b3ef8abfcf840d61acb4f18d9240/mods/wobbly-windows.wh.cpp#L6372)

本机匹配符号提供候选名称，后续指令分析进一步核对了部分动作和释放链。Wobbly 本身没有完整交付这四类替换；静态调用链也不构成改写后效果和稳定性已经成立的证据。

### Windows Animations：优先参考其动作协调，替换其捕获假设

普通关闭由 `DestroyWindow_Hook` 在所属线程调用原始销毁函数之前进入；这比全局看到 × 或 `WM_CLOSE` 就先退场更接近应用已经决定关闭的时点。原始销毁失败而窗口仍存在时，代码撤销标记、解除隐藏并恢复过渡。隐藏到托盘通过调用方拥有的原始隐藏路径完成，不自行把应用当作退出。[销毁入口](https://github.com/ramensoftware/windhawk-mods/blob/bc98c1452d77b3ef8abfcf840d61acb4f18d9240/mods/windows-animations.wh.cpp#L13653)、[关闭协调](https://github.com/ramensoftware/windhawk-mods/blob/bc98c1452d77b3ef8abfcf840d61acb4f18d9240/mods/windows-animations.wh.cpp#L11692)

但特殊类在 `DefWindowProcW` 中拦截并重发 `WM_CLOSE` / `SC_CLOSE`。这一分支不能自动推广为所有程序都保留关闭取消语义；需要单独适配，不能直接照搬。[特殊关闭分支](https://github.com/ramensoftware/windhawk-mods/blob/bc98c1452d77b3ef8abfcf840d61acb4f18d9240/mods/windows-animations.wh.cpp#L13810)

首次打开采用显示前准备隐藏、原生显示、所属线程 `PrintWindow` 快照、工作线程过渡。它检查了所属线程、首次显示、窗口尺寸等条件，并提供失败恢复；其准备阶段仍会临时改 layered alpha，工作线程仍有约 60 毫秒的兼容性等待。尺寸过滤主要处理 ApplicationFrameHost 的临时小框，不能说明 GPU 内容完整。我们应保留“前置准备、原始调用仍由原线程执行、明确失败恢复”的结构，不能把这些捕获和等待假设原样采用。[启动准备与快照](https://github.com/ramensoftware/windhawk-mods/blob/bc98c1452d77b3ef8abfcf840d61acb4f18d9240/mods/windows-animations.wh.cpp#L13045)、[启动工作线程](https://github.com/ramensoftware/windhawk-mods/blob/bc98c1452d77b3ef8abfcf840d61acb4f18d9240/mods/windows-animations.wh.cpp#L14006)

最小化和恢复具有方向重定向、原生提交屏障、窗口身份标记、最大化恢复保护及失败清理，适合参考。其呈现实现本身仍是独立画面层。首帧事件和有限等待存在，不应把“工作线程已启动”或“等待已结束”当成唯一的成功条件。[显示 API 拦截](https://github.com/ramensoftware/windhawk-mods/blob/bc98c1452d77b3ef8abfcf840d61acb4f18d9240/mods/windows-animations.wh.cpp#L13397)、[首帧等待](https://github.com/ramensoftware/windhawk-mods/blob/bc98c1452d77b3ef8abfcf840d61acb4f18d9240/mods/windows-animations.wh.cpp#L11503)

### MacOS Minimize Animation：不作为主要生命周期基础

其关闭同样优先在实际销毁处进入，特殊类使用关闭消息重发；首次打开在显示前 cloak 或改 alpha，工作线程先等待 60 毫秒，再最多 30 次等待其他 cloak 解除。取得快照时依赖 `PrintWindow`，前台关闭还会使用屏幕 `BitBlt`。这些做法不能保证首帧内容完整，也不能把“前台窗口”直接等同于无遮挡的窗口画面。[启动准备](https://github.com/ramensoftware/windhawk-mods/blob/bc98c1452d77b3ef8abfcf840d61acb4f18d9240/mods/macos-minimize-animation.wh.cpp#L2353)、[启动等待](https://github.com/ramensoftware/windhawk-mods/blob/bc98c1452d77b3ef8abfcf840d61acb4f18d9240/mods/macos-minimize-animation.wh.cpp#L2891)、[捕获路径](https://github.com/ramensoftware/windhawk-mods/blob/bc98c1452d77b3ef8abfcf840d61acb4f18d9240/mods/macos-minimize-animation.wh.cpp#L2170)

最小化等待首帧的调用有 200 毫秒上限，随后仍返回启动成功；渲染使用 `UpdateLayeredWindow`。它对灯神形变的参考价值高于对可靠生命周期接管的参考价值。本目标首先是清晰的整窗飞入飞出，故不优先选该项目作为基础。[等待与返回](https://github.com/ramensoftware/windhawk-mods/blob/bc98c1452d77b3ef8abfcf840d61acb4f18d9240/mods/macos-minimize-animation.wh.cpp#L2240)、[呈现路径](https://github.com/ramensoftware/windhawk-mods/blob/bc98c1452d77b3ef8abfcf840d61acb4f18d9240/mods/macos-minimize-animation.wh.cpp#L1355)

## 模块与接口边界

以下是拟议接口职责，不是已实现功能或微软现有 API。

| 模块 | 输入与输出 | 边界 |
| --- | --- | --- |
| Dock 位置提供者 | 应用身份 → 图标屏幕物理坐标、显示器、DPI、布局代号 | 仅提供起点和终点；不隐藏、销毁或改变应用窗口 |
| 原生过渡参数适配器 | 已确认动作、storyboard/target、原始参数 → 本次过渡的改写参数 | 优先保留系统生命周期；只处理明确匹配的窗口动作，不修改无关主题动画 |
| 窗口生命周期适配器 | 原生动作与窗口身份 → 打开、关闭、最小化、恢复请求 | 保留所属线程、调用返回值、错误码、关闭取消和后台存活语义 |
| 过渡协调器 | 动作请求、当前进度、资源能力 → 唯一过渡会话 | 去重、连续反向、取消、超时、清理与后端选择 |
| 画面持有者 | 真实合成图元或已完成的窗口帧 → 有明确寿命的可呈现画面 | 保留透明度、边界、色彩空间；窗口销毁不导致悬空资源 |
| 呈现后端 | 画面、轨迹、时间 → 首帧成功、逐帧更新、末帧成功或失败 | DWM 与独立 GPU 后端实现相同反馈约定；不代替应用作关闭决定 |

窗口身份至少包含 HWND、进程身份和操作代号；取消或换向后到达的旧回调不能恢复、隐藏或关闭新窗口。Dock 布局变化以新的图标坐标处理，不能借用 Windows 原任务栏的位置代替当前 Dock 图标。

## 必须约定的时序

- **首次打开：**显示前登记本次动作；允许应用完成自己的显示和绘制流程；获得属于该窗口的有效图元或有效帧后，提交飞入首帧；最后把显示权交回真实窗口。准备时间有上限，不能因等待画面长期阻塞应用 UI 线程。固定休眠、`PrintWindow` 返回成功或像素“不是全白”均不能单独证明内容就绪。
- **点击 ×：**先让应用处理保存、取消和关闭决定。普通路径在实际销毁或隐藏前协调退场；若应用已先释放绘制内容，需要更早持有的画面或原生关闭图元。没有有效内容就执行原始关闭，不播放空白窗口。隐藏到托盘后保留后台应用入口。
- **最小化：**获得画面并收到首帧成功反馈，才执行或承认本次原生最小化提交；提交失败则结束自定义显示并恢复原窗口。超时属于失败反馈，不能与成功共用一个含糊状态。
- **恢复：**核对实际恢复位置和最大化状态，准备起始画面，再完成展开；真实窗口的末帧、边框、阴影与裁剪准备好后才释放动画画面。连续点击从当前进度反向，避免回到起点重播。

## 后续阶段与转向条件

1. **离线适配核对。**沿已匹配符号和实际导入，查明四类动作与主题 storyboard/target、时间线构造、过渡图元创建及释放入口的关系。输出明确的“已找到、尚未找到”清单。符号缺失或签名不明时，不推测偏移启用。
2. **原生参数路线判定。**优先确认只改起止位置、缩放、透明度和节奏能否完成所需飞入飞出，并继续使用系统原有的快照和完成通知。如果参数表达不足，再研究真实图元变换；如果首次有效画面、关闭画面寿命和失败恢复无法说清，就转应用侧前置适配，不继续仅增加一个弹簧或缩放钩子。
3. **共同接口与最小效果。**进入实现阶段后，先只实现保持形状的平移缩放和短暂透明度变化；打开、× 退场、最小化、恢复分别有轨迹及语义。先解决画面和交接，再增加复杂形变。
4. **应用侧兼容后端。**若采用该路线，参考 Windows Animations 的动作协调结构，重新解决有效帧取得、资源寿命与呈现反馈。可用的 GPU 捕获方式、隐藏状态下是否持续产帧、色彩与 alpha 正确性需要单独证据，不能仅把 GDI 换成 GPU 就宣称问题已解决。
5. **效果确认。**后续实际桌面体验由用户操作。当前仅有源码与静态推断，尚不能声称首帧无闪烁、四类动作覆盖完成或本机兼容性通过。

此选型不要求把所有兼容路径一次做齐。它要求每个启用的路径都准确报告能力：能完整接管时才应用自定义动画；缺少画面或失去显示控制时，及时交还系统行为。

## 2026-10-28 补充：公开动画协议与本机动作映射

本节更新前文“四动作对应关系尚未确认”的研究状态。新增证据包括微软公开 SDK、WinUI 消费者源码和本机匹配模块的离线反汇编；仍无运行、注入或桌面测试结果。

### SDK 定义了什么

固定核对 `microsoft/win32metadata` 提交 `64ed9177fb76eb244fc9051c74b8a93fdfe20873`。`Uxtheme.h` 的注释明确指向 `vsanimation.h` 的 storyboard 和 target 定义；其中公开 `ANIMATIONS` 类只列出 1–36 的通用效果，如展开、淡入、弹出层、页面过渡。`vssym32.h`、`vsstyle.h` 和 `vsanimation.h` 未给出 DWM 顶层窗口打开、关闭、最小化、恢复的四个专用编号；在微软公开源码检索中也未找到 `CTopLevelWindow3D::WindowAnimationType` 的枚举定义。这是此次检索边界，不能据此声称任何地方均无定义。[公开 storyboard](https://github.com/microsoft/win32metadata/blob/64ed9177fb76eb244fc9051c74b8a93fdfe20873/generation/WinSDK/RecompiledIdlHeaders/um/vsanimation.h#L15)、[读取协议](https://github.com/microsoft/win32metadata/blob/64ed9177fb76eb244fc9051c74b8a93fdfe20873/generation/WinSDK/RecompiledIdlHeaders/um/Uxtheme.h#L137)

例如，`TAS_SHOWPOPUP=18` 不能直接解释为“打开桌面应用”。可交叉核对的是 `TAS_CROSSFADE=34`，其 target `1` 为 incoming、`2` 为 outgoing；本机 raw type 15 → 34/1、14 → 34/2 与这个公开定义相符。94、95、100、101、104 超出这份公开头文件的编号范围，本文只记录数字和实际调用路径，不为其杜撰 SDK 常量名。[交叉淡化定义](https://github.com/microsoft/win32metadata/blob/64ed9177fb76eb244fc9051c74b8a93fdfe20873/generation/WinSDK/RecompiledIdlHeaders/um/vsanimation.h#L216)

| 公开协议 | 能表达的内容 | 边界 |
| --- | --- | --- |
| `TA_PROPERTY` | flags、transform count、stagger delay/cap/factor、z-order | 没有直接传入窗口位置或改系统动画的 setter |
| `TA_TRANSFORM` | 变换类型、timing id、开始时间、持续时间、flags | 每个 transform 有自己的时间；延长其中一项须与原生总时长和完成时点一致 |
| `TATT_TRANSLATE_2D=0` / `TATT_SCALE_2D=1` | `TA_TRANSFORM_2D` 中的 X/Y、initial X/Y、origin X/Y | 字段存在不保证消费者读取所有字段，坐标系和单位须按消费者确定 |
| `TATT_OPACITY=2` | 初始与目标透明度 | 需要保留窗口本身的 alpha，不能替换成不透明白底 |
| `TATT_CLIP=3` | 初始与目标四边裁剪值 | 本机下述顶层窗口消费者不接受此类型，不能靠它解决所有遮挡问题 |
| `TA_CUBIC_BEZIER` | 两个控制点，供 timing id 对应的节奏函数使用 | 仅有读取 API；按公共 timing id 全局替换会影响无关动画 |

字段及数值来自 [SDK 变换定义](https://github.com/microsoft/win32metadata/blob/64ed9177fb76eb244fc9051c74b8a93fdfe20873/generation/WinSDK/RecompiledIdlHeaders/um/Uxtheme.h#L171) 和 [时间函数定义](https://github.com/microsoft/win32metadata/blob/64ed9177fb76eb244fc9051c74b8a93fdfe20873/generation/WinSDK/RecompiledIdlHeaders/um/Uxtheme.h#L266)。头文件将开始时间注明为毫秒；微软 WinUI 实际消费者将开始和持续时间按毫秒计算，再转换为 100 纳秒单位，支持这一时间解释，但不能据此直接假定 DWM 私有 `InterpolationParameters` 的布局。[WinUI 时间转换](https://github.com/microsoft/microsoft-ui-xaml/blob/5a610a98e9c92546632bf0a71b723f19035f4d3e/dxaml/xcp/dxaml/lib/ThemeGenerator.cpp#L112)

### flags 是消费者协议，不能只替换浮点数

`TATF_TARGETVALUES_USER=1`、`TATF_HASINITIALVALUES=2`、`TATF_HASORIGINVALUES=4` 是公开位值。微软 WinUI 的 `CreateTranslate2DTimelines` 要求 flags 恰为 `TARGETVALUES_USER`，拒绝 initial/origin flags，随后从调用方 helper 的起止 offset 建立 X/Y 关键帧，没有读取 `TA_TRANSFORM_2D.rX/rY`。其缩放路径会读取 initial、target、origin；透明度路径会读取 initial/target opacity。这是“由使用者提供目标几何”的直接源码例子，说明不能从字段名推出 DWM 将无条件使用字段值。WinUI 与 DWM 是不同消费者，不能把前者的全部限制照搬给后者。[平移消费者](https://github.com/microsoft/microsoft-ui-xaml/blob/5a610a98e9c92546632bf0a71b723f19035f4d3e/dxaml/xcp/dxaml/lib/ThemeGenerator.cpp#L441)、[缩放与透明度消费者](https://github.com/microsoft/microsoft-ui-xaml/blob/5a610a98e9c92546632bf0a71b723f19035f4d3e/dxaml/xcp/dxaml/lib/ThemeGenerator.cpp#L498)

公开三个读取函数均不接收 HWND：动画属性和变换仅用 theme/storyboard/target，节奏函数只用 theme/timing id。因此，按编号全局改写读取结果无法可靠确定当前 Dock 图标或目标窗口。若选此切入点，必须先由已验证的窗口动作入口建立有寿命和重入规则的上下文；优先考虑在已有 `this` 和本次动作语义的 DWM 参数构造处处理。[公开函数签名](https://github.com/microsoft/win32metadata/blob/64ed9177fb76eb244fc9051c74b8a93fdfe20873/generation/WinSDK/RecompiledIdlHeaders/um/Uxtheme.h#L255)

### 本机已识别的四动作路径

以下 raw type 是 `uDWM.dll 10.0.26100.9949` 的逐分支静态解读，绑定 SHA-256 `de3a7bacdcbb49a98bfff41010e772d18a329b77d9bc6b2416e1bc9f5b415863`，不作为跨版本枚举。符号匹配与原始记录见 [本机研究记录](native-animation-research.md)；离线指令证据在仓库上一级 `work/native-motion-symbols/udwm-selected-disassembly.txt`、`udwm-lifecycle-disassembly.txt` 和 `udwm-presentation-disassembly.txt`。

| 动作路径 | raw type → storyboard/target | 已找到的调用关系 | 仍需保留的限制 |
| --- | --- | --- | --- |
| 普通显示 | 1 → 100/1 | `ShowWindow(true, …)` 的普通分支调用 `StartAnimation(1)`；另有延迟分支 | 普通显示不等同于所有程序首次启动；有效首帧、延迟显示和托盘重新显示仍需分别识别 |
| 销毁退场 | 6 → 101/1 | `BeginDestroySprite` 检查后调用 `StartAnimation(6)`；成功可转为待销毁路径 | 这是实际销毁路径，不能代替应用决定是否关闭；隐藏到托盘是另一个动作 |
| 最小化 | 3 → 94/1 | `OnWindowStyleUpdated` 识别 `WS_MINIMIZE` 变为真后调用 `StartAnimation(3)` | 调度条件、原生图元和最终目标矩形仍由 DWM 管理 |
| 从最小化恢复 | 5 → 95/1 | `WS_MINIMIZE` 变为假后登记延迟标志，`ValidateVisual` 满足条件后调用 `StartAnimation(5)` | 需要保持延迟验证与真实恢复位置的关系，不能改成收到事件便立即播放 |

`GetAnimationIds` 的映射函数位于本机 RVA `0xc1c4`；以上动作语义来自调用它之前的状态分支，不能只靠数字或函数名推定。销毁路径在符合条件时可进入 `SnapshotWindow`、`CommitDwmChannel` 与 pending 集合；完成后可沿 `ValidateVisual` → `StopAnimation` → `QueueDestroySprite` → `DestroyPendingSprites` → `ImmediateDestroySprite` 释放。这是保留原生退场画面与清理链的具体依据，仍未证明修改后对取消、反向和异常同样正确。

必须把“点击 ×”进一步区分为真正销毁与隐藏到托盘。`ShowWindow(false)` 同时存在直接隐藏并停止动画的普通路径、以及 raw type 8 的延迟隐藏路径；raw type 6 只对应销毁图元，不能据此宣称微信等应用的 × 已被覆盖。隐藏动作要保留应用后台存活、托盘入口和再次显示语义，不能强行转成销毁。

### 参数路线的实际限制与下一步接口

1. **本机消费者只接受 translate/scale/opacity。**`IsTransformSupported`（RVA `0xdecc`）只接受类型值 0–2。SDK 的 clip=3 在这里不生效；圆角、阴影、祖先裁剪和飞行范围的遮挡需沿实际图元树另查，不能承诺添加 clip transform 就能修好。
2. **打开和关闭不能直接套最小化的平移。**`StartAnimation` 的 translate 分支（RVA `0xd02c`）要求 `TARGETVALUES_USER`，对 raw type 3/5 有专门几何计算；其他路径在 `0xd43c` 进行动作限制，已核对 raw type 1/6 会经 `0xdb53` 和公共错误退出返回 `E_INVALIDARG`。因此，给打开或关闭的主题返回值追加平移 transform 在此分支不可行。后续应核对原生 timeline 构造的可扩展位置；不能把 type 1/6 假装成 type 3 来绕过限制，因为它们的生命周期和完成行为不同。
3. **Dock 锚点应对应最终几何。**最小化和恢复先取得 `GetWindowMinimizeRect`，又经过 `GetFinalMinRect`（RVA `0x9ed4`）按宽度、窗口宽高比及设置重新计算目标位置。直接把入口矩形换成 Dock 图标矩形仍可能产生偏移。候选接口应明确提供“最终可见窗口矩形”，再按已核对的坐标转换接入，避免两次缩放和偏移。
4. **几何能力与生命周期分开判定。**平移加等比缩放足以从数学上描述保持矩形的整窗飞行，透明度负责起止衔接；这不证明四类动作都能通过现有主题参数表达。优先保留 raw action、原生画面和完成处理，在本次窗口的参数生成或 `CreateTimelineForTransform` 附近补足几何。必须继续核对各属性编号、起止值、复合顺序、总时长、停止及完成后清理，才可确定具体修改点。
5. **子时间线与总时间必须同步。**本机 `StartAnimation` 对 transform 的 `dwDurationTime` 求最大值，再用它创建主时间线或调用主时间线的 `Restart`。只延长某一条平移或缩放子时间线，可能仍被原总时长触发的结束链提前清理。时长适配必须同时处理子轨道和主时间线；变换的 `dwStartTime` 与这个最大值计算的实际关系也需保持本机语义，不能自行假定主时长就是所有起始时间与持续时间之和。

当前选型进一步收窄为：**最小化/恢复优先研究最终目标矩形与已有时间线；打开/销毁优先研究在原生生命周期中补充或调整整窗时间线。**保留“原生无法满足效果时转向应用侧”的备选。下面固定拟实施的接口与运动约定；私有接口仍须按本机版本完成构造和场景提交边界的核对，不能承诺只改主题参数就能达到所需效果。

## 拟实施的接口与运动约定

### 动作归属

| 动作 | 拟接入的位置 | 保留的原生责任 | 自定义部分 |
| --- | --- | --- | --- |
| 首次打开 | 普通显示的 type 1 构造过程，叠加 Dock 启动记录和新窗口身份判断 | 应用创建、绘制、原生显示条件与图元建立 | 从对应 Dock 图标到真实窗口位置的整窗轨道 |
| 销毁关闭 | type 6 的原生退场构造过程 | 应用先决定保存/取消/关闭，DWM 保留及释放退场画面 | 独立的短距离飞出与淡出轨道 |
| 最小化 | type 3 的最终几何和时间线构造 | 最小化状态、原生过渡图元和完成清理 | 飞向本应用 Dock 图标，替换原任务栏落点 |
| 恢复 | type 5 的延迟验证与时间线构造 | 真实恢复位置、最大化状态与原生末端交接 | 从图标展开到真实窗口位置 |
| 关闭到托盘 | 独立识别 hide；不能归入 type 6 | 应用继续后台运行，托盘及下次显示行为 | 只有能够控制隐藏前画面与原生隐藏提交时才接入退场 |

首先在目标 `CTopLevelWindow3D` 动作入口建立一次过渡的上下文，再适配该对象的参数生成。上下文包含窗口身份、动作、操作序号、源/目标矩形、已有透明度、轨道时长与图标位置的布局版本；嵌套调用需要上下文栈，不能用一个全局“当前窗口”。不改 raw action 来复用其他动作的代码分支。

DWM 内只读取已准备好的本地参数快照；不等待 WinDock 界面线程、不请求网络、不在每帧读取文件。找不到图标、布局信息已失效、窗口身份改变或没有可用原生画面时，在接管前保留原行为。开始之后发生失败则按该后端清理，不能在画面已经消失后另补一段覆盖动画。

原生函数不是已验证的事务接口。`StartAnimation` 出错时未证明会回滚之前创建的所有子轨道；`CreateTimelineForTransform` 替换轨道前还会释放旧对象。因此参数和资源条件应在第一次改变轨道前完成核对；发生部分修改后的恢复必须核对引用寿命并有独立实现，不能直接调用旧指针或把错误返回当作回滚完成。进入待销毁队列也不能替代画面可用检查，因为原生快照失败后仍可能进入该队列。

### 整窗几何

坐标统一为屏幕物理像素，允许副屏的负坐标。源矩形以实际参与动画的 DWM 可见边界为准；不能未经换算把客户端矩形、窗口逻辑坐标和含阴影边界混用。

设原窗口中心为 `Cw`、宽高为 `w/h`，Dock 图标内部目标矩形中心为 `Cd`、宽高为 `dw/dh`，目标等比缩放为 `s0 = min(dw/w, dh/h)`。保持比例，不能把宽窗口强行压成正方形。窗口内任意原始屏幕点 `q` 的目标模型为：

```text
q(t) = C(t) + s(t) × (q - Cw)
```

打开/恢复时 `C(t)` 从 `Cd` 到 `Cw`，`s(t)` 从 `s0` 到 1；最小化方向相反。位置与缩放可以各用平滑曲线，但开始、结束时刻一致。原生适配层负责把这个模型转换为实际平移、缩放原点和边框偏移，不能把模型坐标直接写入未知字段。

`GetFinalMinRect` 会继续加工入口矩形，故最终接入点应位于它完成换算后，或有明确的逆变换。只拦截 `GetWindowMinimizeRect` 返回图标 RECT 不符合这个约定。

### 透明度与收尾

透明度使用 `windowAlpha × motionAlpha(t)`，保留应用自身透明区域、原生边框和阴影。最小化的大部分行程保留窗口内容，只在接近图标时短暂淡出；打开/恢复完成后严格回到原始透明度和窗口位置。区域与 alpha 必须由同一次原生过渡共同提交，不再由 WinDock 界面计时器分两步掩盖源窗口。

× 退场与最小化采用不同运动：拟采用轻微下移、等比收缩和末段淡出；最小化则飞向图标。具体距离、时长与淡出区间属于可调参数，当前未确定视觉验收值，也没有把默认数值当作性能测量结果。

所有平移、缩放、alpha 子时间线与主时间线共享明确的结束时刻。需要末段才淡出时，应在已确认支持分段或曲线表达的位置实现，不能假定本机消费者会使用主题 `dwStartTime`。同样不能只在子时间线中延长持续时间而保留原主时长。

`StopAnimation` 既可在到期时调用，也会在新动作打断旧动作时调用；其回调不能单独当作“最后一帧已显示”的凭据。需要区分正常完成、中途取消与窗口销毁，按当前操作序号清理。快速反向应从当前可见位置和缩放继续；如果尚不能可靠取得原生当前状态，就不能宣称连续反向已实现，也不能从固定起点重播并称其平滑。

### 原生方案的质量边界

本轮静态证据足以支持以原生时间线为首选开发方向，仍未证明可直接发布。开发中的关键判定是：补足轨道能否在原生首帧提交前完成、结束时能否保留系统原有交接、原生图元在目标缩放下是否保持完整边界。

如果普通四类动作中的某类无法达到这些条件，就针对该动作转向更适合的呈现方案，停止给有缺陷的原生路径叠加补丁。首帧内容、圆角阴影、跨屏落点、反向与取消关闭的实际体验由用户操作确认；当前方案没有代替这部分结果。
