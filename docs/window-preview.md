# 窗口预览

悬停在运行中的应用上时，WinDock 使用 Windows 桌面窗口管理器提供的实时窗口缩略图。预览按屏幕宽度和窗口数量调整尺寸，最多显示六个窗口；屏幕空间充足时，单个预览的宽度上限为 420 个逻辑像素。

源窗口尺寸发生变化时，预览会重新计算保持原比例的显示区域。显示区域使用屏幕物理像素对齐；缩略图尺寸暂时无法取得时，程序会尝试隐藏缩略图并显示应用图标，恢复后重新显示缩略图。跨屏缩放变化时重新计算布局。

Windows 提供的窗口画面、源程序的绘制质量和缩略图的缩放方式会影响结果，放大预览不能补回源画面缺少的细节。部分程序可能为 Windows 任务栏提供专门的标签页预览，而 WinDock 目前显示的是窗口本身；受保护内容和某些最小化窗口也可能无法提供有效画面。

可参考微软的 [DWM 缩略图说明](https://learn.microsoft.com/en-us/windows/win32/dwm/thumbnail-ovw)、[源尺寸查询](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/nf-dwmapi-dwmquerythumbnailsourcesize)和 [缩略图属性](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ns-dwmapi-dwm_thumbnail_properties)。[AppSwitcherBar](https://github.com/adamecr/AppSwitcherBar) 的 WPF 预览实现也使用源尺寸计算居中比例。当前更改尚未进行桌面视觉验证。
