#include "protocol.h"
#include "image_identity.h"
#include "native_26100_9949.h"
#include "MinHook.h"
#include <algorithm>
#include <cmath>
#include <cwchar>

namespace {
using namespace motion;
using abi::Field;
HMODULE image;
Config* sharedConfig;
Status* status;
abi::Start startOriginal;
abi::Stop stopOriginal;
abi::Show showOriginal;
abi::Advance advanceOriginal;
abi::Alloc allocate;
abi::FreeBlock freeBlock;
abi::Ctor construct;
abi::ReleaseObject release;
abi::GetRelative relativeRect;
abi::Stop delayHide;
thread_local unsigned depth;
thread_local bool advancing;

struct Pose { double x, y, scale, alpha; };
struct Active {
    void* window = nullptr;
    void* data = nullptr;
    void* master = nullptr;
    HWND handle = nullptr;
    Rect base{};
    double ox = 0, oy = 0;
    int kind = 0;
    uint64_t stopped = 0;
    bool interrupted = false;
    Pose last{};
};
Active active[256];

bool Finite(double value) { return std::isfinite(value) && std::abs(value) <= 1000000; }
bool Valid(const Rect& rect) {
    return Finite(rect.x) && Finite(rect.y) && Finite(rect.width) && Finite(rect.height)
        && rect.width >= 1 && rect.height >= 1 && rect.width <= 32768 && rect.height <= 32768;
}
bool OwnsWindowState() {
    if (!image) return false;
    auto* lock = reinterpret_cast<const volatile CRITICAL_SECTION*>(reinterpret_cast<const BYTE*>(image) + abi::WindowStateLock);
    return lock->OwningThread == reinterpret_cast<HANDLE>(static_cast<uintptr_t>(GetCurrentThreadId())) && lock->RecursionCount > 0;
}

bool Skip(TransitionReason reason) {
    const auto value = static_cast<LONG>(reason);
    auto previous = InterlockedCompareExchange(&status->error, 0, 0);
    if (BlocksTransition(value)) InterlockedExchange(&status->error, value);
    else while (!BlocksTransition(previous)) {
        const auto observed = InterlockedCompareExchange(&status->error, value, previous);
        if (observed == previous) break;
        previous = observed;
    }
    return false;
}

bool ReadConfig(Config& result) {
    if (!sharedConfig || !status || InterlockedCompareExchange(&status->state, 0, 0) != 1) return false;
    LONG before = sharedConfig->sequence;
    MemoryBarrier();
    if (before & 1) return false;
    memcpy(&result, sharedConfig, sizeof(result));
    MemoryBarrier();
    LONG after = sharedConfig->sequence;
    if (before != after || (after & 1)) return false;
    const auto now = GetTickCount64();
    return result.magic == Magic && result.version == Protocol && result.bytes == sizeof(Config)
        && (result.flags & Enabled) && result.publisher != 0 && now >= result.heartbeat
        && now - result.heartbeat < 2000 && result.count <= Capacity
        && result.openMs >= 100 && result.openMs <= 700 && result.closeMs >= 100 && result.closeMs <= 700
        && result.minimizeMs >= 100 && result.minimizeMs <= 700 && result.restoreMs >= 100 && result.restoreMs <= 700
        && Finite(result.closeScale) && result.closeScale >= .75 && result.closeScale <= 1
        && Finite(result.closeDistance) && result.closeDistance >= 0 && result.closeDistance <= 120
        && Finite(result.fadeHold) && result.fadeHold >= .5 && result.fadeHold <= .95;
}

Active* Find(void* window) {
    for (auto& item : active) if (item.window == window) return &item;
    return nullptr;
}

Active* Reserve(void* window) {
    if (auto* found = Find(window)) return found;
    for (auto& item : active) if (!item.window || (item.stopped && GetTickCount64() - item.stopped > 1000)) {
        return &item;
    }
    return nullptr;
}

double Current(void* window, unsigned index, double fallback) {
    void* track = Field<void*>(window, abi::Tracks + index * sizeof(void*));
    return track ? Field<double>(track, abi::Current) : fallback;
}

void Remember(void* window) {
    if (!OwnsWindowState()) return;
    auto* item = Find(window);
    if (!item || !item->master || item->master != Field<void*>(window, abi::Master)
        || item->data != Field<void*>(window, abi::WindowData)) return;
    const double sx = Current(window, 6, 1), sy = Current(window, 7, 1);
    item->last = {
        item->base.x + Current(window, 0, 0) + item->base.width * (item->ox * (1 - sx) + sx * .5),
        item->base.y + Current(window, 1, 0) + item->base.height * (item->oy * (1 - sy) + sy * .5),
        sx, Current(window, 9, 1)
    };
    item->interrupted = !Field<BYTE>(item->master, abi::Finished)
        && Field<double>(item->master, abi::Current) < .999 && std::abs(sx - sy) < .001;
    item->stopped = GetTickCount64();
    item->master = nullptr;
}

bool Candidate(void* window, const Config& config, HWND& handle, DWORD& pid) {
    void* data = Field<void*>(window, abi::WindowData);
    if (!data || Field<BYTE>(window, abi::SpecialClip) || (Field<BYTE>(data, 0x2e4) & 1)) return false;
    handle = Field<HWND>(data, 0x28);
    const auto style = Field<DWORD>(data, 0x74), ex = Field<DWORD>(data, 0x7c);
    if ((style & WS_CHILD) || (ex & (WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST | WS_EX_TRANSPARENT))) return false;
    pid = 0;
    GetWindowThreadProcessId(handle, &pid);
    if (pid == config.publisher || pid == GetCurrentProcessId() || pid == 4) return false;
    if (pid) {
        if (GetWindow(handle, GW_OWNER)) return false;
        wchar_t name[128]{};
        GetClassNameW(handle, name, 128);
        if (!wcscmp(name, L"Progman") || !wcscmp(name, L"WorkerW") || !wcscmp(name, L"Shell_TrayWnd")
            || !wcscmp(name, L"Shell_SecondaryTrayWnd") || !wcscmp(name, L"#32768")) return false;
    } else {
        bool known = false;
        for (uint32_t i = 0; i < config.count; ++i) if (config.anchors[i].window == reinterpret_cast<uint64_t>(handle)) { known = true; break; }
        if (!known) return false;
    }
    return true;
}

bool Eligible(void* window, const Config& config, HWND& handle, DWORD& pid, Rect& base, TransitionReason& reason) {
    reason = TransitionReason::WindowExcluded;
    if (!Candidate(window, config, handle, pid)) return false;
    reason = TransitionReason::SurfaceUnavailable;
    void* secondary = Field<void*>(window, abi::Secondary);
    if (!secondary) return false;
    const auto style = Field<DWORD>(Field<void*>(window, abi::WindowData), 0x74);
    RECT relative{};
    relativeRect(secondary, &relative);
    const auto original = Field<RECT>(window, abi::CachedRect);
    base = { static_cast<float>(original.left + relative.left), static_cast<float>(original.top + relative.top),
        static_cast<float>(relative.right - relative.left), static_cast<float>(relative.bottom - relative.top) };
    reason = TransitionReason::GeometryExcluded;
    if (!Valid(base) || base.width < 160 || base.height < 80) return false;
    MONITORINFO info{sizeof(MONITORINFO)};
    RECT rect{static_cast<LONG>(base.x), static_cast<LONG>(base.y), static_cast<LONG>(base.x + base.width), static_cast<LONG>(base.y + base.height)};
    if (!(style & WS_CAPTION) && GetMonitorInfoW(MonitorFromRect(&rect, MONITOR_DEFAULTTONEAREST), &info)
        && rect.left <= info.rcMonitor.left && rect.top <= info.rcMonitor.top
        && rect.right >= info.rcMonitor.right && rect.bottom >= info.rcMonitor.bottom) return false;
    return true;
}

Rect AnchorFor(const Config& config, HWND handle, DWORD pid, const Rect& base) {
    const Anchor* best = nullptr;
    double distance = 1e30;
    auto choose = [&](const Anchor& a) {
        if (!Valid(a.rect)) return;
        const double dx = a.rect.x + a.rect.width * .5 - base.x - base.width * .5;
        const double dy = a.rect.y + a.rect.height * .5 - base.y - base.height * .5;
        const double score = (a.flags & 1) ? -1. : dx * dx + dy * dy;
        if (score < distance) { best = &a; distance = score; }
    };
    for (uint32_t i = 0; i < config.count; ++i) {
        const auto& a = config.anchors[i];
        if (a.window == reinterpret_cast<uint64_t>(handle) && (a.pid == pid || pid == 0)) choose(a);
    }
    if (best) return best->rect;
    uint64_t pathHash = 0;
    HANDLE process = pid ? OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid) : nullptr;
    if (process) {
        wchar_t path[32768]; DWORD length = 32768;
        if (QueryFullProcessImageNameW(process, 0, path, &length)) pathHash = PathHash(path);
        CloseHandle(process);
    }
    for (uint32_t i = 0; i < config.count; ++i) {
        const auto& a = config.anchors[i];
        if ((a.pid == pid && pid != 0) || (a.executable == pathHash && pathHash != 0)) choose(a);
    }
    if (best) return best->rect;
    for (uint32_t i = 0; i < config.count; ++i) if (config.anchors[i].flags & 2) choose(config.anchors[i]);
    if (best) return best->rect;
    if (Valid(config.fallback)) return config.fallback;
    return {base.x + base.width * .5f - 20, base.y + base.height - 20, 40, 40};
}

