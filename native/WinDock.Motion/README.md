# Native whole-window transitions

This preview adapts one exact x64 uDWM image. Build 8 was observed connected with zero replaced transitions, 23 skipped transitions and error 7. That error combined thread identity and reentrancy, so the observation does not distinguish those branches. Build 9 corrects the overly restrictive thread check and separates the diagnostics; its runtime and visual behavior have not been verified. User-facing operation and rollback are described in [the animation guide](../../docs/native-window-animation.md).

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

`native_26100_9949.h` is deliberately version-specific. Installation verifies the file hash, loaded PE metadata and entry bytes. Unknown images and conflicting entry patches are rejected. The DLL also verifies that its host executable is the system `dwm.exe`.

## Lifecycle and ownership

StartAnimation runs first to preserve native resource preparation and action semantics. Raw types 1, 6, 3 and 5 correspond to ordinary showing, destruction, minimizing and restoring on this image. The callback then prepares six native heap blocks: X/Y translation, X/Y scale, opacity and a new master timeline. Allocation failure leaves the original timelines intact. Only after all blocks exist are native constructors called and all window slots replaced. Replacement happens while the caller already owns the native window-state critical section; the sixteen pointer assignments are not hardware-atomic.

The initial native reference on each constructed timeline is transferred to the window. Retired references are released through the original Release method. That method schedules deletion on a subsequent native timeline advance; constructed objects must not be directly freed. Ordinary transitions clear unused rotation and special-clip slots. Windows currently using the special-clip path are excluded before replacement.

Build 8 restricted replacement to the thread most recently observed in AdvanceTimelines. That was stronger than the native synchronization contract and could exclude valid window actions. Build 9 checks ownership of CDesktopManager::s_csDwmInstance through the x64 CRITICAL_SECTION OwningThread and RecursionCount fields. It does not acquire or release this lock. Calls without ownership and reentrant replacement during timeline advancement remain excluded. Configuration reads require successful installation. Failed hook activation keeps its trampoline allocation resident, avoiding unloading code that a partially activated hook might still reference.

