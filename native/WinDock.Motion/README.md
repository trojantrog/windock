# Native whole-window transitions

This preview adapts one exact x64 uDWM image. Build 13 captures an interrupted native pose before StartAnimation replaces or restarts existing tracks, including paths that never call StopAnimation. It retains the build 12 named mapping handles, the build 11 extended-style-field correction, explicit `WS_EX_APPWINDOW` support and separated exclusions, the build 9 window-state-lock check and build 10 opacity fallback and stale-record expiry. Its runtime and visual behavior have not been verified. User-facing operation and rollback are described in [the animation guide](../../docs/native-window-animation.md).

## Source and build

`motion.cpp` replaces the translation, uniform scale and opacity timelines created by uDWM for ordinary window transitions. `host.cpp` performs explicit, elevated activation in the caller's Windows session. `NativeWindowMotion.cs` publishes settings, physical-pixel Dock anchors and a heartbeat; it never captures or hides application windows. The legacy overlay classes remain in source for reference but the Dock and application lifetime code no longer call them.

Build with Zig 0.15.2 for x86_64-windows-gnu:

```powershell
& .\native\WinDock.Motion\build.ps1 -Zig 'C:\path\to\zig.exe'
```

The build compiles the vendored MinHook source, produces a DLL, verifies the PE64 machine and DLL/executable types, embeds the DLL's SHA-256 in the activation helper and writes a manifest containing both binary hashes. A failed build removes the previous manifest so it cannot be published as a coherent new build. This does not sign the binaries. The MinHook license, including its constituent notices, is included in published packages.