double BaseAlpha(void* window) {
    void* data = Field<void*>(window, abi::WindowData);
    void* original = data ? Field<void*>(data, 0x1b8) : nullptr;
    double value = original ? Field<double>(original, 0x78) : Field<double>(window, 0x78);
    return Finite(value) ? std::clamp(value, 0., 1.) : 1.;
}

bool ReplaceTracks(void* window, const Config& config, int kind) {
    if (!OwnsWindowState()) return Skip(TransitionReason::WindowLockNotOwned);
    if (advancing) return Skip(TransitionReason::TimelineTraversal);
    HWND handle; DWORD pid; Rect base;
    TransitionReason reason;
    if (!Eligible(window, config, handle, pid, base, reason)) return Skip(reason);
    void* master = Field<void*>(window, abi::Master);
    if (!master || Field<BYTE>(master, abi::Finished)) return Skip(TransitionReason::TimelineUnavailable);
    if (Field<int>(window, abi::AnimationKind) != kind) return Skip(TransitionReason::NativeActionMismatch);
    const auto anchor = AnchorFor(config, handle, pid, base);
    const double ox = Field<float>(window, abi::Origin), oy = Field<float>(window, abi::Origin + 4);
    if (!Finite(ox) || !Finite(oy)) return Skip(TransitionReason::InvalidPose);
    const double alpha = BaseAlpha(window);
    Pose full{base.x + base.width * .5, base.y + base.height * .5, 1, alpha};
    Pose small{anchor.x + anchor.width * .5, anchor.y + anchor.height * .5,
        std::clamp(std::min(anchor.width / base.width, anchor.height / base.height), .005f, 1.f), 0};
    Pose from = full, to = full;
    uint32_t milliseconds;
    if (kind == 1 || kind == 5) { from = small; milliseconds = kind == 1 ? config.openMs : config.restoreMs; }
    else if (kind == 3) { to = small; milliseconds = config.minimizeMs; }
    else {
        to.y += config.closeDistance;
        to.scale = config.closeScale;
        to.alpha = 0;
        milliseconds = config.closeMs;
    }
    Active* previous = Find(window);
    if (previous && previous->data == Field<void*>(window, abi::WindowData) && previous->handle == handle
        && previous->interrupted && GetTickCount64() - previous->stopped < 500
        && (previous->kind == 1 || previous->kind == 3 || previous->kind == 5 || previous->kind == 8)
        && std::abs(previous->base.width - base.width) < 2 && std::abs(previous->base.height - base.height) < 2)
        from = previous->last;
    if (!Finite(from.x) || !Finite(from.y) || !Finite(from.scale) || from.scale <= 0 || !Finite(from.alpha))
        return Skip(TransitionReason::InvalidPose);
    if (kind == 6 || kind == 8)
        to = {from.x, from.y + config.closeDistance, std::min(from.scale, static_cast<double>(config.closeScale)), 0};
    Active* record = Reserve(window);
    if (!record) return Skip(TransitionReason::TrackingCapacity);
    const double begin[5] = {
        from.x - base.x - base.width * (ox * (1 - from.scale) + from.scale * .5),
        from.y - base.y - base.height * (oy * (1 - from.scale) + from.scale * .5), from.scale, from.scale, from.alpha
    };
    const double end[5] = {
        to.x - base.x - base.width * (ox * (1 - to.scale) + to.scale * .5),
        to.y - base.y - base.height * (oy * (1 - to.scale) + to.scale * .5), to.scale, to.scale, to.alpha
    };
    void* prepared[6]{};
    for (auto& value : prepared) {
        value = allocate(abi::AllocationSize);
        if (!value) {
            for (auto raw : prepared) if (raw) freeBlock(raw);
            InterlockedIncrement64(&status->failed);
            return Skip(TransitionReason::AllocationFailed);
        }
    }
    const double duration = milliseconds / 1000.;
    abi::Interpolation spatial{5, 0, .2, 0, config.curve == 1 ? .1 : .25, 1};
    abi::Interpolation fade = (kind == 1 || kind == 5)
        ? abi::Interpolation{5, 0, 0, 1, .12, 1}
        : abi::Interpolation{5, 0, config.fadeHold, 0, .98, .12};
    abi::Interpolation linear{};
    for (unsigned i = 0; i < 6; ++i) {
        construct(prepared[i], duration, i < 5 ? begin[i] : 0, i < 5 ? end[i] : 1,
            i == 5 ? &linear : i == 4 ? &fade : &spatial, 0);
        Field<void*>(prepared[i], 0) = reinterpret_cast<BYTE*>(image) + abi::FloatVtable;
    }
    void* retired[16];
    for (unsigned i = 0; i < 15; ++i) {
        retired[i] = Field<void*>(window, abi::Tracks + i * sizeof(void*));
        Field<void*>(window, abi::Tracks + i * sizeof(void*)) = nullptr;
    }
    retired[15] = Field<void*>(window, abi::Master);
    constexpr unsigned attributes[]{0, 1, 6, 7, 9};
    for (unsigned i = 0; i < 5; ++i) Field<void*>(window, abi::Tracks + attributes[i] * sizeof(void*)) = prepared[i];
    Field<void*>(window, abi::Master) = prepared[5];
    *record = {window, Field<void*>(window, abi::WindowData), prepared[5], handle, base, ox, oy, kind, 0, false, from};
    for (auto value : retired) if (value) release(value);
    volatile LONG64* counter = kind == 1 ? &status->opened : kind == 3 ? &status->minimized : kind == 5 ? &status->restored
        : kind == 6 ? &status->closed : &status->hidden;
    InterlockedIncrement64(counter);
    InterlockedExchange(&status->error, 0);
    return true;
}