The pinned image uses this critical section in ProcessAnimationTick (`0x16d90`), before UpdateGlobalTime (`0x16f40`) calls AdvanceTimelines (`0x17000`), and in HandleThreadMessage, CWindowList::UpdateScene, ForceUpdateScene and DestroySprite. The `lea r15` reference at `0x808f9` identifies the same critical-section base passed directly to EnterCriticalSection; its bytes are included in activation validation. Offline records are `work/native-motion-symbols/udwm-animation-lock-ownership.txt` and `udwm-critical-section-references.txt` outside the source tree. Microsoft describes [critical-section ownership](https://learn.microsoft.com/en-us/windows-hardware/drivers/debugger/displaying-a-critical-section); the uDWM address remains private and constrained to the exact image hash.

Hide-to-tray calls the native StartDelayHideAnimation entry, which hides the original representation, stops the previous transition, and starts raw type 8. Calling StartAnimation(8) directly would omit those native state changes. Repeated hide calls preserve the existing delayed hide. Re-showing a window during this custom hide requests the native show transition so it can replace the pending hide. No WM_CLOSE, DestroyWindow or process termination is generated by the module.

StopAnimation records the native current values before forwarding. A new action can continue an interrupted opening, minimizing, restoring or hiding transition. This includes minimize/restore and show/hide reversal, as well as closing during another transition. Closing during another transition starts at the recorded center and never increases the previous scale. The cache must match the window object, window-data object, HWND and recent stopped transition; size changes invalidate reuse. This preserves positional continuity in the implemented state model, but screen-level continuity has not been observed. Native StopAnimation is neither a first-frame nor last-frame presentation acknowledgment.

The module retains native content and cleanup ownership. It does not prove that every application has supplied a useful first frame or retained its contents until destruction. A successful native allocation or snapshot call alone is not visual validation.

## Shared memory

Names are session-scoped `Local\WinDock.Motion.Config.v1.<session>` and `Local\WinDock.Motion.Status.v1.<session>`. The current user, SYSTEM and administrators own the mapping access. The compositor receives read access to configuration and read/write access to status through the [Windows Manager Group SID](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-dtyp/81d92bba-d22b-4a8c-908a-554ab29148ab), `S-1-5-90-0`.

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

Normal window and geometry exclusions do not erase a blocking reason; a successful replacement clears it. A tracking slot is recorded only after all allocations and replacement succeed. The action check uses window `+0x150`, written by StartAnimation at RVA `0xc7d0`. The master finished byte is checked before replacement, but is not a presentation acknowledgment.

`%APPDATA%\WinDock\motion-status.json` contains one latest status/counter snapshot, written in the background only on change and at most once per two seconds. It contains no window titles or contents. When an incompatible resident component responds, the UI publishes disabled flags and requests sign-out; it does not attempt a live unload.

## Activation and failure behavior

The standard-user Dock launches the helper through normal Windows elevation only after an explicit click. The helper verifies the request, opens the hash-pinned DLL without write/delete sharing, enables the ordinary debug privilege in its elevated token, and targets only the current session's system DWM. If process access or loading is blocked, it reports failure. It does not change Defender, protected-process settings, code-integrity settings or security prompts.

Build 8 stages the verified DLL under `FOLDERID_ProgramData\WinDock.NativeMotion\<payload SHA-256>`. New objects have a protected DACL, administrator ownership, SYSTEM/administrator write access and Windows Manager Group read/execute access. Existing objects must have trusted ownership, expected access entries, no reparse point, and a single link for the file. Parent directory handles deny deletion during preparation/loading. Existing ACLs are never repaired or broadened. Source and staged payload hashes must match the helper's embedded hash; the staged file stays open without write/delete sharing during loading. This avoids requiring the DWM account to read a DLL under a private user profile. It does not bypass image-load policy.

Each activation receives a random 32-hex nonce and a 64-byte mapping named `Local\WinDock.Motion.Activation.v1.<session>.<nonce>`, accessible to the calling user, administrators and SYSTEM. It contains the helper PID, stage, result, Windows error, loader-thread result, compositor PID and build. The Dock checks the header and helper PID before saving `%APPDATA%\WinDock\motion-activation.json`. The loader-thread result contains the low 32 bits of its return value, not a remote `GetLastError`; a zero result does not identify the loader's precise failure reason.

Helper exits 10–14 cover report/configuration/path/hash checks, 15–19 privileges and target process/module checks, 20–24 the load request, 25 an existing module without a current ready handshake, and 26 cache preparation. Stage 25 runs before cache preparation, followed by stages 26 and 19–24. Native status error 10 records an unavailable configuration mapping; byte 28 carries that Windows error. Status is initialized before opening configuration so this failure can be reported.

Dock restoration calls `WindowMotionNative.SetState` once, then uses focus-only activation. Maximization comes from `IsZoomed` and `WINDOWPLACEMENT`; pending requests preserve intent during immediate reversal and suppress duplicate asynchronous commands until observed or expired. The focus-only path does not call `SwitchToThisWindow`. This addresses the former `ShowWindowAsync(SW_SHOWMAXIMIZED)` followed by a second queued `SW_RESTORE`, which could unmaximize the restored window. See Microsoft's [asynchronous show semantics](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-showwindowasync) and [window placement flags](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-windowplacement). Runtime behavior has not been tested in this revision.

The remote load wait is bounded. On a timeout the remote path allocation is retained because the loader thread may still reference it; it is reclaimed with that DWM process. No forced thread termination is used. The Dock waits for the module status before reporting a connection.

Disabling or exiting stops future replacements. Existing native timelines retain their original cleanup route. No live DLL unload is attempted: the component remains dormant until sign-out. No system file, service or elevated startup task is installed. Replacing a loaded component requires signing out.
