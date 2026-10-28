#pragma once
#include <windows.h>
#include <stdint.h>
#include <stddef.h>

namespace motion {
constexpr uint32_t Magic = 0x4d444e57;
constexpr uint32_t Protocol = 1;
constexpr uint32_t Build = 7;
constexpr uint32_t Capacity = 256;
constexpr uint32_t Enabled = 1, Opening = 2, Closing = 4, Minimizing = 8, Restoring = 16, Hiding = 32;
constexpr wchar_t ConfigPrefix[] = L"Local\\WinDock.Motion.Config.v1.";
constexpr wchar_t StatusPrefix[] = L"Local\\WinDock.Motion.Status.v1.";

struct Rect { float x, y, width, height; };
struct Anchor {
    uint64_t window;
    uint32_t pid, flags;
    uint64_t executable, started;
    Rect rect;
};
struct Config {
    uint32_t magic, version, bytes;
    volatile LONG sequence;
    uint32_t publisher, flags;
    uint64_t heartbeat;
    uint32_t openMs, closeMs, minimizeMs, restoreMs;
    float closeDistance, closeScale, fadeHold;
    uint32_t curve, count, reserved;
    uint64_t publisherStarted;
    Rect fallback;
    Anchor anchors[Capacity];
};
struct Status {
    uint32_t magic, version, bytes, pid;
    volatile LONG state, error;
    uint32_t build, reserved;
    volatile LONG64 heartbeat, opened, closed, minimized, restored, hidden, skipped, failed;
};
static_assert(sizeof(Rect) == 16 && sizeof(Anchor) == 48);
static_assert(offsetof(Config, anchors) == 96 && sizeof(Config) == 12384);
static_assert(offsetof(Status, heartbeat) == 32 && sizeof(Status) == 96);

inline uint64_t PathHash(const wchar_t* path) {
    uint64_t hash = 14695981039346656037ull;
    for (; *path; ++path) {
        wchar_t c = *path;
        if (c >= L'a' && c <= L'z') c -= 32;
        if (c == L'/') c = L'\\';
        hash = (hash ^ static_cast<uint16_t>(c)) * 1099511628211ull;
    }
    return hash;
}
}
