# 登录启动状态

WinDock 的普通登录启动和整窗动画使用两个独立入口。普通程序通过当前用户的 `Run\WinDock` 启动；整窗动画由同一用户的 `WinDock.NativeMotion.<SID>` 计划任务启动。普通入口匹配当前安装且读取到允许启动的状态，当前动画设置所需的登录任务也就绪时，安装报告才将 `startupReady` 标记为 `true`。这一标记表示配置核对结果，不代替真实登录验证。

覆盖更新保留已有登录启动选择。普通启动项缺失时不创建；已登记的标准 WinDock 命令只迁移到当前固定安装位置 `D:\Projects\windock\WinDock.exe`，保留注册值类型及 Windows 批准记录。Windows 禁用、策略禁用和未知状态均不会被自动开启。更新前后的登记、批准原始记录和策略状态会再次核对；读取失败或更新期间发生变化时停止继续写入。完整操作见 [覆盖更新](updating.md)。

## 普通启动入口

Microsoft 文档规定 `Run` 在每次用户登录时处理，执行顺序不确定；Windows 可以延迟处理，安全模式默认忽略这些入口。配置存在不能证明程序已经成功运行。[Run and RunOnce Registry Keys](https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys)

Electron 的 Windows 实现分别检查 `Run` 与 `Explorer\StartupApproved\Run`，区分登记命令和 Windows 的启动状态。该实现识别全零记录与以 `02 00 00 00` 开头的启用记录，并用 `03 00 00 00` 表示禁用；其写入逻辑会删除批准记录以重新启用。[固定提交 7afc8d36 的读取代码](https://github.com/electron/electron/blob/7afc8d36e16759bd4353e54ba51adb6a85fe827c/shell/browser/browser_win.cc#L184-L259)、[写入代码](https://github.com/electron/electron/blob/7afc8d36e16759bd4353e54ba51adb6a85fe827c/shell/browser/browser_win.cc#L658-L692)

`StartupApproved` 的二进制格式没有公开的稳定 Windows API 合同。WinDock 只读取本应用同名值，接受已核对的 12 字节记录：状态 0 或 2 且后 8 字节为零时表示启用；状态 3 表示禁用。其他状态、类型、长度或读取失败都表示无法确认。缺少记录时只能确认 Run 登记，不能声称 Windows 已批准启动。该规则比 Electron 的字符串比较更严格，没有复用其删除批准值的行为。

Microsoft 的 ADMX_Logon 文档及本机 `C:\Windows\PolicyDefinitions\Logon.admx` 分别将计算机策略映射到 `DisableLocalMachineRun`，用户策略映射到 `DisableCurrentUserRun`，位于 `Software\Microsoft\Windows\CurrentVersion\Policies\Explorer`。文档说明两种策略都配置时计算机策略优先；显式的 DWORD 0 保留“已配置为允许”的含义，不当作缺失值。未知类型或值不推断为允许。[ADMX_Logon](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-admx-logon#disableexplorerrunlegacy_1)

设置页和托盘菜单会重新读取当前状态：已确认有效的启动项才显示为启用；已登记但状态未确认时保留不确定状态及简短说明。返回设置窗口、重新打开 Dock 设置或托盘菜单时刷新，不在后台持续轮询注册表。用户可以取消未确认的启动登记。

WinDock 不写入或删除任何 `StartupApproved` 值，也不修改启动策略。Windows 禁用时，重新登记命令后仍保留禁用状态，并提供“Windows 启动设置”入口，由用户在系统界面操作。该入口使用 Microsoft 公布的 `ms-settings:startupapps`。[Launch Windows Settings](https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-settings#apps)

## 整窗动画登录任务

安装脚本核对任务根目录中的准确名称、同一用户的登录触发器和主体、交互式登录及最高权限、唯一执行动作、工作目录和参数。程序路径必须与当前安装清单中的组件及登录程序哈希相符，指向管理员保护的缓存。任务存在、启用或曾运行过都不能代替当前组件匹配。

脚本默认只读取任务状态，已匹配的任务直接复用。新登记需要明确传入 `-EnableAnimationStartup`。脚本保留已禁用的任务、已禁用的触发器及限制启动的任务设置，不通过重新注册将它们自动开启。现有动作指向其他组件时，只有明确请求登记、经过正常管理员确认并重新核对成功，才标记当前动画登录任务已就绪。受保护缓存由提升后的登录程序自行验证文件和权限，普通安装过程不降低其访问限制。请求登记不会改写用户的 `nativeWindowAnimations` 设置。

明确请求登记后，管理员确认被取消、注册失败、读取失败或最终核对不匹配时，已安装的 Dock 保留，报告写为 `installedStartupIncomplete`，脚本退出码为 2。默认更新保留原有关闭或未登记状态，安装成功仍报告 `installed`；`startupReady=false` 单独说明登录启动尚未就绪。`animationLogin` 分别记录尝试结果、注册前后状态和是否存在仍可能执行的其他组件；旧任务不算作当前组件已经配置。

设置页不会把任务已登记等同于组件已经连接。尚未连接时，依据实际检查、重试次数和重试等待时间显示正在尝试、等待自动连接或尚未成功；仍需核对历史组件身份时单独说明状态未确认。检测到旧组件仍驻留时继续提示重新登录，不显示为正在恢复，也不额外弹出管理员确认。

## 安装报告

| 字段 | 含义 |
| --- | --- |
| `ordinaryStartup.registered` | 是否读取到 WinDock 的 Run 值；无法读取时为 null |
| `ordinaryStartup.matchesInstalledExecutable` | 命令是否准确指向本次安装目录 |
| `ordinaryStartup.windowsApproval` | enabled、disabled、unrecorded 或 unknown |
| `ordinaryStartup.runPolicy` | allowed、disabled、unconfigured 或 unknown |
| `ordinaryStartup.windowsAllowsStartup` | 已知允许、已知禁止或 null（无法确认） |
| `animationLogin.after.enabled` | 任务是否启用；无法读取时为 null |
| `animationLogin.after.triggerEnabled` | 唯一触发器是否启用；无法读取或有多个触发器时为 null |
| `animationLogin.after.matchesCurrentComponent` | 执行动作是否匹配当前受保护组件路径及参数；独立于启用状态，无法核实时为 null |
| `animationLogin.differentComponentMayRun` | 已核对的登录触发器是否可能执行其他组件；任务或触发器禁用时为 false，无法核实时为 null |
| `animationLogin.registered` | 当前任务的注册或复用和最终核对均成功 |
| `startupChoicePreserved` | 原有普通启动登记选择、Windows 批准记录和策略状态保持不变；标准命令可迁移到固定安装位置 |
| `startupAction` | 保留现状、保持未登记，或迁移原有命令路径 |
| `settingsPreserved` | 启动后核对所有已有设置值和环境文件未丢失或重置 |
| `userDataBackup` | 更新前完整用户目录的备份位置 |
| `installedUserResourcesPreserved` | 安装目录中已有 shortcuts 和 icons 文件保持原字节 |
| `installedUserResourceFilesVerified` | 核对的安装目录用户资源文件数 |
| `installedFilesBackedUp` | 本次将被替换的原文件与已有 shortcuts/icons 备份数量，不含源码仓库和历史发布目录 |
| `startupReady` | 普通启动和当前动画设置所需的登录任务均已核对就绪 |
| `loginCycleTested` | 是否实际完成登录验证；本次保持 false |

## 本机安装记录：preview.25

2026-10-29 07:48 将源码提交 `61de8be05248d847a00d96cf75231da948be55a8` 构建的 preview.25 安装到 `D:\WinDock`。构建零警告、零错误，发布包 21 个文件校验一致，Defender 扫描未发现威胁。程序仍为本地未签名预览版。

安装后核对普通启动命令为 `"D:\WinDock\WinDock.exe" --tray`，Windows 状态为 `enabled`，没有已配置的 Run 禁用策略。动画任务与登录触发器均已启用，唯一执行动作匹配当前 build 13 的受保护组件；直接复用已有任务，本次未申请新的管理员确认。报告的 `startupReady=true`、`differentComponentMayRun=false`，表示这两个启动入口已核对就绪。

Dock 已从安装目录正常启动，18 个固定应用保持不变，原生任务栏可见。当前会话仍驻留动画 build 10，build 13 尚未连接，等待下一次 Windows 登录。未执行登录周期、桌面交互或独立探针验证。上述配置核对不能预判后续策略变化、安全软件拦截或下次登录的真实执行结果。

正常启动产生的 `motion-status.json` 从 07:48:23 继续更新至 07:49:53；期间连接状态保持未连接、登录入口保持启用、动作计数均为零。这确认本次运行中未变化状态的记录仍持续更新，不表示整窗动画已生效。

安装前的程序、设置和动画任务备份位于 `publish/backups/install-20261029-074814-707`。当前固定安装位置已改为 `D:\Projects\windock`。需要回退程序时，把完整旧版本包放到仓库外，再运行 `scripts/Update-InstalledBuild.ps1 -PackagePath '仓库外的旧版本完整包目录'`；当前更新脚本保留已有设置，包括替换任务栏的选择，不替换本次会话已驻留的动画组件。
