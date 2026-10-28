// Copyright (c) 2026 WinDock contributors. MIT License.
// Protocol/layout references and upstream licenses: docs/system-tray.md.
// This DLL observes only an explicitly selected Explorer tray thread. It never
// replaces the tray window procedure and always continues the hook chain.
#define WIN32_LEAN_AND_MEAN
#ifndef _WIN32_WINNT
#define _WIN32_WINNT 0x0A00
#endif
#include <windows.h>
#include <shellapi.h>
#include <stdint.h>
#include <stddef.h>
#include <string.h>

#define MAGIC 0x52544457u
#define PROTOCOL 4u
#define QUEUE_SIZE 128
#define MAX_ICON 256
#define START_MESSAGE L"WinDock.TrayHook.Connect.v4"
#define STOP_MESSAGE L"WinDock.TrayHook.Disconnect.v4"

#pragma pack(push, 1)
// Explorer's Shell_NotifyIcon wire representation is 32-bit on x64 as well.
typedef struct {
    DWORD size, window, id, flags, callback, icon;
    WCHAR tip[128];
    DWORD state, stateMask;
    WCHAR info[256];
    DWORD version;
    WCHAR infoTitle[64];
    DWORD infoFlags;
    GUID guid;
    DWORD balloonIcon;
} IconData32;
typedef struct { DWORD signature, operation; IconData32 icon; } ShellTrayData;
typedef struct { DWORD magic, version, kind, length; } Header;
typedef struct {
    Header header;
    DWORD operation, ownerPid, width, height, pixelBytes;
    IconData32 icon;
} IconPacket;
#pragma pack(pop)
_Static_assert(sizeof(IconData32) == 956, "Explorer notification wire layout changed");
_Static_assert(sizeof(IconPacket) == 992, "WinDock tray protocol layout changed");
typedef struct { DWORD operation, ownerPid; IconData32 data; HICON icon; } QueuedIcon;

static HMODULE module;
static SRWLOCK queueLock = SRWLOCK_INIT;
static QueuedIcon queue[QUEUE_SIZE];
static unsigned int head, count;
static volatile LONG stopped = 1, initialized, dropped;
static HANDLE wakeEvent;
static DWORD hostPid, sessionNonce;
static UINT startMessage, stopMessage;
static HWND verifiedTray;

static BOOL ExplorerTray(HWND window) {
    if (window == verifiedTray) return TRUE;
    DWORD pid = 0;
    WCHAR cls[32], path[MAX_PATH], windows[MAX_PATH];
    GetWindowThreadProcessId(window, &pid);
    if (pid != GetCurrentProcessId() || !GetClassNameW(window, cls, 32) ||
        lstrcmpW(cls, L"Shell_TrayWnd") != 0) return FALSE;
    if (!GetModuleFileNameW(NULL, path, MAX_PATH) || !GetWindowsDirectoryW(windows, MAX_PATH - 14)) return FALSE;
    lstrcatW(windows, L"\\explorer.exe");
    if (lstrcmpiW(path, windows) != 0) return FALSE;
    verifiedTray = window;
    return TRUE;
}

static BOOL ExpectedHost(DWORD pid) {
    // The server must be the WinDock executable adjacent to this module.
    HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    if (!process) return FALSE;
    WCHAR actual[32768], expected[32768]; DWORD length = 32768;
    BOOL ok = QueryFullProcessImageNameW(process, 0, actual, &length);
    CloseHandle(process);
    if (!ok || !GetModuleFileNameW(module, expected, 32768)) return FALSE;
    WCHAR *slash = NULL;
    for (WCHAR *p = expected; *p; ++p) if (*p == L'\\') slash = p;
    if (!slash || slash - expected > 32700) return FALSE;
    lstrcpyW(slash + 1, L"WinDock.exe");
    return lstrcmpiW(actual, expected) == 0;
}

