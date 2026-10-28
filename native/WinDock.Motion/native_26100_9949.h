#pragma once
#include <windows.h>
#include <stdint.h>
#include <stddef.h>

namespace motion::abi {
constexpr uintptr_t StartAnimation = 0xc684;
constexpr uintptr_t StopAnimation = 0x14c70;
constexpr uintptr_t ShowWindow = 0x12dfc;
constexpr uintptr_t StartDelayHide = 0x96750;
constexpr uintptr_t Allocate = 0x93320;
constexpr uintptr_t Free = 0x92d00;
constexpr uintptr_t Construct = 0xc0bc;
constexpr uintptr_t Release = 0x506f0;
constexpr uintptr_t FloatVtable = 0xff180;
constexpr uintptr_t RelativeRect = 0x2eb04;
constexpr uintptr_t AdvanceTimelines = 0x17000;
constexpr uintptr_t WindowStateLock = 0x12bd10;
constexpr size_t AllocationSize = 0x78;
constexpr size_t Master = 0x140, AnimationKind = 0x150, Tracks = 0x158, WindowData = 0x120;
constexpr size_t Secondary = 0x128, CachedRect = 0x110, Origin = 0x1dc, SpecialClip = 0x238;
constexpr size_t Current = 0x30, Finished = 0x48;
// CWindowData::GetNonClientAreaMargins (0x2d6dc) selects SM_CYCAPTION /
// SM_CYSMCAPTION from WS_EX_TOOLWINDOW at +0x78. +0x7c is a different field.
constexpr size_t DataHandle = 0x28, DataStyle = 0x74, DataExtendedStyle = 0x78;

struct Interpolation { uint32_t mode, padding; double x1, y1, x2, y2; };
static_assert(sizeof(Interpolation) == 40);
static_assert(sizeof(CRITICAL_SECTION) == 40);
static_assert(offsetof(CRITICAL_SECTION, RecursionCount) == 12 && offsetof(CRITICAL_SECTION, OwningThread) == 16);

template<class T> inline T& Field(void* object, size_t offset) {
    return *reinterpret_cast<T*>(static_cast<BYTE*>(object) + offset);
}
template<class T> inline T Function(HMODULE module, uintptr_t rva) {
    return reinterpret_cast<T>(reinterpret_cast<BYTE*>(module) + rva);
}
using Start = HRESULT (*)(void*, int);
using Stop = HRESULT (*)(void*);
using Show = HRESULT (*)(void*, bool, bool);
using Advance = void (*)(void*, double);
using Alloc = void* (*)(size_t);
using FreeBlock = void (*)(void*);
using Ctor = void (*)(void*, double, double, double, const Interpolation*, double);
using ReleaseObject = ULONG (*)(void*);
using GetRelative = RECT* (*)(void*, RECT*);
}
