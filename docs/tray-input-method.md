# 系统输入法入口

托盘面板的输入法按钮请求打开 Windows 原生输入指示器。操作仅在点击按钮时执行，不注册输入法快捷键，也不会自动修改当前输入方式、输入模式或系统设置。原生任务栏应保持显示。

程序先确认可见任务栏属于 Windows 的 Explorer 进程，再在该任务栏的 UI Automation 控件树中查找输入指示器。代码尝试识别原生窗口类为 `InputIndicatorButton` 的控件，以及类名为 `SystemTray.InputIndicatorButton`、控件类型为按钮的 Windows 11 虚拟控件；虚拟控件不要求具有原生窗口句柄。两种控件都必须属于任务栏进程且处于可见、可用状态。只接受唯一候选，并在操作前重新确认控件仍然唯一且为同一控件。控件不可用、无法识别或存在多个候选时，显示提示，继续使用 Windows 任务栏中的输入法入口。面板关闭后，尚未发出的请求会被取消。

该入口依赖 Windows 提供的可访问性操作。虚拟控件类名来自特定 Windows 版本的实现，后续版本可能变化；该路径尚未经过实际桌面验证。任务栏自动隐藏、指示器未显示、控件未暴露或界面繁忙等情况也可能导致请求不可用。请求成功只表示控件操作已发送，不能保证选择面板出现，实际效果尚需验证。面板位置由 Windows 决定。

查找或请求超过 2.5 秒会提示用户；查找阶段超时后不会继续发送操作。若操作已经开始且系统响应较慢，窗口可能稍后出现，需等待前一次调用结束后才能再次发出请求。

Windows 的输入语言和输入法的内部中英文模式是不同状态，键盘布局或语言代码不足以可靠区分所有输入法。`SystemTray.IMEButton` 不作为语言选择控件调用。当前入口不根据语言代码推断输入法名称、中英文状态、候选框或输入法专有菜单，也不拦截 Shift 或其他快捷键。

参考资料：[Windows UI Automation Invoke 控件模式](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-implementinginvoke)、[控件类名的适用范围](https://learn.microsoft.com/en-us/dotnet/api/system.windows.automation.automationelement.classnameproperty)、[控件身份比较](https://learn.microsoft.com/en-us/dotnet/api/system.windows.automation.automation.compare)、[输入法与系统输入指示器](https://learn.microsoft.com/en-us/windows/apps/develop/input/input-method-editor-requirements)、[YASB 的语言组件](https://github.com/amnweb/yasb/blob/main/src/core/widgets/yasb/language.py)和 [im-select 的输入模式控件](https://github.com/daipeihust/im-select/pull/88/files)。
