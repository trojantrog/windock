# WinDock 中文版

基于 [Breno Alvim 的 WinDock 0.5.0](https://github.com/obrenoalvim/windock) 维护的分支，增加简体中文、Everything 搜索和 PecoFence 收纳管理。当前版本为 `0.6.0-preview.3`。

## 使用

右击 Dock 空白处，选择“Dock 设置”。界面右上角可切换中文、英语和葡萄牙语。新增功能提供中文和英语文字。

### 全局搜索

点击 Dock 的放大镜，或按 `Alt+Space`。文件结果来自本机正在运行的 [Everything](https://www.voidtools.com/)，同时搜索已安装应用。支持 Everything 搜索语法，例如 `ext:pdf 赛车`。按 Enter 打开选中项目，Esc 关闭搜索。

“搜索设置”可更换快捷键和 Everything 实例名。普通安装的实例名留空；命名实例需要填写对应名称。快捷键被其他程序占用时，设置页会提示，Dock 按钮仍可使用。

搜索通过 Everything 的[本机 IPC 接口](https://www.voidtools.com/support/everything/sdk/ipc/)读取索引，不需要开启 HTTP 服务。Everything 的索引范围和排除项继续生效。Everything 未运行或查询失败时会明确提示，此时只显示应用快捷方式。每次最多显示 100 项，输入更具体的条件可缩小结果范围。

### 桌面收纳

需要先安装并运行 [PecoFence](https://github.com/DayuanJiang/PecoFence)，并保留与程序配套的 `pecofence-cli.exe`。已验证接口版本为 0.2.3。点击 Dock 的收纳按钮，可查看现有分区及分页、新建或重命名分区、归类所选项目、打开分区设置，以及保存和恢复布局。

归类只支持虚拟分区，原文件保留在原文件夹。可将资源管理器中已受 PecoFence 管理的桌面项目拖入所选分区；其他位置的文件不会被移动或导入。文件夹门户不支持在此归类。每次更改归类或分区名称前自动保存布局快照，恢复布局前也会保存当前布局。

连接失败时，点击“选择 PecoFence”定位程序。操作超时后应先刷新并核对状态，避免重复执行已经完成的更改。

### 预览与动画

预览窗口使用 DWM 实时缩略图，默认尺寸扩大至 320 × 200，并随窗口数量和屏幕空间调整。缩略图按实际屏幕像素定位，跨屏时更新 DPI。

图标弹跳默认关闭，系统窗口动画继续生效。此版本不包含自定义窗口过渡和原生托盘常驻功能。预览尺寸与 DPI 调整仍需在不同屏幕上检查画质，不代表已经解决所有缩略图锯齿问题。

## 构建与检查

需要 Windows 和 .NET 10 SDK。

```powershell
dotnet test -c Release
dotnet publish src/WinDock -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

运行 `publish/WinDock.exe`。`--tray` 参数用于登录启动。默认配置位于 `%APPDATA%\WinDock`，固定应用和环境配置可沿用上游版本。

## 恢复上游版本

先从托盘菜单退出 WinDock，确认完整任务栏恢复，再把原版 `WinDock.exe` 放回原位置。更换程序前备份 `%APPDATA%\WinDock`。PecoFence 布局由 PecoFence 单独管理，可在收纳窗口选择已保存的布局恢复；布局快照不备份文件内容。

## 许可

沿用 [MIT 许可](LICENSE)。原 WinDock 作者为 Breno Alvim。Everything 与 PecoFence 是独立软件，本项目通过它们公开的本机接口连接，未打包这两个软件。
