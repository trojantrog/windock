# Independent system tray

The independent tray is an opt-in preview. It displays the icon, tooltip, and tray hints that an application supplies to Windows. Left, right, middle, double, and keyboard clicks use the application's original callback. Explorer continues to process all original messages and notifications. Enabling it is separate from replacing or hiding the Windows taskbar.

The setting explains that a WinDock native component is loaded into Explorer's tray thread. It defaults to **off**. Turning it off disconnects the component and closes the separate tray strip. Closing WinDock also releases its hook; the native worker watches the host process so it can close its pipe and unload after a crash. There is no taskbar movement, clipping, Explorer binary modification, fake `Shell_TrayWnd`, or global hook.

## Source comparison

Source snapshots were inspected on 2026-10-28, including implementations rather than only project descriptions.

| Project and source | License | Relevant behavior and decision |
| --- | --- | --- |
| [ManagedShell TrayService](https://github.com/cairoshell/ManagedShell/blob/855222035e927910ca53fd3215caef056337a048/src/ManagedShell.WindowsTray/TrayService.cs), [ExplorerTrayService](https://github.com/cairoshell/ManagedShell/blob/855222035e927910ca53fd3215caef056337a048/src/ManagedShell.WindowsTray/ExplorerTrayService.cs), [NotifyIcon](https://github.com/cairoshell/ManagedShell/blob/855222035e927910ca53fd3215caef056337a048/src/ManagedShell.WindowsTray/NotifyIcon.cs) | [Apache-2.0](https://github.com/cairoshell/ManagedShell/blob/855222035e927910ca53fd3215caef056337a048/LICENSE) | Implements a replacement tray receiver and legacy toolbar enumeration. Its window ordering and `Shell_TrayWnd` ownership do not fit a Dock that keeps Explorer as the shell. Native callback version handling is useful protocol research. No source or dependency is imported. |
| [Cairo SystemTrayIcon](https://github.com/cairoshell/cairoshell/blob/daf6d7c6addb6e00a6ad20dea2808cd44ee2b4ff/Cairo%20Desktop/CairoDesktop.MenuBarExtensions/SystemTrayIcon.xaml.cs) | [Apache-2.0](https://github.com/cairoshell/cairoshell/blob/daf6d7c6addb6e00a6ad20dea2808cd44ee2b4ff/LICENSE) | Demonstrates WPF tray interaction backed by ManagedShell rather than a process list. It also illustrates keeping app callbacks distinct from the shell's own UI. No source or dependency is imported. |
| [YASB native hook](https://github.com/amnweb/yasb/blob/25c71861c1610f095269295a76fe2c8984e4a7b6/src/core/widgets/services/systray/hook/trayhook.cpp), [host](https://github.com/amnweb/yasb/blob/25c71861c1610f095269295a76fe2c8984e4a7b6/src/core/widgets/services/systray/systray_hook.py), [wire structures](https://github.com/amnweb/yasb/blob/25c71861c1610f095269295a76fe2c8984e4a7b6/src/core/utils/win32/structs.py) | [MIT](https://github.com/amnweb/yasb/blob/25c71861c1610f095269295a76fe2c8984e4a7b6/LICENSE) | Shows the fixed 32-bit notification payload, copying icon handles before they become stale, alpha-mask handling, pipe lifetime, and Explorer-only targeting. WinDock implements its own bounded worker and uses `WH_CALLWNDPROC` directly; it does not copy YASB's window-procedure subclass or perform pipe I/O on Explorer's UI thread. Attribution is retained in `third_party/YASB-LICENSE.txt`. |
| [Seelen hook loader](https://github.com/eythaann/Seelen-UI/blob/2f9aaa321b49fc2ac58aaa9188a2a6678e41c947/src/background/modules/system_tray/application/tray_hook_loader.rs), [tray callbacks](https://github.com/eythaann/Seelen-UI/blob/2f9aaa321b49fc2ac58aaa9188a2a6678e41c947/src/background/modules/system_tray/application/tray_icon.rs), [notification source](https://github.com/eythaann/Seelen-UI/blob/2f9aaa321b49fc2ac58aaa9188a2a6678e41c947/src/background/modules/notifications/wpn_service.rs) | [AGPL-3.0](https://github.com/eythaann/Seelen-UI/blob/2f9aaa321b49fc2ac58aaa9188a2a6678e41c947/LICENSE) | Research only. Relevant concerns include callback parameter packing, negative monitor coordinates, icon identity and recycled windows, preserving hidden-state masks, and the distinction between tray hints and Windows notifications. Its code and private notification-database reader are not incorporated. |

The public [NOTIFYICONDATAW contract](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/ns-shellapi-notifyicondataw) defines icon identity, valid-field masks, tooltip/balloon fields, and version-4 coordinates. [Shell_NotifyIconW](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shell_notifyiconw) defines the operations. [SetWindowsHookExW](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowshookexw) requires a matching native architecture for an in-process hook. Explorer's private wire format is not a stable public API; incompatible payloads are rejected.

## Implementation

### Layout, naming and input

The popup defaults to a compact icon grid. Names can be enabled separately; icon size, column count and name/system order are available in Dock settings. Successful connection text, the hint explanation and empty history are not permanent rows. Refresh, recent hints and layout controls are in the More menu. Only missing/failed connection or an empty icon collection displays status text. The popup and the separate strip use the system acrylic backdrop with the Dock's light/dark theme and a readable fallback.

Names are resolved asynchronously from the actual owner executable's file metadata. Dynamic tooltip text (for example a transfer rate or volume percentage) is kept separate and remains available on hover. Windows shell icons receive concise functional names. Missing names are not replaced with a visible repeated “tray application” label. A strict neutral-silhouette check recolors only near-uniform white/black template icons for contrast; colored and multitone artwork is preserved. Original source resolution remains a limit.

Choose **Arrange icons** to pin/unpin by clicking, move by dragging, or use each icon's edit menu. Normal mode keeps the application's own right-click callback. The left-hand strip displays only icons explicitly pinned in WinDock; the popup retains all non-hidden icons. Preferences use an application-provided GUID when available, otherwise executable path, owner window class and icon ID. Two indistinguishable instances can share a preference. Arrow keys, Home/End, Enter/Space and the context-menu key are supported; Ctrl+Left/Right moves an icon while arranging. Escape leaves arrangement/history before closing the popup.

The detailed behavior was compared with [YASB's tray container](https://github.com/amnweb/yasb/blob/main/src/core/widgets/yasb/systray.py) and [icon input implementation](https://github.com/amnweb/yasb/blob/main/src/core/widgets/services/systray/systray_widget.py) (MIT), and the ManagedShell/Cairo sources above (Apache-2.0, consulted without importing source). In particular, their version-3+ input implementations send both legacy mouse-up and NIN_SELECT/WM_CONTEXTMENU. WinDock retains that compatibility sequence instead of removing a callback on the assumption that it is an accidental duplicate. Keyboard auto-repeat is ignored; custom popup hover is delayed and respects NIF_SHOWTIP. Menu dismissal does not pull focus back from another application. These changes have been compiled, not interactively validated.

### Registration recovery

Initial and manual refresh retain the normal TaskbarCreated broadcast, now asynchronous. They also identify running registered tray applications by their actual process paths, enumerate ordinary hidden top-level and message-only windows, recheck HWND/PID, and post a bounded directed request. A previously unknown NIM_MODIFY without a callable message can request a full registration from its observed owner, at most twice per owner connection and no more often than 30 seconds. It does not infer a callback value, restart the application, change taskbar visibility, modify Explorer settings, or read old icon snapshots as live state.

Microsoft documents that [message-only windows do not receive broadcast messages](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features#message-only-windows), while [SendNotifyMessage](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendnotifymessagew) sends to ordinary hidden top-level windows. This coverage improvement is not a confirmed diagnosis of every missing icon. In the reported Weixin case, the observed Qt tray receiver was an ordinary hidden top-level window; no message-only root cause is claimed. Applications which ignore re-registration or reject messages under Windows integrity rules may still be absent. Registry entries alone do not provide a live callback, so they are never displayed as fabricated tray icons. The refresh command remains available without requiring account logout.

- The host verifies the system tray belongs to `%WINDIR%\explorer.exe`, checks x64 Windows 10 build 19041 or later, verifies the DLL against its adjacent SHA-256 file, and checks the exported protocol version. The checksum detects a mismatched/corrupt build; it is not Authenticode signing.
- The native component observes only that tray thread. It always calls the next hook. It does not replace a window procedure or suppress notifications. A 128-event queue copies `HICON` values immediately; image conversion and pipe writes run on a worker. Pipe writes are cancelled after 200 ms. A full/busy queue reports lost updates instead of blocking Explorer.
- The per-session named pipe uses a random nonce and a current-user ACL. The host checks its client PID against the verified Explorer process. The component checks the pipe server PID and verifies its executable is the adjacent `WinDock.exe`. Owner windows and process IDs are rechecked before callbacks.
- The WPF service validates packet length, dimensions, operation, and protocol. It caps UI backlog at 256 events / 8 MiB and live icons at 512. Icons retain their supplied dimensions up to 256×256; legacy masks are used when alpha is absent. WPF uses high-quality scaling and device-aligned layout. A 16-pixel source cannot gain missing detail.
- Item identity uses GUID when supplied, otherwise window/ID/process. Updates respect `NIF_*` and state masks. Dead owner windows are removed. Mouse positions are physical screen pixels, with signed coordinates packed according to the notification version. App callbacks retain foreground permission for their native menus.
- The optional strip is a separate window at the primary display's bottom left. It shrinks to fit the free space before the main Dock, and hides if even its entrance cannot fit. It never moves or resizes the main Dock. Fullscreen windows hide the strip. The Dock's tray entrance remains available.
- WinDock's own menus now use WPF vector marks, rounded borders, layout rounding, and a submenu-capable template. The former WinForms menu for WinDock's own notification icon is replaced. No app-owned menus are redrawn.

## Notifications and current limits

Only `NIF_INFO` tray hints are recorded by this panel. Its blue dot means a hint was received during this connection; it is cleared when the icon is clicked. It is not an unread-message count. The newest 50 hints remain in memory and clear on exit. Clearing them does not mark messages as read in another app. Native balloons and modern Windows notifications remain with Explorer, and the panel offers the normal Windows Notification Center shortcut.

This module does not read chat logs, screenshots, or notification bodies from the Windows database. The pre-existing `Badges` service separately reads application badge records; this change does not extend that service.

There is no supported public API that provides a complete snapshot of every modern tray icon together with its native callback. The service requests re-registration after connecting and on a rate-limited refresh, including the directed recovery above. Apps that ignore it can still be missing until they send a complete update. An update without a callback cannot yet be clicked. A disconnected or incompatible component leaves Explorer's original tray working. NIS_HIDDEN is an application-controlled hidden/source-icon flag, not the same as Explorer's user-promoted/overflow preference; WinDock does not unconditionally reveal those hidden source icons.

Applications that position menus using `Shell_NotifyIconGetRect` can still use Explorer's original icon location instead of the supplied mouse coordinates. Their menus may consequently appear near the original taskbar location. This observer does not override Explorer's rectangle replies. Elevated or protected apps can reject callbacks under Windows integrity rules. The component does not bypass those rules. Windows changes to the private tray protocol can require an update.

Third-party menu fonts, bitmaps, and drawing quality remain controlled by their application. No claim of complete Windows-tray compatibility or elimination of third-party aliasing is made. Runtime compatibility, notification delivery, and interaction still need desktop validation; compilation alone does not establish these.

## Build

The native source is built locally. No prebuilt hook DLL is downloaded. It is currently x64 only. The C compiler uses Win32 headers and libraries provided by Zig; neither Visual Studio nor a global SDK installation is required.

Compiler used: [Zig 0.15.2 Windows x64](https://ziglang.org/download/0.15.2/zig-x86_64-windows-0.15.2.zip). Its archive SHA-256 was checked against the [official download index](https://ziglang.org/download/index.json):

```text
3a0ed1e8799a2f8ce2a6e6290a9ff22e6906f8227865911fb7ddedc3cc14cb0c
```

```powershell
& .\native\WinDock.TrayHook\build.ps1 -Zig 'C:\path\to\zig.exe'
dotnet build .\src\WinDock\WinDock.csproj -c Release
```

The build script writes `native/WinDock.TrayHook/bin/win-x64/WinDock.TrayHook.x64.dll` and its `.sha256` file. Its compiler cache stays under that ignored output directory. The WPF project copies both beside the application, including when publishing a single-file executable, and includes the YASB attribution under `licenses/`. The DLL and checksum must be distributed together. The executable must retain the name `WinDock.exe` and be run through its app host. A source-only WPF build without the native component still builds; enabling the preview then displays a missing-component message.

Native compilation uses `-Wall -Wextra -Werror` and compile-time wire-layout checks. The implementation was compiled with Zig 0.15.2 and .NET 10 in Release configuration. Static PE inspection confirmed x64 machine type, the two expected exports, and only Windows/UCRT library imports. This locally built DLL is unsigned. Do not disable a Windows or security-product block to load it. No runtime or desktop validation is claimed.

## Disable and rollback

Turn off **Enable independent tray (preview)** in Dock settings, or quit WinDock. No Explorer restart is required by this implementation. The strip closes and the hook/pipe are released. To remove the component from a distribution, close WinDock and remove its two adjacent `WinDock.TrayHook.x64.dll*` files; the rest of the app can run with the preview disabled. Replacing the app with an earlier build also removes this implementation. This module makes no Windows registry edits and does not change the taskbar visibility setting.