static BOOL WritePacket(HANDLE pipe, HANDLE process, const void *data, DWORD length) {
    OVERLAPPED operation = {0};
    operation.hEvent = CreateEventW(NULL, TRUE, FALSE, NULL);
    if (!operation.hEvent) return FALSE;
    DWORD written = 0;
    BOOL ok = WriteFile(pipe, data, length, &written, &operation);
    if (!ok && GetLastError() == ERROR_IO_PENDING) {
        HANDLE waits[] = { operation.hEvent, process };
        DWORD wait = WAIT_TIMEOUT;
        while (!stopped && (wait = WaitForMultipleObjects(2, waits, FALSE, 200)) == WAIT_TIMEOUT) {}
        if (wait != WAIT_OBJECT_0) CancelIoEx(pipe, &operation);
        ok = GetOverlappedResult(pipe, &operation, &written, TRUE);
    }
    CloseHandle(operation.hEvent);
    return ok && written == length;
}

static BYTE *IconPixels(HICON icon, DWORD *width, DWORD *height) {
    ICONINFO info = {0}; BITMAP bitmap = {0}; BYTE *pixels = NULL;
    if (!icon || !GetIconInfo(icon, &info)) return NULL;
    HBITMAP sizing = info.hbmColor ? info.hbmColor : info.hbmMask;
    if (!sizing || !GetObjectW(sizing, sizeof(bitmap), &bitmap)) goto done;
    int w = bitmap.bmWidth, h = info.hbmColor ? bitmap.bmHeight : bitmap.bmHeight / 2;
    if (w <= 0 || h <= 0 || w > MAX_ICON || h > MAX_ICON) goto done;
    HDC dc = CreateCompatibleDC(NULL);
    if (!dc) goto done;
    BITMAPINFO bi = {0};
    bi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    bi.bmiHeader.biWidth = w; bi.bmiHeader.biHeight = -h;
    bi.bmiHeader.biPlanes = 1; bi.bmiHeader.biBitCount = 32; bi.bmiHeader.biCompression = BI_RGB;
    BYTE *bits = NULL;
    HBITMAP dib = CreateDIBSection(dc, &bi, DIB_RGB_COLORS, (void **)&bits, NULL, 0);
    if (dib && bits) {
        HGDIOBJ old = SelectObject(dc, dib);
        ZeroMemory(bits, (SIZE_T)w * h * 4);
        if (DrawIconEx(dc, 0, 0, icon, w, h, 0, NULL, DI_NORMAL)) {
            BOOL hasAlpha = FALSE;
            for (int p = 0; p < w * h; ++p) if (bits[p * 4 + 3]) { hasAlpha = TRUE; break; }
            // Legacy icons have no alpha: use their AND mask, not an opaque box.
            if (!hasAlpha && info.hbmMask) {
                HDC mask = CreateCompatibleDC(NULL);
                if (mask) {
                    HGDIOBJ oldMask = SelectObject(mask, info.hbmMask);
                    for (int y = 0; y < h; ++y) for (int x = 0; x < w; ++x)
                        bits[(y * w + x) * 4 + 3] = GetPixel(mask, x, y) == RGB(255,255,255) ? 0 : 255;
                    SelectObject(mask, oldMask); DeleteDC(mask);
                }
            }
            pixels = HeapAlloc(GetProcessHeap(), 0, (SIZE_T)w * h * 4);
            if (pixels) { CopyMemory(pixels, bits, (SIZE_T)w * h * 4); *width = w; *height = h; }
        }
        SelectObject(dc, old);
    }
    if (dib) DeleteObject(dib);
    DeleteDC(dc);
done:
    if (info.hbmColor) DeleteObject(info.hbmColor);
    if (info.hbmMask) DeleteObject(info.hbmMask);
    return pixels;
}