uint32_t FlagFor(int kind) {
    switch (kind) {
        case 1: return Opening;
        case 3: return Minimizing;
        case 5: return Restoring;
        case 6: return Closing;
        case 8: return Hiding;
        default: return 0;
    }
}

HRESULT StartHook(void* window, int kind) {
    ++depth;
    const HRESULT result = startOriginal(window, kind);
    if (depth == 1 && SUCCEEDED(result) && FlagFor(kind)) {
        Config config;
        if (ReadConfig(config) && (config.flags & FlagFor(kind)) && !ReplaceTracks(window, config, kind))
            InterlockedIncrement64(&status->skipped);
    }
    --depth;
    return result;
}

HRESULT StopHook(void* window) {
    Remember(window);
    return stopOriginal(window);
}

HRESULT ShowHook(void* window, bool visible, bool initial) {
    if (!visible && depth == 0 && OwnsWindowState() && !advancing) {
        Config config;
        void* data = Field<void*>(window, abi::WindowData);
        if (data && ReadConfig(config) && (config.flags & Hiding)
            && !(Field<DWORD>(data, 0x74) & WS_MINIMIZE) && !(Field<BYTE>(data, 0x2e0) & 4)) {
            const HWND handle = Field<HWND>(data, 0x28);
            DWORD pid = 0;
            GetWindowThreadProcessId(handle, &pid);
            bool known = false;
            for (uint32_t i = 0; i < config.count; ++i)
                if (config.anchors[i].window == reinterpret_cast<uint64_t>(handle) && config.anchors[i].pid == pid) { known = true; break; }
            HWND eligibleHandle;
            DWORD eligiblePid;
            if (known && pid && pid != config.publisher && Candidate(window, config, eligibleHandle, eligiblePid)) {
                const auto hr = delayHide(window);
                if (SUCCEEDED(hr)) return hr;
            }
        }
    }
    if (visible && depth == 0 && OwnsWindowState()) {
        auto* item = Find(window);
        if (item && item->kind == 8 && item->master && item->master == Field<void*>(window, abi::Master)
            && item->data == Field<void*>(window, abi::WindowData)) initial = true;
    }
    return showOriginal(window, visible, initial);
}

