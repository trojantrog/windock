# 原生托盘登记快照

更新日期：2026-10-29。

WinDock 在托盘连接后读取一次当前登记信息，此后最多每 30 秒补全一次。它用于找回 Dock 启动前已经存在、又没有重发完整注册请求的图标。原有的图标修改、删除和消息采集继续运行；快照只补缺失信息，不能代替应用的实时协议。

实现参考 Chromium 的 [ITrayNotifyWin8 声明和短时订阅](https://github.com/chromium/chromium/blob/8d2dccf401a294e106d6ec9614dead0b567f7608/chrome/browser/ui/views/status_icons/status_tray_state_changer_win.cc)。只使用带订阅标识的接口，不使用可能占用其他程序回调位置的旧接口，不调用偏好、隐藏或排序设置。启用前检查系统注册的 COM 服务、代理及当前 Explorer 来源；遇到用户级替换注册时停止这条读取路径。

注册返回后，独立 STA 线程继续分派回调消息：首条最多等待 1 秒，收到回调后等待 150 毫秒静默，整个接收阶段最多 3 秒；取消会唤醒等待并停止接收。这样同时处理注册期间和注册返回之后到达的记录。接收结束后先封闭接收器、取消订阅，再核对记录。实现依据是 [hoverdock 修复提交](https://github.com/thomasboyle/hoverdock/commit/1a97df5bbb0dd339bd36d0313d0da3511c4ddedd)、其 [STA 消息泵源码](https://github.com/thomasboyle/hoverdock/blob/1a97df5bbb0dd339bd36d0313d0da3511c4ddedd/src/SystemTray.cpp#L2034) 和微软的 [STA 消息分派要求](https://learn.microsoft.com/en-us/windows/win32/com/single-threaded-apartments)。这说明延后回调是一条需要处理的路径，尚不能证明它就是本机此前零条目的唯一原因。

Explorer 回调期间只复制文字、图标和窗口标识，并通过本地窗口查询过滤已经失效的历史窗口。退出订阅后，在独立线程核对窗口线程、进程创建时间、真实可执行文件路径和图标存在性，并丢弃相互冲突的 GUID 归属。同步 Shell 查询不会在 Explorer 回调内执行，以免阻塞原生托盘。

可执行文件路径可能采用“已知文件夹 GUID＋相对路径”。对此使用 `SHGetKnownFolderPath` 的 `KF_FLAG_DONT_VERIFY` 解析前缀，再与实际进程路径比较；不扫描文件、不创建目录、不在解析失败时用进程路径替代登记证据。[路径解析参考](https://github.com/thomasboyle/hoverdock/blob/1a97df5bbb0dd339bd36d0313d0da3511c4ddedd/src/SystemTray.cpp#L1937)、[微软接口](https://learn.microsoft.com/en-us/windows/win32/api/shlobj_core/nf-shlobj_core-shgetknownfolderpath)

不带 GUID 的图标按窗口和图标 ID 查询存在性后可以补入。GUID 查询会忽略窗口和 ID，因此历史快照不能独立证明它当前属于哪个应用：首次发现的 GUID 记录不会创建新图标，只给实时协议已经确认关联的同一项补显示信息。仅有历史记录的 GUID 图标仍可能缺失。[微软标识约定](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/ns-shellapi-notifyiconidentifier)

读取全局最多一个工作线程在途，调用方等待最多 6 秒。超时不会额外创建替代线程，断线或注册变化时不会应用过期结果。取消订阅失败后，本次 WinDock 运行期间停止再次订阅，防止积累失效监听。记录数量最多 512 项。

诊断分别记录收到回调、复制记录、身份核对通过的数量，不记录原始路径或提示文字。零条目区分为未收到回调（`no_callbacks`）或没有记录通过过滤（`filtered`）；只有取得有效记录才报告 `available`。调用成功或零条目均不能当作找回图标已经生效。

快照包含显示信息和登记关联，**没有点击回调、协议版本及应用隐藏状态**。这些字段只由真实捕获的协议事件确认；折叠区偏好不会被当作 `NIS_HIDDEN`。初次补入的图标可能仍需等待状态更新。未知点击协议只可尝试[原生默认操作](native-tray-actions.md)支持的范围，不能据此宣称右键、输入法或所有应用已完整适配。

接口属于 Windows 的未公开实现，后续系统更新可能使读取不可用。注册信息核对和源码编译不能替代真实应用验证；原生任务栏继续保留。该功能不改动输入法配置、不读取 Windows 通知中心内容，也不改变系统托盘显示偏好。