The pinned Zig Windows archive has SHA-256 `3a0ed1e8799a2f8ce2a6e6290a9ff22e6906f8227865911fb7ddedc3cc14cb0c`, checked against the [official download index](https://ziglang.org/download/index.json). MinHook source provenance is recorded in [SOURCE.json](../../third_party/minhook/SOURCE.json).

## Image and ABI binding

Supported file: `udwm.dll 10.0.26100.9949`, SHA-256 `de3a7bacdcbb49a98bfff41010e772d18a329b77d9bc6b2416e1bc9f5b415863`.

Matching PDB: GUID `28cf5a3b-d2bc-549f-2f4e-4efcb7fbf89c`, image age 1, DBI age 1, PDB info age 2. Symbols and machine-code consumers were used together; public PDB names alone do not establish a private class layout. No Microsoft binaries or PDBs are vendored.

| Operation | RVA or offset |
| --- | --- |
| StartAnimation / StopAnimation | `0xc684` / `0x14c70` |
| ShowWindow / StartDelayHideAnimation | `0x12dfc` / `0x96750` |
| AdvanceTimelines | `0x17000`, returns void |
| DefaultHeap AllocClear / Free | `0x93320` / `0x92d00` |
| Parameterized CTimelineBase constructor | `0xc0bc` |
| Float timeline vtable / Release | `0xff180` / `0x506f0` |
| Secondary relative window rectangle | `0x2eb04` |
| Master / fifteen timeline slots | window `+0x140` / `+0x158` |
| Timeline allocation / current / finished | `0x78` bytes / `+0x30` / `+0x48` |
| CDesktopManager::s_csDwmInstance | `0x12bd10`, x64 CRITICAL_SECTION |
| CWindowData HWND / style / extended style | `+0x28` / `+0x74` / `+0x78` |

`native_26100_9949.h` is deliberately version-specific. Installation verifies the file hash, loaded PE metadata and entry bytes. Unknown images and conflicting entry patches are rejected. The DLL also verifies that its host executable is the system `dwm.exe`.

Build 10 incorrectly read extended styles from `CWindowData +0x7c`. On this pinned image, `GetNonClientAreaMargins` at RVA `0x2d6dc` reads `+0x78`, tests `0x80`, and selects metric 4 or 51: [SM_CYCAPTION / SM_CYSMCAPTION](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getsystemmetrics). `ShouldShowTransition` at `0x13156` and `ShouldBeIncludedInOwnerSWR` at `0x2d939` test the same byte. These consumers independently establish [WS_EX_TOOLWINDOW](https://learn.microsoft.com/en-us/windows/win32/winmsg/extended-window-styles) at `+0x78`; `HasNonTrivialRepresentation` at `0x41173` also tests `WS_EX_NOREDIRECTIONBITMAP` there. Build 11 uses that field and validates the first consumer's instruction bytes during installation. Offline evidence is recorded in `work/native-motion-symbols/udwm-window-style-disassembly.txt` beside the original research workspace. No Windows bytes or symbols are redistributed.

Read-only status snapshots from build 10 on 2026-10-29 showed zero replacements / three exclusions at 01:59:54, then one opening, one closing, one hiding and seven exclusions at 02:02:17. This establishes that the old component handled some actions; it does not identify which exclusion branch caused each miss or prove a correct displayed frame. Build 11 keeps the exclusions for tool, no-activate, topmost, transparent, shell and special-clip windows. An owned window is accepted only when it explicitly has `WS_EX_APPWINDOW`; this does not broadly enable owned popups.

## Lifecycle and ownership

StartAnimation runs first to preserve native resource preparation and action semantics. Raw types 1, 6, 3 and 5 correspond to ordinary showing, destruction, minimizing and restoring on this image. The callback then prepares six native heap blocks: X/Y translation, X/Y scale, opacity and a new master timeline. Allocation failure leaves the original timelines intact. Only after all blocks exist are native constructors called and all window slots replaced. Replacement happens while the caller already owns the native window-state critical section; the sixteen pointer assignments are not hardware-atomic.

The initial native reference on each constructed timeline is transferred to the window. Retired references are released through the original Release method. That method schedules deletion on a subsequent native timeline advance; constructed objects must not be directly freed. Ordinary transitions clear unused rotation and special-clip slots. Windows currently using the special-clip path are excluded before replacement.

Build 8 restricted replacement to the thread most recently observed in AdvanceTimelines. That was stronger than the native synchronization contract and could exclude valid window actions. Build 9 checks ownership of CDesktopManager::s_csDwmInstance through the x64 CRITICAL_SECTION OwningThread and RecursionCount fields. It does not acquire or release this lock. Calls without ownership and reentrant replacement during timeline advancement remain excluded. Configuration reads require successful installation. Failed hook activation keeps its trampoline allocation resident, avoiding unloading code that a partially activated hook might still reference.

The pinned image uses this critical section in ProcessAnimationTick (`0x16d90`), before UpdateGlobalTime (`0x16f40`) calls AdvanceTimelines (`0x17000`), and in HandleThreadMessage, CWindowList::UpdateScene, ForceUpdateScene and DestroySprite. The `lea r15` reference at `0x808f9` identifies the same critical-section base passed directly to EnterCriticalSection; its bytes are included in activation validation. Offline records are `work/native-motion-symbols/udwm-animation-lock-ownership.txt` and `udwm-critical-section-references.txt` outside the source tree. Microsoft describes [critical-section ownership](https://learn.microsoft.com/en-us/windows-hardware/drivers/debugger/displaying-a-critical-section); the uDWM address remains private and constrained to the exact image hash.

Hide-to-tray calls the native StartDelayHideAnimation entry, which hides the original representation, stops the previous transition, and starts raw type 8. Calling StartAnimation(8) directly would omit those native state changes. Repeated hide calls preserve the existing delayed hide. Re-showing a window during this custom hide requests the native show transition so it can replace the pending hide. No WM_CLOSE, DestroyWindow or process termination is generated by the module.

StopAnimation records the native current values before forwarding. A new action can continue an interrupted opening, minimizing, restoring or hiding transition. This includes minimize/restore and show/hide reversal, as well as closing during another transition. Closing during another transition starts at the recorded center and never increases the previous scale. The cache must match the window object, window-data object, HWND and recent stopped transition; size changes invalidate reuse. This preserves positional continuity in the implemented state model, but screen-level continuity has not been observed. Native StopAnimation is neither a first-frame nor last-frame presentation acknowledgment.

Build 13 also takes a local pose snapshot before forwarding StartAnimation. The pinned image reads an existing master at `0xcdd5` and calls `CTimelineBase::Restart` at `0xcded`; that entry does not require a preceding StopAnimation call. Previously, such a restart could overwrite the translation, scale and opacity tracks before WinDock recorded them, causing replacement to restart from a full-window or Dock pose. The local snapshot is used only after the native call succeeds and all replacement checks pass. Snapshot capture does not retire a timeline or mark the tracked action stopped; a failed native start therefore does not prematurely change the interruption record. The implementation adds no ABI fields or offsets. The evidence remains in `work/native-motion-symbols/udwm-selected-disassembly.txt` in the original research workspace.

Tracking records expire after four seconds without dereferencing an old window or timeline pointer. Expiry discards only the optional interruption state; it does not free or otherwise alter a native timeline. This bounds stale records when a finished transition is not followed by the observed StopAnimation callback. When the window data has no original visual, the opacity fallback is 1.0, matching the pinned image's StartAnimation branch at RVA `0xd69e`.

The module retains native content and cleanup ownership. It does not prove that every application has supplied a useful first frame or retained its contents until destruction. A successful native allocation or snapshot call alone is not visual validation.

## Related implementations

[Windhawk's genie animation](https://github.com/ramensoftware/windhawk-mods/blob/main/mods/genie-minimize-animation.wh.cpp) uses a captured bitmap and a separate animated window. Its first-frame preparation and scaling choices are useful comparisons, but that approach has different content, stacking and handoff behavior from this native timeline path. No code from it is included here.

[OpenGlass's DWM frame pipeline notes](https://github.com/ALTaleX531/OpenGlass/wiki/DWM-frame-pipeline) describe the distinction between changing animation properties, propagating dirty regions and presenting the resulting frame. Their examples span different Windows builds and do not establish the private layout for this image. In this component, the counters report timeline replacement only; they do not report a presented frame.

## Shared memory

Names are session-scoped `Local\WinDock.Motion.Config.v1.<session>` and `Local\WinDock.Motion.Status.v1.<session>`. The current user, SYSTEM and administrators own the mapping access. The compositor receives read access to configuration and read/write access to status through the [Windows Manager Group SID](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-dtyp/81d92bba-d22b-4a8c-908a-554ab29148ab), `S-1-5-90-0`.

Build 12 keeps the successful `OpenFileMapping` handles as well as the mapped views until DWM exits; failed views close their handles immediately. Earlier builds closed the handles after mapping. After restarting Dock on 2026-10-29 the public named status mapping was observed entirely zero while the same compositor process still existed. The retained handles avoid relying on view lifetime to preserve reopening by name. The managed diagnostic now separates header validity from heartbeat freshness and retains stale build/counter values when a valid header exists. A historical successful activation is reported separately and used conservatively only after checking the same session, PID, boot interval and compositor creation time; it is not reported as a current heartbeat.

Disconnected clients now recheck the owning compositor every ten seconds, with one inspection in flight. A successful process snapshot can establish absence; WMI checks name, session and creation time when the PID exists. A failed identity query is unknown, never proof of exit. [Microsoft documents PID reuse and the need to compare CreationDate](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-process). Only a confirmed exited or replaced compositor retires the exact old status identity/heartbeat and historical marker. The shared bytes are not cleared, so a new publisher cannot be erased by a delayed inspection. A changed heartbeat makes the new status visible. Healthy connections do not poll WMI.

Once no previous compositor owns the component, the client can retry the already verified protected login task, at most three times with delays. Enabling the feature or confirming a new compositor permits another recovery sequence. The registered task keeps its IgnoreNew policy, protected package checks and elevation boundary. The host rechecks the publisher's request immediately before loading, after package and cache preparation, so exiting Dock or turning off animations can cancel a pending load. No recovery path unloads a component or restarts DWM.

Configuration is 12384 bytes. The sequence at byte 12 is odd while the UI publisher copies the packet and even after completion. A read-only consumer uses volatile reads and memory barriers, never an interlocked write to the read-only mapping. Settings occupy bytes 16–95; 256 anchors of 48 bytes start at 96. Anchor flags 1 and 2 mean an explicit Dock action and a fallback Dock center. `started` in an anchor is reserved and currently zero; no process-start identity is inferred from it.

Anchors match HWND/PID first, then executable path/PID, then the nearest Dock center. Path identity is FNV-1a over UTF-16 code units, with ASCII case and slash normalization shared by C++ and C#. Explicit Dock actions have priority over passive positions. Multiple-screen positions are retained separately. Closed-window anchors expire after 1.5 seconds; explicit launch anchors after 8 seconds. The heartbeat is published every 150 ms. Missing or stale configuration after two seconds disables new custom actions.

Status is 96 bytes. State 1 means hooks installed, 2 image/entry mismatch, 3 initialization failure. Counters at bytes 40–88 count replaced opening, closing, minimizing, restoring and hiding timelines, skipped replacements and allocation failures. These are code-path counters, not rendered-frame measurements. Build 8 used error 7 for the combined thread/reentrancy exclusion, without per-reason counters. The status heartbeat is refreshed every 250 ms. The UI distinguishes waiting for a transition, zero recorded replacements, recorded replacements, and a later transition falling back to Windows.

Build 9 records these transition reasons:

| Value | Reason |
| --- | --- |
| 8 / 9 | Caller does not own the window-state lock / timeline traversal is in progress |
| 11 / 18 | No unfinished native master timeline / native action type differs from the requested action |
| 12 / 14 | Excluded window / excluded geometry |
| 13 / 15 | No secondary window representation / invalid pose |
| 16 / 17 | Tracking slots exhausted / allocation failed |

Build 11 further splits the former generic window-excluded reason:

| Value | Reason |
| --- | --- |
| 19 / 20 / 21 | No window data / native special clipping / native visibility exclusion |
| 22 / 23 | Excluded style / Dock or system-owned window |
| 24 / 25 / 26 | Owned popup / shell window / no current or recently published identity |

Normal window and geometry exclusions do not erase a blocking reason; a successful replacement clears it. A tracking slot is recorded only after all allocations and replacement succeed. The action check uses window `+0x150`, written by StartAnimation at RVA `0xc7d0`. The master finished byte is checked before replacement, but is not a presentation acknowledgment.

`%APPDATA%\WinDock\motion-status.json` contains one latest status/counter snapshot and a readable reason, written in the background only on change and at most once per two seconds. It contains no window titles or contents. When an incompatible resident component responds, the UI publishes disabled flags and requests sign-out; it does not attempt a live unload.

## Activation and failure behavior

The standard-user Dock launches the helper through normal Windows elevation only after an explicit click. The helper verifies the request, opens the hash-pinned DLL without write/delete sharing, enables the ordinary debug privilege in its elevated token, and targets only the current session's system DWM. If process access or loading is blocked, it reports failure. It does not change Defender, protected-process settings, code-integrity settings or security prompts.

Build 8 stages the verified DLL under `FOLDERID_ProgramData\WinDock.NativeMotion\<payload SHA-256>`. New objects have a protected DACL, administrator ownership, SYSTEM/administrator write access and Windows Manager Group read/execute access. Existing objects must have trusted ownership, expected access entries, no reparse point, and a single link for the file. Parent directory handles deny deletion during preparation/loading. Existing ACLs are never repaired or broadened. Source and staged payload hashes must match the helper's embedded hash; the staged file stays open without write/delete sharing during loading. This avoids requiring the DWM account to read a DLL under a private user profile. It does not bypass image-load policy.

Each activation receives a random 32-hex nonce and a 64-byte mapping named `Local\WinDock.Motion.Activation.v1.<session>.<nonce>`, accessible to the calling user, administrators and SYSTEM. It contains the helper PID, stage, result, Windows error, loader-thread result, compositor PID and build. The Dock checks the header and helper PID before saving `%APPDATA%\WinDock\motion-activation.json`. The loader-thread result contains the low 32 bits of its return value, not a remote `GetLastError`; a zero result does not identify the loader's precise failure reason.

Helper exits 10–14 cover report/configuration/path/hash checks, 15–19 privileges and target process/module checks, 20–24 the load request, 25 an existing module without a current ready handshake, and 26 cache preparation. Stage 25 runs before cache preparation, followed by stages 26 and 19–24. Native status error 10 records an unavailable configuration mapping; byte 28 carries that Windows error. Status is initialized before opening configuration so this failure can be reported.

Dock restoration calls `WindowMotionNative.SetState` once, then uses focus-only activation. Maximization comes from `IsZoomed` and `WINDOWPLACEMENT`; pending requests preserve intent during immediate reversal and suppress duplicate asynchronous commands until observed or expired. The focus-only path does not call `SwitchToThisWindow`. This addresses the former `ShowWindowAsync(SW_SHOWMAXIMIZED)` followed by a second queued `SW_RESTORE`, which could unmaximize the restored window. See Microsoft's [asynchronous show semantics](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-showwindowasync) and [window placement flags](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-windowplacement). Runtime behavior has not been tested in this revision.

The client records a reversible request only after `ShowWindowAsync` has accepted it (or the same accepted request is still pending), with the corresponding minimize/restore option enabled. Failed and already-completed requests do not create a reversal period. PID and window-thread identity are checked before consuming a reversal and after submission; acceptance still does not prove a rendered transition. Manual activation, login registration/removal and automatic recovery inspection share an operation guard. Manual activation takes that guard before changing the animation setting, so the setting's callback cannot launch the login task concurrently. The settings controls follow the same guard. Close duration, travel and scale remain available when either close or hide animation is enabled, since the native hide path uses those parameters too.

The remote load wait is bounded. On a timeout the remote path allocation is retained because the loader thread may still reference it; it is reclaimed with that DWM process. No forced thread termination is used. The Dock waits for the module status before reporting a connection.

Disabling or exiting stops future replacements. Existing native timelines retain their original cleanup route. No live DLL unload is attempted: the component remains dormant until sign-out. No system file or service is installed. Replacing a loaded component requires signing out.

## Optional sign-in activation

`WinDock.MotionHost.exe --register-login <current-user-SID>` requires normal Windows elevation, stages the fixed component and registers only `WinDock.NativeMotion.<SID>`. This command does not load DWM or start the registered task. `--remove-login <SID>` deletes only that task, leaving the currently loaded component and cache alone. The SID must equal the elevated process's TokenUser; a different-account credential elevation is rejected. Registration returns 0 only for Task Scheduler's `S_OK`; partial trigger-registration successes are failures.

The task uses [TASK_LOGON_INTERACTIVE_TOKEN and highest available privileges](https://learn.microsoft.com/en-us/windows/win32/taskschd/security-contexts-for-running-tasks) for that same SID. It stores no password and never runs the user-writable Dock, a script, or a path supplied through settings. The task's protected ACL gives SYSTEM and Administrators full access, and the current SID only read/execute access; `TASK_DONT_ADD_PRINCIPAL_ACE` prevents a broader principal ACE from being added. Both the principal and logon trigger are registered using the SID. Windows may return those values as account names, so readback translates both names and SID strings to `SecurityIdentifier` before comparison. The task action is a fixed hash-named executable under the existing administrator-owned cache.

The build first compiles a login-only helper, embeds its bytes and hash into the main helper, then compiles the main helper. Installation writes the embedded bytes from the already loaded image; it does not reopen and trust an executable in the writable source directory. `manifest.loginHost.sha256` identifies that embedded helper. The cache rejects reparse points, extra hard links, untrusted owners and unexpected ACLs; held directory/file handles deny deletion while staging and registering. The login-only helper accepts only `--login <SID>`, verifies its protected location and file hash, and retains the payload and Windows image checks.

After logon the helper waits up to three minutes for a current request. The publisher must be in the same session, have the same user SID, and match the published process creation time and fresh heartbeat. The helper does not launch Dock. A later Dock launch may read and run the exact registered task without another UAC prompt; the action is checked against the local manifest, principal, trigger and arguments before use. This check does not require a standard-user read of the protected cache. A mismatched resident component is left inactive until the next sign-in. Disabling automatic activation removes the task; disabling animation or exiting Dock keeps the existing heartbeat/flags stop behavior.

Registration/removal exit codes: 40 identity or elevation, 41 unsupported Windows image, 42 protected-cache preparation, 43 task registration, 44 task removal. Login execution also uses the existing activation exit codes. Task Scheduler retains the last result. Sign-in behavior has not been exercised in this revision.