static BOOL SendIcon(HANDLE pipe, HANDLE process, QueuedIcon *event) {
    DWORD width = 0, height = 0;
    BYTE *pixels = IconPixels(event->icon, &width, &height);
    DWORD bytes = width * height * 4, length = sizeof(IconPacket) + bytes;
    IconPacket *packet = HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, length);
    if (!packet) { if (pixels) HeapFree(GetProcessHeap(), 0, pixels); return FALSE; }
    packet->header = (Header){MAGIC, PROTOCOL, 1, length};
    packet->operation = event->operation; packet->ownerPid = event->ownerPid;
    packet->width = width; packet->height = height; packet->pixelBytes = bytes;
    packet->icon = event->data;
    if (event->operation <= NIM_MODIFY && (packet->icon.flags & NIF_ICON) && event->data.icon && !pixels) {
        packet->icon.flags &= ~NIF_ICON;
        InterlockedIncrement(&dropped);
    }
    // Never send handles belonging to Explorer to the host process.
    packet->icon.icon = 0; packet->icon.balloonIcon = 0;
    if (pixels) { CopyMemory((BYTE *)packet + sizeof(IconPacket), pixels, bytes); HeapFree(GetProcessHeap(), 0, pixels); }
    BOOL ok = WritePacket(pipe, process, packet, length);
    HeapFree(GetProcessHeap(), 0, packet);
    return ok;
}

static DWORD WINAPI Worker(void *unused) {
    (void)unused;
    WCHAR name[128];
    wsprintfW(name, L"\\\\.\\pipe\\WinDock.Tray.%lu.%08lX", hostPid, sessionNonce);
    HANDLE process = OpenProcess(SYNCHRONIZE, FALSE, hostPid);
    HANDLE pipe = INVALID_HANDLE_VALUE;
    for (int tries = 0; tries < 40 && !stopped; ++tries) {
        pipe = CreateFileW(name, GENERIC_WRITE, 0, NULL, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, NULL);
        if (pipe != INVALID_HANDLE_VALUE) break;
        Sleep(50);
    }
    ULONG serverPid = 0;
    if (!process || pipe == INVALID_HANDLE_VALUE || !GetNamedPipeServerProcessId(pipe, &serverPid) ||
        serverPid != hostPid || !ExpectedHost(serverPid)) goto finish;
    Header connected = { MAGIC, PROTOCOL, 2, sizeof(Header) };
    if (!WritePacket(pipe, process, &connected, sizeof(connected))) goto finish;
    while (!stopped && WaitForSingleObject(process, 0) == WAIT_TIMEOUT) {
        if (InterlockedExchange(&dropped, 0)) {
            Header loss = { MAGIC, PROTOCOL, 3, sizeof(Header) };
            if (!WritePacket(pipe, process, &loss, sizeof(loss))) break;
        }
        QueuedIcon event = {0}; BOOL ready = FALSE;
        AcquireSRWLockExclusive(&queueLock);
        if (count) { event = queue[head]; head = (head + 1) % QUEUE_SIZE; --count; ready = TRUE; }
        ReleaseSRWLockExclusive(&queueLock);
        if (ready) {
            BOOL ok = SendIcon(pipe, process, &event);
            if (event.icon) DestroyIcon(event.icon);
            if (!ok) break;
        } else {
            HANDLE waits[] = { wakeEvent, process };
            if (WaitForMultipleObjects(2, waits, FALSE, 250) == WAIT_OBJECT_0 + 1) break;
        }
    }
finish:
    AcquireSRWLockExclusive(&queueLock);
    InterlockedExchange(&stopped, 1);
    while (count) { if (queue[head].icon) DestroyIcon(queue[head].icon); head = (head + 1) % QUEUE_SIZE; --count; }
    head = 0;
    InterlockedExchange(&dropped, 0);
    if (wakeEvent) { CloseHandle(wakeEvent); wakeEvent = NULL; }
    ReleaseSRWLockExclusive(&queueLock);
    if (pipe != INVALID_HANDLE_VALUE) CloseHandle(pipe);
    if (process) CloseHandle(process);
    InterlockedExchange(&initialized, 0);
    // The Windows hook owns a separate module reference until the host unhooks.
    FreeLibraryAndExitThread(module, 0);
    return 0;
}

static BOOL SameIcon(const QueuedIcon *left, const QueuedIcon *right) {
    if (left->ownerPid != right->ownerPid) return FALSE;
    BOOL leftGuid = (left->data.flags & NIF_GUID) != 0;
    BOOL rightGuid = (right->data.flags & NIF_GUID) != 0;
    if (leftGuid != rightGuid) return FALSE;
    if (leftGuid) return memcmp(&left->data.guid, &right->data.guid, sizeof(GUID)) == 0;
    return left->data.window == right->data.window && left->data.id == right->data.id;
}

