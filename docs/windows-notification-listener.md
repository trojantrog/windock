# Windows 系统通知接入草案

`SystemNotificationReader` 是只读模块。运行程序具有 Windows 包身份时，托盘面板显示“读取系统通知”入口；用户点击后才请求权限和读取内容。当前发布目录没有相关包身份，也没有可安装的通知身份包，因此该入口不会显示；原生任务栏和 Windows 通知中心继续工作。

## 功能范围

Windows 的 `UserNotificationListener` 可读取用户授权后系统当前保留的应用通知。结果包含通知 ID、时间、应用标识、应用名称及通知提供的文字；模块最多返回最近 100 条，每条最多读取 10 段、每段 4096 个字符。无法读取的单条通知会跳过。文本只作为内存数据交给调用者，不写入日志、文件或网络。窗口或功能关闭时，调用方应清空自己持有的结果。

这些通知与应用使用 `Shell_NotifyIcon` 请求的气泡提示分别处理，不能当作应用的真实未读数。通知的图片、内嵌操作及点击激活尚未接入；原生 Windows 通知中心仍负责显示和操作，模块不删除系统通知，也不伪造通知点击。来源：[微软通知监听接口](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/notification-listener)。

## 接入与授权

1. 用户点击入口时，在界面线程创建模块并调用 `EnableAsync`。仅此显式调用可能触发系统通知访问授权；没有包身份时返回 `IdentityRequired`，不会请求授权。
2. 已授权后，由界面调用 `ReadAsync` 读取快照；读取前后都会重新检查权限。系统通知发生变化时，`Changed` 通知界面刷新。若事件订阅不可用，界面提示用户手动刷新。
3. 返回图标列表或关闭托盘面板时，停止监听并清空界面中的快照。用户也可在 Windows 的通知访问隐私设置中撤销授权；视图打开期间每 5 秒检查一次访问状态，并在发现撤销后清空内容。Windows 可能在权限撤销后返回空列表，因此每次读取都必须检查状态。

源码中的读取操作仅在用户启用后、视图可用时发生。授权被撤销、功能关闭或当前读取被取消后，旧的异步结果不会交给调用方。系统授权和 WinDock 功能开关是两个独立状态，关闭功能不会替用户修改 Windows 隐私设置。

## 包身份

`packaging/notifications/` 中的两个 XML 文件仅为外部位置身份包及可执行文件身份信息的模板。身份包使用 `uap3:userNotificationListener` 声明；`Publisher` 必须与可信签名证书的主体一致，包名、发布者和应用 ID 必须与可执行文件的身份信息一致。集成可执行文件清单时，应保留现有的 DPI 和权限设置，不直接覆盖现有清单。应用图标资源需要随外部目录提供。

微软的外部位置身份包适用于 Windows 10 2004 及更新系统；它仍需使用 Windows SDK 的 MakeAppx 与 SignTool 构建、签名，并按当前用户正常注册。当前未生成、签名或注册任何身份包，也未修改证书信任。模板中的发布者与图标资源需要在正式打包时确定。来源：[微软外部位置身份包说明](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/grant-identity-to-nonpackaged-apps)、[Seelen UI 清单](https://github.com/eythaann/Seelen-UI/blob/2f9aaa321b49fc2ac58aaa9188a2a6678e41c947/src/templates/AppxManifest.xml)。

本项目使用 `net10.0-windows10.0.19041.0` 的 Windows SDK .NET 投影编译此模块。系统通知监听的包身份、授权对话框、事件及各应用内容仍须在用户选择的环境中验证；模块编译成功不能证明授权或通知访问已经生效。