void AdvanceHook(void* manager, double time) {
    const bool previous = advancing;
    advancing = true;
    advanceOriginal(manager, time);
    advancing = previous;
}

bool Prolog(uintptr_t address, const BYTE* expected, size_t bytes) {
    return memcmp(reinterpret_cast<BYTE*>(image) + address, expected, bytes) == 0;
}

bool Install() {
    image = GetModuleHandleW(L"udwm.dll");
    if (!image || !SupportedModule(image)) { status->state = 2; status->error = 2; return false; }
    const BYTE startBytes[]{0x48,0x8b,0xc4,0x48,0x89,0x58,0x18,0x55};
    const BYTE stopBytes[]{0x48,0x8b,0xc4,0x48,0x89,0x58,0x10};
    const BYTE showBytes[]{0x48,0x89,0x5c,0x24,0x08,0x48,0x89,0x6c,0x24,0x10};
    const BYTE advanceBytes[]{0x48,0x83,0xec,0x68,0x48,0x8b,0x05};
    const BYTE delayBytes[]{0x48,0x89,0x5c,0x24,0x08,0x57};
    const BYTE lockReference[]{0x4c,0x8d,0x3d,0x10,0xb4,0x0a,0x00};
    if (!Prolog(abi::StartAnimation,startBytes,sizeof(startBytes)) || !Prolog(abi::StopAnimation,stopBytes,sizeof(stopBytes))
        || !Prolog(abi::ShowWindow,showBytes,sizeof(showBytes)) || !Prolog(abi::AdvanceTimelines,advanceBytes,sizeof(advanceBytes))
        || !Prolog(abi::StartDelayHide,delayBytes,sizeof(delayBytes)) || !Prolog(0x808f9,lockReference,sizeof(lockReference))) {
        status->state = 2; status->error = 3; return false;
    }
    allocate = abi::Function<abi::Alloc>(image, abi::Allocate);
    freeBlock = abi::Function<abi::FreeBlock>(image, abi::Free);
    construct = abi::Function<abi::Ctor>(image, abi::Construct);
    release = abi::Function<abi::ReleaseObject>(image, abi::Release);
    relativeRect = abi::Function<abi::GetRelative>(image, abi::RelativeRect);
    delayHide = abi::Function<abi::Stop>(image, abi::StartDelayHide);
    if (MH_Initialize() != MH_OK) return false;
    struct Hook { uintptr_t address; void* callback; void** original; };
    Hook hooks[]{
        {abi::StartAnimation,reinterpret_cast<void*>(StartHook),reinterpret_cast<void**>(&startOriginal)},
        {abi::StopAnimation,reinterpret_cast<void*>(StopHook),reinterpret_cast<void**>(&stopOriginal)},
        {abi::ShowWindow,reinterpret_cast<void*>(ShowHook),reinterpret_cast<void**>(&showOriginal)},
        {abi::AdvanceTimelines,reinterpret_cast<void*>(AdvanceHook),reinterpret_cast<void**>(&advanceOriginal)}
    };
    for (const auto& hook : hooks) {
        void* target = reinterpret_cast<BYTE*>(image) + hook.address;
        if (MH_CreateHook(target, hook.callback, hook.original) != MH_OK || MH_QueueEnableHook(target) != MH_OK) {
            MH_Uninitialize(); return false;
        }
    }
    if (MH_ApplyQueued() != MH_OK) { MH_DisableHook(MH_ALL_HOOKS); return false; }
    return true;
}