static BOOL IconUpdate(const QueuedIcon *event) {
    return event->operation == NIM_MODIFY && (event->data.flags & NIF_ICON) != 0 &&
        (event->data.flags & ~(NIF_ICON | NIF_GUID | NIF_MESSAGE | NIF_TIP | NIF_STATE | NIF_SHOWTIP)) == 0;
}

static BOOL SameIconUpdate(const QueuedIcon *left, const QueuedIcon *right) {
    const IconData32 *a = &left->data, *b = &right->data;
    if (!IconUpdate(left) || a->size != b->size || a->flags != b->flags ||
        a->window != b->window || a->id != b->id) return FALSE;
    if ((a->flags & NIF_MESSAGE) && a->callback != b->callback) return FALSE;
    if ((a->flags & NIF_STATE) && (a->state != b->state || a->stateMask != b->stateMask)) return FALSE;
    return TRUE;
}

static void Capture(const COPYDATASTRUCT *copy) {
    if (stopped || !copy || copy->dwData != 1 || !copy->lpData ||
        copy->cbData < 8 + 24 || copy->cbData > 4096) return;
    const ShellTrayData *source = (const ShellTrayData *)copy->lpData;
    if (source->signature != 0x34753423 || source->operation > NIM_SETVERSION || source->icon.size < 24 ||
        source->icon.size > sizeof(IconData32) || source->icon.size > copy->cbData - 8) return;
    QueuedIcon event = {0};
    event.operation = source->operation;
    CopyMemory(&event.data, &source->icon, source->icon.size);
    if (event.operation == NIM_SETVERSION && event.data.size < offsetof(IconData32, version) + sizeof(DWORD)) return;
    if (event.data.size < offsetof(IconData32, stateMask) + sizeof(DWORD)) event.data.flags &= ~NIF_STATE;
    if (event.data.size < offsetof(IconData32, infoFlags) + sizeof(DWORD)) event.data.flags &= ~(NIF_INFO | NIF_REALTIME);
    if (event.data.size < offsetof(IconData32, tip) + 64 * sizeof(WCHAR)) event.data.flags &= ~(NIF_TIP | NIF_SHOWTIP);
    else {
        size_t tipLength = (event.data.size - offsetof(IconData32, tip)) / sizeof(WCHAR);
        event.data.tip[(tipLength < 128 ? tipLength : 128) - 1] = 0;
    }
    const GUID emptyGuid = {0};
    BOOL guidOnly = (event.data.flags & NIF_GUID) != 0;
    if (guidOnly && (event.data.size < offsetof(IconData32, guid) + sizeof(GUID) ||
        memcmp(&event.data.guid, &emptyGuid, sizeof(GUID)) == 0)) return;
    if (!guidOnly) event.data.guid = emptyGuid;
    HWND owner = (HWND)(LONG_PTR)(LONG)event.data.window;
    GetWindowThreadProcessId(owner, &event.ownerPid);
    if (!event.ownerPid && event.operation != NIM_DELETE && !guidOnly) return;
    DWORD ownerSession = 0, ownSession = 0;
    if (event.ownerPid && (!ProcessIdToSessionId(event.ownerPid, &ownerSession) ||
        !ProcessIdToSessionId(GetCurrentProcessId(), &ownSession) || ownerSession != ownSession)) return;
    event.data.tip[127] = 0; event.data.info[255] = 0; event.data.infoTitle[63] = 0;
    // CopyIcon is the only image work on Explorer's thread; bitmap conversion and
    // pipe I/O happen on Worker. Never wait behind a busy renderer or pipe.
    if (!TryAcquireSRWLockExclusive(&queueLock)) { InterlockedIncrement(&dropped); return; }
    if (stopped) { ReleaseSRWLockExclusive(&queueLock); return; }
    if (IconUpdate(&event)) {
        for (unsigned int i = count; i > 0; --i) {
            QueuedIcon *pending = &queue[(head + i - 1) % QUEUE_SIZE];
            if (!SameIcon(pending, &event)) continue;
            if (!SameIconUpdate(pending, &event)) break;
            HICON copied = event.data.icon ? CopyIcon((HICON)(LONG_PTR)(LONG)event.data.icon) : NULL;
            if (event.data.icon && !copied) InterlockedIncrement(&dropped);
            else {
                if (pending->icon) DestroyIcon(pending->icon);
                event.icon = copied;
                for (unsigned int position = i - 1; position + 1 < count; ++position)
                    queue[(head + position) % QUEUE_SIZE] = queue[(head + position + 1) % QUEUE_SIZE];
                queue[(head + count - 1) % QUEUE_SIZE] = event;
                if (wakeEvent) SetEvent(wakeEvent);
            }
            ReleaseSRWLockExclusive(&queueLock);
            return;
        }
    }
    if (count == QUEUE_SIZE) { ReleaseSRWLockExclusive(&queueLock); InterlockedIncrement(&dropped); return; }
    if (event.operation <= NIM_MODIFY && (event.data.flags & NIF_ICON) && event.data.icon)
        event.icon = CopyIcon((HICON)(LONG_PTR)(LONG)event.data.icon);
    queue[(head + count) % QUEUE_SIZE] = event;
    ++count;
    if (wakeEvent) SetEvent(wakeEvent);
    ReleaseSRWLockExclusive(&queueLock);
}

