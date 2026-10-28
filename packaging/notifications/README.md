# Windows 通知身份包

这里的脚本为 WinDock 构建外部位置 MSIX 身份包。程序仍由原安装流程放在固定目录；身份包声明系统通知访问能力。用户在 WinDock 中点击“读取系统通知”后，才会请求 Windows 授权。构建、签名、包注册都不会读取通知内容。

适用 Windows 10 2004 及更新版本，脚本使用 PowerShell 7。本目录没有签名私钥，也不会创建证书、导入信任、开启开发者模式或更改安全策略。未签名或不受信任的包不能通过安装脚本注册。

## 构建

在仓库根目录执行。`Publisher` 必须填写签名证书的完整 Subject；下面的 `CN=WinDock.Local` 只适合生成本地审查工件，不代表已取得该发布者的证书。

```powershell
$tools = & .\packaging\notifications\Get-Tools.ps1
& .\packaging\notifications\Build.ps1 `
    -BaseExecutable 'D:\WinDock\WinDock.exe' `
    -Publisher 'CN=WinDock.Local' `
    -PublisherDisplayName 'WinDock' `
    -PackageVersion '0.6.0.18' `
    -SdkToolsDirectory $tools
```

`Get-Tools.ps1` 只将固定版本 Windows SDK 构建工具下载到本目录下被 Git 忽略的 `bin`，核对归档 SHA-256 和工具的微软签名。版本、来源和哈希见 `sdk-tools.lock.json`。已有完整 Windows SDK 时，也可直接将其 x64 工具目录传给 `Build.ps1`。

`BaseExecutable` 用于读取原有清单，保留权限和 DPI 声明。脚本在单独的目录编译 WinDock，把包身份写入编译时清单，再创建 .NET 单文件程序；不会事后改写已经发布的程序。输出目录必须不存在，不覆盖旧产物。`-Dotnet` 可指定 .NET 10 SDK 路径，`-OutputDirectory` 可指定新目录。

输出包含：

- `app/`：包含身份声明的新程序与原有组件。
- `*.unsigned.msix`：尚未签名的身份包。
- `manifests/`：原清单、合并清单及从新程序读回的清单。
- `notification-package.json`：身份、版本、程序及包的 SHA-256。
- `setup/`：注册、移除脚本及说明，不包含签名私钥或 SDK 工具。

包版本必须为四段数字。更新时使用更高版本，保持发布者、包名和应用 ID 一致。MakeAppx 的 `/nv` 用于外部位置包的文件路径校验例外；程序不在 MSIX 内部。签名与 Windows 信任检查仍须完成。

## 签名

使用明确用于代码签名、包含私钥且 Subject 与 Publisher 完全一致的有效证书：

```powershell
& .\packaging\notifications\Sign.ps1 `
    -BundleDirectory '<构建输出目录>' `
    -CertificateThumbprint '<代码签名证书的指纹>' `
    -SdkToolsDirectory $tools
```

默认从当前用户的 `My` 证书库读取；机器证书使用 `-CertificateStore LocalMachine`。`-TimestampServer` 可指定证书签发方提供的 RFC 3161 时间戳服务。没有时间戳时，安装时仍要求证书在有效期内。

脚本保留未签名原件，在新目录签名 WinDock.exe 和身份包，完成签名信任检查后才更新描述文件。签名失败时，原描述文件继续指向未签名产物。自签名证书须先通过另行决定的证书管理流程获得本机信任；本脚本不会代为修改信任。

## 安装与授权

1. 从 `notification-package.json` 中的 `executable` 找到签名后的应用目录。先对该目录运行仓库现有 `scripts/Package-LocalBuild.ps1 -PublishPath <目录>`，再通过现有安装流程部署到固定位置；签名会改变程序哈希，因此发布校验清单必须在签名之后生成。构建、签名和封装须使用同一源码提交；源码版本变化后应重新构建。
2. 使用本目录的 `Install.ps1` 注册身份。它先核对可信签名、发布者、已安装程序哈希和图标资源，再调用标准的 `Add-AppxPackage -ExternalLocation`。可先加 `-WhatIf` 查看注册目标。

```powershell
& .\packaging\notifications\Install.ps1 `
    -BundleDirectory '<构建输出目录>' `
    -ExternalLocation 'D:\WinDock'
```

3. 下次启动 WinDock 后，托盘面板才会识别进程的包身份。点击“读取系统通知”并在 Windows 对话框中自行决定是否允许。包已注册不等于通知访问已授权；安装脚本不会启动 WinDock 或打开授权对话框。

外部位置包不会代替原安装程序管理 WinDock 的全部文件。升级时保持身份匹配，部署程序后再注册更高版本的身份包。重复注册相同版本或降级会被脚本拒绝，避免先卸载旧身份再尝试安装所产生的中断。

## 回滚

```powershell
& .\packaging\notifications\Remove.ps1 -Publisher '<包的 Publisher>'
```

这只移除当前用户、指定发布者的 WinDock 通知身份包。不会删除外部程序目录、用户配置、原生动画任务或证书。下次启动 WinDock 后通知读取入口不再可用。其他 Dock 功能按原安装版本运行；原生 Windows 通知中心保留。

## 验证边界与来源

当前已完成独立程序编译、清单读回和未签名 MSIX 构建。签名、包注册、进程身份获取、Windows 授权对话框及真实通知内容尚未验证。桌面 WinRT 事件支持仍有应用模型差异，界面保留手动刷新；不能把构建成功当作通知功能已生效。

依据：[微软外部位置身份包](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/grant-identity-to-nonpackaged-apps)、[微软 WinApp CLI 的 sparse package 实现说明](https://github.com/microsoft/winappCli/blob/main/docs/guides/sparse.md)、[MSIX 签名要求](https://learn.microsoft.com/en-us/windows/msix/package/signing-package-overview)、[通知监听授权要求](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/notification-listener)。未复制其他项目的通知管理代码。