DWORD WINAPI Initialize(void*) {
    DWORD session = 0;
    if (!ProcessIdToSessionId(GetCurrentProcessId(), &session)) return 1;
    wchar_t executable[MAX_PATH], system[MAX_PATH], expected[MAX_PATH];
    if (!GetModuleFileNameW(nullptr, executable, MAX_PATH) || !GetSystemDirectoryW(system, MAX_PATH)) return 1;
    swprintf_s(expected, L"%ls\\dwm.exe", system);
    if (_wcsicmp(executable, expected) != 0) return 1;
    wchar_t name[128];
    swprintf_s(name, L"%ls%lu", StatusPrefix, session);
    HANDLE statusMap = OpenFileMappingW(FILE_MAP_WRITE | FILE_MAP_READ, FALSE, name);
    if (!statusMap) return 3;
    status = static_cast<Status*>(MapViewOfFile(statusMap, FILE_MAP_WRITE | FILE_MAP_READ, 0, 0, sizeof(Status)));
    CloseHandle(statusMap);
    if (!status) return 4;
    ZeroMemory(status, sizeof(*status));
    status->magic = Magic; status->version = Protocol; status->bytes = sizeof(Status);
    status->pid = GetCurrentProcessId(); status->build = Build;
    swprintf_s(name, L"%ls%lu", ConfigPrefix, session);
    HANDLE configMap = OpenFileMappingW(FILE_MAP_READ, FALSE, name);
    if (configMap) {
        sharedConfig = static_cast<Config*>(MapViewOfFile(configMap, FILE_MAP_READ, 0, 0, sizeof(Config)));
        if (!sharedConfig) status->reserved = GetLastError();
        CloseHandle(configMap);
    } else status->reserved = GetLastError();
    if (!sharedConfig) { status->error = 10; InterlockedExchange(&status->state, 3); }
    else if (Install()) InterlockedExchange(&status->state, 1);
    else if (!status->state) { status->error = 4; InterlockedExchange(&status->state, 3); }
    for (;;) { InterlockedExchange64(&status->heartbeat, static_cast<LONG64>(GetTickCount64())); Sleep(250); }
}
}

BOOL WINAPI DllMain(HINSTANCE module, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(module);
        HANDLE worker = CreateThread(nullptr, 0, Initialize, nullptr, 0, nullptr);
        if (worker) CloseHandle(worker);
    }
    return TRUE;
}