__declspec(dllexport) DWORD WINAPI WinDockTrayProtocol(void) { return PROTOCOL; }

__declspec(dllexport) LRESULT CALLBACK WinDockTrayHook(int code, WPARAM wParam, LPARAM lParam) {
    if (code >= 0 && lParam) {
        const CWPSTRUCT *message = (const CWPSTRUCT *)lParam;
        if (!startMessage) { startMessage = RegisterWindowMessageW(START_MESSAGE); stopMessage = RegisterWindowMessageW(STOP_MESSAGE); }
        if ((message->message == startMessage || message->message == stopMessage ||
            message->message == WM_COPYDATA || message->message == WM_NCDESTROY) && ExplorerTray(message->hwnd)) {
            if (message->message == startMessage && !initialized && message->wParam && message->lParam && ExpectedHost((DWORD)message->wParam)) {
                if (InterlockedCompareExchange(&initialized, 1, 0) == 0) {
                    hostPid = (DWORD)message->wParam; sessionNonce = (DWORD)message->lParam;
                    wakeEvent = CreateEventW(NULL, FALSE, FALSE, NULL);
                    HMODULE held = NULL;
                    if (wakeEvent && GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
                        (LPCWSTR)(ULONG_PTR)&WinDockTrayHook, &held)) {
                        InterlockedExchange(&stopped, 0);
                        HANDLE worker = CreateThread(NULL, 0, Worker, NULL, 0, NULL);
                        if (worker) CloseHandle(worker);
                        else {
                            InterlockedExchange(&stopped, 1);
                            FreeLibrary(held); CloseHandle(wakeEvent); wakeEvent = NULL;
                            InterlockedExchange(&initialized, 0);
                        }
                    } else {
                        if (wakeEvent) { CloseHandle(wakeEvent); wakeEvent = NULL; }
                        InterlockedExchange(&initialized, 0);
                    }
                }
            } else if (message->message == stopMessage && message->wParam == hostPid && (DWORD)message->lParam == sessionNonce) {
                AcquireSRWLockExclusive(&queueLock);
                InterlockedExchange(&stopped, 1);
                if (wakeEvent) SetEvent(wakeEvent);
                ReleaseSRWLockExclusive(&queueLock);
            } else if (message->message == WM_COPYDATA) Capture((const COPYDATASTRUCT *)message->lParam);
            else if (message->message == WM_NCDESTROY) {
                AcquireSRWLockExclusive(&queueLock);
                InterlockedExchange(&stopped, 1);
                if (wakeEvent) SetEvent(wakeEvent);
                ReleaseSRWLockExclusive(&queueLock);
                verifiedTray = NULL;
            }
        }
    }
    return CallNextHookEx(NULL, code, wParam, lParam);
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, void *reserved) {
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH) { module = instance; DisableThreadLibraryCalls(instance); }
    return TRUE;
}
