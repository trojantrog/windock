// Copyright (c) 2026 WinDock contributors. MIT License.
#ifndef WINDOCK_TRAY_IMAGES_H
#define WINDOCK_TRAY_IMAGES_H
#include <windows.h>
#include <stdint.h>

#define TRAY_IMAGE_MAX_DIMENSION 256u
#define TRAY_IMAGE_MAX_BYTES (TRAY_IMAGE_MAX_DIMENSION * TRAY_IMAGE_MAX_DIMENSION * 4u)
#define TRAY_IMAGE_REQUEST_MESSAGE L"WinDock.TrayHook.Images.v6"

#pragma pack(push, 1)
typedef struct {
    uint32_t nonce, pixelFormat;
    uint64_t sequence, window;
    uint32_t process, thread, id, reserved;
    GUID guid;
    uint64_t processStarted;
    uint32_t width, height, stride, pixelBytes;
} TrayImageBody;
typedef struct {
    uint32_t nonce, status;
    uint64_t sequence;
    uint32_t sampled, published;
} TrayImageStatus;
#pragma pack(pop)
_Static_assert(sizeof(TrayImageBody) == 80, "v6 image body");
_Static_assert(sizeof(TrayImageStatus) == 24, "v6 image status body");

typedef struct {
    TrayImageBody body;
    BYTE pixels[];
} TrayImageResult;
_Static_assert(sizeof(TrayImageResult) == 80, "v6 image allocation prefix");
_Static_assert(offsetof(TrayImageResult, pixels) == 80, "v6 image pixel offset");

// Begin/Request run on the verified Explorer hook thread. Cancel never waits
// for a COM operation, and may also run on the pipe thread during teardown.
void TrayImagesBegin(HMODULE module, HWND tray, DWORD host, DWORD nonce);
void TrayImagesRequest(HWND tray, DWORD host, DWORD nonce);
void TrayImagesCancel(DWORD nonce);
void TrayImagesFreeResult(TrayImageResult *result);

// Implemented by tray_hook.c. A successful PublishImage transfers ownership.
// Status has a separate, replaceable slot and cannot displace a protocol packet.
BOOL TrayHookPublishImage(TrayImageResult *result);
void TrayHookPublishImageStatus(TrayImageStatus status);
BOOL TrayHookImageBoundary(DWORD nonce);
uint64_t TrayHookNextSequence(void);
uint64_t TrayHookCurrentSequence(void);
uint64_t TrayHookCurrentMutation(void);
#endif
